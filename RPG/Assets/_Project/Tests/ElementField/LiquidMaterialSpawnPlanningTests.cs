using System.Runtime.InteropServices;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidMaterialSpawnPlanningTests
    {
        private const string SimulationPath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidSimulationProfile_Water.asset";

        [Test]
        public void ProductionLiquidsUseOneAmountScaleButKeepIndependentPhysicalBehavior()
        {
            LiquidSimulationProfile profile = AssetDatabase.LoadAssetAtPath<LiquidSimulationProfile>(SimulationPath);
            Assert.That(profile, Is.Not.Null);
            LiquidMaterialSettingsTable table = profile.CreateMaterialSettingsTable();

            Assert.That(table.TryGet(MaterialId.Water, out LiquidMaterialSettings water), Is.True);
            Assert.That(table.TryGet(MaterialId.Poison, out LiquidMaterialSettings poison), Is.True);
            Assert.That(table.TryGet(MaterialId.Sticky, out LiquidMaterialSettings sticky), Is.True);
            Assert.That(water.AmountUnitsPerParticle, Is.EqualTo(8u));
            Assert.That(poison.AmountUnitsPerParticle, Is.EqualTo(8u));
            Assert.That(sticky.AmountUnitsPerParticle, Is.EqualTo(8u));
            Assert.That(poison.Viscosity, Is.GreaterThan(water.Viscosity));
            Assert.That(poison.RestSpacing, Is.Not.EqualTo(water.RestSpacing));
            Assert.That(Marshal.SizeOf<FluidGpuLiquidMaterialParameters>(), Is.EqualTo(32));
        }

        [Test]
        public void DepositUsesMaterialSpecificScaleAndRestSpacing()
        {
            LiquidSimulationProfile profile = AssetDatabase.LoadAssetAtPath<LiquidSimulationProfile>(SimulationPath);
            LiquidMaterialSettingsTable table = profile.CreateMaterialSettingsTable();
            var queue = new FluidSpawnQueue(2);
            var adapter = new FluidDepositQueueAdapter(queue, table, 128, 0.1f);
            var water = new ElementWriteRequest(Vector3.zero, MaterialId.Water, 16, 1f, false);
            var poison = new ElementWriteRequest(Vector3.zero, MaterialId.Poison, 16, 1f, false);

            Assert.That(adapter.TryEnqueueDeposit(in water), Is.True);
            Assert.That(adapter.TryEnqueueDeposit(in poison), Is.True);
            var requests = new FluidSpawnRequest[2];
            Assert.That(queue.CopyAndClear(requests), Is.EqualTo(2));
            Assert.That(requests[0].ParticleCount, Is.EqualTo(2u));
            Assert.That(requests[1].ParticleCount, Is.EqualTo(2u));
            Assert.That(requests[0].RestSpacing, Is.GreaterThan(0f));
            Assert.That(requests[1].RestSpacing, Is.GreaterThan(0f));
            Assert.That(requests[0].RestSpacing, Is.Not.EqualTo(requests[1].RestSpacing));
            Assert.That(requests[0].MaterialId, Is.EqualTo((uint)MaterialId.Water));
            Assert.That(requests[1].MaterialId, Is.EqualTo((uint)MaterialId.Poison));
        }

        [Test]
        public void PackedSpawnRequiresPositiveFiniteRestSpacing()
        {
            var invalid = new FluidSpawnRequest(
                Vector3.zero,
                Vector3.zero,
                1f,
                1u,
                (uint)MaterialId.Water,
                1u,
                FluidSpawnFlags.DensityPacked,
                0f);
            Assert.That(FluidSpawnRequestValidator.IsValidForParticleCapacity(in invalid, 8), Is.False);
        }

        [Test]
        public void SettingsTableAcceptsStickyAsRegisteredLiquidIdentity()
        {
            var sticky = new LiquidMaterialSettings(
                MaterialId.Sticky,
                4u,
                1.2f,
                1100f,
                0.65f,
                0.0004f,
                36f,
                1f,
                0.12f,
                0.03f);

            var table = new LiquidMaterialSettingsTable(new[] { sticky });

            Assert.That(table.TryGet(MaterialId.Sticky, out LiquidMaterialSettings actual), Is.True);
            Assert.That(actual.Viscosity, Is.EqualTo(0.65f));
            Assert.That(actual.CohesionStrength, Is.EqualTo(36f));
        }
    }
}
