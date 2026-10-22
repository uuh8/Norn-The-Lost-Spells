using System;
using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidSimulationProfileTests
    {
        private const string ProfilePath =
            "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidSimulationProfile_Water.asset";
        [Test]
        public void DefaultProfileSnapshotsBoundedCohesionAndTensileParameters()
        {
            LiquidSimulationProfile profile = CreateConfiguredClone();
            try
            {
                LiquidSimulationSettings settings = profile.CreateSettings();
                Assert.That(settings.LiquidMaterials.TryGet(MaterialId.Water, out LiquidMaterialSettings water), Is.True);
                Assert.That(water.CohesionStrength, Is.EqualTo(12f));
                Assert.That(water.CohesionRestDistanceRatio, Is.EqualTo(1f));
                Assert.That(water.MaximumCohesionDeltaSpeed, Is.EqualTo(0.2f));
                Assert.That(settings.TensileStrength, Is.EqualTo(0.00005f));
                Assert.That(settings.MaximumTensilePositionCorrection, Is.EqualTo(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ZeroTensileStrengthIsLegalAndPreservesStableSolverFallback()
        {
            LiquidSimulationProfile profile = CreateProfileWith("_tensileStrength", 0f);
            try
            {
                Assert.That(profile.CreateSettings().TensileStrength, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [TestCase("_tensileStrength", -1f)]
        [TestCase("_tensileStrength", float.NaN)]
        [TestCase("_tensileStrength", float.PositiveInfinity)]
        [TestCase("_maximumTensilePositionCorrection", 0f)]
        [TestCase("_maximumTensilePositionCorrection", -1f)]
        [TestCase("_maximumTensilePositionCorrection", float.PositiveInfinity)]
        public void InvalidBackendWideTensileValuesAreRejected(string fieldName, float value)
        {
            LiquidSimulationProfile profile = CreateProfileWith(fieldName, value);
            try
            {
                Assert.That(() => profile.CreateSettings(), Throws.TypeOf<ArgumentOutOfRangeException>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        private static LiquidSimulationProfile CreateProfileWith(string fieldName, float value)
        {
            LiquidSimulationProfile profile = CreateConfiguredClone();
            FieldInfo field = typeof(LiquidSimulationProfile).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少序列化字段 {fieldName}。");
            field.SetValue(profile, value);
            return profile;
        }

        private static LiquidSimulationProfile CreateConfiguredClone()
        {
            LiquidSimulationProfile source = AssetDatabase.LoadAssetAtPath<LiquidSimulationProfile>(ProfilePath);
            Assert.That(source, Is.Not.Null);
            LiquidSimulationProfile profile = ScriptableObject.CreateInstance<LiquidSimulationProfile>();
            CopyReference(source, profile, "_materialCatalog");
            CopyReference(source, profile, "_liquidMaterials");
            return profile;
        }

        private static void CopyReference(
            LiquidSimulationProfile source,
            LiquidSimulationProfile destination,
            string fieldName)
        {
            FieldInfo field = typeof(LiquidSimulationProfile).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(destination, field.GetValue(source));
        }
    }
}
