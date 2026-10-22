using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// PBF 在创建 GPU Buffer 前必须把所有容量决策固定下来；这些测试用手工推导的数值，
    /// 锁住 Pool、Hash Table 与 Amount 到粒子数之间的容量契约，而不依赖任何 GPU 或场景。
    /// </summary>
    public sealed class FluidCapacityPlannerTests
    {
        [TestCase(1, 1)]
        [TestCase(5000, 8192)]
        [TestCase(8192, 8192)]
        public void RequestedParticleCapacityNormalizesUpToPowerOfTwo(
            int requestedCapacity,
            int expectedCapacity)
        {
            Assert.That(
                FluidCapacityPlanner.NormalizeParticleCapacity(requestedCapacity),
                Is.EqualTo(expectedCapacity));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void RequestedParticleCapacityRejectsNonPositiveValues(int requestedCapacity)
        {
            Assert.That(
                () => FluidCapacityPlanner.NormalizeParticleCapacity(requestedCapacity),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void AmountUnitsRoundUpToWholeParticles()
        {
            // 21 / 8 = 2.625；少生成一个粒子会吞掉尚未被表达的 Amount，因此必须向上取整为 3。
            Assert.That(
                FluidCapacityPlanner.CalculateSpawnParticleCount(
                    totalAmount: 21,
                    amountUnitsPerParticle: 8,
                    maxParticlesPerSpawn: 10),
                Is.EqualTo(3u));
        }

        [Test]
        public void SpawnParticleCountDoesNotExceedProfilePerRequestLimit()
        {
            // ceil(100 / 8) = 13，但一次 Water 写入只能占用 Profile 允许的 7 个 Spawn Slot。
            Assert.That(
                FluidCapacityPlanner.CalculateSpawnParticleCount(
                    totalAmount: 100,
                    amountUnitsPerParticle: 8,
                    maxParticlesPerSpawn: 7),
                Is.EqualTo(7u));
        }

        [Test]
        public void HashTableCapacityIsAtLeastTwiceTheNormalizedParticleCapacity()
        {
            int hashTableCapacity = FluidCapacityPlanner.CalculateHashTableCapacity(5000);

            Assert.That(hashTableCapacity, Is.EqualTo(16384));
            Assert.That(hashTableCapacity, Is.GreaterThanOrEqualTo(10000));
        }

        [Test]
        public void LargestSupportedParticleCapacityKeepsItsPowerOfTwoValue()
        {
            const int largestSupportedCapacity = 1 << 29;

            Assert.That(
                FluidCapacityPlanner.NormalizeParticleCapacity(largestSupportedCapacity),
                Is.EqualTo(largestSupportedCapacity));
            Assert.That(
                FluidCapacityPlanner.CalculateHashTableCapacity(largestSupportedCapacity),
                Is.EqualTo(1 << 30));
        }

        [Test]
        public void MaximumUnsignedAmountRoundsUpWithoutOverflow()
        {
            // ceil(4,294,967,295 / 2) = 2,147,483,648；不能在加一时回绕为 0。
            Assert.That(
                FluidCapacityPlanner.CalculateSpawnParticleCount(
                    uint.MaxValue,
                    amountUnitsPerParticle: 2u,
                    maxParticlesPerSpawn: uint.MaxValue),
                Is.EqualTo(2147483648u));
        }

        [Test]
        public void ProfileCreatesIndependentValidatedRuntimeSettingsSnapshot()
        {
            LiquidSimulationProfile profile = CreateConfiguredProfile();
            try
            {
                LiquidSimulationSettings first = profile.CreateSettings();
                SetPrivateField(profile, "_particleCapacity", 4096);
                SetPrivateField(profile, "_particleRadius", 0.25f);
                SetPrivateField(profile, "_gravity", new Vector3(4f, -2f, 7f));
                SetPrivateField(profile, "_fixedTickRate", 30f);
                SetPrivateField(profile, "_solverIterations", 5);
                LiquidSimulationSettings second = profile.CreateSettings();

                Assert.That(first.ParticleCapacity, Is.EqualTo(8192));
                Assert.That(first.ParticleRadius, Is.EqualTo(0.1f));
                Assert.That(first.Gravity, Is.EqualTo(new Vector3(0f, -9.81f, 0f)));
                Assert.That(first.FixedTickRate, Is.EqualTo(60f));
                Assert.That(first.SolverIterations, Is.EqualTo(4));
                Assert.That(first.MaxFluidColliders, Is.EqualTo(64));
                Assert.That(first.Substeps, Is.EqualTo(2));
                Assert.That(first.SolverIterations, Is.EqualTo(4));
                Assert.That(second.ParticleCapacity, Is.EqualTo(4096));
                Assert.That(second.ParticleRadius, Is.EqualTo(0.25f));
                Assert.That(second.Gravity, Is.EqualTo(new Vector3(4f, -2f, 7f)));
                Assert.That(second.FixedTickRate, Is.EqualTo(30f));
                Assert.That(second.SolverIterations, Is.EqualTo(5));
                Assert.That(first.ParticleCapacity, Is.Not.EqualTo(second.ParticleCapacity),
                    "已创建的 Runtime Snapshot 不能随可编辑 ScriptableObject 的后续改动而改变。");
                Assert.That(first.ParticleRadius, Is.Not.EqualTo(second.ParticleRadius));
                Assert.That(first.Gravity, Is.Not.EqualTo(second.Gravity));
                Assert.That(first.FixedTickRate, Is.Not.EqualTo(second.FixedTickRate));
                Assert.That(first.SolverIterations, Is.Not.EqualTo(second.SolverIterations));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ProfileSnapshotsPositiveFluidColliderCapacity()
        {
            LiquidSimulationProfile profile = CreateConfiguredProfile();
            try
            {
                SetPrivateField(profile, "_maxFluidColliders", 17);
                Assert.That(profile.CreateSettings().MaxFluidColliders, Is.EqualTo(17));

                SetPrivateField(profile, "_maxFluidColliders", 0);
                Assert.That(
                    () => profile.CreateSettings(),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ProfileRejectsMaxDisplacementBeyondBoundedCollisionSweepCoverage()
        {
            LiquidSimulationProfile profile = CreateConfiguredProfile();
            try
            {
                // 10000 / (60*2) = 83.33m，而 32 个 0.1m sweep sample 只能无缝覆盖 3.2m。
                SetPrivateField(profile, "_maxSpeed", 10000f);
                Assert.That(
                    () => profile.CreateSettings(),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ProfileRejectsCatchUpLimitAboveClockHardMaximum()
        {
            LiquidSimulationProfile profile = CreateConfiguredProfile();
            try
            {
                SetPrivateField(
                    profile,
                    "_maxCatchUpTicks",
                    FluidSimulationClock.MaxSupportedCatchUpTicks + 1);

                Assert.That(
                    () => profile.CreateSettings(),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ProfileSnapshotsMaximumPositionCorrectionAndRejectsUnsafeValues()
        {
            LiquidSimulationProfile profile = CreateConfiguredProfile();
            try
            {
                SetPrivateField(profile, "_maximumPositionCorrection", 0.08f);
                LiquidSimulationSettings settings = profile.CreateSettings();

                Assert.That(settings.MaximumPositionCorrection, Is.EqualTo(0.08f));

                SetPrivateField(profile, "_maximumPositionCorrection", 0f);
                Assert.That(
                    () => profile.CreateSettings(),
                    Throws.TypeOf<ArgumentOutOfRangeException>());

                SetPrivateField(profile, "_maximumPositionCorrection", float.PositiveInfinity);
                Assert.That(
                    () => profile.CreateSettings(),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        private static void SetPrivateField<TValue>(
            LiquidSimulationProfile profile,
            string fieldName,
            TValue value)
        {
            FieldInfo field = typeof(LiquidSimulationProfile).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少 Profile 序列化字段 {fieldName}。");
            field.SetValue(profile, value);
        }

        private static LiquidSimulationProfile CreateConfiguredProfile()
        {
            LiquidSimulationProfile source = AssetDatabase.LoadAssetAtPath<LiquidSimulationProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Fluid/LiquidSimulationProfile_Water.asset");
            Assert.That(source, Is.Not.Null);
            LiquidSimulationProfile profile = ScriptableObject.CreateInstance<LiquidSimulationProfile>();
            foreach (string fieldName in new[] { "_materialCatalog", "_liquidMaterials" })
            {
                FieldInfo field = typeof(LiquidSimulationProfile).GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                field.SetValue(profile, field.GetValue(source));
            }
            return profile;
        }
    }
}
