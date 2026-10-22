using Game.Materials;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 使用真实稀疏 World Runtime、Physics Collider 与 StatusController 验证 Exposure Adapter。
    /// 这里刻意不 Mock Physics，因为跨 Chunk Bounds、父级 Status 查找和多 Collider 去重都是集成风险。
    /// </summary>
    public sealed class ElementWorldExposureSystemTests
    {
        private const float ExposureInterval = 0.25f;
        private const float MaxApplyPerTick = 10f;

        private GameObject _worldRoot;
        private GameObject _interestObject;
        private ElementWorldRuntime _runtime;
        private ElementWorldExposureSystem _exposure;
        private ElementWorldProfile _profile;
        private ElementReactionProfile _reactionProfile;
        private MaterialDefinition[] _materialDefinitions;
        private MaterialCatalog _materialCatalog;
        private MaterialSimulationRoutingProfile _routingProfile;
        private MaterialStatusProjectionProfile _statusProjection;
        private LiquidWorldProfile _gridLiquidProfile;
        private LiquidBehaviorProfile[] _gridBehaviors;
        private readonly List<GameObject> _createdTargets = new List<GameObject>(4);

        [SetUp]
        public void SetUp()
        {
            _reactionProfile = ScriptableObject.CreateInstance<ElementReactionProfile>();
            CreateMaterialRoutingFixture();
            _profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            SetPrivateField(_profile, "_cellSize", 1f);
            SetPrivateField(_profile, "_chunkSize", 2);
            SetPrivateField(_profile, "_maximumResidentChunks", 32);
            SetPrivateField(_profile, "_activeRadiusXZChunks", 1);
            SetPrivateField(_profile, "_activeRadiusYChunks", 1);
            SetPrivateField(_profile, "_sleepGraceSeconds", 0f);
            SetPrivateField(_profile, "_tickRate", 10f);
            SetPrivateField(_profile, "_maxPendingWrites", 16);
            SetPrivateField(_profile, "_maxCatchUpTicks", 1);
            SetPrivateField(_profile, "_maxDownFlowPerTick", 0);
            SetPrivateField(_profile, "_maxLateralFlowPerTick", 0);
            SetPrivateField(_profile, "_fireDecayPerTick", 0);
            SetPrivateField(_profile, "_reactionProfile", _reactionProfile);
            SetPrivateField(_profile, "_materialCatalog", _materialCatalog);
            SetPrivateField(_profile, "_materialSimulationRouting", _routingProfile);
            _statusProjection = ScriptableObject.CreateInstance<MaterialStatusProjectionProfile>();
            SetPrivateField(
                _statusProjection,
                "_bindings",
                new[]
                {
                    new MaterialStatusProjectionBinding
                    {
                        Material = MaterialId.Water,
                        Status = StatusKind.Wet,
                        MaximumApplyPerExposureTick = MaxApplyPerTick,
                    },
                    new MaterialStatusProjectionBinding
                    {
                        Material = MaterialId.Fire,
                        Status = StatusKind.Burning,
                        MaximumApplyPerExposureTick = MaxApplyPerTick,
                    },
                });
            SetPrivateField(_profile, "_materialStatusProjection", _statusProjection);
            SetPrivateField(
                _profile,
                "_materialReactionBindings",
                AssetDatabase.LoadAssetAtPath<MaterialReactionBindingProfile>(
                    "Assets/_Project/ScriptableObjects/Combat/Reactions/MaterialReactionBindings_Default.asset"));

            _interestObject = new GameObject("ElementWorldExposure.Interest");
            _interestObject.transform.position = CellCenter(0);

            _worldRoot = new GameObject("ElementWorldExposure.World");
            _worldRoot.SetActive(false);
            _runtime = _worldRoot.AddComponent<ElementWorldRuntime>();
            _exposure = _worldRoot.AddComponent<ElementWorldExposureSystem>();
            SetPrivateField(_runtime, "_profile", _profile);
            SetPrivateField(_runtime, "_interestPoint", _interestObject.transform);
            SetPrivateField(_exposure, "_targetLayers", (LayerMask)~0);
            SetPrivateField(_exposure, "_exposureInterval", ExposureInterval);
            SetPrivateField(_exposure, "_naturalDecayHoldGraceSeconds", 0.1f);

            Assert.That(InvokePrivateResult<bool>(_runtime, "TryInitialize"), Is.True);
            InvokePrivateVoid(_exposure, "Awake");
            _worldRoot.SetActive(true);
        }

        [Test] public void ExposureAwakeBeforeWorldProjectionRecoversAtStart()
        {
            var field=typeof(ElementWorldRuntime).GetField("_materialStatusProjection",BindingFlags.Instance|BindingFlags.NonPublic);
            var snapshot=field.GetValue(_runtime);field.SetValue(_runtime,null);
            InvokePrivateVoid(_exposure,"Awake");field.SetValue(_runtime,snapshot);
            Assert.DoesNotThrow(()=>InvokePrivateVoid(_exposure,"Start"));
            var projection=typeof(ElementWorldExposureSystem).GetField("_statusProjection",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(_exposure);
            Assert.That(projection,Is.SameAs(snapshot));
            Assert.DoesNotThrow(()=>_exposure.TickForTests(1));
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

            if (_worldRoot != null)
                Object.DestroyImmediate(_worldRoot);
            if (_interestObject != null)
                Object.DestroyImmediate(_interestObject);
            if (_profile != null)
                Object.DestroyImmediate(_profile);
            if (_reactionProfile != null)
                Object.DestroyImmediate(_reactionProfile);
            if (_routingProfile != null)
                Object.DestroyImmediate(_routingProfile);
            if (_statusProjection != null)
                Object.DestroyImmediate(_statusProjection);
            if (_gridLiquidProfile != null)
                Object.DestroyImmediate(_gridLiquidProfile);
            if (_gridBehaviors != null)
                for (int i = 0; i < _gridBehaviors.Length; i++)
                    if (_gridBehaviors[i] != null)
                        Object.DestroyImmediate(_gridBehaviors[i]);
            if (_materialCatalog != null)
                Object.DestroyImmediate(_materialCatalog);
            if (_materialDefinitions != null)
            {
                for (int i = 0; i < _materialDefinitions.Length; i++)
                    Object.DestroyImmediate(_materialDefinitions[i]);
            }
        }

        [Test]
        public void GridLiquidWaterPoisonAndStickyApplyTheirOwnStatuses()
        {
            // 保留真实 Physics/Status Adapter，换一套独立 Grid 配置重建 World。
            // 这里直接预置三格稳定数量，路由写入另由 P7 Player 的实际法术 Trace 验证。
            Object.DestroyImmediate(_worldRoot);
            _worldRoot = new GameObject("ElementWorldExposure.GridWorld");
            _worldRoot.SetActive(false);
            SetPrivateField(_profile, "_waterSimulationMode", WaterSimulationMode.GridLiquid);
            var routes = new SerializedObject(_routingProfile).FindProperty("_routes");
            SetRoute(routes.GetArrayElementAtIndex(1), MaterialId.Poison,
                MaterialSimulationBackendKind.GridLiquid);
            SetRoute(routes.GetArrayElementAtIndex(2), MaterialId.Sticky,
                MaterialSimulationBackendKind.GridLiquid);
            routes.serializedObject.ApplyModifiedPropertiesWithoutUndo();
            SetPrivateField(_statusProjection, "_bindings", new[]
            {
                new MaterialStatusProjectionBinding { Material = MaterialId.Water,
                    Status = StatusKind.Wet, MaximumApplyPerExposureTick = MaxApplyPerTick },
                new MaterialStatusProjectionBinding { Material = MaterialId.Fire,
                    Status = StatusKind.Burning, MaximumApplyPerExposureTick = MaxApplyPerTick },
                new MaterialStatusProjectionBinding { Material = MaterialId.Poison,
                    Status = StatusKind.Poisoned, MaximumApplyPerExposureTick = MaxApplyPerTick },
                new MaterialStatusProjectionBinding { Material = MaterialId.Sticky,
                    Status = StatusKind.Sticky, MaximumApplyPerExposureTick = MaxApplyPerTick },
            });
            _reactionProfile.AbsorbWater = new AbsorbWaterTuning
                { WaterConvertPerSecond = 20f };
            _gridBehaviors = new LiquidBehaviorProfile[3];
            MaterialId[] materials = { MaterialId.Water, MaterialId.Poison, MaterialId.Sticky };
            for (int i = 0; i < materials.Length; i++)
            {
                _gridBehaviors[i] = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
                SetPrivateField(_gridBehaviors[i], "_material", materials[i]);
            }
            _gridLiquidProfile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            SetPrivateField(_gridLiquidProfile, "_materials", _gridBehaviors);

            _runtime = _worldRoot.AddComponent<ElementWorldRuntime>();
            LiquidWorldRuntime liquids = _worldRoot.AddComponent<LiquidWorldRuntime>();
            _exposure = _worldRoot.AddComponent<ElementWorldExposureSystem>();
            SetPrivateField(liquids, "_profile", _gridLiquidProfile);
            SetPrivateField(_runtime, "_profile", _profile);
            SetPrivateField(_runtime, "_gridLiquidRuntime", liquids);
            SetPrivateField(_runtime, "_interestPoint", _interestObject.transform);
            SetPrivateField(_exposure, "_targetLayers", (LayerMask)~0);
            SetPrivateField(_exposure, "_exposureInterval", ExposureInterval);
            Assert.That(InvokePrivateResult<bool>(_runtime, "TryInitialize"), Is.True);
            InvokePrivateVoid(_exposure, "Awake");
            _worldRoot.SetActive(true);

            Assert.That(liquids.TryAddInitial(new Vector3Int(0, 0, 0),
                MaterialId.Water, 128, wake: false), Is.True);
            Assert.That(liquids.TryAddInitial(new Vector3Int(1, 0, 0),
                MaterialId.Poison, 96, wake: false), Is.True);
            Assert.That(liquids.TryAddInitial(new Vector3Int(2, 0, 0),
                MaterialId.Sticky, 160, wake: false), Is.True);
            StatusController waterTarget = CreateTarget(CellCenter(0), Vector3.one * .4f);
            StatusController poisonTarget = CreateTarget(CellCenter(1), Vector3.one * .4f);
            StatusController stickyTarget = CreateTarget(CellCenter(2), Vector3.one * .4f);

            _exposure.TickForTests(ExposureInterval);

            Assert.That(waterTarget.GetIntensity(StatusKind.Wet),
                Is.EqualTo(MaxApplyPerTick * 128f / 255f).Within(.0001f));
            Assert.That(poisonTarget.GetIntensity(StatusKind.Poisoned),
                Is.EqualTo(MaxApplyPerTick * 96f / 255f).Within(.0001f));
            Assert.That(stickyTarget.GetIntensity(StatusKind.Sticky),
                Is.EqualTo(MaxApplyPerTick * 160f / 255f).Within(.0001f));
            Assert.That(waterTarget.GetIntensity(StatusKind.Poisoned), Is.Zero);
            Assert.That(poisonTarget.GetIntensity(StatusKind.Sticky), Is.Zero);

            // 玩家脚下的 Water 与 Sticky 接触后，元素世界应原位转换且保持总 GMU。
            // 不唤醒流动队列，避免重力移动把“反应”断言混成 Solver 断言。
            Assert.That(liquids.TryAddInitial(new Vector3Int(0, 0, 0),
                MaterialId.Sticky, 96, wake: false), Is.True);
            ulong beforeReaction = liquids.TotalQuantity;
            Assert.That(_runtime.TickForTests(.1f), Is.EqualTo(1));
            Assert.That(liquids.TryGetAmount(new Vector3Int(0, 0, 0),
                MaterialId.Water, out byte waterAfter), Is.True);
            Assert.That(liquids.TryGetAmount(new Vector3Int(0, 0, 0),
                MaterialId.Sticky, out byte stickyAfter), Is.True);
            Assert.That(waterAfter, Is.LessThan(128));
            Assert.That(stickyAfter, Is.GreaterThan(96));
            Assert.That(liquids.TotalQuantity, Is.EqualTo(beforeReaction));
        }

        [Test]
        public void WaterCellAppliesWetProportionalToAmount()
        {
            Deposit(0, MaterialId.Water, amount: 128);
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 128f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f));
            Assert.That(target.GetIntensity(StatusKind.Burning), Is.Zero);
        }

        [Test]
        public void NewLiquidSnapshotTriggersExposureWithoutWaitingForIndependentInterval()
        {
            Deposit(0, MaterialId.Water, amount: 128);
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);
            SetPrivateField(
                _exposure,
                "_liquidOccupancy",
                new FakeLiquidOccupancy(
                    snapshotVersion: 1u,
                    new Bounds(CellCenter(0), Vector3.one)));

            _exposure.TickForTests(0.01f);

            float expected = MaxApplyPerTick * 128f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f),
                "新的 GPU 液体 Snapshot 已可查询时，Exposure 应立即消费该版本，不能再等待独立的 0.25 秒周期。");
        }

        [Test]
        public void FrequentSnapshotsDoNotMultiplySustainedStatusDose()
        {
            Deposit(0, MaterialId.Water, amount: 128);
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);
            // 首次完整周期施加一次；随后五份不同 Snapshot 总计只经过一个周期。
            _exposure.TickForTests(ExposureInterval);
            for (uint version = 1; version <= 5; version++)
            {
                SetPrivateField(_exposure, "_liquidOccupancy",
                    new FakeLiquidOccupancy(version, new Bounds(CellCenter(0), Vector3.one)));
                _exposure.TickForTests(ExposureInterval / 5f);
            }
            Assert.That(target.GetIntensity(StatusKind.Wet),
                Is.EqualTo(2f * MaxApplyPerTick * 128f / byte.MaxValue).Within(0.0001f));
        }

        [Test]
        public void UnchangedSnapshotKeepsPeriodicExposureCadence()
        {
            Deposit(0, MaterialId.Water, amount: 128);
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);
            SetPrivateField(_exposure, "_liquidOccupancy",
                new FakeLiquidOccupancy(1u, new Bounds(CellCenter(0), Vector3.one)));
            _exposure.TickForTests(0.01f);
            _exposure.TickForTests(ExposureInterval / 2f);
            float oneDose = MaxApplyPerTick * 128f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(oneDose).Within(0.0001f));
            _exposure.TickForTests(ExposureInterval / 2f);
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(2f * oneDose).Within(0.0001f));
        }

        [Test]
        public void MultipleSourcesExposeRemoteWaterOnceAndLeaveGapUnreadable()
        {
            SetPrivateField(_profile, "_enableWorldActivityExpansion", true);
            InvokePrivateVoid(_runtime, "DisableActivitySources");
            InvokePrivateVoid(_runtime, "EnableActivitySources");
            StatusController target = CreateTarget(CellCenter(40), Vector3.one * .4f);
            StatusController nearby = CreateTarget(CellCenter(41), Vector3.one * .4f);
            // EditMode 不依赖 Unity 自动调用 MonoBehaviour.OnEnable，显式模拟真实生命周期事件。
            Game.Core.EventBus<Game.Core.WorldActivitySourceEvent>.Publish(new Game.Core.WorldActivitySourceEvent
                { Source = target.transform, Kind = Game.Core.WorldActivitySourceKind.Unit, Enabled = true });
            Game.Core.EventBus<Game.Core.WorldActivitySourceEvent>.Publish(new Game.Core.WorldActivitySourceEvent
                { Source = nearby.transform, Kind = Game.Core.WorldActivitySourceKind.Unit, Enabled = true });
            Deposit(40, MaterialId.Water, 128);
            Deposit(20, MaterialId.Water, 128);
            // 远程写入本身持有短租期；到期后验证中间区域没有被两个单位的包络永久激活。
            for (int i = 0; i < 24; i++) _runtime.TickForTests(.1f);
            _exposure.TickForTests(ExposureInterval);
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(MaxApplyPerTick * 128f / 255).Within(.0001f));
            Assert.That(_runtime.TryGetActiveCell(new Vector3Int(20, 0, 0), out _), Is.False);
        }

        [Test]
        public void OffscreenFireContinuesDecayingAfterPlayerLeaves()
        {
            SetPrivateField(_profile, "_enableWorldActivityExpansion", true);
            SetPrivateField(_runtime, "_settings", new ElementFieldSimulationSettings(
                1f, 0, 0, 1, 2, true, _reactionProfile.Extinguish));
            Deposit(0, MaterialId.Fire, 30);
            _interestObject.transform.position = CellCenter(100);
            _runtime.TickForTests(.1f);
            _runtime.TryGetCell(new Vector3Int(0, 0, 0), out ElementCell before);
            _runtime.TickForTests(.1f);
            _runtime.TryGetCell(new Vector3Int(0, 0, 0), out ElementCell after);
            Assert.That(after.Amount, Is.LessThan(before.Amount));
        }

        [Test]
        public void LiquidCellImmediatelyBelowColliderStillCountsAsGroundContact()
        {
            Deposit(0, MaterialId.Water, amount: 128);
            StatusController target = CreateTarget(
                new Vector3(0.5f, 1.125f, 0.5f),
                new Vector3(0.4f, 0.05f, 0.4f));

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 128f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f),
                "CharacterController 的 Skin Width 会让 Collider 底面略高于地面；脚底相邻 Cell 的液体仍应算持续接触。");
        }

        [Test]
        public void SettledSleepingWaterStillAppliesWetInsideInterestRegion()
        {
            Deposit(0, MaterialId.Water, amount: 128);

            // 写入 Tick 之后再推进两个无变化 Tick，使该非空 Chunk 达到默认 Settlement Hysteresis。
            Assert.That(_runtime.TickForTests(0.1f), Is.EqualTo(1));
            Assert.That(_runtime.TickForTests(0.1f), Is.EqualTo(1));
            StatusController target = CreateTarget(CellCenter(0), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 128f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f),
                "Solver-Sleeping 只表示水已稳定，不代表 Water Cell 从 Gameplay 世界消失。");
        }

        [Test]
        public void FireCellAppliesBurningProportionalToAmount()
        {
            Deposit(1, MaterialId.Fire, amount: 64);
            StatusController target = CreateTarget(CellCenter(1), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 64f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Burning), Is.EqualTo(expected).Within(0.0001f));
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.Zero);
        }

        [Test]
        public void ColliderCrossingChunkBoundaryUsesMaximumIntensity()
        {
            Deposit(1, MaterialId.Water, amount: 64);
            Deposit(2, MaterialId.Water, amount: 200);
            StatusController target = CreateTarget(
                new Vector3(2f, 0.5f, 0.5f),
                new Vector3(1.8f, 0.4f, 0.4f));

            _exposure.TickForTests(ExposureInterval);

            float expected = MaxApplyPerTick * 200f / byte.MaxValue;
            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(expected).Within(0.0001f),
                "Collider 横跨 Chunk(0) Local X=1 与 Chunk(1) Local X=0 时必须读取最大 Water Amount。");
        }

        [Test]
        public void MultipleCollidersApplyOnlyOncePerStatusTarget()
        {
            Deposit(0, MaterialId.Water, byte.MaxValue);
            GameObject targetRoot = new GameObject("WorldExposureTarget.MultiCollider");
            _createdTargets.Add(targetRoot);
            targetRoot.transform.position = CellCenter(0);
            StatusController target = targetRoot.AddComponent<StatusController>();
            CreateChildCollider(targetRoot.transform, new Vector3(-0.1f, 0f, 0f));
            CreateChildCollider(targetRoot.transform, new Vector3(0.1f, 0f, 0f));
            Physics.SyncTransforms();

            _exposure.TickForTests(ExposureInterval);

            Assert.That(target.GetIntensity(StatusKind.Wet), Is.EqualTo(MaxApplyPerTick).Within(0.0001f));
        }

        [Test]
        public void TargetOutsideActiveWorldRegionIsIgnored()
        {
            Deposit(8, MaterialId.Water, byte.MaxValue);
            StatusController target = CreateTarget(CellCenter(8), Vector3.one * 0.4f);

            _exposure.TickForTests(ExposureInterval);

            Assert.That(target.GetIntensity(StatusKind.Wet), Is.Zero,
                "远离 Interest Region 的 Collider 即使所在 Sleeping Chunk 保留 Water，也不应继续接受 Exposure。");
        }

        private void Deposit(int globalX, MaterialId kind, byte amount)
        {
            var request = new ElementWriteRequest(
                CellCenter(globalX),
                kind,
                amount,
                radius: 0f,
                useLinearFalloff: false);
            Assert.That(_runtime.TryEnqueueWrite(in request), Is.True);
            Assert.That(_runtime.TickForTests(0.1f), Is.EqualTo(1));
        }

        private StatusController CreateTarget(Vector3 position, Vector3 colliderSize)
        {
            GameObject target = new GameObject("WorldExposureTarget");
            _createdTargets.Add(target);
            target.transform.position = position;
            StatusController controller = target.AddComponent<StatusController>();
            BoxCollider collider = target.AddComponent<BoxCollider>();
            collider.size = colliderSize;
            Physics.SyncTransforms();
            return controller;
        }

        private static void CreateChildCollider(Transform parent, Vector3 localPosition)
        {
            GameObject child = new GameObject("WorldExposureTarget.Collider");
            child.transform.SetParent(parent, false);
            child.transform.localPosition = localPosition;
            BoxCollider collider = child.AddComponent<BoxCollider>();
            collider.size = Vector3.one * 0.3f;
        }

        private static Vector3 CellCenter(int x)
        {
            return new Vector3(x + 0.5f, 0.5f, 0.5f);
        }

        private void CreateMaterialRoutingFixture()
        {
            _materialDefinitions = new[]
            {
                CreateDefinition(MaterialId.Water, MaterialBehaviorKind.Liquid),
                CreateDefinition(MaterialId.Fire, MaterialBehaviorKind.ReactiveField),
                CreateDefinition(MaterialId.Poison, MaterialBehaviorKind.Liquid),
                // 默认反应表也包含 Sticky；独立测试 Catalog 必须满足其依赖。
                CreateDefinition(MaterialId.Sticky, MaterialBehaviorKind.Liquid),
            };
            _materialCatalog = ScriptableObject.CreateInstance<MaterialCatalog>();
            SetPrivateField(_materialCatalog, "_definitions", _materialDefinitions);

            _routingProfile = ScriptableObject.CreateInstance<MaterialSimulationRoutingProfile>();
            var serialized = new SerializedObject(_routingProfile);
            SerializedProperty routes = serialized.FindProperty("_routes");
            routes.arraySize = 3;
            SetRoute(routes.GetArrayElementAtIndex(0), MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            SetRoute(routes.GetArrayElementAtIndex(1), MaterialId.Poison, MaterialSimulationBackendKind.Unsupported);
            SetRoute(routes.GetArrayElementAtIndex(2), MaterialId.Sticky, MaterialSimulationBackendKind.Unsupported);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static MaterialDefinition CreateDefinition(MaterialId id, MaterialBehaviorKind behavior)
        {
            MaterialDefinition definition = ScriptableObject.CreateInstance<MaterialDefinition>();
            SetPrivateField(definition, "_id", id);
            SetPrivateField(definition, "_behavior", behavior);
            return definition;
        }

        private static void SetRoute(
            SerializedProperty route,
            MaterialId material,
            MaterialSimulationBackendKind backend)
        {
            route.FindPropertyRelative("_material").intValue = (byte)material;
            route.FindPropertyRelative("_backend").intValue = (byte)backend;
        }

        private static void SetPrivateField<TTarget, TValue>(
            TTarget target,
            string fieldName,
            TValue value)
        {
            FieldInfo field = typeof(TTarget).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少预期字段 {typeof(TTarget).Name}.{fieldName}。");
            field.SetValue(target, value);
        }

        private static void InvokePrivateVoid<TTarget>(TTarget target, string methodName)
        {
            MethodInfo method = typeof(TTarget).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"缺少预期生命周期方法 {typeof(TTarget).Name}.{methodName}。");
            method.Invoke(target, null);
        }

        private static TResult InvokePrivateResult<TResult>(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"缺少预期初始化方法 {target.GetType().Name}.{methodName}。");
            return (TResult)method.Invoke(target, null);
        }

        private sealed class FakeLiquidOccupancy : ILiquidOccupancyReadOnly
        {
            public FakeLiquidOccupancy(uint snapshotVersion, Bounds snapshotBounds)
            {
                SnapshotVersion = snapshotVersion;
                SnapshotBounds = snapshotBounds;
            }

            public bool HasValidSnapshot => true;
            public Bounds SnapshotBounds { get; }
            public uint SnapshotVersion { get; }

            public bool TryGetAmount(Vector3Int globalCell, MaterialId materialKind, out byte amount)
            {
                amount = 0;
                return false;
            }

            public bool TryGetAmountUnitsPerParticle(MaterialId material, out uint amountUnits)
            {
                amountUnits = 8u;
                return true;
            }

            public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination) => 0;
        }
    }
}
