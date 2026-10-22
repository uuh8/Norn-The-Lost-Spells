using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>验证第二种 Liquid 复用同一 Occupancy、Reaction 与 Consume Transaction，而非复制 Water Runtime。</summary>
    public sealed class GpuMultiLiquidGameplayTests
    {
        [Test]
        public void PoisonFire_WaitsForWindupThenUsesPoisonScaleAndContactCenter()
        {
            var occupancy = new MixedOccupancy();
            var fireCell = new Vector3Int(2, 0, 3);
            var poisonCell = fireCell + Vector3Int.right;
            occupancy.Set(poisonCell, MaterialId.Poison, 12);
            occupancy.Set(poisonCell, MaterialId.Water, 200);
            var fires = new[] { new LiquidCellFireSample(fireCell, 100) };
            var queue = new FluidReactionCommandQueue(4);
            var system = new LiquidCellReactionSystem(4, ReactionCatalog());
            ExtinguishTuning extinguish = default;
            var toxic = new ToxicCombustionTuning
            {
                FireThreshold = 1f,
                PoisonThreshold = 1f,
                WindUpSeconds = 0.2f,
                MaxPoisonConsume = 40f,
                MinEffectiveConsume = 1f,
                BaseDamage = 5f,
                DamagePerPoison = 1f,
                Radius = 2f,
                CooldownSeconds = 0.5f,
            };

            Assert.That(system.TryPlanAndCommit(
                occupancy, fires, 1, 0.1f, in extinguish, in toxic, Vector3.one, 2f, queue), Is.False);
            Assert.That(queue.PendingConsumeCount, Is.Zero, "Wind-up 未结束不能提前消费 Poison。");

            Assert.That(system.TryPlanAndCommit(
                occupancy, fires, 1, 0.1f, in extinguish, in toxic, Vector3.one, 2f, queue), Is.True);
            Assert.That(queue.PendingFireDeltaCount, Is.Zero, "ToxicCombustion 中 Fire 只是触发条件。");
            var consume = new FluidConsumeCommand[1];
            Assert.That(queue.CopyConsumeCommandsAndClear(consume), Is.EqualTo(1));
            Assert.That(consume[0].MaterialSeedPadding.X, Is.EqualTo((uint)MaterialId.Poison));
            Assert.That(consume[0].MaximumParticleCount, Is.EqualTo(3u),
                "12 GMU 必须按 Poison 自己的 4 GMU/particle 量化，不能借用 Water 的 8。");
            Assert.That(queue.TryDequeueDamage(out WorldReactionDamageCommand damage), Is.True);
            Assert.That(damage.Amount, Is.EqualTo(17f));
            Assert.That(damage.WorldPosition,
                Is.EqualTo(Vector3.one + ((Vector3)poisonCell + Vector3.one * 0.5f) * 2f));
            Assert.That(occupancy.WaterAmount, Is.EqualTo(200), "Poison Consume 不能命中同 Cell 的 Water。");
        }

        private static MaterialReactionCatalogSnapshot ReactionCatalog()
        {
            MaterialCatalog materials = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(
                "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
            MaterialReactionBindingProfile bindings =
                AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(
                    "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset");
            Assert.That(materials, Is.Not.Null);
            Assert.That(bindings, Is.Not.Null);
            return bindings.CreateSnapshot(materials.CreateSnapshot());
        }

        private sealed class MixedOccupancy : ILiquidOccupancyReadOnly
        {
            private Vector3Int _cell;
            private byte _poison;
            public byte WaterAmount { get; private set; }
            public bool HasValidSnapshot => true;
            public Bounds SnapshotBounds => new Bounds(Vector3.zero, Vector3.one * 100f);
            public uint SnapshotVersion => 42u;
            public void Set(Vector3Int cell, MaterialId material, byte amount)
            {
                _cell = cell;
                if (material == MaterialId.Poison) _poison = amount;
                if (material == MaterialId.Water) WaterAmount = amount;
            }
            public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint amountUnits)
            {
                amountUnits = material == MaterialId.Poison ? 4u : 8u;
                return material == MaterialId.Poison || material == MaterialId.Water;
            }
            public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination) => 0;
            public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
            {
                amount = cell == _cell
                    ? material == MaterialId.Poison ? _poison
                    : material == MaterialId.Water ? WaterAmount : (byte)0
                    : (byte)0;
                return amount > 0;
            }
        }
    }
}
