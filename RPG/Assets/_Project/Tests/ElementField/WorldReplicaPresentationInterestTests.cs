using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 原生P8暴露的显示距离中心缺口：使用真实Replica初始化及Core注册，
    /// 检查公开InterestPosition/Origin，不以私有字段存在或事件订阅次数代替行为。
    /// 配置仅克隆到测试内存，绝不写正式Profile/Scene或启用网络。
    /// </summary>
    public sealed class WorldReplicaPresentationInterestTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private const int ActorOwner = 197101, NextActorOwner = 197102;
        private const int CameraOwner = 197201, NextCameraOwner = 197202;
        private GameObject _host, _actor, _nextActor, _cameraHost, _otherCameraHost, _authored;
        private ElementWorldRuntime _world;
        private ElementWorldProfile _profile;
        private LiquidWorldProfile _liquidProfile;
        private FixedSolidWorldProfile _solidProfile;
        private GasWorldProfile _gasProfile;
        private Camera _camera, _otherCamera;
        private readonly Vector3 _origin = new Vector3(10, 3, 20);
        private readonly Vector3 _localPosition = new Vector3(-51, 0, 52);

        [SetUp] public void SetUp()
        {
            _host = new GameObject("Replica presentation-interest fixture"); _host.SetActive(false);
            _host.transform.position = _origin;
            _world = _host.AddComponent<ElementWorldRuntime>();
            _profile = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Liquids/ElementWorldProfile_P7_DemoRun_Grid.asset"));
            _liquidProfile = Object.Instantiate(AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            _solidProfile = ScriptableObject.CreateInstance<FixedSolidWorldProfile>();
            _gasProfile = ScriptableObject.CreateInstance<GasWorldProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumResidentChunks\":8,\"_maxPendingWrites\":16}", _profile);
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_maximumActiveCells\":128,\"_maximumPendingDeposits\":16,\"_maximumCellsPerTick\":64,\"_maximumStableRegions\":4,\"_maximumStableOutlets\":4,\"_maximumStableRegionCells\":256,\"_maximumStableSurfacePatches\":16,\"_maximumImmediateExpansionCells\":64,\"_maximumImmediateExpansionSurfaceChunks\":2,\"_maximumAutoCandidateCells\":64,\"_autoValidationCellsPerTick\":32}", _liquidProfile);
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_maximumPendingDeposits\":16,\"_maximumNavigationBoxesPerChunk\":4}", _solidProfile);
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":4,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":32,\"_maximumPendingDeposits\":16,\"_maximumTerrainSamplesPerTick\":16,\"_maximumBirthScansPerTick\":128}", _gasProfile);
            var liquid = _host.AddComponent<LiquidWorldRuntime>();
            var solid = _host.AddComponent<FixedSolidWorldRuntime>();
            var gas = _host.AddComponent<GasWorldRuntime>();
            Set(liquid, "_profile", _liquidProfile); Set(solid, "_profile", _solidProfile); Set(gas, "_profile", _gasProfile);
            Set(_world, "_profile", _profile); Set(_world, "_gridLiquidRuntime", liquid);
            Set(_world, "_fixedSolidRuntime", solid); Set(_world, "_gasRuntime", gas);
            Set(_host.GetComponent<ElementFieldSolidBaker>(), "_solidLayerMask", (LayerMask)0);
            Set(_host.GetComponent<ElementFieldSolidBaker>(), "_includeDefaultEnvironment", false);
            _actor = new GameObject("Registered local Actor"); _actor.transform.position = _localPosition;
            _nextActor = new GameObject("Next local Actor"); _nextActor.transform.position = new Vector3(80, 2, -40);
            _cameraHost = new GameObject("Registered local Camera"); _camera = _cameraHost.AddComponent<Camera>();
            _otherCameraHost = new GameObject("Replacement Camera"); _otherCamera = _otherCameraHost.AddComponent<Camera>();
            _authored = new GameObject("Offline authored interest"); _authored.transform.position = new Vector3(5, 6, 7);
        }

        [TearDown] public void TearDown()
        {
            // 只释放本夹具的Owner，不清理其他测试/场景的表现注册。
            LocalActorCameraPresentation.Clear(CameraOwner); LocalActorCameraPresentation.Clear(NextCameraOwner);
            LocalActorPresentation.Clear(ActorOwner); LocalActorPresentation.Clear(NextActorOwner);
            if (_world != null) Invoke("OnDisable");
            Object.DestroyImmediate(_host); Object.DestroyImmediate(_actor); Object.DestroyImmediate(_nextActor);
            Object.DestroyImmediate(_cameraHost); Object.DestroyImmediate(_otherCameraHost); Object.DestroyImmediate(_authored);
            Object.DestroyImmediate(_profile); Object.DestroyImmediate(_liquidProfile);
            Object.DestroyImmediate(_solidProfile); Object.DestroyImmediate(_gasProfile);
        }

        private void Replica()
        {
            _world.ConfigureReplica(new WorldReplicaStore(7, 8, 256, 16, 4096));
            Assert.That(_world.Origin, Is.EqualTo(_origin));
        }
        private void BindLocal()
        {
            LocalActorPresentation.Bind(ActorOwner, _actor, true, _actor.transform);
            LocalActorCameraPresentation.Bind(CameraOwner, _actor, _camera);
        }

        [TestCase(false)] [TestCase(true)]
        public void ReplicaFollowsRegisteredLocalActorBeforeOrAfterEnableWithoutMovingGridOrigin(bool lateEnable)
        {
            Replica();
            if (lateEnable) { BindLocal(); Invoke("OnEnable"); }
            else { Invoke("OnEnable"); BindLocal(); }
            Assert.That(_world.InterestPosition, Is.EqualTo(_localPosition), "Replica显示距离不能继续围绕世界原点裁剪。");
            _actor.transform.position = new Vector3(-45, 1, 58);
            Assert.That(_world.InterestPosition, Is.EqualTo(new Vector3(-45, 1, 58)));
            Assert.That(_world.Origin, Is.EqualTo(_origin), "显示兴趣改变不得移动网格原点。");
        }

        [Test] public void CameraClearAndReconnectionRestoreOnlyCurrentOwnerWithoutChangingCoreEvents()
        {
            Replica(); Invoke("OnEnable"); BindLocal();
            Assert.That(_world.InterestPosition, Is.EqualTo(_localPosition));
            int defaultEvents = 0;
            System.Action<LocalActorCameraBoundEvent> observer = value => { if (value.Actor == null && value.Camera == null) defaultEvents++; };
            EventBus<LocalActorCameraBoundEvent>.Subscribe(observer);
            try
            {
                LocalActorCameraPresentation.Clear(CameraOwner);
                Assert.That(_world.InterestPosition, Is.EqualTo(_origin));
                Assert.That(defaultEvents, Is.EqualTo(1), "其他消费者仍必须收到原default解绑事件。");
                LocalActorPresentation.Bind(NextActorOwner, _nextActor, true, _nextActor.transform);
                LocalActorCameraPresentation.Bind(NextCameraOwner, _nextActor, _otherCamera);
                LocalActorCameraPresentation.Clear(CameraOwner); LocalActorPresentation.Clear(ActorOwner);
                Assert.That(_world.InterestPosition, Is.EqualTo(new Vector3(80, 2, -40)), "旧连接/Scene Owner不能清掉新本地目标。");
                Assert.That(defaultEvents, Is.EqualTo(1));
            }
            finally { EventBus<LocalActorCameraBoundEvent>.Unsubscribe(observer); }
        }

        [Test] public void ForeignActorCameraCannotTakeOverRegisteredLocalInterest()
        {
            Replica(); Invoke("OnEnable"); BindLocal();
            EventBus<LocalActorCameraBoundEvent>.Publish(new LocalActorCameraBoundEvent { Actor = _nextActor, Camera = _otherCamera });
            Assert.That(_world.InterestPosition, Is.EqualTo(_localPosition), "事件载荷不是本地身份来源。");
            LocalActorCameraPresentation.Bind(NextCameraOwner, _nextActor, _otherCamera);
            Assert.That(_world.InterestPosition, Is.EqualTo(_origin), "Actor/Camera注册不匹配时安全解绑，不能跟随远端角色。");
            LocalActorCameraPresentation.Bind(CameraOwner, _actor, _camera);
            Assert.That(_world.InterestPosition, Is.EqualTo(_localPosition));
        }

        [TestCase(false)] [TestCase(true)]
        public void DestroyedActorOrCameraLeavesSafePresentationFallback(bool destroyCamera)
        {
            Replica(); Invoke("OnEnable"); BindLocal();
            Assert.That(_world.InterestPosition, Is.EqualTo(_localPosition));
            if (destroyCamera) Object.DestroyImmediate(_cameraHost); else Object.DestroyImmediate(_actor);
            Assert.That(_world.InterestPosition, Is.EqualTo(_origin), "Unity假null不得保留旧Actor/Camera作为有效目标。");
            Assert.That(_world.Origin, Is.EqualTo(_origin));
        }

        [Test] public void DisabledReplicaDoesNotListenAndReenableUsesLatestLocalBinding()
        {
            Replica(); Invoke("OnEnable"); BindLocal();
            Assert.That(_world.InterestPosition, Is.EqualTo(_localPosition));
            Invoke("OnDisable");
            LocalActorPresentation.Bind(NextActorOwner, _nextActor, true, _nextActor.transform);
            LocalActorCameraPresentation.Bind(NextCameraOwner, _nextActor, _otherCamera);
            Assert.That(_world.InterestPosition, Is.EqualTo(_origin));
            Invoke("OnEnable");
            Assert.That(_world.InterestPosition, Is.EqualTo(new Vector3(80, 2, -40)));
        }

        [Test] public void ActorWithoutCurrentCameraAndEmptyRegistryAreSafeUntilRealCameraBinds()
        {
            Replica(); Invoke("OnEnable");
            Assert.That(_world.InterestPosition, Is.EqualTo(_origin));
            LocalActorPresentation.Bind(ActorOwner, _actor, true, _actor.transform);
            Assert.That(_world.InterestPosition, Is.EqualTo(_origin));
            LocalActorCameraPresentation.Bind(CameraOwner, _actor, _camera);
            Assert.That(_world.InterestPosition, Is.EqualTo(_localPosition));
            LocalActorPresentation.Clear(ActorOwner);
            Assert.That(_world.InterestPosition, Is.EqualTo(_origin));
        }

        [TestCase(false)] [TestCase(true)]
        public void InactiveRegisteredPairRecoversWithoutExtraBindingEvent(bool inactiveCamera)
        {
            Replica(); BindLocal();
            if (inactiveCamera) _camera.enabled = false; else _actor.SetActive(false);
            // 晚订阅时身份仍合法；暂时失活只应关闭显示门，不能永久丢掉已注册身份。
            Invoke("OnEnable");
            Assert.That(_world.InterestPosition, Is.EqualTo(_origin));
            if (inactiveCamera) _camera.enabled = true; else _actor.SetActive(true);
            Assert.That(_world.InterestPosition, Is.EqualTo(_localPosition),
                "仅恢复active/enabled不会发布Core绑定事件，不应永远退回世界原点。");
        }

        [TestCase(WorldExecutionMode.SinglePlayer)] [TestCase(WorldExecutionMode.Authority)]
        public void OtherExecutionModesKeepAuthoredInterestWhenLocalPresentationChanges(WorldExecutionMode mode)
        {
            _world.ConfigureExecutionMode(mode); Set(_world, "_interestPoint", _authored.transform);
            Invoke("OnEnable"); BindLocal();
            Assert.That(_world.InterestPosition, Is.EqualTo(new Vector3(5, 6, 7)));
            LocalActorCameraPresentation.Clear(CameraOwner);
            Assert.That(_world.InterestPosition, Is.EqualTo(new Vector3(5, 6, 7)));
        }

        private void Invoke(string method) => typeof(ElementWorldRuntime).GetMethod(method, Hidden).Invoke(_world, null);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Hidden).SetValue(target, value);
    }
}
