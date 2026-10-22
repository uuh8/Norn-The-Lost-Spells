using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialStateQueryRouterTests
    {
        [Test]
        public void QueryReadsOnlyAuthoritativeBackendAndNeverAddsResiduals()
        {
            var routes = new Routes();
            var cells = new AmountSource(31);
            var liquids = new LiquidSource(47);
            var query = new MaterialStateQueryRouter(routes, cells, liquids);

            Assert.That(query.TryGetAmount(Vector3Int.zero, MaterialId.Water, out byte water), Is.True);
            Assert.That(water, Is.EqualTo(47), "PBF Water 不能与残留 Cell Amount 31 相加。");
            Assert.That(query.TryGetAmount(Vector3Int.zero, MaterialId.Fire, out byte fire), Is.True);
            Assert.That(fire, Is.EqualTo(31));
            Assert.That(query.TryGetAmount(Vector3Int.zero, MaterialId.Poison, out _), Is.False);
            Assert.That(query.TryGetAmount(Vector3Int.zero, MaterialId.Sticky, out _), Is.False);
        }

        private sealed class Routes : IMaterialSimulationRouteReadOnly
        {
            public bool TryResolve(MaterialId material, out MaterialSimulationBackendKind backend)
            {
                if (material == MaterialId.Water) { backend = MaterialSimulationBackendKind.GpuPbfLiquid; return true; }
                if (material == MaterialId.Fire) { backend = MaterialSimulationBackendKind.ElementCell; return true; }
                if (material == MaterialId.Poison) { backend = MaterialSimulationBackendKind.Unsupported; return true; }
                backend = default;
                return false;
            }
        }

        private sealed class AmountSource : IMaterialAmountReadOnly
        {
            private readonly byte _amount;
            public AmountSource(byte amount) => _amount = amount;
            public bool TryGetAmount(Vector3Int globalCell, MaterialId material, out byte amount) { amount = _amount; return true; }
        }

        private sealed class LiquidSource : ILiquidOccupancyReadOnly
        {
            private readonly byte _amount;
            public LiquidSource(byte amount) => _amount = amount;
            public bool HasValidSnapshot => true;
            public Bounds SnapshotBounds => new Bounds(Vector3.zero, Vector3.one);
            public uint SnapshotVersion => 1u;
            public bool TryGetAmount(Vector3Int globalCell, MaterialId material, out byte amount) { amount = _amount; return true; }
            public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint units) { units = 8u; return true; }
            public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination) => 0;
        }
    }
}
