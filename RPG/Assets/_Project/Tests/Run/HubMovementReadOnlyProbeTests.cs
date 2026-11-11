using System;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>仅EditMode调用原Probe入口，不进入Hub、不注入玩家输入、不改用户存档。</summary>
    public sealed class HubMovementReadOnlyProbeTests
    {
        private GameObject _root;
        private GameObject _cachedRoot, _currentRoot;
        private PortalRoomAcceptanceProbe _probe;
        private string _directory, _output, _command;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [SetUp] public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "NornHubProbe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _output = Path.Combine(_directory, "observation.json"); _command = Path.Combine(_directory, "command.txt");
            _root = new GameObject("TestReadOnlyHubProbe"); _root.SetActive(false);
            _probe = _root.AddComponent<PortalRoomAcceptanceProbe>();
        }

        [TearDown] public void TearDown()
        {
            if (_probe != null) typeof(PortalRoomAcceptanceProbe).GetMethod("OnDisable", Private).Invoke(_probe, null);
            UnityEngine.Object.DestroyImmediate(_root);
            if (_cachedRoot != null) UnityEngine.Object.DestroyImmediate(_cachedRoot);
            if (_currentRoot != null) UnityEngine.Object.DestroyImmediate(_currentRoot);
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        private void Configure(int samples, float seconds = 5)
        {
            var type = typeof(PortalRoomAcceptanceProbe).GetNestedType("Config", BindingFlags.NonPublic);
            string json = JsonUtility.ToJson(new TestConfig
            { Output = _output, Command = _command, ObserveMovementOnly = true, MovementSamples = samples, MovementSeconds = seconds });
            typeof(PortalRoomAcceptanceProbe).GetField("_config", Private).SetValue(_probe, JsonUtility.FromJson(json, type));
        }

        private void Capture(int frame, double now)
        {
            var method = typeof(PortalRoomAcceptanceProbe).GetMethod("CaptureMovementObservation", Private);
            Assert.That(method, Is.Not.Null, "原Probe缺少有界只读采样入口");
            method.Invoke(_probe, new object[] { frame, now });
        }

        [Serializable] private sealed class TestConfig
        {
            public string Output, Command;
            public bool ObserveMovementOnly;
            public int MovementSamples;
            public float MovementSeconds;
        }

        [Serializable] private sealed class ReportView
        {
            public string StopReason;
            public int SampleCount, ContactCount;
            public bool LastMoveFrameKnown, RequestedDisplacementAvailable;
            public SampleView[] Samples;
        }
        [Serializable] private sealed class SampleView
        {
            public ActorView CachedActor, CurrentActor;
            public bool CachedEqualsCurrent, ObservedDeltaAvailable;
            public bool OnlineViewObserved, OnlineViewAppliesToCurrent, VisibilityEventObserved, VisibilityOverflow;
            public bool OnlineFrozen;
            public int ObservedOpenUiOwners;
            public Vector3 ObservedDelta;
            public PlayerMovementReadOnlyObservation Current, Cached;
        }
        [Serializable] private sealed class ActorView { public int InstanceId; }

        private ReportView ReadReport() => JsonUtility.FromJson<ReportView>(File.ReadAllText(_output));

        [Test] public void ReadOnlyModeNeverConsumesCommandsOrRequestsRoomObservation()
        {
            Configure(2); File.WriteAllText(_command, "position-portal");
            typeof(PortalRoomAcceptanceProbe).GetField("_view", Private).SetValue(_probe,
                new OnlineRoomChangedEvent { PortalId = 123 });
            int requests = 0; Action<OnlineRoomRequestEvent> handler = _ => requests++;
            EventBus<OnlineRoomRequestEvent>.Subscribe(handler);
            try { typeof(PortalRoomAcceptanceProbe).GetMethod("Update", Private).Invoke(_probe, null); }
            finally { EventBus<OnlineRoomRequestEvent>.Unsubscribe(handler); }
            Assert.That(File.Exists(_command), Is.True, "只读模式不得取走旧验收动作命令");
            Assert.That(requests, Is.Zero, "不能为补观测而发布Observe/回放请求");
            Assert.That(File.Exists(_output), Is.False, "1Hz旧报告不能覆盖同帧观测输出");
        }

        [TestCase(0)] [TestCase(-1)] public void NoPositiveOptInNeverSamplesOrWrites(int samples)
        {
            Configure(samples); Capture(1, 10); Capture(2, 11);
            Assert.That(File.Exists(_output), Is.False);
        }

        [Test] public void SampleCapacityStopsOnceAndDuplicateFrameDoesNotSpendCapacity()
        {
            Configure(2); Capture(10, 10); Capture(10, 10.1);
            Assert.That(File.Exists(_output), Is.False);
            Capture(11, 10.2); Assert.That(File.Exists(_output), Is.True);
            Assert.That(_probe.enabled, Is.False, "结束后停止Unity逐帧回调，而不只是在每帧return");
            string first = File.ReadAllText(_output);
            StringAssert.Contains("\"SampleCount\": 2", first);
            StringAssert.Contains("\"StopReason\": \"capacity\"", first);
            Capture(12, 10.3); Assert.That(File.ReadAllText(_output), Is.EqualTo(first));
        }

        [Test] public void DurationHardLimitStopsWithoutAddingExpiredFrame()
        {
            Configure(10000, 100); Capture(1, 10); Capture(2, 15.01);
            string result = File.ReadAllText(_output);
            StringAssert.Contains("\"SampleCount\": 1", result);
            StringAssert.Contains("\"StopReason\": \"deadline\"", result);
            Capture(3, 15.02); Assert.That(File.ReadAllText(_output), Is.EqualTo(result));
        }

        [Test] public void DisableFlushesPartialWindowExactlyOnce()
        {
            Configure(3); Capture(1, 10);
            typeof(PortalRoomAcceptanceProbe).GetMethod("OnDisable", Private).Invoke(_probe, null);
            string result = File.ReadAllText(_output);
            StringAssert.Contains("\"StopReason\": \"disabled\"", result);
            typeof(PortalRoomAcceptanceProbe).GetMethod("OnDisable", Private).Invoke(_probe, null);
            Assert.That(File.ReadAllText(_output), Is.EqualTo(result));
        }

        [Test] public void ExcessiveSampleRequestIsClampedTo240AndNeverRestarts()
        {
            Configure(int.MaxValue, 5);
            for (int i = 0; i < 240; i++) Capture(100 + i, 10 + i * .001);
            Assert.That(ReadReport().SampleCount, Is.EqualTo(240));
            Assert.That(ReadReport().StopReason, Is.EqualTo("capacity"));
            string result = File.ReadAllText(_output); Capture(400, 11);
            Assert.That(File.ReadAllText(_output), Is.EqualTo(result));
        }

        [Test] public void ActualCurrentIsDistinctFromCachedActorAndDeltaIsOnlyForSameActor()
        {
            Configure(3); Capture(1, 10);
            _cachedRoot = new GameObject("Cached test actor"); _currentRoot = new GameObject("Current test actor");
            var cached = _cachedRoot.AddComponent<ProbeObservationTestSource>();
            _currentRoot.AddComponent<ProbeObservationTestSource>(); cached.Current = _currentRoot.transform;
            _cachedRoot.transform.position = new Vector3(5, 0, 0); _currentRoot.transform.position = new Vector3(9, 0, 0);
            typeof(PortalRoomAcceptanceProbe).GetField("_movementSource", Private).SetValue(_probe, cached);
            typeof(PortalRoomAcceptanceProbe).GetField("_movementCachedActor", Private).SetValue(_probe, _cachedRoot.transform);
            Capture(2, 10.01); _currentRoot.transform.position += Vector3.right; Capture(3, 10.02);
            var result = ReadReport(); var sample = result.Samples[1];
            Assert.That(sample.CachedActor.InstanceId, Is.EqualTo(_cachedRoot.GetInstanceID()));
            Assert.That(sample.CurrentActor.InstanceId, Is.EqualTo(_currentRoot.GetInstanceID()));
            Assert.That(sample.CachedEqualsCurrent, Is.False);
            Assert.That(sample.Cached.ActorInstanceId, Is.EqualTo(_cachedRoot.GetInstanceID()));
            Assert.That(sample.Current.ActorInstanceId, Is.EqualTo(_currentRoot.GetInstanceID()));
            Assert.That(sample.Cached.Frame, Is.EqualTo(sample.Current.Frame));
            Assert.That(sample.ObservedDeltaAvailable, Is.False);
            Assert.That(result.Samples[2].ObservedDeltaAvailable, Is.True);
            Assert.That(result.Samples[2].ObservedDelta, Is.EqualTo(Vector3.right));
            Assert.That(result.LastMoveFrameKnown || result.RequestedDisplacementAvailable, Is.False);
            Assert.That(_cachedRoot.transform.position, Is.EqualTo(new Vector3(5, 0, 0)));
            Assert.That(_currentRoot.transform.position, Is.EqualTo(new Vector3(10, 0, 0)));
        }

        [Test] public void LateSubscribedUiIsUnknownAndObservedOwnersHaveFixedBound()
        {
            Configure(2); Capture(1, 10);
            // 使用正式订阅入口和EventBus，不为晚订阅发送回放或改变UI。
            typeof(PortalRoomAcceptanceProbe).GetMethod("OnEnable", Private).Invoke(_probe, null);
            for (int i = 1; i <= 17; i++) EventBus<InventoryPresentationVisibilityEvent>.Publish(
                new InventoryPresentationVisibilityEvent { OwnerId = i, Open = true });
            EventBus<InventoryPresentationVisibilityEvent>.Publish(new InventoryPresentationVisibilityEvent { OwnerId = 1, Open = false });
            EventBus<OnlineMovementPresentationEvent>.Publish(new OnlineMovementPresentationEvent { WorldSceneHandle = -1, Frozen = true });
            Capture(2, 10.01);
            var result = ReadReport();
            Assert.That(result.Samples[0].VisibilityEventObserved, Is.False);
            Assert.That(result.Samples[0].OnlineViewObserved, Is.False);
            Assert.That(result.Samples[1].VisibilityEventObserved, Is.True);
            Assert.That(result.Samples[1].VisibilityOverflow, Is.True);
            Assert.That(result.Samples[1].ObservedOpenUiOwners, Is.EqualTo(15));
            Assert.That(result.Samples[1].OnlineViewObserved, Is.True);
            Assert.That(result.Samples[1].OnlineViewAppliesToCurrent, Is.False);
            Assert.That(result.Samples[1].OnlineFrozen, Is.True, "事件必须复制成可序列化值，不能写成空JSON对象");
        }

        [Test] public void ContactsAreBoundedTo32AndRejectExpiredOrStoppedObservations()
        {
            var type = typeof(PortalRoomAcceptanceProbe).Assembly.GetType("Game.Run.HubMovementContactWindow");
            Assert.That(type, Is.Not.Null);
            object window = Activator.CreateInstance(type, new object[] { 15d });
            var observe = type.GetMethod("Observe");
            for (int i = 0; i < 35; i++) observe.Invoke(window, new object[] { 1, 2, 10, 11d, Vector3.zero, Vector3.up, Vector3.right, 1f });
            Assert.That(type.GetProperty("Count").GetValue(window), Is.EqualTo(32));
            Assert.That(type.GetProperty("Dropped").GetValue(window), Is.EqualTo(3));
            observe.Invoke(window, new object[] { 1, 2, 11, 15d, Vector3.zero, Vector3.up, Vector3.right, 1f });
            Assert.That(type.GetProperty("Accepting").GetValue(window), Is.EqualTo(false));
            observe.Invoke(window, new object[] { 1, 2, 12, 12d, Vector3.zero, Vector3.up, Vector3.right, 1f });
            Assert.That(type.GetProperty("Count").GetValue(window), Is.EqualTo(32));
            Assert.That(type.GetProperty("Dropped").GetValue(window), Is.EqualTo(3));
        }

        [Test] public void SameActorContactObserverRebindsAfterDisableWithoutChangingController()
        {
            Configure(3); Capture(1, 10);
            _currentRoot = new GameObject("Contact observation test actor");
            var controller = _currentRoot.AddComponent<CharacterController>();
            var bind = typeof(PortalRoomAcceptanceProbe).GetMethod("BindMovementContactObservation", Private);
            bind.Invoke(_probe, new object[] { _currentRoot.transform });
            var observer = _currentRoot.GetComponent<HubMovementContactProbe>();
            Assert.That(observer, Is.Not.Null);
            var windowField = typeof(HubMovementContactProbe).GetField("_window", Private);
            object window = windowField.GetValue(observer);
            typeof(HubMovementContactProbe).GetMethod("OnDisable", Private).Invoke(observer, null);
            Assert.That(windowField.GetValue(observer), Is.Null);
            bind.Invoke(_probe, new object[] { _currentRoot.transform });
            Assert.That(windowField.GetValue(observer), Is.SameAs(window));
            Assert.That(_currentRoot.GetComponents<HubMovementContactProbe>().Length, Is.EqualTo(1));
            Assert.That(controller.enabled, Is.True);
            Assert.That(_currentRoot.transform.position, Is.EqualTo(Vector3.zero));
        }
    }

    /// <summary>只替换数据提供口以验证正式Probe的关联，不模拟玩家输入/运动。</summary>
    public sealed class ProbeObservationTestSource : MonoBehaviour, IPlayerMovementReadOnlyObservation
    {
        public Transform Current;
        public Transform CurrentInputActor => Current;
        public PlayerMovementReadOnlyObservation CaptureReadOnlyObservation() => new PlayerMovementReadOnlyObservation
        { ActorInstanceId = gameObject.GetInstanceID(), Frame = Time.frameCount, Position = transform.position };
    }
}
