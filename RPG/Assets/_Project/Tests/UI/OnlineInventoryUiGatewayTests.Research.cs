using System;
using System.Collections.Generic;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.UI.Tests
{
    public sealed partial class OnlineInventoryUiGatewayTests
    {
        private WandResearchController _research;
        private object _researchOwner;
        private ulong _researchSnapshotSequence;
        private uint _researchSessionVersion;
        private sealed class HomeResearchAccess : IOnlineHomeResearchAccess
        {
            public GameObject Actor { get; set; }
            public int WorldSceneHandle => (int)Actor.scene.handle;
            public ulong ActorId => 1;
            public uint ConnectionEpoch => 7;
            public bool CanResearch { get; set; } = true;
        }

        [Test]
        public void OnlineHomeResearchWaitsForCompleteOwnerStateBeforeOpeningTheOriginalWorkbench()
        {
            CreateOnlineHomeResearch(); CaptureRequests();
            var visibility = CaptureResearchVisibility(); float scale = Time.timeScale;
            Assert.That(_research.Open(default), Is.True, "F只提交可信Home研究意图，不能要求旧Hub grant或本地Profile。");
            Assert.That(_research.IsOpen, Is.False); Assert.That(_research.Presenter, Is.Null);
            Assert.That(_requests, Is.EqualTo(1)); AssertResearchRequest((OnlineInventoryCommandRequestedEvent)_request, 16);
            Assert.That(visibility, Is.Empty, "等待DS时不能先抢鼠标并显示空白工作台。");
            InstallHomeResearchState(true);
            Assert.That(_research.IsOpen, Is.True); Assert.That(_research.Presenter, Is.Not.Null);
            Assert.That(_research.Presenter.IsOwned, Is.True); Assert.That(Get(Get(_research.Presenter, "Program"), "Count"), Is.EqualTo(1));
            Assert.That(visibility, Is.EqualTo(new[] { true })); Assert.That(Time.timeScale, Is.EqualTo(scale));
            Assert.That(_localWrites, Is.Zero); Assert.That(Get(_research, "_session"), Is.Null);
        }

        [Test]
        public void OnlineHomeResearchCloseKeepsTheWorkbenchUntilOwnerFalseAndAllowsRejectedCloseRetry()
        {
            CreateOnlineHomeResearch(); CaptureRequests(); var visibility = CaptureResearchVisibility();
            _research.Open(default); InstallHomeResearchState(true); _research.Close();
            Assert.That(_research.IsOpen, Is.True); Assert.That(_requests, Is.EqualTo(2));
            var close = (OnlineInventoryCommandRequestedEvent)_request; AssertResearchRequest(close, 17);
            Receipt(close.Intent.CommandId, 7, close.CallerId + 1, 1, OnlineInventoryCommandFailure.Rejected);
            _research.Close(); Assert.That(_requests, Is.EqualTo(2), "别的UI收据不能解除正在等待的关闭。");
            Receipt(close.Intent.CommandId, 7, close.CallerId, 1, OnlineInventoryCommandFailure.Rejected);
            Assert.That(_research.IsOpen, Is.True); _research.Close(); Assert.That(_requests, Is.EqualTo(3));
            var retry = (OnlineInventoryCommandRequestedEvent)_request;
            Assert.That(retry.Intent.CommandId, Is.GreaterThan(close.Intent.CommandId));
            Receipt(retry.Intent.CommandId, 7, retry.CallerId, 1);
            Assert.That(_research.IsOpen, Is.True, "成功收据不能代替完整Owner的研究会话关闭事实。");
            InstallHomeResearchState(false);
            Assert.That(_research.IsOpen, Is.False); Assert.That(visibility, Is.EqualTo(new[] { true, false }));
            Assert.That(_localWrites, Is.Zero);
        }

        [TestCase(false)] [TestCase(true)]
        public void OnlineHomeResearchEditsUseRealOwnerProgramRevisionsAndKeepItemsUntilSnapshot(bool remove)
        {
            CreateOnlineHomeResearch(); CaptureRequests(); _research.Open(default); InstallHomeResearchState(true);
            _requests = 0; _request = null;
            var before = ReadView(remove ? "card" : "loose");
            var programArgs = new object[] { "wand", null };
            Assert.That((bool)Get(_source, "WandsReadOnly").GetType().GetMethod("TryGetProgram").Invoke(_source, programArgs), Is.True);
            uint revision = (uint)Get(programArgs[1], "ProgramRevision");
            if (remove) _research.Remove("card"); else _research.Install("loose", 1);
            Assert.That(_requests, Is.EqualTo(1)); var request = (OnlineInventoryCommandRequestedEvent)_request;
            Assert.That(request.ActorId, Is.EqualTo(1ul)); Assert.That(request.CallerId, Is.EqualTo(_research.GetInstanceID()));
            Assert.That(request.Intent.Kind, Is.EqualTo(OnlineInventoryCommandKind.MoveOwned));
            Assert.That(request.Intent.ExpectedInventoryVersion, Is.EqualTo(InventoryVersion()));
            Assert.That(request.Intent.ConnectionEpoch, Is.EqualTo(7u)); Assert.That(request.Intent.ItemId, Is.EqualTo(remove ? "card" : "loose"));
            Assert.That(request.Intent.SourceProgramRevision, Is.EqualTo(remove ? revision : 0u));
            Assert.That(request.Intent.TargetProgramRevision, Is.EqualTo(remove ? 0u : revision));
            Assert.That(request.Intent.TargetLocationKind, Is.EqualTo(remove ? 7 : 2));
            Assert.That(request.Intent.TargetSlot, Is.EqualTo(1));
            Assert.That(Get(ReadView(request.Intent.ItemId), "Location"), Is.EqualTo(Get(before, "Location")));
            Assert.That(_localWrites, Is.Zero, "Home必须使用原DS命令网关，不能广播旧本地InventoryCommand或写Profile。");
        }

        [Test]
        public void OnlineHomeResearchDetachReleasesItsCaptureAndCannotReopenFromLateOwnerData()
        {
            CreateOnlineHomeResearch(); CaptureRequests(); var visibility = CaptureResearchVisibility();
            _research.Open(default); InstallHomeResearchState(true);
            ConfigureResearch(null, null);
            Assert.That(_research.IsOpen, Is.False); Assert.That(visibility, Is.EqualTo(new[] { true, false }));
            Invoke(_research, "Update"); Assert.That(_research.IsOpen, Is.False);
            Assert.That(_requests, Is.EqualTo(1), "卸载只释放本机表现；可信退出链负责先关闭DS会话，不能向迟到Owner发新命令。");
            Assert.That(_localWrites, Is.Zero);
        }

        [TestCase("actor")] [TestCase("epoch")] [TestCase("scene")] [TestCase("old-hub")]
        public void OnlineHomeResearchFRequestUsesOnlyTheCurrentActorEpochAndWorld(string invalid)
        {
            CreateOnlineHomeResearch(); CaptureRequests();
            var request = new OnlineHomeResearchRequestedEvent
            { ActorId=1,ConnectionEpoch=7,WorldSceneHandle=(int)_research.gameObject.scene.handle };
            if (invalid=="old-hub") EventBus<HubInteractionResultEvent>.Publish(new HubInteractionResultEvent
            { Kind=HubInteractionKind.SpellResearch,Success=true,InventoryId="inventory",CallerId="old-hub",RequestId=1 });
            else
            {
                var wrong=request;
                if (invalid=="actor") wrong.ActorId++;
                if (invalid=="epoch") wrong.ConnectionEpoch++;
                if (invalid=="scene") wrong.WorldSceneHandle++;
                EventBus<OnlineHomeResearchRequestedEvent>.Publish(wrong);
            }
            Assert.That(_requests, Is.Zero); Assert.That(_research.IsOpen, Is.False);
            EventBus<OnlineHomeResearchRequestedEvent>.Publish(request);
            Assert.That(_requests, Is.EqualTo(1)); AssertResearchRequest((OnlineInventoryCommandRequestedEvent)_request,16);
            Assert.That(_research.IsOpen, Is.False);
        }

        private void CreateOnlineHomeResearch()
        {
            var catalog = Get(_source, "_catalog"); var assets = (OnlineOwnedAssetSnapshot)Get(Snapshot(), "Assets");
            assets.Items = new List<OnlineOwnedItemSnapshot>(assets.Items)
            { new OnlineOwnedItemSnapshot { InstanceId = "loose", ContentId = "ui.online.spell", ParentInstanceId = "", Revision = 1,
                LocationKind = 7, Slot = 0, RemainingCharges = 5 } }.ToArray();
            object[] create = { catalog, assets, Hash, null, null };
            Assert.That((bool)Type("Game.Run.OnlineOwnedWorkspaceFactory").GetMethod("TryCreatePractice").Invoke(null, create), Is.True, create[4] as string);
            _researchOwner = create[3]; _unsubscribers.Add(() => ((IDisposable)_researchOwner).Dispose());
            _researchSnapshotSequence = 0; _researchSessionVersion = 0;
            var initial = CaptureHomePresentation(false);
            var identity = Activator.CreateInstance(Type("Game.Core.OnlineInventoryPresentationIdentity"), "run", "player", 1ul,
                "inventory", Hash, 7u, "home");
            object[] source = { catalog, identity, initial, null, null };
            Assert.That((bool)Type("Game.Run.OnlineInventoryPresentationSource").GetMethod("TryCreate").Invoke(null, source), Is.True, source[4] as string);
            (_source as IDisposable)?.Dispose(); _source = source[3]; _read = Get(_source, "ReadOnly");
            _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new[] { _source }); _ui.Close();
            var root = Track(new GameObject("OriginalOnlineHomeResearch")); root.SetActive(false);
            _research = root.AddComponent<WandResearchController>(); Invoke(_research, "Awake");
            _unsubscribers.Add(() => { if (_research != null) { Invoke(_research, "OnDisable"); Invoke(_research, "OnDestroy"); } });
            Set(_research, "_inventory", _ui);
            var actor = Track(new GameObject("AuthenticatedHomeActor"));
            ConfigureResearch(_source, new HomeResearchAccess { Actor = actor }); Invoke(_research, "OnEnable");
        }
        private void ConfigureResearch(object source, IOnlineHomeResearchAccess access)
        {
            var method = typeof(WandResearchController).GetMethod("ConfigureOnline");
            Assert.That(method, Is.Not.Null, "原研究控制器需要显式在线绑定，禁止回落旧Session。");
            method.Invoke(_research, new[] { source, access });
        }
        private object CaptureHomePresentation(bool open)
        {
            object[] args = { 7u, ++_researchSnapshotSequence, 1u, null };
            Assert.That((bool)_researchOwner.GetType().GetMethod("TryCapturePresentation").Invoke(_researchOwner, args), Is.True);
            var snapshot = args[3]; Set(snapshot, "ResearchOpen", open); Set(snapshot, "ResearchSessionVersion", _researchSessionVersion);
            if (open)
            {
                object[] capture = { null };
                Assert.That((bool)_researchOwner.GetType().GetMethod("TryCaptureResearchState").Invoke(_researchOwner, capture), Is.True);
                Set(snapshot, "Research", capture[0]);
            }
            return snapshot;
        }
        private void InstallHomeResearchState(bool open)
        {
            _researchSessionVersion++;
            object[] args = { CaptureHomePresentation(open), null };
            Assert.That((bool)_source.GetType().GetMethod("TryInstall").Invoke(_source, args), Is.True, args[1] as string);
            Invoke(_research, "Update");
        }
        private List<bool> CaptureResearchVisibility()
        {
            var result = new List<bool>();
            Subscribe(typeof(InventoryPresentationVisibilityEvent), value =>
            { var e = (InventoryPresentationVisibilityEvent)value; if (e.OwnerId == _research.GetInstanceID()) result.Add(e.Open); });
            return result;
        }
        private void AssertResearchRequest(OnlineInventoryCommandRequestedEvent request, int kind)
        {
            Assert.That((int)request.Intent.Kind, Is.EqualTo(kind)); Assert.That(request.ActorId, Is.EqualTo(1ul));
            Assert.That(request.CallerId, Is.EqualTo(_research.GetInstanceID())); Assert.That(request.Intent.ConnectionEpoch, Is.EqualTo(7u));
            Assert.That(request.Intent.ExpectedInventoryVersion, Is.EqualTo(InventoryVersion())); Assert.That(request.Intent.CommandId, Is.GreaterThan(0));
            Assert.That(request.Intent.ItemId, Is.Null.Or.Empty); Assert.That(request.Intent.SourceWandId, Is.Null.Or.Empty);
            Assert.That(request.Intent.TargetWandId, Is.Null.Or.Empty); Assert.That(request.Intent.SourceProgramRevision, Is.Zero);
            Assert.That(request.Intent.TargetProgramRevision, Is.Zero); Assert.That(request.Intent.TargetLocationKind, Is.Zero);
            AssertNeutralAttachmentFields(request.Intent);
        }
    }
}
