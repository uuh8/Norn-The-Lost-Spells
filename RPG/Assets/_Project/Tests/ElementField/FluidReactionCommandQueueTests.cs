using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class FluidReactionCommandQueueTests
    {
        [Test]
        public void Batch_WhenEitherSideHasNoCapacity_CommitsNeitherSide()
        {
            var queue = new FluidReactionCommandQueue(1);
            var fire = new[]
            {
                new ElementCellDeltaRequest(Vector3Int.zero, MaterialId.Fire, 3),
                new ElementCellDeltaRequest(Vector3Int.right, MaterialId.Fire, 4),
            };
            var consume = new[]
            {
                new FluidConsumeCommand(Vector3Int.zero, Vector3Int.zero, 0u, 1u, 7u),
                new FluidConsumeCommand(Vector3Int.right, Vector3Int.right, 0u, 1u, 8u),
            };

            Assert.That(queue.TryEnqueueBatch(fire, consume, 2), Is.False);
            Assert.That(queue.PendingFireDeltaCount, Is.Zero);
            Assert.That(queue.PendingConsumeCount, Is.Zero);
            Assert.That(queue.RejectedBatchCount, Is.EqualTo(1));
        }

        [Test]
        public void Batch_ExposesIndependentFifoConsumersAfterAtomicCommit()
        {
            var queue = new FluidReactionCommandQueue(2);
            var fire = new[]
            {
                new ElementCellDeltaRequest(new Vector3Int(-1, 0, 0), MaterialId.Fire, 3),
                new ElementCellDeltaRequest(new Vector3Int(2, 0, 0), MaterialId.Fire, 4),
            };
            var consume = new[]
            {
                new FluidConsumeCommand(new Vector3Int(-1, 0, 0), Vector3Int.zero, 0u, 1u, 7u),
                new FluidConsumeCommand(new Vector3Int(2, 0, 0), new Vector3Int(3, 0, 0), 0u, 2u, 8u),
            };

            Assert.That(queue.TryEnqueueBatch(fire, consume, 2), Is.True);
            Assert.That(queue.TryDequeueFireDelta(out ElementCellDeltaRequest first), Is.True);
            Assert.That(first.GlobalCell, Is.EqualTo(new Vector3Int(-1, 0, 0)));

            var destination = new FluidConsumeCommand[2];
            Assert.That(queue.CopyConsumeCommandsAndClear(destination), Is.EqualTo(2));
            Assert.That(destination[0].MinimumCell, Is.EqualTo(new Vector3Int(-1, 0, 0)));
            Assert.That(destination[1].MaximumParticleCount, Is.EqualTo(2u));
            Assert.That(queue.PendingConsumeCount, Is.Zero);
            Assert.That(queue.PendingFireDeltaCount, Is.EqualTo(1));
        }

        [Test]
        public void DamageCommand_PreservesReactionIdentityAndWorldPosition()
        {
            // World Adapter 只负责搬运已确定的 Gameplay Fact，Rendering 不应再从伤害数值反猜反应类型。
            var queue = new FluidReactionCommandQueue(1);
            var consume = new[]
            {
                new FluidConsumeCommand(Vector3Int.zero, Vector3Int.zero, (uint)MaterialId.Poison, 1u, 9u),
            };
            Vector3 position = new Vector3(2f, 0.5f, -3f);
            var damage = new[]
            {
                new WorldReactionDamageCommand(
                    ElementReactionId.ToxicCombustion,
                    position,
                    12f,
                    1.5f),
            };

            Assert.That(queue.TryEnqueueBatch(
                System.Array.Empty<ElementCellDeltaRequest>(), 0,
                consume, 1,
                damage, 1), Is.True);
            Assert.That(queue.TryDequeueDamage(out WorldReactionDamageCommand received), Is.True);
            Assert.That(received.Reaction, Is.EqualTo(ElementReactionId.ToxicCombustion));
            Assert.That(received.WorldPosition, Is.EqualTo(position));
        }

        [Test]
        public void StickyBatches_SupportAtomicWaterConversionAndFireProduction()
        {
            var conversionOnly = new FluidReactionCommandQueue(1);
            var conversions = new[]
            {
                new FluidConvertCommand(Vector3Int.zero, Vector3Int.zero,
                    (uint)MaterialId.Water, (uint)MaterialId.Sticky, 2u, 3u),
            };
            Assert.That(conversionOnly.TryEnqueueBatch(
                System.Array.Empty<ElementCellDeltaRequest>(), 0,
                System.Array.Empty<FluidConsumeCommand>(), 0,
                System.Array.Empty<WorldReactionDamageCommand>(), 0,
                System.Array.Empty<ElementCellAddRequest>(), 0,
                conversions, 1), Is.True,
                "AbsorbWater 只有 GPU Convert，也必须是合法事务。");
            var uploaded = new FluidConvertCommand[1];
            Assert.That(conversionOnly.CopyConvertCommandsAndClear(uploaded), Is.EqualTo(1));
            Assert.That(uploaded[0].SourceMaterialId, Is.EqualTo((uint)MaterialId.Water));
            Assert.That(uploaded[0].TargetMaterialId, Is.EqualTo((uint)MaterialId.Sticky));

            var consume = new[]
            {
                new FluidConsumeCommand(Vector3Int.zero, Vector3Int.zero,
                    (uint)MaterialId.Sticky, 2u, 3u),
            };

            var ignite = new FluidReactionCommandQueue(1);
            var adds = new[]
            {
                new ElementCellAddRequest(Vector3Int.right, MaterialId.Fire, 12),
            };
            Assert.That(ignite.TryEnqueueBatch(
                System.Array.Empty<ElementCellDeltaRequest>(), 0,
                consume, 1,
                System.Array.Empty<WorldReactionDamageCommand>(), 0,
                adds, 1), Is.True);
            Assert.That(ignite.TryDequeueElementAdd(out ElementCellAddRequest add), Is.True);
            Assert.That((add.GlobalCell, add.Material, add.AmountToAdd),
                Is.EqualTo((Vector3Int.right, MaterialId.Fire, (byte)12)));
        }
    }
}
