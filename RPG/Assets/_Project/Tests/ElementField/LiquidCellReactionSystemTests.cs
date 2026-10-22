using Game.Materials;
using Game.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidCellReactionSystemTests
    {
        private static readonly ExtinguishTuning Tuning = new ExtinguishTuning
        {
            FormalThreshold = 20f,
            LowRatePerSecond = 5f,
            FormalRatePerSecond = 40f,
            FireRemovedPerWater = 2f,
        };

        [Test]
        public void Plan_UsesSameCellAndSixAxialNeighbors_ThenDeduplicatesSnapshotVersion()
        {
            var occupancy = new FakeOccupancy(11u);
            occupancy.Set(new Vector3Int(1, 0, 0), 80);
            var fires = new[] { new LiquidCellFireSample(Vector3Int.zero, 100) };
            var queue = new FluidReactionCommandQueue(8);
            var system = new LiquidCellReactionSystem(8, ReactionCatalog());

            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, queue), Is.True);
            Assert.That(queue.PendingFireDeltaCount, Is.EqualTo(1));
            Assert.That(queue.PendingConsumeCount, Is.EqualTo(1));
            Assert.That(queue.TryDequeueFireDelta(out ElementCellDeltaRequest formalDelta), Is.True);
            Assert.That(formalDelta.AmountToRemove, Is.EqualTo(100),
                "正式反应应以 1 Water : 2 Fire 灭火，并受 Fire 存量上限限制。");
            var consume = new FluidConsumeCommand[1];
            Assert.That(queue.CopyConsumeCommandsAndClear(consume), Is.EqualTo(1));
            Assert.That(consume[0].MaximumParticleCount, Is.EqualTo(7u),
                "移除 100 GMU Fire 只应预留 50 GMU Water，再按每粒 8 GMU 向上量化。");
            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, queue), Is.False);
        }

        [TestCase(3, 6)]
        [TestCase(5, 10)]
        public void Plan_LowIntensityUsesLowRateAndParticleQuantizationIsBelowOneParticle(byte waterAmount, int expectedFireRemoved)
        {
            var occupancy = new FakeOccupancy(12u);
            occupancy.Set(Vector3Int.zero, waterAmount);
            var fires = new[] { new LiquidCellFireSample(Vector3Int.zero, 10) };
            var queue = new FluidReactionCommandQueue(4);
            var system = new LiquidCellReactionSystem(4, ReactionCatalog());

            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, queue), Is.True);
            Assert.That(queue.TryDequeueFireDelta(out ElementCellDeltaRequest delta), Is.True);
            Assert.That(delta.AmountToRemove, Is.EqualTo(expectedFireRemoved),
                "Water : Fire = 1 : 2，移除量同时受低强度速率和现有 Water 数量约束。");
            var commands = new FluidConsumeCommand[1];
            Assert.That(queue.CopyConsumeCommandsAndClear(commands), Is.EqualTo(1));
            Assert.That(commands[0].MaximumParticleCount, Is.EqualTo(1u),
                "3 或 5 GMU Water 均不足每粒 8 GMU，向上量化后删除 1 粒。");
        }

        [Test]
        public void Plan_ReservesSharedWaterAcrossMultipleFireCells()
        {
            var occupancy = new FakeOccupancy(13u);
            occupancy.Set(Vector3Int.zero, 100);
            var fires = new[]
            {
                new LiquidCellFireSample(Vector3Int.left, 10),
                new LiquidCellFireSample(Vector3Int.right, 10),
            };
            var queue = new FluidReactionCommandQueue(8);
            var system = new LiquidCellReactionSystem(8, ReactionCatalog());

            Assert.That(system.TryPlanAndCommit(occupancy, fires, 2, 1f, in Tuning, queue), Is.True);
            int removed = 0;
            while (queue.TryDequeueFireDelta(out ElementCellDeltaRequest delta))
                removed += delta.AmountToRemove;
            Assert.That(removed, Is.EqualTo(20));
            var consume = new FluidConsumeCommand[2];
            Assert.That(queue.CopyConsumeCommandsAndClear(consume), Is.EqualTo(1));
            Assert.That(consume[0].MaximumParticleCount, Is.EqualTo(2u),
                "两格共移除 20 GMU Fire，只应预留 10 GMU Water。");
        }

        [Test]
        public void Plan_QueueFailureDoesNotConsumeVersionAndCanRetry()
        {
            var occupancy = new FakeOccupancy(14u);
            occupancy.Set(Vector3Int.zero, 80);
            var fires = new[] { new LiquidCellFireSample(Vector3Int.zero, 100) };
            var fullQueue = new FluidReactionCommandQueue(1);
            var fillerFire = new[] { new ElementCellDeltaRequest(Vector3Int.one, MaterialId.Fire, 1) };
            var fillerConsume = new[] { new FluidConsumeCommand(Vector3Int.one, Vector3Int.one, 0u, 1u, 1u) };
            fullQueue.TryEnqueueBatch(fillerFire, fillerConsume, 1);
            var system = new LiquidCellReactionSystem(4, ReactionCatalog());

            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, fullQueue), Is.False);
            fullQueue.TryDequeueFireDelta(out _);
            fullQueue.CopyConsumeCommandsAndClear(new FluidConsumeCommand[1]);
            Assert.That(system.TryPlanAndCommit(occupancy, fires, 1, 1f, in Tuning, fullQueue), Is.True);
        }

        [Test]
        public void Plan_WaterTouchingStickyEnqueuesConversionWithoutStickyConsume()
        {
            var occupancy = new FakeOccupancy(15u);
            occupancy.Set(Vector3Int.zero, MaterialId.Water, 100);
            occupancy.Set(Vector3Int.right, MaterialId.Sticky, 80);
            var queue = new FluidReactionCommandQueue(4);
            var system = new LiquidCellReactionSystem(4, ReactionCatalog());
            var absorb = new AbsorbWaterTuning { WaterConvertPerSecond = 40f };

            Assert.That(system.TryPlanAndCommit(
                occupancy,
                System.Array.Empty<LiquidCellFireSample>(),
                0,
                1f,
                in Tuning,
                default,
                default,
                in absorb,
                Vector3.zero,
                1f,
                queue), Is.True);
            Assert.That(queue.PendingConsumeCount, Is.Zero,
                "AbsorbWater 不能再删除 Sticky 粒子。");
            Assert.That(queue.PendingConvertCount, Is.EqualTo(1));
            var conversions = new FluidConvertCommand[1];
            Assert.That(queue.CopyConvertCommandsAndClear(conversions), Is.EqualTo(1));
            Assert.That(conversions[0].SourceMaterialId, Is.EqualTo((uint)MaterialId.Water));
            Assert.That(conversions[0].TargetMaterialId, Is.EqualTo((uint)MaterialId.Sticky));
            Assert.That(conversions[0].MaximumParticleCount, Is.EqualTo(13u));
        }

        private sealed class FakeOccupancy : ILiquidOccupancyReadOnly
        {
            private readonly Vector3Int[] _cells = new Vector3Int[8];
            private readonly byte[] _amounts = new byte[8];
            private readonly MaterialId[] _materials = new MaterialId[8];
            private int _count;

            internal FakeOccupancy(uint version) => SnapshotVersion = version;
            public bool HasValidSnapshot => true;
            public Bounds SnapshotBounds => new Bounds(Vector3.zero, Vector3.one * 100f);
            public uint SnapshotVersion { get; }
            public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint amountUnits)
            {
                if (material == MaterialId.Water) { amountUnits = 8u; return true; }
                if (material == MaterialId.Sticky) { amountUnits = 4u; return true; }
                amountUnits = 0u;
                return false;
            }
            public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination)
            {
                int write = 0;
                for (int i = 0; i < _count && write < destination.Length; i++)
                {
                    if (_materials[i] != material)
                        continue;
                    destination[write++] = new LiquidMaterialCellSample(_cells[i], _amounts[i]);
                }
                return write;
            }

            internal void Set(Vector3Int cell, byte amount)
            {
                Set(cell, MaterialId.Water, amount);
            }

            internal void Set(Vector3Int cell, MaterialId material, byte amount)
            {
                _cells[_count] = cell;
                _materials[_count] = material;
                _amounts[_count++] = amount;
            }

            public bool TryGetAmount(Vector3Int globalCell, MaterialId materialKind, out byte amount)
            {
                for (int i = 0; i < _count; i++)
                {
                    if (_cells[i] == globalCell && _materials[i] == materialKind)
                    {
                        amount = _amounts[i];
                        return true;
                    }
                }

                amount = 0;
                return false;
            }
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
    }
}
