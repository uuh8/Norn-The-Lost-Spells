using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;

namespace Game.ElementField.Tests
{
    public sealed class MaterialStatusProjectionTests
    {
        [TestCase(0, MaterialId.Water, StatusKind.Wet, 10f)]
        [TestCase(1, MaterialId.Fire, StatusKind.Burning, 10f)]
        [TestCase(2, MaterialId.Poison, StatusKind.Poisoned, 10f)]
        [TestCase(3, MaterialId.Sticky, StatusKind.Sticky, 10f)]
        [TestCase(4, MaterialId.ToxicVapor, StatusKind.Poisoned, 10f)]
        [TestCase(5, MaterialId.Steam, StatusKind.Wet, 4f)]
        public void DefaultProjectionMapsLiquidFireAndGasToTheirExposureStatus(
            int index, MaterialId material, StatusKind status, float maximumApply)
        {
            MaterialCatalog catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(
                "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
            MaterialStatusProjectionProfile profile = AssetDatabase.LoadAssetAtPath<MaterialStatusProjectionProfile>(
                "Assets/_Project/ScriptableObjects/Materials/MaterialStatusProjection_Default.asset");
            Assert.That(catalog, Is.Not.Null);
            Assert.That(profile, Is.Not.Null);
            MaterialStatusProjectionSnapshot snapshot = profile.CreateSnapshot(catalog.CreateSnapshot());
            // 三相扩展后共有六种Exposure映射；同时锁定身份与剂量，防止毒气误变水状态。
            Assert.That(snapshot.Count, Is.EqualTo(6));
            MaterialStatusProjection entry = snapshot.Get(index);
            Assert.That(entry.Material, Is.EqualTo(material));
            Assert.That(entry.Status, Is.EqualTo(status));
            Assert.That(entry.MaximumApplyPerExposureTick, Is.EqualTo(maximumApply));
        }
    }
}
