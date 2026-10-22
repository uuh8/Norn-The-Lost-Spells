using Game.Materials;
using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 在正式 Stage 枚举前准备跨边界邻居。它先把 Key 收集到预分配 Buffer，之后才统一创建 Chunk，
    /// 从结构上杜绝“枚举 Dictionary 时修改 Dictionary”的异常，也让本 Tick 的 Snapshot 集合固定。
    /// </summary>
    public sealed class ElementBoundaryNeighborPlanner
    {
        private readonly ElementChunkKey[] _pendingKeys;
        private int _pendingCount;

        public ElementBoundaryNeighborPlanner(int maximumResidentChunks)
        {
            if (maximumResidentChunks <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumResidentChunks));

            _pendingKeys = new ElementChunkKey[maximumResidentChunks];
        }

        public int PrepareNeighbors(
            ElementWorldStore store,
            ElementWorldChunk[] simulationChunks,
            int simulationChunkCount,
            long worldTick,
            byte maxDownFlowPerTick,
            byte maxLateralFlowPerTick,
            byte maxFireDownFlowPerTick=0,
            bool simulateCellWater=true)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (simulationChunks == null)
                throw new ArgumentNullException(nameof(simulationChunks));
            if (simulationChunkCount < 0 || simulationChunkCount > simulationChunks.Length)
                throw new ArgumentOutOfRangeException(nameof(simulationChunkCount));
            if (worldTick < 0)
                throw new ArgumentOutOfRangeException(nameof(worldTick));

            _pendingCount = 0;
            int last = store.ChunkSize - 1;

            // 只扫描调用方已经判定为 Active 的初始集合；新建邻居是接收端，不递归向外扩张空 Chunk。
            for (int chunkIndex = 0; chunkIndex < simulationChunkCount; chunkIndex++)
            {
                ElementWorldChunk chunk = simulationChunks[chunkIndex];
                if (chunk == null)
                    continue;
                // GPU/Grid液体不经过旧Cell Water；没有Fire的Chunk无需为火焰准备任何邻居。
                if (!simulateCellWater && !chunk.HasFire)
                    continue;

                for (int z = 0; z <= last; z++)
                for (int y = 0; y <= last; y++)
                for (int x = 0; x <= last; x++)
                {
                    // 只有外表面才可能跨Chunk；内部Cell无需读取和重复入队同一个邻居。
                    if(x>0 && x<last && y>0 && y<last && z>0 && z<last)continue;
                    ElementCell cell = chunk.GetCell(new Vector3Int(x, y, z));
                    if (cell.IsEmpty)
                        continue;

                    bool water = cell.MaterialKind == MaterialId.Water;
                    bool fire = cell.MaterialKind == MaterialId.Fire;
                    if (!water && !fire)
                        continue;
                    if(!simulateCellWater)
                    {
                        if(fire && y==0 && maxFireDownFlowPerTick>0)
                            QueueNeighbor(store,new ElementChunkKey(chunk.Key.X,chunk.Key.Y-1,chunk.Key.Z),true);
                        continue;
                    }

                    // 对“不存在的水平邻居”，先用与 Solver 完全相同的扩散公式做廉价预测。
                    // transfer = min(maxTransfer, floor((source - 0) / 8))。
                    // 结果为 0 时创建 8^3 Cell 的 Chunk 既不会改变 Gameplay，又会触发 Solid Bake 和后续扫描。
                    bool canCreateHorizontalReceiver = water
                        && ElementFieldSimulator.CalculateStableLateralTransfer(
                            cell.Amount,
                            targetAmount: 0,
                            maxLateralFlowPerTick) > 0;
                    bool canCreateDownReceiver = cell.Amount>0
                        && (water && maxDownFlowPerTick>0 || fire && maxFireDownFlowPerTick>0);

                    if (x == 0)
                        QueueNeighbor(store, new ElementChunkKey(chunk.Key.X - 1, chunk.Key.Y, chunk.Key.Z), canCreateHorizontalReceiver);
                    if (x == last)
                        QueueNeighbor(store, new ElementChunkKey(chunk.Key.X + 1, chunk.Key.Y, chunk.Key.Z), canCreateHorizontalReceiver);
                    if (z == 0)
                        QueueNeighbor(store, new ElementChunkKey(chunk.Key.X, chunk.Key.Y, chunk.Key.Z - 1), canCreateHorizontalReceiver);
                    if (z == last)
                        QueueNeighbor(store, new ElementChunkKey(chunk.Key.X, chunk.Key.Y, chunk.Key.Z + 1), canCreateHorizontalReceiver);

                    // Water 只向下流，因此 Y- 缺失时需要创建；Y+ 只可能参与反应，缺失时不应浪费内存。
                    if (y == 0)
                        QueueNeighbor(store, new ElementChunkKey(chunk.Key.X, chunk.Key.Y - 1, chunk.Key.Z), canCreateDownReceiver);
                    if (y == last)
                        QueueNeighbor(store, new ElementChunkKey(chunk.Key.X, chunk.Key.Y + 1, chunk.Key.Z), false);
                }
            }

            for (int i = 0; i < _pendingCount; i++)
            {
                if (simulationChunkCount >= simulationChunks.Length)
                    break;

                ElementChunkKey key = _pendingKeys[i];
                if (Contains(simulationChunks, simulationChunkCount, key)
                    || !store.TryGetOrCreateChunk(key, out ElementWorldChunk neighbor))
                {
                    continue;
                }

                // 跨边界传输/反应属于真实 Gameplay 事件，需要唤醒邻居并刷新 Grace Lease。
                neighbor.ActivityState = ElementChunkActivityState.Active;
                neighbor.WakeForSimulation(worldTick);
                simulationChunks[simulationChunkCount++] = neighbor;
            }

            return simulationChunkCount;
        }

        private void QueueNeighbor(
            ElementWorldStore store,
            ElementChunkKey key,
            bool createIfMissing)
        {
            if (!createIfMissing && !store.TryGetChunk(key, out _))
                return;
            if (_pendingCount >= _pendingKeys.Length)
                return;

            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pendingKeys[i] == key)
                    return;
            }

            _pendingKeys[_pendingCount++] = key;
        }

        private static bool Contains(
            ElementWorldChunk[] chunks,
            int count,
            ElementChunkKey key)
        {
            for (int i = 0; i < count; i++)
            {
                if (chunks[i] != null && chunks[i].Key == key)
                    return true;
            }

            return false;
        }
    }
}
