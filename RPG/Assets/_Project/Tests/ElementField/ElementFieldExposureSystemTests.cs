using Game.Materials;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 使用真实 ElementFieldRuntime、Physics Collider 与 StatusController 验证环境暴露链路。
    /// 这里不 Mock Physics：Bounds、GetComponentInParent 和多 Collider 去重正是本功能的风险所在。
    /// </summary>
    public sealed class ElementFieldExposureSystemTests
    {
        private const float ExposureInterval = 0.25f;
        private const float MaxApplyPerTick = 10f;

        private GameObject _fieldRoot;
        private ElementFieldRuntime _runtime;
        private ElementFieldExposureSystem _exposure;
        private ElementFieldProfile _profile;
        private ElementReactionProfile _reactionProfile;
        private readonly List<GameObject> _createdTargets = new List<GameObject>(4);
        private readonly List<StatusDefinition> _createdDefinitions = new List<StatusDefinition>(4);

        [SetUp]
        public void SetUp()
        {
            _reactionProfile = ScriptableObject.CreateInstance<ElementReactionProfile>();
            _profile = ScriptableObject.CreateInstance<ElementFieldProfile>();
            SetPrivateField(_profile, "_dimensions", new Vector3Int(4, 1, 1));
            SetPrivateField(_profile, "_cellSize", 1f);
            SetPrivateField(_profile, "_chunkSize", 2);
            SetPrivateField(_profile, "_maximumCellCount", 4);
            SetPrivateField(_profile, "_tickRate", 10f);
            SetPrivateField(_profile, "_maxPendingWrites", 16);
            SetPrivateField(_profile, "_maxCatchUpTicks", 1);
            SetPrivateField(_profile, "_maxDownFlowPerTick", 0);
            SetPrivateField(_profile, "_maxLateralFlowPerTick", 0);
            SetPrivateField(_profile, "_fireDecayPerTick", 0);
            SetPrivateField(_profile, "_reactionProfile", _reactionProfile);

            // Inactive 状态先完成 SerializedField 装配，避免组件拿到尚未配置的 Profile。
            // 注意：EditMode Test 不运行正常的 PlayerLoop，因此 SetActive(true) 不保证自动调用
            // MonoBehaviour.Awake。测试必须显式执行与 Scene 加载相同的初始化入口，不能把
            // “Unity 生命周期是否运行”误当成 Exposure 业务逻辑的测试前提。
            _fieldRoot = new GameObject("ElementFieldExposureSystemTests.Field");
            _fieldRoot.SetActive(false);
            ElementFieldSolidBaker baker = _fieldRoot.AddComponent<ElementFieldSolidBaker>();
            _runtime = _fieldRoot.AddComponent<ElementFieldRuntime>();
            _exposure = _fieldRoot.AddComponent<ElementFieldExposureSystem>();

            // 使用一个没有测试 Collider 的有效 Layer，避免 SolidBaker 的空 Mask Warning 干扰测试日志。
            SetPrivateField(baker, "_solidLayerMask", (LayerMask)(1 << 31));
            SetPrivateField(_runtime, "_profile", _profile);
            SetPrivateField(_exposure, "_targetLayers", (LayerMask)~0);
            SetPrivateField(_exposure, "_exposureInterval", ExposureInterval);
            SetPrivateField(_exposure, "_naturalDecayHoldGraceSeconds", 0.1f);
            SetPrivateField(_exposure, "_maxWetApplyPerTick", MaxApplyPerTick);
            SetPrivateField(_exposure, "_maxBurningApplyPerTick", MaxApplyPerTick);

            Assert.That(InvokePrivateResult<bool>(_runtime, "TryInitialize"), Is.True,
                "测试用 ElementFieldRuntime 必须成功初始化。");
            InvokePrivateVoid(_exposure, "Awake");
            _fieldRoot.SetActive(true);
            Assert.That(_runtime.IsInitialized, Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _createdTargets.Count; i++)
            {
                if (_createdTargets[i] != null)
                    Object.DestroyImmediate(_createdTargets[i]);
            }
            _createdTargets.Clear();

            for (int i = 0; i < _createdDefinitions.Count; i++)
            {
                if (_createdDefinitions[i] != null)
                    Object.DestroyImmediate(_createdDefinitions[i]);
            }
            _createdDefinitions.Clear();

            if (_fieldRoot != null)
                Object.DestroyImmediate(_fieldRoot);
            if (_profile != null)
                Object.DestroyImmediate(_profile);
            if (_reactionProfile != null)
                Object.DestroyImmediate(_reactionProfile);
        }

        [Test]
        public void WaterCellAppliesWetProportionalToAmount()
        {
            Deposit(new Vector3Int(0, 0, 0), MaterialId.Water, amount: 128);
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 128f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f));
            Assert.That(target.GetIntensity(StatusKind.Burning), Is.Zero);
        }

        [Test]
        public void FireCellAppliesBurningProportionalToAmount()
        {
            Deposit(new Vector3Int(1, 0, 0), MaterialId.Fire, amount: 64);
            StatusController target = CreateTarget(CellCenter(1), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 64f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Burning), Is.EqualTo(expected).Within(0.0001f));
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.Zero);
        }

        [Test]
        public void EmptyCellAppliesNothing()
        {
            StatusController target = CreateTarget(CellCenter(2), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            Assert.That(target.GetIntensity(StatusKind.Wet), Is.Zero);
            Assert.That(target.GetIntensity(StatusKind.Burning), Is.Zero);
        }

        [Test]
        public void MultipleCollidersApplyOnlyOncePerTarget()
        {
            Deposit(new Vector3Int(0, 0, 0), MaterialId.Water, byte.MaxValue);
            GameObject targetRoot = new GameObject("ExposureTarget.MultiCollider");
            _createdTargets.Add(targetRoot);
            targetRoot.transform.position = CellCenter(0);
            StatusController target = targetRoot.AddComponent<StatusController>();
            CreateChildCollider(targetRoot.transform, new Vector3(-0.1f, 0f, 0f));
            CreateChildCollider(targetRoot.transform, new Vector3(0.1f, 0f, 0f));
            Physics.SyncTransforms();

            _exposure.TickForTests(ExposureInterval);

            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(MaxApplyPerTick).Within(0.0001f),
                "同一 StatusController 的两个 Collider 不能把一次 Exposure 重复应用两遍。 ");
        }

        [Test]
        public void ColliderCoveringSeveralCellsUsesMaximumIntensity()
        {
            Deposit(new Vector3Int(0, 0, 0), MaterialId.Water, amount: 64);
            Deposit(new Vector3Int(1, 0, 0), MaterialId.Water, amount: 200);
            StatusController target = CreateTarget(
                new Vector3(1f, 0.5f, 0.5f),
                new Vector3(1.8f, 0.4f, 0.4f));

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 200f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f),
                "采样必须保留 Collider 覆盖范围内的最大值，不能被 Empty/低强度 Cell 平均稀释。 ");
        }

        [Test]
        public void TargetOutsideFieldIsIgnored()
        {
            StatusController target = CreateTarget(
                new Vector3(5f, 0.5f, 0.5f),
                Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            Assert.That(target.GetIntensity(StatusKind.Wet), Is.Zero);
            Assert.That(target.GetIntensity(StatusKind.Burning), Is.Zero);
        }

        [Test]
        public void RepeatedWaterExposure_RefreshesDecayHoldUntilTargetLeavesField()
        {
            // Exposure 每 0.25 秒只采样一次，但接触在 Gameplay 语义上是连续的。
            // 因此每次补充都把 Hold 刷新为 0.25 + 0.10 秒；离场后才让剩余时间耗尽并恢复衰减。
            Deposit(new Vector3Int(0, 0, 0), MaterialId.Water, byte.MaxValue);
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);
            target.SetDefinitionsForTests(CreateDefinition(StatusKind.Wet, naturalDecayPerSecond: 8f));

            _exposure.TickForTests(ExposureInterval);
            target.TickForTests(0.24f);
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(10f).Within(0.0001f),
                "下一次 Exposure 到来前，Wet 不应被 NaturalDecay 抵消。");

            _exposure.TickForTests(ExposureInterval);
            target.TickForTests(0.25f);
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(20f).Within(0.0001f),
                "持续站在 Water Cell 中时，重复 Exposure 应刷新 Hold 并稳定累积 Wet。");

            target.transform.position = Vector3.right * 10f;
            Physics.SyncTransforms();
            _exposure.TickForTests(ExposureInterval);
            target.TickForTests(0.2f);

            // 第二次刷新后的 0.35 秒 Hold 已在前一个 0.25 秒 Tick 中消耗 0.25 秒；
            // 离场后的 0.20 秒中先消耗剩余 0.10 秒，再以 8/s 衰减 0.10 秒：20 - 0.8 = 19.2。
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(19.2f).Within(0.0001f));
        }

        private void Deposit(Vector3Int coordinate, MaterialId kind, byte amount)
        {
            var request = new ElementWriteRequest(
                CellCenter(coordinate.x),
                kind,
                amount,
                radius: 0f,
                useLinearFalloff: false);
            Assert.That(_runtime.TryEnqueueWrite(in request), Is.True);
            _runtime.StepOnceForDebug();
        }

        private StatusController CreateTarget(Vector3 position, Vector3 colliderSize)
        {
            GameObject target = new GameObject("ExposureTarget");
            _createdTargets.Add(target);
            target.transform.position = position;
            StatusController controller = target.AddComponent<StatusController>();
            BoxCollider collider = target.AddComponent<BoxCollider>();
            collider.size = colliderSize;
            Physics.SyncTransforms();
            return controller;
        }

        private StatusDefinition CreateDefinition(StatusKind kind, float naturalDecayPerSecond)
        {
            StatusDefinition definition = ScriptableObject.CreateInstance<StatusDefinition>();
            definition.Kind = kind;
            definition.NaturalDecayPerSecond = naturalDecayPerSecond;
            _createdDefinitions.Add(definition);
            return definition;
        }

        private static void CreateChildCollider(Transform parent, Vector3 localPosition)
        {
            GameObject child = new GameObject("ExposureTarget.Collider");
            child.transform.SetParent(parent, false);
            child.transform.localPosition = localPosition;
            BoxCollider collider = child.AddComponent<BoxCollider>();
            collider.size = Vector3.one * 0.3f;
        }

        private static Vector3 CellCenter(int x)
        {
            return new Vector3(x + 0.5f, 0.5f, 0.5f);
        }

        private static void SetPrivateField<TTarget, TValue>(
            TTarget target,
            string fieldName,
            TValue value)
        {
            FieldInfo field = typeof(TTarget).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期的序列化字段 {typeof(TTarget).Name}.{fieldName}。 ");
            field.SetValue(target, value);
        }

        private static void InvokePrivateVoid<TTarget>(TTarget target, string methodName)
        {
            MethodInfo method = typeof(TTarget).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"缺少预期的生命周期方法 {typeof(TTarget).Name}.{methodName}。");
            method.Invoke(target, null);
        }

        private static TResult InvokePrivateResult<TResult>(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"缺少预期的初始化方法 {target.GetType().Name}.{methodName}。");
            return (TResult)method.Invoke(target, null);
        }
    }
}
