using Game.Combat;
using Game.Materials;
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>只允许 Runtime 内部扣减既有 Fire；公共 Projectile 写入口永远看不到负 Amount。</summary>
    public readonly struct ElementCellDeltaRequest
    {
        public ElementCellDeltaRequest(
            Vector3Int globalCell,
            MaterialId expectedMaterial,
            byte amountToRemove)
        {
            GlobalCell = globalCell;
            ExpectedMaterial = expectedMaterial;
            AmountToRemove = amountToRemove;
        }

        public Vector3Int GlobalCell { get; }
        public MaterialId ExpectedMaterial { get; }
        public byte AmountToRemove { get; }
    }

    /// <summary>
    /// 反应生成 CPU Element Cell 的增量命令。当前用于 Sticky 转化为 Fire；
    /// 生成与 GPU Sticky 消耗必须进入同一个 Batch，队列满时两者一起拒绝。
    /// </summary>
    public readonly struct ElementCellAddRequest
    {
        public ElementCellAddRequest(Vector3Int globalCell, MaterialId material, byte amountToAdd)
        {
            GlobalCell = globalCell;
            Material = material;
            AmountToAdd = amountToAdd;
        }
        public Vector3Int GlobalCell { get; }
        public MaterialId Material { get; }
        public byte AmountToAdd { get; }
    }

    /// <summary>
    /// CPU/HLSL 共用的 48-byte 消耗命令。AABB 使用 inclusive Global Cell；GPU 只释放匹配材质的粒子。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidConsumeCommand
    {
        public readonly FluidGpuInt4 MinimumCellAndPadding;
        public readonly FluidGpuInt4 MaximumCellAndParticleCount;
        public readonly FluidGpuUInt4 MaterialSeedPadding;

        public FluidConsumeCommand(
            Vector3Int minimumCell,
            Vector3Int maximumCell,
            uint materialId,
            uint maximumParticleCount,
            uint seed)
        {
            MinimumCellAndPadding = new FluidGpuInt4(
                minimumCell.x, minimumCell.y, minimumCell.z, 0);
            MaximumCellAndParticleCount = new FluidGpuInt4(
                maximumCell.x, maximumCell.y, maximumCell.z,
                unchecked((int)maximumParticleCount));
            MaterialSeedPadding = new FluidGpuUInt4(materialId, seed, 0u, 0u);
        }

        public Vector3Int MinimumCell => new Vector3Int(
            MinimumCellAndPadding.X,
            MinimumCellAndPadding.Y,
            MinimumCellAndPadding.Z);
        public Vector3Int MaximumCell => new Vector3Int(
            MaximumCellAndParticleCount.X,
            MaximumCellAndParticleCount.Y,
            MaximumCellAndParticleCount.Z);
        public uint MaximumParticleCount => unchecked((uint)MaximumCellAndParticleCount.W);
    }

    /// <summary>
    /// CPU/HLSL 共用的 48-byte 原位材质转换命令。它只把 AABB 内有限数量的 Source 粒子
    /// Retag 为 Target，不创建/释放粒子，因此 Active/Free Counter 和粒子总数保持不变。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidConvertCommand
    {
        public readonly FluidGpuInt4 MinimumCellAndPadding;
        public readonly FluidGpuInt4 MaximumCellAndParticleCount;
        public readonly FluidGpuUInt4 SourceTargetSeedPadding;

        public FluidConvertCommand(
            Vector3Int minimumCell,
            Vector3Int maximumCell,
            uint sourceMaterialId,
            uint targetMaterialId,
            uint maximumParticleCount,
            uint seed)
        {
            MinimumCellAndPadding = new FluidGpuInt4(
                minimumCell.x, minimumCell.y, minimumCell.z, 0);
            MaximumCellAndParticleCount = new FluidGpuInt4(
                maximumCell.x, maximumCell.y, maximumCell.z,
                unchecked((int)maximumParticleCount));
            SourceTargetSeedPadding = new FluidGpuUInt4(
                sourceMaterialId, targetMaterialId, seed, 0u);
        }

        public uint SourceMaterialId => SourceTargetSeedPadding.X;
        public uint TargetMaterialId => SourceTargetSeedPadding.Y;
        public uint MaximumParticleCount => unchecked((uint)MaximumCellAndParticleCount.W);
    }

    /// <summary>
    /// 两条固定容量 Ring Buffer 共用一次 Batch Admission。只有 Fire Delta 与 GPU Consume 都放得下时
    /// 才同时提交，避免“火先灭了，但水粒子因队列满没有减少”的跨 Timeline 失守恒。
    /// </summary>
    public sealed class FluidReactionCommandQueue
    {
        private readonly ElementCellDeltaRequest[] _fireDeltas;
        private readonly FluidConsumeCommand[] _consumeCommands;
        private readonly WorldReactionDamageCommand[] _damageCommands;
        private readonly ElementCellAddRequest[] _elementAdds;
        private readonly FluidConvertCommand[] _convertCommands;
        private int _fireHead;
        private int _fireCount;
        private int _consumeHead;
        private int _consumeCount;
        private int _damageHead;
        private int _damageCount;
        private int _addHead;
        private int _addCount;
        private int _convertHead;
        private int _convertCount;

        public FluidReactionCommandQueue(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _fireDeltas = new ElementCellDeltaRequest[capacity];
            _consumeCommands = new FluidConsumeCommand[capacity];
            _damageCommands = new WorldReactionDamageCommand[capacity];
            _elementAdds = new ElementCellAddRequest[capacity];
            _convertCommands = new FluidConvertCommand[capacity];
        }

        public int Capacity => _fireDeltas.Length;
        public int PendingFireDeltaCount => _fireCount;
        public int PendingConsumeCount => _consumeCount;
        public uint RejectedBatchCount { get; private set; }
        public int PendingDamageCount => _damageCount;
        public int PendingElementAddCount => _addCount;
        public int PendingConvertCount => _convertCount;

        public bool TryEnqueueBatch(
            ElementCellDeltaRequest[] fireDeltas,
            FluidConsumeCommand[] consumeCommands,
            int count)
        {
            return TryEnqueueBatch(fireDeltas, count, consumeCommands, count);
        }

        public bool TryEnqueueBatch(
            ElementCellDeltaRequest[] fireDeltas,
            int fireDeltaCount,
            FluidConsumeCommand[] consumeCommands,
            int consumeCommandCount)
        {
            return TryEnqueueBatch(fireDeltas, fireDeltaCount, consumeCommands, consumeCommandCount,
                System.Array.Empty<WorldReactionDamageCommand>(), 0,
                System.Array.Empty<ElementCellAddRequest>(), 0);
        }

        public bool TryEnqueueBatch(
            ElementCellDeltaRequest[] fireDeltas,
            int fireDeltaCount,
            FluidConsumeCommand[] consumeCommands,
            int consumeCommandCount,
            WorldReactionDamageCommand[] damageCommands,
            int damageCommandCount)
        {
            return TryEnqueueBatch(fireDeltas, fireDeltaCount, consumeCommands, consumeCommandCount,
                damageCommands, damageCommandCount, System.Array.Empty<ElementCellAddRequest>(), 0);
        }

        public bool TryEnqueueBatch(
            ElementCellDeltaRequest[] fireDeltas,
            int fireDeltaCount,
            FluidConsumeCommand[] consumeCommands,
            int consumeCommandCount,
            WorldReactionDamageCommand[] damageCommands,
            int damageCommandCount,
            ElementCellAddRequest[] elementAdds,
            int elementAddCount)
        {
            return TryEnqueueBatch(
                fireDeltas, fireDeltaCount,
                consumeCommands, consumeCommandCount,
                damageCommands, damageCommandCount,
                elementAdds, elementAddCount,
                System.Array.Empty<FluidConvertCommand>(), 0);
        }

        public bool TryEnqueueBatch(
            ElementCellDeltaRequest[] fireDeltas,
            int fireDeltaCount,
            FluidConsumeCommand[] consumeCommands,
            int consumeCommandCount,
            WorldReactionDamageCommand[] damageCommands,
            int damageCommandCount,
            ElementCellAddRequest[] elementAdds,
            int elementAddCount,
            FluidConvertCommand[] convertCommands,
            int convertCommandCount)
        {
            if (fireDeltas == null)
                throw new ArgumentNullException(nameof(fireDeltas));
            if (consumeCommands == null)
                throw new ArgumentNullException(nameof(consumeCommands));
            if (damageCommands == null)
                throw new ArgumentNullException(nameof(damageCommands));
            if (elementAdds == null)
                throw new ArgumentNullException(nameof(elementAdds));
            if (convertCommands == null)
                throw new ArgumentNullException(nameof(convertCommands));
            if (fireDeltaCount < 0 || fireDeltaCount > fireDeltas.Length)
                throw new ArgumentOutOfRangeException(nameof(fireDeltaCount));
            if (consumeCommandCount < 0 || consumeCommandCount > consumeCommands.Length)
                throw new ArgumentOutOfRangeException(nameof(consumeCommandCount));
            if (damageCommandCount < 0 || damageCommandCount > damageCommands.Length)
                throw new ArgumentOutOfRangeException(nameof(damageCommandCount));
            if (elementAddCount < 0 || elementAddCount > elementAdds.Length)
                throw new ArgumentOutOfRangeException(nameof(elementAddCount));
            if (convertCommandCount < 0 || convertCommandCount > convertCommands.Length)
                throw new ArgumentOutOfRangeException(nameof(convertCommandCount));
            // 世界液体反应必须至少包含一次 GPU 粒子改变；只写 CPU Fire/伤害不能独立成交。
            if (consumeCommandCount == 0 && convertCommandCount == 0)
                return false;
            if (fireDeltaCount > Capacity - _fireCount
                || consumeCommandCount > Capacity - _consumeCount
                || damageCommandCount > Capacity - _damageCount
                || elementAddCount > Capacity - _addCount
                || convertCommandCount > Capacity - _convertCount)
            {
                if (RejectedBatchCount < uint.MaxValue)
                    RejectedBatchCount++;
                return false;
            }

            for (int index = 0; index < fireDeltaCount; index++)
            {
                int fireTail = (_fireHead + _fireCount) % Capacity;
                _fireDeltas[fireTail] = fireDeltas[index];
                _fireCount++;
            }
            for (int index = 0; index < consumeCommandCount; index++)
            {
                int consumeTail = (_consumeHead + _consumeCount) % Capacity;
                _consumeCommands[consumeTail] = consumeCommands[index];
                _consumeCount++;
            }
            for (int index = 0; index < damageCommandCount; index++)
            {
                int tail = (_damageHead + _damageCount) % Capacity;
                _damageCommands[tail] = damageCommands[index];
                _damageCount++;
            }
            for (int index = 0; index < elementAddCount; index++)
            {
                int tail = (_addHead + _addCount) % Capacity;
                _elementAdds[tail] = elementAdds[index];
                _addCount++;
            }
            for (int index = 0; index < convertCommandCount; index++)
            {
                int tail = (_convertHead + _convertCount) % Capacity;
                _convertCommands[tail] = convertCommands[index];
                _convertCount++;
            }
            return true;
        }

        public int CopyConvertCommandsAndClear(FluidConvertCommand[] destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            int count = Math.Min(destination.Length, _convertCount);
            for (int index = 0; index < count; index++)
                destination[index] = _convertCommands[(_convertHead + index) % Capacity];
            _convertHead = (_convertHead + count) % Capacity;
            _convertCount -= count;
            return count;
        }

        public bool TryDequeueElementAdd(out ElementCellAddRequest request)
        {
            if (_addCount == 0) { request = default; return false; }
            request = _elementAdds[_addHead];
            _addHead = (_addHead + 1) % Capacity;
            _addCount--;
            return true;
        }

        public bool TryDequeueDamage(out WorldReactionDamageCommand command)
        {
            if (_damageCount == 0) { command = default; return false; }
            command = _damageCommands[_damageHead];
            _damageHead = (_damageHead + 1) % Capacity;
            _damageCount--;
            return true;
        }

        public bool TryDequeueFireDelta(out ElementCellDeltaRequest request)
        {
            if (_fireCount == 0)
            {
                request = default;
                return false;
            }
            request = _fireDeltas[_fireHead];
            _fireHead = (_fireHead + 1) % Capacity;
            _fireCount--;
            return true;
        }

        public int CopyConsumeCommandsAndClear(FluidConsumeCommand[] destination)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            int count = Math.Min(destination.Length, _consumeCount);
            for (int index = 0; index < count; index++)
                destination[index] = _consumeCommands[(_consumeHead + index) % Capacity];
            _consumeHead = (_consumeHead + count) % Capacity;
            _consumeCount -= count;
            return count;
        }
    }

    public readonly struct WorldReactionDamageCommand
    {
        public readonly ElementReactionId Reaction;
        public readonly Vector3 WorldPosition;
        public readonly float Amount;
        public readonly float Radius;
        public WorldReactionDamageCommand(
            ElementReactionId reaction,
            Vector3 worldPosition,
            float amount,
            float radius)
        {
            Reaction = reaction;
            WorldPosition = worldPosition;
            Amount = amount;
            Radius = radius;
        }
    }
}
