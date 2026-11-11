using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Run;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>真实生产 Catalog / Owned事务 / SpellCaster 的组合回归；没有网络或本地存档捷径。</summary>
    public sealed class OnlineCastDriverTests
    {
        [Test] public void OriginalReturnInteractionUsesTheActualColliderOwnerAndAReadOnlyWorldGate()
        {
            var type = typeof(OnlineCastDriver).Assembly.GetType("Game.Character.OnlineReturnPortalInteraction");
            Assert.That(type, Is.Not.Null);
            var portalRoot = new GameObject("OriginalReturnPortal"); var actorRoot = new GameObject("LocalReturnActor");
            var strangerRoot = new GameObject("ForeignReturnActor");
            try
            {
                var portal = portalRoot.AddComponent<BoxCollider>(); portal.isTrigger = true; portal.size = Vector3.one * 3;
                var actor = actorRoot.AddComponent<CapsuleCollider>(); var stranger = strangerRoot.AddComponent<CapsuleCollider>();
                var target = portalRoot.AddComponent(type); bool available = false;
                var method = type.GetMethod("Configure"); Assert.That(method, Is.Not.Null);
                method.Invoke(target, new object[] { portal, actor, 1UL, 7u, (int)actorRoot.scene.handle, (Func<bool>)(() => available) });
                Physics.SyncTransforms();
                Assert.That(type.GetMethod("CanInteract").Invoke(target, new object[] { actor }), Is.False);
                available = true;
                Assert.That(type.GetMethod("CanInteract").Invoke(target, new object[] { stranger }), Is.False);
                Assert.That(type.GetMethod("CanInteract").Invoke(target, new object[] { actor }), Is.True);
                actorRoot.transform.position = Vector3.right * 10; Physics.SyncTransforms();
                Assert.That(type.GetMethod("CanInteract").Invoke(target, new object[] { actor }), Is.False);
            }
            finally
            {
                // Configure在本夹具中显式登记；EditMode不保证未执行Awake的组件收到OnDisable，
                // 因而成对注销这个确切目标，不能用全局Clear掩盖其它F优先级用例的生命周期问题。
                var registered = portalRoot.GetComponent(type) as IPortalInteractionTarget;
                if (registered != null) PortalInteractionRegistry.Unregister(registered);
                UnityEngine.Object.DestroyImmediate(portalRoot); UnityEngine.Object.DestroyImmediate(actorRoot); UnityEngine.Object.DestroyImmediate(strangerRoot);
            }
        }
        private const string Wand = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Card = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private GameObject _root;
        private EditorWindow _selectionPreviousWindow;
        private OnlineCombatDefinition _containerDefinitionClone;
        private Component _containerInput;
        private Camera _containerInputCamera;
        private UnityEngine.InputSystem.Keyboard _containerKeyboard;
        private UnityEngine.InputSystem.Mouse _containerMouse;
        private readonly List<OnlineInventoryCommandIntent> _containerInputCommands = new List<OnlineInventoryCommandIntent>();
        private readonly List<UnityEngine.Object> _containerInputObjects = new List<UnityEngine.Object>();
        private readonly List<Component> _containerPriorityInputs = new List<Component>();
        private readonly List<KeyValuePair<PropertyInfo, object>> _containerInputStatics = new List<KeyValuePair<PropertyInfo, object>>();
        private IPortalInteractionTarget _containerPortalTarget;
        private bool _containerInputSubscribed, _containerPickupFrameSaved;
        private int _containerPreviousPickupFrame;
        private const string ContainerPlayerUpdatesFlag = "RUN_PLAYER_UPDATES_IN_EDIT_MODE";
        private UnityEngine.InputSystem.InputSettings _containerInputSettings;
        private bool _containerPlayerUpdatesSaved, _containerPreviousPlayerUpdates;
        private readonly List<GameObject> _outputs = new List<GameObject>();
        private int _born;
        [SetUp] public void Setup() { _born = 0; EventBus<CombatOutputSpawnedEvent>.Subscribe(OnBorn); }
        private void OnBorn(CombatOutputSpawnedEvent fact) { _born++; _outputs.Add(fact.Output); }
        [TearDown] public void Cleanup()
        {
            CleanupContainerInputFixture();
            EventBus<CombatOutputSpawnedEvent>.Unsubscribe(OnBorn);
            if (_root != null)
            {
                var adapter = _root.GetComponent<OwnedWandCastAdapter>();
                if (adapter != null) typeof(OwnedWandCastAdapter).GetMethod("OnDisable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
                var driver = _root.GetComponent(typeof(WizardController).Assembly.GetType("Game.Character.OnlineCastDriver"));
                if (driver != null) driver.GetType().GetMethod("OnDestroy",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(driver,null);
            }
            if (_root != null) Object.DestroyImmediate(_root);
            foreach (var output in _outputs) if (output != null) Object.DestroyImmediate(output);
            _outputs.Clear();
            if (_selectionPreviousWindow != null) _selectionPreviousWindow.Focus();
            _selectionPreviousWindow = null;
            if (_containerDefinitionClone != null) Object.DestroyImmediate(_containerDefinitionClone);
            _containerDefinitionClone = null;
        }
        private object Create(out OnlineOwnedAssetSnapshot assets, bool insufficient = false, bool includeContainer = false,
            OnlineCombatDefinition definitionOverride = null, OnlineOwnedAssetSnapshot assetsOverride = null, OnlineHomeWorldBindings homeWorld = null)
        {
            var type = typeof(WizardController).Assembly.GetType("Game.Character.OnlineCastDriver");
            Assert.That(type, Is.Not.Null, "缺少正式角色的权威施法装配，不能解除单机 guard 代替。");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab");
            _root = Object.Instantiate(prefab); _root.name = "N4 Cast Driver Fixture";
            _root.transform.position = new Vector3(23143, 34251, 12312);
            var wizard = _root.GetComponent<WizardController>(); wizard.ConfigureExecutionMode(PlayerExecutionMode.Authority);
            // EditMode 不保证自动发送 Awake；仅夹具补真实生命周期，不在生产代码手动触发 Unity 生命周期。
            if (wizard.SpellCaster == null) typeof(WizardController).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(wizard,null);
            var health = _root.GetComponent<HealthComponent>();
            if (health.CurrentHp <= 0) typeof(HealthComponent).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(health,null);
            var definition = definitionOverride ?? AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1, PlayerId = "owner", RunId = "run", InventoryId = "online:test:" + Guid.NewGuid().ToString("N"),
                // 夹具明确提供已安装的零余额事实；CurrencyVersion=0表示缺失钱包版本，不能作为合法Entry前置。
                InventoryVersion = 1, Gold = 0, CurrencyVersion = 1, SelectedWandInstanceId = Wand, GameplayConfigHash = definition.GameplayConfigHash,
                Items = new[] {
                    new OnlineOwnedItemSnapshot { InstanceId=Wand,ContentId="wandtest.wand.a",Revision=1,LocationKind=3,Slot=0,UseTemplateWandStats=!insufficient,
                        WandStats=insufficient ? new OnlineOwnedWandStatsSnapshot {MaxMana=10,ManaRegenPerSecond=0,BaseCastDelay=.8f,BaseReloadTime=.8f,ProgramCapacity=4,BaseDraws=1} : null },
                    new OnlineOwnedItemSnapshot {InstanceId=Card,ContentId="wandtest.fire",Revision=1,LocationKind=2,ParentInstanceId=Wand,Slot=0,RemainingCharges=30}
                }};
            if (includeContainer)
            {
                // 专例只加真实Catalog容器，不改变原有战斗fixture或授予额外法术/材料。
                var carried = new List<OnlineOwnedItemSnapshot>(assets.Items);
                carried.Add(new OnlineOwnedItemSnapshot
                {
                    InstanceId = "selection-authority-bottle", ContentId = "containertest.bottle", Revision = 1,
                    LocationKind = 8, Slot = 0,
                    ContainerState = new OnlineOwnedContainerStateSnapshot
                    { SchemaVersion = 1, Components = Array.Empty<OnlineOwnedMaterialAmountSnapshot>() }
                });
                assets.Items = carried.ToArray();
            }
            if (assetsOverride != null) assets = assetsOverride;
            var driver = _root.AddComponent(type);
            if (homeWorld != null)
            {
                var configure = type.GetMethod("ConfigureHomeWorld");
                Assert.That(configure, Is.Not.Null, "DS必须由真实Home绑定选择和平Owner，不能相信Client flag。");
                Assert.That((bool)configure.Invoke(driver, new object[] { homeWorld }), Is.True);
            }
            object[] init = { assets, definition, new CombatSimulationWorld(), null };
            Assert.That((bool)type.GetMethod("InitializeAuthority").Invoke(driver, init), Is.True, init[3] as string);
            var adapter = _root.GetComponent<OwnedWandCastAdapter>();
            // 和上面的 Awake 一样，EditMode 夹具必须建立正式 OnEnable 的同步收据订阅。
            typeof(OwnedWandCastAdapter).GetMethod("OnDisable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
            typeof(OwnedWandCastAdapter).GetMethod("OnEnable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
            type.GetMethod("SetConnection").Invoke(driver, new object[] { 1u, true });
            return driver;
        }

        [Test] public void HomeAuthorityUsesTheRealAuthoringBindingForPracticePurposeAndResearchGeometry()
        {
            var map = new GameObject("Trusted Home test geometry"); map.transform.position = new Vector3(23143, 34251, 12312);
            try
            {
                var research = new GameObject("Original research Box"); research.transform.SetParent(map.transform, false);
                var researchBox = research.AddComponent<BoxCollider>(); researchBox.size = Vector3.one * 10;
                var portal = new GameObject("Original return Box"); portal.transform.SetParent(map.transform, false); var portalBox = portal.AddComponent<BoxCollider>();
                var home = map.AddComponent<OnlineHomeWorldBindings>(); var serialized = new SerializedObject(home);
                serialized.FindProperty("_researchVolume").objectReferenceValue = researchBox;
                serialized.FindProperty("_returnPortalCollider").objectReferenceValue = portalBox;
                serialized.FindProperty("_returnPortalTransform").objectReferenceValue = portal.transform; serialized.ApplyModifiedPropertiesWithoutUndo();
                var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
                var seed = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1,
                    PlayerId = new string('c', 32), RunId = new string('d', 32), InventoryId = "home-driver", InventoryVersion = 1,
                    CurrencyVersion = 1, GameplayConfigHash = definition.GameplayConfigHash, SelectedWandInstanceId = Wand,
                    Items = new[] { new OnlineOwnedItemSnapshot { InstanceId = Wand, ContentId = "wandtest.wand.a", Revision = 1,
                        LocationKind = 3, Slot = 0, UseTemplateWandStats = true }, new OnlineOwnedItemSnapshot { InstanceId = Card,
                        ContentId = "wandtest.fire", Revision = 1, LocationKind = 2, ParentInstanceId = Wand, Slot = 0, RemainingCharges = 30 } } };
                var driver = Create(out _, assetsOverride: seed, homeWorld: home);
                var workspace = (OnlineOwnedWorkspace)driver.GetType().GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
                Assert.That(workspace.IsPeacefulPractice, Is.True);
                Assert.That(driver, Is.InstanceOf<IOnlineHomeResearchAccess>());
                var access = (IOnlineHomeResearchAccess)driver; Assert.That(access.CanResearch, Is.True);
                _root.transform.position += Vector3.right * 20; Assert.That(access.CanResearch, Is.False);
                Assert.That(driver.GetType().GetMethod("ConfigureHomeWorld").Invoke(driver, new object[] { home }), Is.True);
            }
            finally { Object.DestroyImmediate(map); }
        }
        private static OnlineCastIntent Intent(ulong command = 1) => new OnlineCastIntent { CommandId=command,ConnectionEpoch=1,ProgramRevision=1,WandInstanceId=Wand,
            ClaimedOrigin=new Vector3(23143,34251,12312),AimPoint=new Vector3(23143,34251,12332) };
        private static bool Queue(object driver, OnlineCastIntent intent)
        { object[] args = { intent, null }; return (bool)driver.GetType().GetMethod("TryQueue").Invoke(driver,args); }
        private static OnlineCastResourceSnapshot Resources(object driver)
        { object[] args = { null }; Assert.That((bool)driver.GetType().GetMethod("TryGetAuthorityResources").Invoke(driver,args), Is.True); return (OnlineCastResourceSnapshot)args[0]; }
        private static void Step(object driver, uint tick) => driver.GetType().GetMethod("StepAuthority").Invoke(driver,new object[] {1f/30f,tick});
        [Test] public void ForeignWandAndInvalidOrigin_AreRejectedWithoutOutputOrCharge()
        {
            var driver=Create(out _); var intent=Intent(); intent.WandInstanceId=new string('c',32);
            Assert.That(Queue(driver,intent),Is.False); intent=Intent(2);intent.ClaimedOrigin+=Vector3.right*5;
            Assert.That(Queue(driver,intent),Is.False); Assert.That(_born,Is.Zero); Assert.That(Resources(driver).CurrentMana,Is.EqualTo(100));
        }
        [Test] public void DuplicateRootCommand_OnlyCommitsOneRealOwnedCast()
        {
            var driver=Create(out _); Assert.That(Queue(driver,Intent()),Is.True);
            for(uint tick=1;tick<=15;tick++) Step(driver,tick);
            Assert.That(_born,Is.EqualTo(1)); Assert.That(Resources(driver).RemainingCharges,Is.EqualTo(29));
            Assert.That(Queue(driver,Intent()),Is.True);
            for(uint tick=16;tick<=45;tick++) Step(driver,tick);
            Assert.That(_born,Is.EqualTo(1)); Assert.That(Resources(driver).RemainingCharges,Is.EqualTo(29));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConsecutiveAuthorityCastsRefreshTheFormalInventoryCardCountThroughTheClientReplica(bool throughOwnerPaging)
        {
            // 同时跨过真实支付、Owner快照、Replica安装、InventoryChanged和原Tab视图，
            // 不能用手改RemainingCharges、直接Refresh或更换Source来绕过待调查的更新接缝。
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            // 本例使用当前Owned3完整合法来源；不修改旧fixture的历史前置或把新货币门禁误当施法RED。
            var entry = new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1,
                PlayerId = new string('c', 32), RunId = new string('d', 32),
                InventoryId = new string('d', 32) + "/" + new string('c', 32), InventoryVersion = 1,
                SelectedWandInstanceId = Wand, GameplayConfigHash = definition.GameplayConfigHash, Gold = 0, CurrencyVersion = 1,
                Items = new[]
                {
                    new OnlineOwnedItemSnapshot { InstanceId = Wand, ContentId = "wandtest.wand.a", Revision = 1,
                        LocationKind = 3, Slot = 0, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = Card, ContentId = "wandtest.fire", Revision = 1,
                        LocationKind = 2, ParentInstanceId = Wand, Slot = 0, RemainingCharges = 30 }
                }
            };
            var authority = (OnlineCastDriver)Create(out var assets, assetsOverride: entry);
            GameObject clientRoot = null, uiRoot = null;
            Component ui = null; Font font = null;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                Assert.That(authority.TryCaptureInventory(1, 1, 1, out var initial), Is.True);
                clientRoot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab"));
                clientRoot.SetActive(false);
                var wizard = clientRoot.GetComponent<WizardController>(); wizard.ConfigureExecutionMode(PlayerExecutionMode.Prediction);
                if (wizard.SpellCaster == null) typeof(WizardController).GetMethod("Awake", flags).Invoke(wizard, null);
                var client = clientRoot.AddComponent<OnlineCastDriver>(); client.InitializeClient(1, 1, definition, true);
                var expected = new OnlineInventoryPresentationIdentity(assets.RunId, assets.PlayerId, assets.ActorId,
                    assets.InventoryId, assets.GameplayConfigHash, 1, clientRoot.scene.path);
                // EditMode RunnerScene可能无路径；网络世界实际身份使用非空的已认证Scene键。
                if (string.IsNullOrEmpty(expected.WorldSceneKey)) expected = new OnlineInventoryPresentationIdentity(assets.RunId,
                    assets.PlayerId, assets.ActorId, assets.InventoryId, assets.GameplayConfigHash, 1, "OnlineRoom");
                var ownerRoute = throughOwnerPaging ? new CardCountOwnerRoute(assets, definition.GameplayConfigHash,
                    snapshot => client.CanInstallClientInventory(expected, snapshot, out _)) : null;
                if (ownerRoute != null) initial = ownerRoute.Deliver(initial);
                Assert.That(client.InstallClientInventory(expected, initial, out var error), Is.True, error);
                ownerRoute?.Confirm(initial.SnapshotSequence);

                uiRoot = Object.Instantiate(UnityEngine.Resources.Load<GameObject>("Presentation/CombatInventory"));
                foreach (var component in uiRoot.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component.GetType().FullName == "Game.UI.SinglePlayerInventoryController") ui = component;
                    if (component.GetType().FullName == "Game.UI.ToolbeltView")
                        component.GetType().GetField("_templatesApplied", flags).SetValue(component, true);
                }
                Assert.That(ui, Is.Not.Null);
                var uiType = ui.GetType();
                var source = clientRoot.GetComponent<OnlineInventoryReplica>().Source;
                uiType.GetMethod("BindPresentationSource").Invoke(ui, new object[] { source });
                font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Arial" }, 16);
                uiType.GetProperty("UiFont").SetValue(ui, font);
                uiType.GetProperty("BuiltinUiFont").SetValue(ui, UnityEngine.Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
                uiType.GetMethod("OnEnable", flags).Invoke(ui, null);
                uiType.GetMethod("OpenBackpack").Invoke(ui, null);
                AssertFormalCardCount(uiRoot, Card, "30");

                for (uint cast = 1; cast <= 2; cast++)
                {
                    Assert.That(Queue(authority, Intent(cast)), Is.True);
                    uint firstTick = (cast - 1) * 60 + 1;
                    for (uint tick = firstTick; tick < firstTick + 60; tick++) Step(authority, tick);
                    Assert.That(authority.TryCaptureInventory(1, cast + 1ul, firstTick + 59, out var snapshot), Is.True);
                    Assert.That(snapshot.Assets.InventoryVersion, Is.GreaterThan(initial.Assets.InventoryVersion));
                    if (ownerRoute != null) snapshot = ownerRoute.Deliver(snapshot);
                    Assert.That(client.InstallClientInventory(expected, snapshot, out error), Is.True, error);
                    ownerRoute?.Confirm(snapshot.SnapshotSequence);
                    Assert.That(clientRoot.GetComponent<OnlineInventoryReplica>().Source, Is.SameAs(source));
                    uiType.GetMethod("LateUpdate", flags).Invoke(ui, null);
                    AssertFormalCardCount(uiRoot, Card, (30 - cast).ToString());
                }
            }
            finally
            {
                if (ui != null)
                {
                    ui.GetType().GetMethod("Close").Invoke(ui, null);
                    ui.GetType().GetMethod("OnDisable", flags).Invoke(ui, null);
                    ui.GetType().GetProperty("UiFont").SetValue(ui, null);
                }
                if (uiRoot != null) Object.DestroyImmediate(uiRoot);
                if (font != null) Object.DestroyImmediate(font);
                if (clientRoot != null)
                {
                    var client = clientRoot.GetComponent<OnlineCastDriver>();
                    if (client != null) typeof(OnlineCastDriver).GetMethod("OnDestroy", flags).Invoke(client, null);
                    Object.DestroyImmediate(clientRoot);
                }
            }
        }

        private static void AssertFormalCardCount(GameObject root, string itemId, string expected)
        {
            foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component.GetType().FullName != "Game.UI.InventoryItemView") continue;
                var snapshot = (InventoryItemSnapshot)component.GetType().GetProperty("Snapshot").GetValue(component);
                if (snapshot.InstanceId != itemId) continue;
                var label = (UnityEngine.UI.Text)component.GetType().GetField("_spellCount",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(component);
                Assert.That(label, Is.Not.Null);
                Assert.That(label.text, Is.EqualTo(expected), "原Tab卡片角标必须消费最新安装的Owner事实。");
                Assert.That(label.gameObject.activeSelf, Is.True);
                return;
            }
            Assert.Fail("正式库存未生成当前施法卡实例的原InventoryItemView。");
        }

        private sealed class CardCountOwnerRoute
        {
            private readonly object _codec, _sender, _assembler, _inbox;
            internal CardCountOwnerRoute(OnlineOwnedAssetSnapshot assets, string hash,
                Func<OnlineInventoryPresentationSnapshot, bool> validate)
            {
                _codec = Activator.CreateInstance(RouteType("Game.Networking.InventorySnapshotCodec"));
                _sender = Activator.CreateInstance(RouteType("Game.Networking.InventorySnapshotSender"));
                _assembler = Activator.CreateInstance(RouteType("Game.Networking.InventorySnapshotAssembler"), 1u);
                var identity = Activator.CreateInstance(RouteType("Game.Networking.JoinConnectionIdentity"),
                    assets.PlayerId, "room", assets.RunId, 1u, 1u, assets.ActorId, "p7");
                _inbox = Activator.CreateInstance(RouteType("Game.Multiplayer.InventorySnapshotInbox"), identity, hash, validate);
            }

            internal OnlineInventoryPresentationSnapshot Deliver(OnlineInventoryPresentationSnapshot source)
            {
                var encode = _codec.GetType().GetMethod("TryEncode", new[]
                { typeof(OnlineInventoryPresentationSnapshot), typeof(byte[]).MakeByRefType() });
                object[] encoded = { source, null };
                Assert.That((bool)encode.Invoke(_codec, encoded), Is.True, "真实Owner完整快照必须能编码。");
                Assert.That((bool)_sender.GetType().GetMethod("TryQueue").Invoke(_sender,
                    new object[] { 1u, source.SnapshotSequence, encoded[1] }), Is.True);
                OnlineInventoryPresentationSnapshot delivered = null;
                while (true)
                {
                    object[] peek = { null };
                    if (!(bool)_sender.GetType().GetMethod("TryPeek").Invoke(_sender, peek)) break;
                    object[] assembled = { ThroughMirrorEnvelope(peek[0]), null };
                    Assert.That((bool)_assembler.GetType().GetMethod("TryAccept").Invoke(_assembler, assembled), Is.True);
                    _sender.GetType().GetMethod("Advance").Invoke(_sender, null);
                    var complete = (ArraySegment<byte>)assembled[1];
                    if (complete.Array == null) continue;
                    object[] decoded = { complete, null };
                    Assert.That((bool)_codec.GetType().GetMethod("TryDecode").Invoke(_codec, decoded), Is.True);
                    Assert.That((bool)_inbox.GetType().GetMethod("TryQueue").Invoke(_inbox, new[] { decoded[1] }), Is.True,
                        "身份、目录或安装水位拒绝了真实后续Owner快照。");
                    object[] taken = { null };
                    Assert.That((bool)_inbox.GetType().GetMethod("TryTake").Invoke(_inbox, taken), Is.True);
                    delivered = (OnlineInventoryPresentationSnapshot)taken[0];
                }
                Assert.That(_sender.GetType().GetProperty("BufferedCount").GetValue(_sender), Is.EqualTo(0),
                    "当前队列不等待应用ACK；真实Send后Advance应释放current/pending。");
                Assert.That(delivered, Is.Not.Null);
                return delivered;
            }

            internal void Confirm(ulong sequence)
            {
                Assert.That((bool)_inbox.GetType().GetMethod("ConfirmInstalled").Invoke(_inbox,
                    new object[] { sequence, true }), Is.True);
                _assembler.GetType().GetMethod("Confirm").Invoke(_assembler, new object[] { sequence, true });
                Assert.That(_assembler.GetType().GetProperty("InstalledSequence").GetValue(_assembler), Is.EqualTo(sequence));
            }

            private static object ThroughMirrorEnvelope(object page)
            {
                var type = RouteType("Game.Multiplayer.InventorySnapshotEnvelope");
                var envelope = Activator.CreateInstance(type);
                var pageType = page.GetType();
                foreach (var pair in new[] { ("ConnectionEpoch", "Epoch"), ("SnapshotSequence", "Sequence"),
                    ("TotalBytes", "TotalBytes"), ("PageIndex", "Index"), ("PageCount", "Count"),
                    ("Checksum", "Checksum"), ("Payload", "Payload") })
                    type.GetField(pair.Item1).SetValue(envelope, pageType.GetField(pair.Item2).GetValue(page));
                var serialization = RouteType("Game.Multiplayer.InventorySnapshotEnvelopeSerialization");
                var writer = Activator.CreateInstance(RouteType("Mirror.NetworkWriter"));
                serialization.GetMethod("WriteInventorySnapshotEnvelope").Invoke(null, new[] { writer, envelope });
                var bytes = (ArraySegment<byte>)writer.GetType().GetMethod("ToArraySegment").Invoke(writer, null);
                var reader = Activator.CreateInstance(RouteType("Mirror.NetworkReader"), bytes);
                var restored = serialization.GetMethod("ReadInventorySnapshotEnvelope").Invoke(null, new[] { reader });
                return type.GetProperty("Page").GetValue(restored);
            }

            private static Type RouteType(string name)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                { var type = assembly.GetType(name); if (type != null) return type; }
                Assert.Fail("生产Owner链缺少类型：" + name); return null;
            }
        }
        [Test] public void InsufficientMana_DoesNotSpawnOrConsumeFiniteCharge()
        {
            var driver=Create(out _,true); Queue(driver,Intent());
            for(uint tick=1;tick<=15;tick++) Step(driver,tick);
            Assert.That(_born,Is.Zero); Assert.That(Resources(driver).RemainingCharges,Is.EqualTo(30));
            Assert.That(Resources(driver).CurrentMana,Is.EqualTo(10));
        }
        [Test] public void ReinitializingSameRunAndNewEpoch_DoesNotRefillOwnedWorkspace()
        {
            var driver=Create(out var assets); Queue(driver,Intent()); for(uint tick=1;tick<=12;tick++) Step(driver,tick);
            var before=Resources(driver);
            object[] init={assets,AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset"),new CombatSimulationWorld(),null};
            Assert.That((bool)driver.GetType().GetMethod("InitializeAuthority").Invoke(driver,init),Is.True);
            driver.GetType().GetMethod("SetConnection").Invoke(driver,new object[]{2u,true});
            var after=Resources(driver); Assert.That(after.CurrentMana,Is.EqualTo(before.CurrentMana)); Assert.That(after.RemainingCharges,Is.EqualTo(29));
        }

        [Test] public void ClientInventoryDiagnosticReadsCurrentSourceCopiesValuesAndRejectsOldEpoch()
        {
            var type = typeof(WizardController).Assembly.GetType("Game.Character.OnlineCastDriver");
            var capture = type.GetMethod("TryCaptureClientInventoryDiagnostic");
            Assert.That(capture, Is.Not.Null, "诊断必须从Client已安装Source导出实例值，不能把入局模板当作当前库存。");
            var authority = Create(out _); Queue(authority, Intent());
            for (uint tick = 1; tick <= 12; tick++) Step(authority, tick);
            object[] exported = { 1u, 1ul, 12u, null };
            Assert.That((bool)type.GetMethod("TryCaptureInventory").Invoke(authority, exported), Is.True);
            var actual = (OnlineInventoryPresentationSnapshot)exported[3];
            actual.ConnectionEpoch = 7;
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var clientRoot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab"));
            clientRoot.SetActive(false);
            try
            {
                var wizard = clientRoot.GetComponent<WizardController>(); wizard.ConfigureExecutionMode(PlayerExecutionMode.Prediction);
                if (wizard.SpellCaster == null) typeof(WizardController).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(wizard, null);
                var client = clientRoot.AddComponent(type);
                type.GetMethod("InitializeClient").Invoke(client, new object[] { 1ul, 7u, definition, true });
                var expected = new OnlineInventoryPresentationIdentity(actual.Assets.RunId, actual.Assets.PlayerId, 1,
                    actual.Assets.InventoryId, definition.GameplayConfigHash, 7, "diagnostic-world");
                object[] install = { expected, actual, null };
                Assert.That((bool)type.GetMethod("InstallClientInventory").Invoke(client, install), Is.True, install[2] as string);
                // 资源事实晚于整份库存：诊断必须读当前Source，不能克隆旧snapshot里的Mana。
                uint revision = actual.Wands[0].ProgramRevision;
                type.GetMethod("InstallClientInventoryResources").Invoke(client, new object[] { 7u, 80u, 1u, revision, 17f, .25f });
                object[] read = { 90u, null };
                Assert.That((bool)capture.Invoke(client, read), Is.True);
                var diagnostic = read[1];
                Assert.That(DiagnosticValue(diagnostic, "ActorId"), Is.EqualTo(1ul));
                Assert.That(DiagnosticValue(diagnostic, "ConnectionEpoch"), Is.EqualTo(7u));
                Assert.That(DiagnosticValue(diagnostic, "ProbeSampleTick"), Is.EqualTo(90u));
                Assert.That(DiagnosticValue(diagnostic, "ActualSnapshotTickAvailable"), Is.EqualTo(false));
                Assert.That(DiagnosticValue(diagnostic, "SelectedWandInstanceId"), Is.EqualTo(Wand));
                Assert.That(DiagnosticValue(diagnostic, "Mana"), Is.EqualTo(17f));
                Assert.That(DiagnosticValue(diagnostic, "ProgramRevision"), Is.EqualTo(revision));
                var cards = (Array)DiagnosticValue(diagnostic, "Cards"); Assert.That(cards.Length, Is.EqualTo(1));
                var card = cards.GetValue(0);
                Assert.That(DiagnosticValue(card, "InstanceId"), Is.EqualTo(Card));
                Assert.That(DiagnosticValue(card, "ParentWandInstanceId"), Is.EqualTo(Wand));
                Assert.That(DiagnosticValue(card, "TemplateId"), Is.EqualTo("wandtest.fire"));
                Assert.That(DiagnosticValue(card, "RemainingCharges"), Is.EqualTo(29));
                Assert.That(DiagnosticValue(card, "Unlimited"), Is.EqualTo(false));
                card.GetType().GetField("RemainingCharges").SetValue(card, 999);
                // 返回DTO是独立值副本，改诊断结果不能改下一份读数或实际Source。
                read = new object[] { 91u, null }; Assert.That((bool)capture.Invoke(client, read), Is.True);
                Assert.That(DiagnosticValue(((Array)DiagnosticValue(read[1], "Cards")).GetValue(0), "RemainingCharges"), Is.EqualTo(29));
                type.GetMethod("SetConnection").Invoke(client, new object[] { 8u, true });
                read = new object[] { 92u, null }; Assert.That((bool)capture.Invoke(client, read), Is.False);
                Assert.That(read[1], Is.Null, "新Epoch尚未安装时不能给旧Source换标签制造当前证据。");
                read = new object[] { 92u, null }; Assert.That((bool)capture.Invoke(authority, read), Is.False);
            }
            finally
            {
                var replica = clientRoot.GetComponent<OnlineInventoryReplica>();
                if (replica != null) typeof(OnlineInventoryReplica).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(replica, null);
                var client = clientRoot.GetComponent(type);
                if (client != null) type.GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client, null);
                Object.DestroyImmediate(clientRoot);
            }
        }

        [Test] public void AuthorityInventoryDiagnosticMapsConsumedInstanceValuesWithoutChangingWorkspace()
        {
            var diagnosticType = typeof(WizardController).Assembly.GetType("Game.Character.OnlineCastInventoryDiagnosticSnapshot");
            Assert.That(diagnosticType, Is.Not.Null, "DS和Client应共享纯值诊断格式，不能让DS依赖仅Client安装的Probe。");
            var map = diagnosticType.GetMethod("TryFromAuthoritySnapshot"); Assert.That(map, Is.Not.Null);
            var driver = Create(out _); Queue(driver, Intent()); for (uint tick = 1; tick <= 12; tick++) Step(driver, tick);
            var before = Resources(driver);
            object[] export = { 1u, 1ul, 12u, null };
            Assert.That((bool)driver.GetType().GetMethod("TryCaptureInventory").Invoke(driver, export), Is.True);
            object[] args = { export[3], 90u, null }; Assert.That((bool)map.Invoke(null, args), Is.True);
            var result = args[2];
            Assert.That(DiagnosticValue(result, "ProbeSampleTick"), Is.EqualTo(90u));
            Assert.That(DiagnosticValue(result, "ActualSnapshotTickAvailable"), Is.EqualTo(true));
            Assert.That(DiagnosticValue(result, "ActualSnapshotTick"), Is.EqualTo(12u));
            Assert.That(DiagnosticValue(result, "ConnectionEpoch"), Is.EqualTo(1u));
            Assert.That(DiagnosticValue(result, "SelectedWandInstanceId"), Is.EqualTo(Wand));
            var card = ((Array)DiagnosticValue(result, "Cards")).GetValue(0);
            Assert.That(DiagnosticValue(card, "InstanceId"), Is.EqualTo(Card));
            Assert.That(DiagnosticValue(card, "RemainingCharges"), Is.EqualTo(29));
            card.GetType().GetField("RemainingCharges").SetValue(card, 999);
            args = new object[] { export[3], 91u, null }; Assert.That((bool)map.Invoke(null, args), Is.True);
            Assert.That(DiagnosticValue(((Array)DiagnosticValue(args[2], "Cards")).GetValue(0), "RemainingCharges"), Is.EqualTo(29));
            var after = Resources(driver);
            Assert.That(after.CurrentMana, Is.EqualTo(before.CurrentMana)); Assert.That(after.RemainingCharges, Is.EqualTo(29));
        }

        private static object DiagnosticValue(object value, string name)
        {
            Assert.That(value, Is.Not.Null); var field = value.GetType().GetField(name);
            Assert.That(field, Is.Not.Null, "诊断缺少明确值字段：" + name); return field.GetValue(value);
        }

        [TestCase(172)]
        [TestCase(4096)]
        public void AuthorityDiagnosticKeepsEveryCarriedItemAndExactComponentsWithoutCallingNonSpellsCards(int count)
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var source = ExtendedDiagnosticSnapshot(definition, count);
            Assert.That(OnlineCastInventoryDiagnosticSnapshot.TryFromAuthoritySnapshot(source, 90, out var diagnostic), Is.True);
            Assert.That(DiagnosticValue(diagnostic, "TotalItemCount"), Is.EqualTo(count));
            var items = (Array)DiagnosticValue(diagnostic, "Items"); Assert.That(items.Length, Is.EqualTo(count));
            Assert.That(diagnostic.Cards.Length, Is.EqualTo(count - 4), "包、工具瓶和备用杖不属于法术卡。");
            var bottle = DiagnosticItem(diagnostic, "validation-bottle");
            Assert.That(DiagnosticValue(bottle, "LocationKind"), Is.EqualTo(8));
            Assert.That(DiagnosticValue(bottle, "OwnerId"), Is.EqualTo("validation-owner"));
            Assert.That(DiagnosticValue(bottle, "HasContainerState"), Is.EqualTo(true));
            Assert.That(DiagnosticValue(bottle, "ContainerTotal"), Is.EqualTo(1090519040ul));
            Assert.That(DiagnosticValue(bottle, "ContainerCapacityAvailable"), Is.EqualTo(false), "Authority DTO未携带容量，诊断不能猜测。");
            var composition = (MaterialAmount[])DiagnosticValue(bottle, "Components");
            Assert.That(composition[0].MaterialKey, Is.EqualTo(1)); Assert.That(composition[0].Quantity, Is.EqualTo(16777217ul));
            Assert.That(composition[1].MaterialKey, Is.EqualTo(3)); Assert.That(composition[1].Quantity, Is.EqualTo(1073741823ul));
            var spare = DiagnosticItem(diagnostic, "diagnostic-spare");
            Assert.That(DiagnosticValue(spare, "OwnerId"), Is.EqualTo("diagnostic-bag"));
            Assert.That(DiagnosticValue(spare, "X"), Is.EqualTo(1)); Assert.That(DiagnosticValue(spare, "Y"), Is.EqualTo(2));
            Assert.That(DiagnosticValue(spare, "QuarterTurns"), Is.EqualTo(1));
            composition[0].Quantity = 999;
            Assert.That(source.Assets.Items[2].ContainerState.Components[0].Quantity, Is.EqualTo(16777217ul));
            Assert.That(OnlineCastInventoryDiagnosticSnapshot.TryFromAuthoritySnapshot(source, 91, out var again), Is.True);
            Assert.That(((MaterialAmount[])DiagnosticValue(DiagnosticItem(again, "validation-bottle"), "Components"))[0].Quantity, Is.EqualTo(16777217ul));
            Assert.That(source.Assets.Items[1].RemainingCharges, Is.EqualTo(3)); Assert.That(source.Wands[0].CurrentMana, Is.EqualTo(37f));
        }

        [Test]
        public void ClientDiagnosticCopiesTheFormalEightByEightCarriedSourceIncludingToolsSpareAndContainers()
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            try
            {
                _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab"));
                _root.SetActive(false);
                var wizard = _root.GetComponent<WizardController>(); wizard.ConfigureExecutionMode(PlayerExecutionMode.Prediction);
                if (wizard.SpellCaster == null) typeof(WizardController).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(wizard, null);
                var driver = _root.AddComponent<OnlineCastDriver>(); driver.InitializeClient(1, 7, definition, true);
                var source = FormalCarriedDiagnosticSnapshot(definition);
                Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), source, out var error), Is.True, error);
                Assert.That(driver.TryCaptureClientInventoryDiagnostic(90, out var diagnostic), Is.True);
                Assert.That(DiagnosticValue(diagnostic, "TotalItemCount"), Is.EqualTo(665));
                Assert.That(((Array)DiagnosticValue(diagnostic, "Items")).Length, Is.EqualTo(665));
                Assert.That(diagnostic.Cards.Length, Is.EqualTo(641));
                var capturedBottle = DiagnosticItem(diagnostic, "validation-bottle");
                Assert.That(DiagnosticValue(capturedBottle, "ContainerCapacityAvailable"), Is.EqualTo(true));
                Assert.That(DiagnosticValue(capturedBottle, "ContainerCapacity"), Is.EqualTo(15000ul));
                Assert.That(DiagnosticValue(capturedBottle, "ContainerTotal"), Is.EqualTo(150ul));
                var empty = DiagnosticItem(diagnostic, "diagnostic-empty");
                Assert.That(DiagnosticValue(empty, "HasContainerState"), Is.EqualTo(true));
                Assert.That((MaterialAmount[])DiagnosticValue(empty, "Components"), Is.Empty);
                Assert.That(DiagnosticValue(empty, "ContainerTotal"), Is.EqualTo(0ul));
                Assert.That(DiagnosticValue(DiagnosticItem(diagnostic, "diagnostic-bag"), "HasContainerState"), Is.EqualTo(false));
                var components = (MaterialAmount[])DiagnosticValue(capturedBottle, "Components"); components[0].Quantity = 999;
                Assert.That(driver.TryCaptureClientInventoryDiagnostic(91, out var again), Is.True);
                Assert.That(((MaterialAmount[])DiagnosticValue(DiagnosticItem(again, "validation-bottle"), "Components"))[0].Quantity, Is.EqualTo(100ul));
                Assert.That(driver.ClientInventoryMana, Is.EqualTo(37f)); Assert.That(_born, Is.Zero);
                driver.SetConnection(8, true);
                Assert.That(driver.TryCaptureClientInventoryDiagnostic(92, out _), Is.False);
            }
            finally
            {
                if (_root != null)
                {
                    var replica = _root.GetComponent<OnlineInventoryReplica>();
                    if (replica != null) typeof(OnlineInventoryReplica).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(replica, null);
                    var driver = _root.GetComponent<OnlineCastDriver>();
                    if (driver != null) typeof(OnlineCastDriver).GetMethod("OnDestroy", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);
                    Object.DestroyImmediate(_root); _root = null;
                }
            }
        }

        [Test]
        public void AuthorityDiagnosticRejectsOver4096ItemsInsteadOfTruncatingTheInventory()
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(OnlineCastInventoryDiagnosticSnapshot.TryFromAuthoritySnapshot(ExtendedDiagnosticSnapshot(definition, 4097), 90, out var diagnostic), Is.False);
            Assert.That(diagnostic, Is.Null);
        }

        private static object DiagnosticItem(object diagnostic, string id)
        {
            foreach (var item in (Array)DiagnosticValue(diagnostic, "Items"))
                if ((string)DiagnosticValue(item, "InstanceId") == id) return item;
            Assert.Fail("诊断漏掉真实实例：" + id); return null;
        }
        private static OnlineInventoryPresentationSnapshot FormalCarriedDiagnosticSnapshot(OnlineCombatDefinition definition)
        {
            var value = ValidationSnapshot(definition);
            var items = new List<OnlineOwnedItemSnapshot>(value.Assets.Items);
            var uses = new List<OnlineInventorySpellUsePresentationSnapshot>(value.SpellUses);
            var resources = new List<OnlineInventoryWandPresentationSnapshot>(value.Wands);
            items.Add(new OnlineOwnedItemSnapshot { InstanceId = "diagnostic-bag", ContentId = "wandtest.bag", Revision = 1, LocationKind = 4 });
            items.Add(new OnlineOwnedItemSnapshot { InstanceId = "diagnostic-empty", ContentId = "containertest.bottle", Revision = 1,
                LocationKind = 1, ParentInstanceId = "diagnostic-bag", Slot = -1, X = 6, Y = 6,
                ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = Array.Empty<OnlineOwnedMaterialAmountSnapshot>() } });
            for (int wand = 0; wand < 20; wand++)
            {
                string id = "diagnostic-spare." + wand;
                // 正式8x8中先放16根横杖，右侧两列再竖放4根，(6,6)保留给1x1空瓶。
                items.Add(new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = "wandtest.wand.a", Revision = 1,
                    LocationKind = 1, ParentInstanceId = "diagnostic-bag", Slot = -1,
                    X = wand < 16 ? wand % 2 * 3 : 6 + (wand - 16) % 2,
                    Y = wand < 16 ? wand / 2 : (wand - 16) / 2 * 3, QuarterTurns = wand < 16 ? 0 : 1,
                    WandStats = new OnlineOwnedWandStatsSnapshot { MaxMana = 150, ManaRegenPerSecond = 5,
                        BaseCastDelay = .2f, BaseReloadTime = .4f, ProgramCapacity = 32, BaseDraws = 1, AlwaysCastContentIds = Array.Empty<string>() } });
                // 新完整合同要求备用杖同样有权威资源来源，不能只补物品壳后让Client猜成满Mana/rev1。
                resources.Add(new OnlineInventoryWandPresentationSnapshot
                { InstanceId = id, ProgramRevision = (uint)wand + 20, CurrentMana = 73, CooldownRemaining = .17f });
                for (int slot = 0; slot < 32; slot++)
                {
                    string card = id + ".card." + slot;
                    items.Add(new OnlineOwnedItemSnapshot { InstanceId = card, ContentId = "wandtest.fire", Revision = 1,
                        LocationKind = 2, ParentInstanceId = id, Slot = slot, RemainingCharges = 3 });
                    uses.Add(new OnlineInventorySpellUsePresentationSnapshot { InstanceId = card });
                }
            }
            value.Assets.Items = items.ToArray(); value.SpellUses = uses.ToArray(); value.Wands = resources.ToArray(); return value;
        }

        private static OnlineInventoryPresentationSnapshot ExtendedDiagnosticSnapshot(OnlineCombatDefinition definition, int count)
        {
            var value = ValidationSnapshot(definition);
            value.Assets.Items[2].ContainerState.Components = new[]
            { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 16777217 },
                new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 3, Quantity = 1073741823 } };
            Array.Resize(ref value.Assets.Items, count);
            value.Assets.Items[3] = new OnlineOwnedItemSnapshot { InstanceId = "diagnostic-bag", ContentId = "wandtest.bag", Revision = 1,
                LocationKind = 4, Slot = 0 };
            value.Assets.Items[4] = new OnlineOwnedItemSnapshot { InstanceId = "diagnostic-spare", ContentId = "wandtest.wand.a", Revision = 1,
                LocationKind = 1, ParentInstanceId = "diagnostic-bag", Slot = -1, X = 1, Y = 2, QuarterTurns = 1, UseTemplateWandStats = true };
            value.Assets.Items[5] = new OnlineOwnedItemSnapshot { InstanceId = "diagnostic-spare-card", ContentId = "wandtest.fire", Revision = 1,
                LocationKind = 2, ParentInstanceId = "diagnostic-spare", Slot = 0, RemainingCharges = 7 };
            value.SpellUses = new OnlineInventorySpellUsePresentationSnapshot[count - 4];
            value.SpellUses[0] = new OnlineInventorySpellUsePresentationSnapshot { InstanceId = Card, Eligible = true };
            value.SpellUses[1] = new OnlineInventorySpellUsePresentationSnapshot { InstanceId = "diagnostic-spare-card", Eligible = false };
            for (int i = 6; i < count; i++)
            {
                string id = "diagnostic-cargo." + (i - 6);
                value.Assets.Items[i] = new OnlineOwnedItemSnapshot { InstanceId = id, ContentId = "wandtest.fire", Revision = 1,
                    LocationKind = 1, ParentInstanceId = "diagnostic-bag", Slot = -1, X = i - 6, RemainingCharges = 3 };
                value.SpellUses[i - 4] = new OnlineInventorySpellUsePresentationSnapshot { InstanceId = id };
            }
            return value;
        }

        [Test]
        public void StaticClientInventoryValidationAcceptsLegalCatalogAndComponentsWithoutCreatingAnActorOrAuthoritySubscriptions()
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var snapshot = ValidationSnapshot(definition);
            var expected = ValidationIdentity(definition);
            int responses = 0; Action<CastResourceResultEvent> receive = _ => responses++;
            EventBus<CastResourceResultEvent>.Subscribe(receive);
            try
            {
                Assert.That(ValidateClientInventory(definition, expected, snapshot, out var error), Is.True, error);
                EventBus<CastResourceRequestedEvent>.Publish(new CastResourceRequestedEvent
                { InventoryId = "validation-inventory", SourceWandId = Wand, CallerId = "prequeue", RequestId = 1, UseV2 = true });
                Assert.That(responses, Is.Zero, "预队列校验不能通过Authority Factory创建施法订阅。");
                Assert.That(_root == null, Is.True, "验证在没有Actor/Replica组件时也必须可执行。");
                Assert.That(_born, Is.Zero);
                Assert.That(snapshot.Wands[0].CurrentMana, Is.EqualTo(37f));
                Assert.That(snapshot.Assets.Items[1].RemainingCharges, Is.EqualTo(3));
                Assert.That(snapshot.Assets.Items[2].ContainerState.Components[0].Quantity, Is.EqualTo(100ul));
            }
            finally { EventBus<CastResourceResultEvent>.Unsubscribe(receive); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StaticClientInventoryValidationRejectsUnknownContentOrForbiddenComponentsWithoutChangingExistingResources(bool badComponent)
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var expected = ValidationIdentity(definition);
            Assert.That(definition.Catalog.TryCreateIndex(out var catalog, out var catalogError), Is.True, catalogError);
            Assert.That(OnlineInventoryPresentationSource.TryCreate(catalog, expected, ValidationSnapshot(definition), out var source, out var sourceError), Is.True, sourceError);
            using (source)
            {
                var bad = ValidationSnapshot(definition); bad.SnapshotSequence = 999;
                if (badComponent) bad.Assets.Items[2].ContainerState.Components[0].MaterialKey = 2;
                else bad.Assets.Items[2].ContentId = "unknown.content.from.bad.packet";
                Assert.That(ValidateClientInventory(definition, expected, bad, out var error), Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(source.SnapshotSequence, Is.EqualTo(1ul));
                Assert.That(source.WandsReadOnly.TryGetState(Wand, out var wand), Is.True);
                Assert.That(wand.CurrentMana, Is.EqualTo(37f));
                Assert.That(source.ReadOnly.TryGetView(Card, out var card), Is.True);
                Assert.That(card.RemainingCharges, Is.EqualTo(3));
                Assert.That(source.TryCopyContainer("validation-bottle", new MaterialAmount[2], out _, out var total, out _), Is.True);
                Assert.That(total, Is.EqualTo(150ul));
                // 坏高序号没有安装或提交任何水位；下一份正常低序号仍能通过纯领域验证。
                Assert.That(ValidateClientInventory(definition, expected, ValidationSnapshot(definition), out error), Is.True, error);
                Assert.That(_root == null, Is.True); Assert.That(_born, Is.Zero);
            }
        }

        private static bool ValidateClientInventory(OnlineCombatDefinition definition, OnlineInventoryPresentationIdentity expected,
            OnlineInventoryPresentationSnapshot snapshot, out string error)
        {
            var method = typeof(OnlineCastDriver).GetMethod("ValidateClientInventory", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "Inbox候选排序前需要无需Actor的完整目录/空间/组分校验Facade。");
            object[] args = { definition, expected, snapshot, null };
            bool accepted = (bool)method.Invoke(null, args); error = (string)args[3]; return accepted;
        }
        private static OnlineInventoryPresentationIdentity ValidationIdentity(OnlineCombatDefinition definition)
            => new OnlineInventoryPresentationIdentity("validation-run", "validation-owner", 1, "validation-inventory",
                definition.GameplayConfigHash, 7, "validation-world");
        private static OnlineInventoryPresentationSnapshot ValidationSnapshot(OnlineCombatDefinition definition)
            => new OnlineInventoryPresentationSnapshot
            {
                SchemaVersion = OnlineInventoryPresentationSnapshot.CurrentSchemaVersion, ConnectionEpoch = 7,
                SnapshotSequence = 1, ServerTick = 40, ResourceVersion = 12, SelectionVersion = 5, SpellUseVersion = 4,
                Assets = new OnlineOwnedAssetSnapshot
                {
                    SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, RunId = "validation-run", PlayerId = "validation-owner",
                    ActorId = 1, InventoryId = "validation-inventory", GameplayConfigHash = definition.GameplayConfigHash,
                    // 资源/选择/次数版本保留本例原有非零事实，钱包另有独立的存在性修订。
                    InventoryVersion = 9, Gold = 0, CurrencyVersion = 1, SelectedWandInstanceId = Wand,
                    Items = new[]
                    {
                        new OnlineOwnedItemSnapshot { InstanceId = Wand, ContentId = "wandtest.wand.a", Revision = 1,
                            LocationKind = 3, Slot = 0, UseTemplateWandStats = true },
                        new OnlineOwnedItemSnapshot { InstanceId = Card, ContentId = "wandtest.fire", Revision = 1,
                            LocationKind = 2, Slot = 0, ParentInstanceId = Wand, RemainingCharges = 3 },
                        new OnlineOwnedItemSnapshot { InstanceId = "validation-bottle", ContentId = "containertest.bottle", Revision = 1,
                            LocationKind = 8, Slot = 0, ContainerState = new OnlineOwnedContainerStateSnapshot
                            { SchemaVersion = 1, Components = new[] { new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 1, Quantity = 100 },
                                new OnlineOwnedMaterialAmountSnapshot { MaterialKey = 3, Quantity = 50 } } } }
                    }
                },
                Wands = new[] { new OnlineInventoryWandPresentationSnapshot
                    { InstanceId = Wand, ProgramRevision = 17, CurrentMana = 37, CooldownRemaining = .25f } },
                SpellUses = new[] { new OnlineInventorySpellUsePresentationSnapshot { InstanceId = Card, Eligible = true } }
            };

        [Test] public void DeadActorCancelsDueSchedulerBeforeItCanRelease()
        {
            var driver = Create(out _);
            var caster = _root.GetComponent<SpellCaster>();
            var scheduler = (AuthorityCastScheduler)typeof(SpellCaster).GetField("_authorityScheduler", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(caster);
            int released = 0;
            Assert.That(scheduler.TrySchedule(.01f, 1, _ => released++, out _), Is.True);
            var health = _root.GetComponent<HealthComponent>();
            var hit = new DamageRequest(99, 2, 10000, DamageType.True, _root.transform.position, Vector3.forward);
            health.ReceiveHit(in hit);
            Assert.That(health.IsAlive, Is.False);
            Step(driver, 1);
            Assert.That(released, Is.Zero, "先检查死亡再推进 deadline；上一 Physics Tick 死亡不能留下最后一发。");
        }

        [Test] public void QueuedCastRejectsInventoryEditAndRetryCannotBecomeSuccessfulLater()
        {
            var driver = Create(out _);
            var method = driver.GetType().GetMethod("ExecuteInventoryCommand");
            Assert.That(method, Is.Not.Null, "库存写入必须经过Character动作/生存状态门，不能只调用Run。 ");
            object intent = InventoryIntent();
            Assert.That(Queue(driver, Intent()), Is.True);
            object result = method.Invoke(driver, new[] { intent, Enum.ToObject(method.GetParameters()[1].ParameterType, 0) });
            Assert.That(result.GetType().GetField("Failure").GetValue(result).ToString(), Is.EqualTo("Busy"));
            driver.GetType().GetMethod("CancelPending", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);
            object retry = method.Invoke(driver, new[] { intent, Enum.ToObject(method.GetParameters()[1].ParameterType, 0) });
            Assert.That(retry.GetType().GetField("Failure").GetValue(retry).ToString(), Is.EqualTo("Busy"), "Busy也是本命令的最终回执；不能稍后用同ID更改程序。 ");
        }

        [Test] public void DeadAuthorityRejectsInventoryEditWithoutMovingOwnedCard()
        {
            var driver = Create(out _);
            var method = driver.GetType().GetMethod("ExecuteInventoryCommand");
            Assert.That(method, Is.Not.Null);
            var health = _root.GetComponent<HealthComponent>();
            var hit = new DamageRequest(99, 2, 10000, DamageType.True, _root.transform.position, Vector3.forward); health.ReceiveHit(in hit);
            object result = method.Invoke(driver, new[] { InventoryIntent(), Enum.ToObject(method.GetParameters()[1].ParameterType, 0) });
            Assert.That(result.GetType().GetField("Failure").GetValue(result).ToString(), Is.EqualTo("NotReady"));
            Assert.That(Resources(driver).RemainingCharges, Is.EqualTo(30));
        }

        private static object InventoryIntent()
        {
            var type = typeof(OnlineOwnedAssetSnapshot).Assembly.GetType("Game.Core.OnlineInventoryCommandIntent");
            Assert.That(type, Is.Not.Null);
            object intent = Activator.CreateInstance(type);
            type.GetField("CommandId").SetValue(intent, 1ul); type.GetField("ConnectionEpoch").SetValue(intent, 1u);
            type.GetField("ExpectedInventoryVersion").SetValue(intent, 1u);
            type.GetField("Kind").SetValue(intent, Enum.ToObject(type.GetField("Kind").FieldType, 2));
            type.GetField("ItemId").SetValue(intent, Card); type.GetField("SourceWandId").SetValue(intent, Wand);
            type.GetField("SourceProgramRevision").SetValue(intent, 1u); type.GetField("ToSpellStorage").SetValue(intent, true);
            return intent;
        }


        [Test]
        public void AuthorityRejectsTheOldWandIntentAfterSelectingAToolWithoutTimelineOrConsumption()
        {
            var driver = (OnlineCastDriver)Create(out _, includeContainer: true);
            var before = Resources(driver);
            var selection = new OnlineInventoryCommandIntent
            {
                CommandId = 51, ConnectionEpoch = 1, ExpectedInventoryVersion = driver.OwnedInventoryVersion,
                Kind = OnlineInventoryCommandKind.SelectTool, ItemId = "selection-authority-bottle", TargetSlot = 0
            };
            Assert.That(driver.ExecuteInventoryCommand(selection).Success, Is.True);
            Assert.That(driver.TryQueue(Intent(), out var receipt), Is.False,
                "工具选中时即使客户端伪造最后法杖ID，也必须在启动施法Timeline前拒绝。");
            Assert.That(receipt.Failure, Is.EqualTo(OnlineCastFailure.NotOwned));
            Assert.That((bool)typeof(OnlineCastDriver).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(driver), Is.False);
            for (uint tick = 1; tick <= 15; tick++) Step(driver, tick);
            Assert.That(_born, Is.Zero);
            Assert.That(Resources(driver).RemainingCharges, Is.EqualTo(before.RemainingCharges));
            Assert.That(Resources(driver).CurrentMana, Is.EqualTo(before.CurrentMana));
        }

        // 线上选择只发送意图：Fixture 安装真实 Catalog 校验过的 Source，
        // 断言不能依靠本地改选中项制造“服务器已确认”的假象。
        private OnlineCastDriver CreateSelectionClient(out OnlineInventoryPresentationSnapshot snapshot,
            bool localOwner = true, bool install = true, int toolSlot = 0, bool toolSelected = false)
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            _root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab"));
            _root.SetActive(false);
            var wizard = _root.GetComponent<WizardController>();
            wizard.ConfigureExecutionMode(PlayerExecutionMode.Prediction);
            if (wizard.SpellCaster == null)
                typeof(WizardController).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(wizard, null);
            var health = _root.GetComponent<HealthComponent>();
            if (health.CurrentHp <= 0)
                typeof(HealthComponent).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(health, null);
            var driver = _root.AddComponent<OnlineCastDriver>();
            driver.InitializeClient(1, 7, definition, localOwner);
            snapshot = ValidationSnapshot(definition);
            snapshot.Assets.Items[2].Slot = toolSlot;
            snapshot.SelectedToolSlotPlusOne = toolSelected ? (uint)toolSlot + 1 : 0;
            if (install)
                Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            return driver;
        }

        private static bool RequestTool(OnlineCastDriver driver, int slot)
        {
            var request = typeof(OnlineCastDriver).GetMethod("RequestToolSelection");
            Assert.That(request, Is.Not.Null, "线上数字4–0需要与选杖一致的工具选择意图入口。");
            return (bool)request.Invoke(driver, new object[] { slot });
        }

        private void PrepareFocusedSelection(OnlineCastDriver driver)
        {
            // Request入口保留真实Application.isFocused门；测试必须建立GameView焦点，不能绕门制造成功。
            // EditMode inactive fixture不会自动收Focus生命周期，像现有Focus测试一样明确交付正边沿与World门。
            _selectionPreviousWindow = EditorWindow.focusedWindow;
            var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            Assert.That(gameViewType, Is.Not.Null);
            EditorWindow.GetWindow(gameViewType).Focus();
            typeof(OnlineCastDriver).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(driver, new object[] { true });
            driver.SetBlocked(false);
            Assert.That(driver.Ready, Is.True, "成功请求fixture必须是完成程序装配的真实Ready角色。");
            Assert.That(driver.ClientInventoryReady, Is.True, "成功请求fixture必须装好当前Epoch的Source。");
            Assert.That(_root.GetComponent<WizardController>().Health.IsAlive, Is.True);
            Assert.That(Time.timeScale, Is.GreaterThan(0));
            Assert.That(Application.isFocused, Is.True,
                "成功请求fixture需要真实Editor/GameView焦点；无焦点应拒绝请求，不能放宽生产门禁。");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void ToolSelectionRequestsTheBoundInstanceAndInstalledVersionWithoutChangingSource(int slot)
        {
            var driver = CreateSelectionClient(out _, toolSlot: slot);
            PrepareFocusedSelection(driver);
            var source = _root.GetComponent<OnlineInventoryReplica>().Source;
            int count = 0; OnlineInventoryCommandRequestedEvent observed = default;
            Action<OnlineInventoryCommandRequestedEvent> listener = value => { count++; observed = value; };
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                Assert.That(RequestTool(driver, slot), Is.True);
                Assert.That(count, Is.EqualTo(1));
                Assert.That(observed.ActorId, Is.EqualTo(1ul));
                Assert.That(observed.CallerId, Is.EqualTo(driver.GetInstanceID()));
                Assert.That(observed.Intent.CommandId, Is.Not.Zero);
                Assert.That(observed.Intent.ConnectionEpoch, Is.EqualTo(7u));
                Assert.That(observed.Intent.ExpectedInventoryVersion, Is.EqualTo(9u));
                Assert.That(observed.Intent.Kind.ToString(), Is.EqualTo("SelectTool"));
                Assert.That(observed.Intent.ItemId, Is.EqualTo("validation-bottle"));
                Assert.That(observed.Intent.TargetSlot, Is.EqualTo(slot));
                Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(0));
                Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo(Wand));
                Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
                Assert.That(source.ToolbeltReadOnly.SelectionVersion, Is.EqualTo(5u));
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        [TestCase(-1)] [TestCase(1)] [TestCase(7)]
        public void ToolSelectionRejectsOutOfRangeOrUnboundSlotsWithoutPublishing(int slot)
        {
            var driver = CreateSelectionClient(out _);
            int count = 0;
            Action<OnlineInventoryCommandRequestedEvent> listener = _ => count++;
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try { Assert.That(RequestTool(driver, slot), Is.False); Assert.That(count, Is.Zero); }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        [Test]
        public void ConfirmedToolSelectionDoesNotPublishTheSameToolAgain()
        {
            var driver = CreateSelectionClient(out _, toolSlot: 6, toolSelected: true);
            int count = 0;
            Action<OnlineInventoryCommandRequestedEvent> listener = _ => count++;
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                Assert.That(RequestTool(driver, 6), Is.False);
                Assert.That(count, Is.Zero);
                Assert.That(_root.GetComponent<OnlineInventoryReplica>().Source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(9));
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        [Test]
        public void ToolSelectionCanRequestTheSameLastWandWithoutClearingSourceBeforeConfirmation()
        {
            var driver = CreateSelectionClient(out _, toolSelected: true);
            var source = _root.GetComponent<OnlineInventoryReplica>().Source;
            PrepareFocusedSelection(driver);
            Assert.That(source.WandsReadOnly.SelectedWandId, Is.EqualTo(Wand));
            int count = 0; OnlineInventoryCommandRequestedEvent observed = default;
            Action<OnlineInventoryCommandRequestedEvent> listener = value => { count++; observed = value; };
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                Assert.That(driver.RequestWandSelection(0), Is.True, "选工具保留最后法杖ID，不能用ID相等拒绝切回。");
                Assert.That(count, Is.EqualTo(1));
                Assert.That(observed.Intent.Kind, Is.EqualTo(OnlineInventoryCommandKind.SelectWand));
                Assert.That(observed.Intent.ItemId, Is.EqualTo(Wand));
                Assert.That(observed.Intent.TargetSlot, Is.Zero);
                Assert.That(observed.Intent.ExpectedInventoryVersion, Is.EqualTo(9u));
                Assert.That(source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(3));
                Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("validation-bottle"));
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        [TestCase("remote")] [TestCase("authority")] [TestCase("not-ready")]
        [TestCase("disconnected")] [TestCase("stale-epoch")] [TestCase("suppressed")]
        [TestCase("dead")] [TestCase("paused")] [TestCase("unfocused")]
        public void ToolSelectionRequiresTheLiveReadyFocusedLocalOwner(string condition)
        {
            OnlineCastDriver driver;
            if (condition == "authority") driver = (OnlineCastDriver)Create(out _);
            else driver = CreateSelectionClient(out _, localOwner: condition != "remote",
                install: condition != "remote" && condition != "not-ready");
            float previousTimeScale = Time.timeScale;
            int count = 0;
            Action<OnlineInventoryCommandRequestedEvent> listener = _ => count++;
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                if (condition == "disconnected") driver.SetConnection(7, false);
                if (condition == "stale-epoch") driver.SetConnection(8, true);
                if (condition == "suppressed") driver.SetBlocked(true);
                if (condition == "dead")
                {
                    var damage = new DamageRequest(99, 2, 10000, DamageType.True, _root.transform.position, Vector3.forward);
                    _root.GetComponent<HealthComponent>().ReceiveHit(in damage);
                }
                if (condition == "paused") Time.timeScale = 0;
                if (condition == "unfocused")
                {
                    typeof(OnlineCastDriver).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(driver, new object[] { false });
                    // Gameplay每帧可重新计算suppressed；窗口失焦本身仍必须独立阻止新意图。
                    driver.SetBlocked(false);
                }
                Assert.That(RequestTool(driver, 0), Is.False);
                Assert.That(count, Is.Zero);
            }
            finally
            {
                Time.timeScale = previousTimeScale;
                EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener);
            }
        }

        [Test]
        public void ConfirmedToolSelectionDoesNotQueueOrPredictAWandCast()
        {
            var driver = CreateSelectionClient(out _, toolSelected: true);
            int casts = 0; driver.IntentProduced += _ => casts++;
            driver.CaptureAttackPress();
            Assert.That((bool)typeof(OnlineCastDriver).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(driver), Is.False, "工具左键不能进入原法杖施法缓冲。");
            driver.StepPrediction(.01f, 41, false);
            Assert.That(casts, Is.Zero);
            Assert.That(_born, Is.Zero);
        }

        [Test]
        public void ConfirmedToolSelectionCancelsAnAlreadyBufferedWandPrediction()
        {
            var driver = CreateSelectionClient(out var snapshot);
            int casts = 0; driver.IntentProduced += _ => casts++;
            // 不依赖设备或相机：直接建立CaptureAttackPress原本创建的有限缓冲，测试服务器换选后不得漏发。
            typeof(OnlineCastDriver).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(driver, true);
            typeof(OnlineCastDriver).GetField("_queueRemaining", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(driver, 1f);
            typeof(OnlineCastDriver).GetField("_waitRelease", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(driver, false);
            snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.SelectionVersion++;
            snapshot.SelectedToolSlotPlusOne = 1;
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            driver.StepPrediction(.01f, 42, false);
            Assert.That(casts, Is.Zero);
            Assert.That((bool)typeof(OnlineCastDriver).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(driver), Is.False);
            Assert.That(_born, Is.Zero);
        }

        private static void ConfirmEmptyHands(OnlineInventoryPresentationSnapshot snapshot, bool keepWandInBackpack)
        {
            snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.Assets.InventoryVersion++; snapshot.SelectionVersion++;
            snapshot.Assets.SelectedWandInstanceId = string.Empty; snapshot.SelectedToolSlotPlusOne = 0;
            if (keepWandInBackpack)
            {
                var items = new List<OnlineOwnedItemSnapshot>(snapshot.Assets.Items);
                items.Add(new OnlineOwnedItemSnapshot
                { InstanceId = "empty-hands-bag", ContentId = "wandtest.bag", Revision = 1, LocationKind = (int)ItemLocationKind.EquippedBackpack });
                var wand = items[0]; wand.LocationKind = (int)ItemLocationKind.Backpack;
                wand.ParentInstanceId = "empty-hands-bag"; wand.Slot = -1;
                snapshot.Assets.Items = items.ToArray();
            }
            else
            {
                snapshot.Assets.Items = new[] { snapshot.Assets.Items[2] };
                snapshot.Wands = Array.Empty<OnlineInventoryWandPresentationSnapshot>();
                snapshot.SpellUses = Array.Empty<OnlineInventorySpellUsePresentationSnapshot>();
            }
        }

        [TestCase(false)] [TestCase(true)]
        public void ConfirmedEmptyHandsClearsTheOldWandCooldownAndPendingAnimationWithoutProducingACast(bool keepWandInBackpack)
        {
            var driver = CreateSelectionClient(out var snapshot);
            PrepareFocusedSelection(driver);
            var wizard = _root.GetComponent<WizardController>();
            int casts = 0; driver.IntentProduced += _ => casts++;
            SetInputField(driver, "_queued", true); SetInputField(driver, "_queueRemaining", 1f);
            SetInputField(driver, "_waitRelease", false); driver.InstallClientCooldown(4);
            var timeline = (AuthorityActionTimeline)typeof(OnlineCastDriver)
                .GetField("_timeline", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
            Assert.That(timeline.TryStart(71, 1, .5f), Is.True); wizard.SetOnlineActionPresentation(true);
            ConfirmEmptyHands(snapshot, keepWandInBackpack);
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            Assert.That(driver.ClientInventoryReady, Is.True, "空手仍是完整的已安装Owner库存。");
            Assert.That(driver.Ready, Is.False, "无装备选中杖时不能沿上一把程序继续施法。");
            Assert.That(typeof(OnlineCastDriver).GetField("_clientWand", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver), Is.Null.Or.Empty);
            Assert.That(typeof(OnlineCastDriver).GetField("_clientProgramRevision", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver), Is.EqualTo(0u));
            Assert.That(typeof(OnlineCastDriver).GetField("_clientCooldown", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver), Is.EqualTo(0f));
            Assert.That(typeof(OnlineCastDriver).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver), Is.False);
            Assert.That(timeline.IsActive, Is.False); Assert.That(wizard.IsOnlineActionPresentationActive, Is.False);
            driver.CaptureAttackPress(); driver.StepPrediction(.1f, 42, false);
            Assert.That(casts, Is.Zero); Assert.That(_born, Is.Zero);
        }

        [Test]
        public void ConfirmedEmptyHandsBlocksLateSetupCastingAndRetainsItsSoundWhenTheWandIsConfirmedAgain()
        {
            var driver = CreateSelectionClient(out var snapshot); PrepareFocusedSelection(driver);
            ConfirmEmptyHands(snapshot, false);
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            int casts = 0; driver.IntentProduced += _ => casts++;
            // Setup可靠事实与Owner完整快照没有跨消息排序保证：它可以是旧包，也可以是下一次重装备的元数据。
            // 应保留音效配置供后续确认使用，但不能覆盖当前空手事实授予施法。
            driver.InstallClientSetup(Wand, 17, .8f, 1717);
            Assert.That(driver.Ready, Is.False, "晚到Setup不能把当前完整快照已确认的空手变回可施法。");
            driver.CaptureAttackPress(); driver.StepPrediction(.1f, 42, false);
            Assert.That(casts, Is.Zero); Assert.That(_born, Is.Zero);
            var reequipped = ValidationSnapshot(definition);
            reequipped.SnapshotSequence = snapshot.SnapshotSequence + 1; reequipped.ServerTick = snapshot.ServerTick + 1;
            reequipped.Assets.InventoryVersion = snapshot.Assets.InventoryVersion + 1; reequipped.SelectionVersion = snapshot.SelectionVersion + 1;
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), reequipped, out error), Is.True, error);
            Assert.That(driver.Ready, Is.True);
            Assert.That(typeof(OnlineCastDriver).GetField("_predictionSoundKey", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver),
                Is.EqualTo(1717u), "合法重装备的Setup可能先到；不能为挡旧包把下一把杖的音效元数据直接丢弃。");
        }

        [Test]
        public void ConfirmedEmptyHandsCanStillRequestABoundToolWithoutAStaleCastProgram()
        {
            var driver = CreateSelectionClient(out var snapshot); PrepareFocusedSelection(driver);
            ConfirmEmptyHands(snapshot, false);
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            int requests = 0;
            Action<OnlineInventoryCommandRequestedEvent> listener = value => { requests++; Assert.That(value.Intent.Kind, Is.EqualTo(OnlineInventoryCommandKind.SelectTool)); };
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                Assert.That(RequestTool(driver, 0), Is.True); Assert.That(requests, Is.EqualTo(1));
                Assert.That(_root.GetComponent<OnlineInventoryReplica>().Source.ToolbeltReadOnly.SelectedSlot, Is.EqualTo(-1),
                    "只发意图，不能在服务器确认前改手持状态。");
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        [Test]
        public void OrdinaryUsableToolCanBeSelectedButCannotEnterAnyContainerUsePath()
        {
            var driver = CreateSelectionClient(out var snapshot, install: false);
            snapshot.Assets.Items[2].InstanceId = "validation-apple";
            snapshot.Assets.Items[2].ContentId = "item.food.apple"; snapshot.Assets.Items[2].ContainerState = null;
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            PrepareFocusedSelection(driver);
            var source = _root.GetComponent<OnlineInventoryReplica>().Source;
            Assert.That(source.ReadOnly.TryGetView("validation-apple", out var tool), Is.True);
            Assert.That(tool.Kind, Is.Not.EqualTo(ItemKind.Container)); Assert.That(tool.Rules.UseKind, Is.Not.EqualTo(ItemUseKind.None));
            int requests = 0; OnlineInventoryCommandRequestedEvent observed = default;
            Action<OnlineInventoryCommandRequestedEvent> listener = value => { requests++; observed = value; };
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                Assert.That(RequestTool(driver, 0), Is.True); Assert.That(requests, Is.EqualTo(1));
                Assert.That(observed.Intent.Kind, Is.EqualTo(OnlineInventoryCommandKind.SelectTool));
                Assert.That(observed.Intent.ItemId, Is.EqualTo("validation-apple"));
                Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo(Wand));
                snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.SelectionVersion++; snapshot.SelectedToolSlotPlusOne = 1;
                Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out error), Is.True, error);
                foreach (var kind in new[] { OnlineInventoryCommandKind.CollectMaterial, OnlineInventoryCommandKind.PourContainer, OnlineInventoryCommandKind.ThrowContainer })
                    Assert.That(driver.TryGetClientContainer(kind, out _), Is.False);
                var ray = new Ray(Vector3.one, Vector3.forward);
                Assert.That(driver.RequestContainerTransfer(OnlineInventoryCommandKind.CollectMaterial, 1, ray), Is.False);
                Assert.That(driver.RequestContainerTransfer(OnlineInventoryCommandKind.PourContainer, 1, ray), Is.False);
                Assert.That(driver.RequestContainerThrow(ray), Is.False);
                Assert.That(requests, Is.EqualTo(1), "选择普通可用工具不会新增线上使用效果或容器意图。");
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        [TestCase(false)] [TestCase(true)]
        public void EmptyHandsDiagnosticsKeepTheInventoryAndExplicitlyMarkMissingSelectedWandResources(bool keepWandInBackpack)
        {
            var driver = CreateSelectionClient(out var snapshot); ConfirmEmptyHands(snapshot, keepWandInBackpack);
            // 两个合法边界：所有随身物已移走，或仍有包内备用杖但当前空手。
            if (!keepWandInBackpack) snapshot.Assets.Items = Array.Empty<OnlineOwnedItemSnapshot>();
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            Assert.That(OnlineCastInventoryDiagnosticSnapshot.TryFromAuthoritySnapshot(snapshot, 91, out var authority), Is.True);
            Assert.That(driver.TryCaptureClientInventoryDiagnostic(91, out var client), Is.True);
            foreach (var diagnostic in new[] { authority, client })
            {
                Assert.That(diagnostic.TotalItemCount, Is.EqualTo(snapshot.Assets.Items.Length));
                Assert.That(diagnostic.SelectedHeldSlot, Is.EqualTo(-1)); Assert.That(diagnostic.SelectedItemInstanceId, Is.Null.Or.Empty);
                Assert.That(diagnostic.SelectedWandInstanceId, Is.Null.Or.Empty); Assert.That(diagnostic.ProgramRevision, Is.Zero);
                Assert.That(DiagnosticValue(diagnostic, "SelectedWandResourcesAvailable"), Is.EqualTo(false),
                    "空手时0不是一把已选法杖的Mana/CD读数，诊断必须明确缺少该资源来源。");
                Assert.That(diagnostic.Mana, Is.Zero); Assert.That(diagnostic.CooldownRemaining, Is.Zero);
            }
        }

        [Test]
        public void ToolSelectionInputsUseOriginalFourThroughZeroKeysAndOnlyDisableOwnedActions()
        {
            var driver = CreateSelectionClient(out _);
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var shared = new UnityEngine.InputSystem.InputAction[3];
            var originallyEnabled = new bool[3];
            for (int i = 0; i < shared.Length; i++)
            { shared[i] = definition.GetWandSelectionAction(i).action; originallyEnabled[i] = shared[i].enabled; }
            try
            {
                _root.SetActive(true);
                typeof(OnlineCastDriver).GetMethod("BindSelectionInput", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);
                UnityEngine.InputSystem.InputAction[] owned = null;
                foreach (var field in typeof(OnlineCastDriver).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                    if (field.FieldType == typeof(UnityEngine.InputSystem.InputAction[]))
                    {
                        var candidate = (UnityEngine.InputSystem.InputAction[])field.GetValue(driver);
                        if (candidate != null && candidate.Length == HeldItemSlots.ToolCount) owned = candidate;
                    }
                Assert.That(owned, Is.Not.Null, "Driver需生命周期有界的七个工具Action，不能挪用单机Session输入。");
                for (int i = 0; i < owned.Length; i++)
                {
                    Assert.That(owned[i], Is.Not.Null);
                    Assert.That(owned[i].enabled, Is.True);
                    Assert.That(owned[i].bindings.Count, Is.EqualTo(1));
                    Assert.That(owned[i].bindings[0].path,
                        Is.EqualTo("<Keyboard>/" + HeldItemSlots.KeyLabel(i + HeldItemSlots.WandCount)));
                }
                driver.enabled = false;
                // EditMode非ExecuteAlways脚本不会保证派发OnDisable，需与现有Focus fixture一样手动交付生命周期。
                typeof(OnlineCastDriver).GetMethod("OnDisable", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, null);
                for (int i = 0; i < owned.Length; i++) Assert.That(owned[i].enabled, Is.False);
                for (int i = 0; i < shared.Length; i++) Assert.That(shared[i].enabled, Is.True,
                    "1–3属于共享InputActionReference，Driver解绑不能Disable它。");
            }
            finally
            {
                for (int i = 0; i < shared.Length; i++)
                    if (originallyEnabled[i]) shared[i].Enable(); else shared[i].Disable();
            }
        }


        private OnlineCombatDefinition CreateContainerDefinition(bool configured)
        {
            var original = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            _containerDefinitionClone = Object.Instantiate(original);
            var field = typeof(OnlineCombatDefinition).GetField("_containerUseDefinition", BindingFlags.Instance | BindingFlags.NonPublic);
            if (configured)
            {
                Assert.That(field, Is.Not.Null, "线上Definition必须显式引用正式ContainerUseDefinition，不能猜默认数值。");
                var use = AssetDatabase.LoadAssetAtPath<ContainerUseDefinition>(
                    "Assets/_Project/ScriptableObjects/Items/Containers/Use/UseDefinition.asset");
                Assert.That(use, Is.Not.Null);
                field.SetValue(_containerDefinitionClone, use);
                var getter = typeof(OnlineCombatDefinition).GetProperty("ContainerUse");
                Assert.That(getter, Is.Not.Null);
                Assert.That(getter.GetValue(_containerDefinitionClone), Is.SameAs(use));
            }
            else field?.SetValue(_containerDefinitionClone, null);
            return _containerDefinitionClone;
        }

        private static bool RequestContainer(OnlineCastDriver driver, OnlineInventoryCommandKind kind, ulong budget, Ray ray)
        {
            var method = typeof(OnlineCastDriver).GetMethod("RequestContainerTransfer", new[]
                { typeof(OnlineInventoryCommandKind), typeof(ulong), typeof(Ray) });
            Assert.That(method, Is.Not.Null, "F/H需要复用已认证Owner的有限容器请求入口。");
            return (bool)method.Invoke(driver, new object[] { kind, budget, ray });
        }

        private static void ConfigureContainerWorld(OnlineCastDriver driver, string worldId)
        {
            var method = typeof(OnlineCastDriver).GetMethod("ConfigureContainerWorld", new[] { typeof(string) });
            Assert.That(method, Is.Not.Null, "DS必须显式绑定正式材料WorldId，不能猜场景名或广播写入。");
            method.Invoke(driver, new object[] { worldId });
        }

        [TestCase(OnlineInventoryCommandKind.CollectMaterial)]
        [TestCase(OnlineInventoryCommandKind.PourContainer)]
        public void ConfirmedContainerRequestsCarryCurrentInstanceVersionBudgetAndNormalizedRayWithoutChangingSource(
            OnlineInventoryCommandKind kind)
        {
            var definition = CreateContainerDefinition(true);
            var driver = CreateSelectionClient(out _, toolSelected: true);
            driver.InitializeClient(1, 7, definition, true);
            PrepareFocusedSelection(driver);
            var source = _root.GetComponent<OnlineInventoryReplica>().Source;
            var ray = new Ray(new Vector3(4, 5, 6), new Vector3(0, 3, 4));
            int count = 0; OnlineInventoryCommandRequestedEvent observed = default;
            Action<OnlineInventoryCommandRequestedEvent> listener = value => { count++; observed = value; };
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                Assert.That(RequestContainer(driver, kind, 123, ray), Is.True);
                Assert.That(count, Is.EqualTo(1));
                Assert.That(observed.ActorId, Is.EqualTo(1ul));
                Assert.That(observed.CallerId, Is.EqualTo(driver.GetInstanceID()));
                Assert.That(observed.Intent.CommandId, Is.Not.Zero);
                Assert.That(observed.Intent.ConnectionEpoch, Is.EqualTo(7u));
                Assert.That(observed.Intent.ExpectedInventoryVersion, Is.EqualTo(9u));
                Assert.That(observed.Intent.Kind, Is.EqualTo(kind));
                Assert.That(observed.Intent.ItemId, Is.EqualTo("validation-bottle"));
                Assert.That(observed.Intent.TransferBudget, Is.EqualTo(123ul));
                Assert.That(observed.Intent.AimOrigin, Is.EqualTo(ray.origin));
                Assert.That((observed.Intent.AimDirection - ray.direction.normalized).sqrMagnitude, Is.LessThan(1e-8f));
                Assert.That(observed.Intent.AimDirection.sqrMagnitude, Is.EqualTo(1f).Within(1e-5f));
                Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
                Assert.That(source.ToolbeltReadOnly.SelectionVersion, Is.EqualTo(5u));
                Assert.That(source.ToolbeltReadOnly.SelectedItemId, Is.EqualTo("validation-bottle"));
                var components = new MaterialAmount[8];
                Assert.That(source.TryCopyContainer("validation-bottle", components, out int copied, out ulong total, out _), Is.True);
                Assert.That(copied, Is.EqualTo(2)); Assert.That(total, Is.EqualTo(150ul));
                Assert.That(components[0].Quantity, Is.EqualTo(100ul));
                Assert.That(components[1].Quantity, Is.EqualTo(50ul));
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        [TestCase("wand")] [TestCase("no-config")] [TestCase("blocked")] [TestCase("focus")] [TestCase("remote")]
        public void ContainerRequestsRequireConfirmedContainerConfigurationAndLiveLocalInput(string condition)
        {
            var definition = CreateContainerDefinition(condition != "no-config");
            bool remote = condition == "remote";
            var driver = CreateSelectionClient(out _, localOwner: !remote, install: !remote,
                toolSelected: condition != "wand");
            driver.InitializeClient(1, 7, definition, !remote);
            if (!remote) PrepareFocusedSelection(driver);
            if (condition == "blocked") driver.SetBlocked(true);
            if (condition == "focus")
            {
                typeof(OnlineCastDriver).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(driver, new object[] { false });
                driver.SetBlocked(false); // 失焦本身必须阻止请求，不能仅依靠外层suppressed的暂态值。
            }
            int count = 0;
            Action<OnlineInventoryCommandRequestedEvent> listener = _ => count++;
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                Assert.That(RequestContainer(driver, OnlineInventoryCommandKind.CollectMaterial, 10,
                    new Ray(Vector3.zero, Vector3.forward)), Is.False);
                Assert.That(count, Is.Zero);
                if (!remote)
                {
                    var source = _root.GetComponent<OnlineInventoryReplica>().Source;
                    Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
                    Assert.That(source.ToolbeltReadOnly.SelectionVersion, Is.EqualTo(5u));
                }
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        private OnlineCastDriver CreateContainerAuthority(bool bindWorld)
        {
            var definition = CreateContainerDefinition(true);
            var driver = (OnlineCastDriver)Create(out _, includeContainer: true, definitionOverride: definition);
            if (bindWorld) ConfigureContainerWorld(driver, "container-world");
            var selection = new OnlineInventoryCommandIntent
            {
                CommandId = 71, ConnectionEpoch = 1, ExpectedInventoryVersion = driver.OwnedInventoryVersion,
                Kind = OnlineInventoryCommandKind.SelectTool, ItemId = "selection-authority-bottle", TargetSlot = 0
            };
            Assert.That(driver.ExecuteInventoryCommand(selection).Success, Is.True);
            return driver;
        }

        private static OnlineInventoryCommandIntent ContainerAuthorityIntent(OnlineCastDriver driver)
            => new OnlineInventoryCommandIntent
            {
                CommandId = 72, ConnectionEpoch = 1, ExpectedInventoryVersion = driver.OwnedInventoryVersion,
                Kind = OnlineInventoryCommandKind.CollectMaterial, ItemId = "selection-authority-bottle",
                TransferBudget = 10, AimOrigin = driver.transform.position + Vector3.up, AimDirection = Vector3.forward
            };

        [Test]
        public void AuthorityPourOriginUsesHorizontalAimWithoutRotatingTheActor()
        {
            var driver = CreateContainerAuthority(true);
            // 独立于地图碰撞和真实相机：只验证原AlignWithCamera后的水平起点，不能让DS旋转Actor来补偿。
            driver.transform.SetPositionAndRotation(new Vector3(9000, 7000, 9000), Quaternion.identity);
            var rotation = driver.transform.rotation;
            Vector3 direction = new Vector3(1, .5f, 0).normalized;
            var prepare = typeof(OnlineCastDriver).GetMethod("PreparePourTarget", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(prepare, Is.Not.Null);
            object[] arguments = { direction, Vector3.zero };
            Assert.That((MaterialTransferFailure)prepare.Invoke(driver, arguments), Is.EqualTo(MaterialTransferFailure.None));
            var use = _containerDefinitionClone.ContainerUse;
            float time = 47 * .065f;
            Vector3 horizontal = direction; horizontal.y = 0; horizontal.Normalize();
            Vector3 origin = driver.transform.position + horizontal * .8f + Vector3.up * 1.2f;
            Vector3 expected = origin + (direction * use.PourSpeed + Vector3.up * use.PourLift) * time
                + Physics.gravity * (.5f * time * time);
            Assert.That(Vector3.Distance((Vector3)arguments[1], expected), Is.LessThan(.01f),
                "原H先按镜头水平朝向计算起点；DS应按意图水平投影等效计算，不能误用旧移动forward。");
            Assert.That(driver.transform.rotation, Is.EqualTo(rotation), "权威几何计算不能改变Actor移动或朝向。");
        }

        [Test]
        public void AuthorityContainerTransferWithoutBoundWorldRejectsBeforeAnyMaterialWrite()
        {
            var driver = CreateContainerAuthority(false);
            int writes = 0;
            Action<MaterialWorldTransferRequestedEvent> listener = _ => writes++;
            EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(listener);
            try
            {
                var receipt = driver.ExecuteInventoryCommand(ContainerAuthorityIntent(driver));
                Assert.That(receipt.Success, Is.False);
                Assert.That(receipt.Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotReady));
                Assert.That(receipt.Transferred, Is.Zero);
                Assert.That(writes, Is.Zero);
            }
            finally { EventBus<MaterialWorldTransferRequestedEvent>.Unsubscribe(listener); }
        }

        [Test]
        public void ForcedFrozenContainerFailureStaysTheSameWhenTheExactCommandIsRetried()
        {
            var driver = CreateContainerAuthority(true);
            var intent = ContainerAuthorityIntent(driver);
            int writes = 0;
            Action<MaterialWorldTransferRequestedEvent> listener = _ => writes++;
            EventBus<MaterialWorldTransferRequestedEvent>.Subscribe(listener);
            try
            {
                var first = driver.ExecuteInventoryCommand(intent, OnlineInventoryCommandFailure.NotReady);
                var retry = driver.ExecuteInventoryCommand(intent);
                Assert.That(first.Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotReady));
                Assert.That(retry.Failure, Is.EqualTo(first.Failure));
                Assert.That(retry.CommandId, Is.EqualTo(first.CommandId));
                Assert.That(retry.InventoryVersion, Is.EqualTo(first.InventoryVersion));
                Assert.That(retry.TransferFailure, Is.EqualTo(first.TransferFailure));
                Assert.That(retry.Transferred, Is.EqualTo(first.Transferred));
                Assert.That(retry.Transferred, Is.Zero);
                Assert.That(writes, Is.Zero, "Frozen失败已记账，恢复后同ID不能重新进入材料事务。");
            }
            finally { EventBus<MaterialWorldTransferRequestedEvent>.Unsubscribe(listener); }
        }


        // Input fixture只添加虚拟设备和临时Camera；仍由Driver负责创建/绑定Owner组件。
        // EditMode不自动派发非ExecuteAlways的生命周期，像原Focus测试一样显式交付回调。
        private Type ContainerInputType()
        {
            var type = typeof(OnlineCastDriver).Assembly.GetType("Game.Character.OnlineContainerInput");
            Assert.That(type, Is.Not.Null, "缺少正式Owner容器输入组件，不能由测试直接调用请求API替代按键。");
            return type;
        }

        private OnlineCastDriver CreateContainerInput(out OnlineInventoryPresentationSnapshot snapshot, bool selected = true)
        {
            // 无参InputSystem.Update在EditMode默认跑Editor缓冲；包源码明确Editor更新不处理InputAction。
            // 只为这些真实按键测试启用包自带Player更新，TearDown恢复，既不改生产门也不写.inputsettings资产。
            // 精确依据：Library/PackageCache/com.unity.inputsystem@21a28c3a6c83/InputSystem/InputManager.cs:151–163；
            // 同目录Actions/InputActionState.cs:1280–1285、1359–1360直接跳过Editor；
            // InputSettings.cs:707–715明确feature flag不持久化，717–730只改内存HashSet并通知运行时。
            _containerInputSettings = UnityEngine.InputSystem.InputSystem.settings;
            var queryFeature = typeof(UnityEngine.InputSystem.InputSettings).GetMethod("IsFeatureEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(queryFeature, Is.Not.Null);
            _containerPreviousPlayerUpdates = (bool)queryFeature.Invoke(_containerInputSettings, new object[] { ContainerPlayerUpdatesFlag });
            _containerPlayerUpdatesSaved = true;
            _containerInputSettings.SetInternalFeatureFlag(ContainerPlayerUpdatesFlag, true);
            var definition = CreateContainerDefinition(true);
            var driver = CreateSelectionClient(out snapshot, toolSelected: selected);
            driver.InitializeClient(1, 7, definition, true);
            PrepareFocusedSelection(driver);
            _root.SetActive(true);
            _containerInput = _root.GetComponent(ContainerInputType());
            Assert.That(_containerInput, Is.Not.Null, "Driver.InitializeClient必须给本地Owner装配输入。");
            InvokeInputLifecycle(_containerInput, "Awake");
            var cameraRoot = new GameObject("Temporary online container input camera");
            _containerInputObjects.Add(cameraRoot);
            _containerInputCamera = cameraRoot.AddComponent<Camera>();
            cameraRoot.transform.position = _root.transform.position + Vector3.up * 1.6f - Vector3.forward * 2;
            cameraRoot.transform.rotation = Quaternion.LookRotation(new Vector3(1, -.2f, 0).normalized);
            var configure = _containerInput.GetType().GetMethod("Configure", new[] { typeof(OnlineCastDriver), typeof(Camera) });
            Assert.That(configure, Is.Not.Null, "输入应显式接受共享相机引用，不抢读Editor其它MainCamera。");
            configure.Invoke(_containerInput, new object[] { driver, _containerInputCamera });
            InvokeInputLifecycle(_containerInput, "OnEnable");
            Assert.That(_root.activeInHierarchy && driver.enabled && driver.isActiveAndEnabled, Is.True,
                "真实输入fixture需要激活的owned Actor/Driver；不通过修改生产active门解决前置错误。");
            Assert.That(((Behaviour)_containerInput).isActiveAndEnabled, Is.True);
            Assert.That(_containerInputCamera.isActiveAndEnabled, Is.True);
            var readyForInput = typeof(OnlineCastDriver).GetProperty("CanUseContainerHeldInput", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((bool)readyForInput.GetValue(driver), Is.True,
                $"输入前置：Ready={driver.Ready}, Source={driver.ClientInventoryReady}, Focus={Application.isFocused}, ActorActive={_root.activeInHierarchy}");
            _containerKeyboard = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Keyboard>();
            _containerMouse = UnityEngine.InputSystem.InputSystem.AddDevice<UnityEngine.InputSystem.Mouse>();
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(OnContainerInputCommand);
            _containerInputSubscribed = true;
            InputState(false);
            StepContainerInput(.001f); // 初次装配先见到释放，不能继承跨图或选择前按住的输入。
            return driver;
        }

        private void OnContainerInputCommand(OnlineInventoryCommandRequestedEvent value) => _containerInputCommands.Add(value.Intent);
        private void InputState(bool mouse, params UnityEngine.InputSystem.Key[] keys)
        {
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(_containerKeyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(keys));
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(_containerMouse,
                new UnityEngine.InputSystem.LowLevel.MouseState { buttons = mouse ? (ushort)1 : (ushort)0 });
            UnityEngine.InputSystem.InputSystem.Update();
            Assert.That(UnityEngine.InputSystem.LowLevel.InputState.currentUpdateType,
                Is.Not.EqualTo(UnityEngine.InputSystem.LowLevel.InputUpdateType.Editor), "虚拟按键必须推进Player Action而非Editor输入缓冲。");
            Assert.That(_containerKeyboard.fKey.isPressed, Is.EqualTo(Array.IndexOf(keys, UnityEngine.InputSystem.Key.F) >= 0));
            Assert.That(_containerKeyboard.hKey.isPressed, Is.EqualTo(Array.IndexOf(keys, UnityEngine.InputSystem.Key.H) >= 0));
            Assert.That(_containerMouse.leftButton.isPressed, Is.EqualTo(mouse));
            if (_containerInput != null)
            {
                foreach (string field in new[] { "_collect", "_pour", "_left" })
                {
                    var action = (UnityEngine.InputSystem.InputAction)_containerInput.GetType()
                        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_containerInput);
                    Assert.That(action.enabled, Is.True, "Setup必须真实启用自己的Action：" + field);
                    bool pressed = field == "_left" ? mouse : Array.IndexOf(keys,
                        field == "_collect" ? UnityEngine.InputSystem.Key.F : UnityEngine.InputSystem.Key.H) >= 0;
                    Assert.That(action.IsPressed(), Is.EqualTo(pressed), "设备状态必须真的进入Action：" + field);
                }
            }
        }
        private void StepContainerInput(float seconds)
        {
            var step = _containerInput.GetType().GetMethod("Step", new[] { typeof(float) });
            Assert.That(step, Is.Not.Null);
            step.Invoke(_containerInput, new object[] { seconds });
            UnityEngine.InputSystem.InputSystem.Update();
        }
        private static void InvokeInputLifecycle(Component component, string method, params object[] arguments)
        {
            component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(component,
                arguments.Length == 0 ? null : arguments);
        }
        private static void SetInputField(object owner, string name, object value)
        {
            var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name); field.SetValue(owner, value);
        }
        private static PropertyInfo InputStaticProperty(Type type, string name)
            => type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private void ActivatePriorityInput(Component input)
        {
            var property = InputStaticProperty(input.GetType(), "Current");
            _containerInputStatics.Add(new KeyValuePair<PropertyInfo, object>(property, property.GetValue(null)));
            _containerPriorityInputs.Add(input);
            InvokeInputLifecycle(input, "Awake");
            InvokeInputLifecycle(input, "OnEnable");
        }

        private void CleanupContainerInputFixture()
        {
            if (_containerInputSubscribed)
                EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(OnContainerInputCommand);
            _containerInputSubscribed = false;
            if (_containerInput != null)
            { InvokeInputLifecycle(_containerInput, "OnDisable"); InvokeInputLifecycle(_containerInput, "OnDestroy"); }
            _containerInput = null;
            foreach (var input in _containerPriorityInputs)
                if (input != null)
                { InvokeInputLifecycle(input, "OnDisable"); InvokeInputLifecycle(input, "OnDestroy"); }
            _containerPriorityInputs.Clear();
            if (_containerPortalTarget != null) PortalInteractionRegistry.Unregister(_containerPortalTarget);
            _containerPortalTarget = null;
            for (int i = _containerInputStatics.Count - 1; i >= 0; i--)
                _containerInputStatics[i].Key.SetValue(null, _containerInputStatics[i].Value);
            _containerInputStatics.Clear();
            if (_containerPickupFrameSaved)
                typeof(NearbyPickupInput).GetField("<LastPickupFrame>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, _containerPreviousPickupFrame);
            _containerPickupFrameSaved = false;
            foreach (var item in _containerInputObjects) if (item != null) Object.DestroyImmediate(item);
            _containerInputObjects.Clear();
            if (_containerKeyboard != null) UnityEngine.InputSystem.InputSystem.RemoveDevice(_containerKeyboard);
            if (_containerMouse != null) UnityEngine.InputSystem.InputSystem.RemoveDevice(_containerMouse);
            _containerKeyboard = null; _containerMouse = null; _containerInputCamera = null;
            if (_containerPlayerUpdatesSaved && _containerInputSettings != null)
                _containerInputSettings.SetInternalFeatureFlag(ContainerPlayerUpdatesFlag, _containerPreviousPlayerUpdates);
            _containerPlayerUpdatesSaved = false; _containerInputSettings = null;
            _containerInputCommands.Clear();
        }

        [Test]
        public void AuthoritySupplyUsesExplicitWorldIdAcrossSameRunReconnect()
        {
            var definition = CreateContainerDefinition(true);
            var driver = (OnlineCastDriver)Create(out _, definitionOverride: definition);
            ConfigureContainerWorld(driver, "authority-supply-world");
            var adapter = _root.GetComponent<OwnedWandCastAdapter>();
            CastResourceRequestedEvent observed = default; int requests = 0;
            Action<CastResourceRequestedEvent> listener = value =>
            { if (value.Operation == CastResourceOperation.PrepareRoot) { observed = value; requests++; } };
            EventBus<CastResourceRequestedEvent>.Subscribe(listener);
            try
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    if (pass != 0) { driver.SetConnection(2, false); driver.SetConnection(2, true); }
                    Assert.That(adapter.TryCapture(out _, out var context), Is.True);
                    var request = typeof(OwnedWandCastAdapter).GetMethod("Request", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.That(request, Is.Not.Null);
                    request.Invoke(adapter, new object[] { context, CastResourceOperation.PrepareRoot });
                    Assert.That(requests, Is.EqualTo(pass + 1));
                    Assert.That(observed.MaterialWorldId, Is.EqualTo("authority-supply-world"),
                        "正式供料必须沿实际绑定World，重连不能退回Prefab默认container-world。");
                    typeof(OwnedWandCastAdapter).GetMethod("CancelOutstanding", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(adapter, null);
                }
            }
            finally { EventBus<CastResourceRequestedEvent>.Unsubscribe(listener); }
        }

        private MaterialPayloadRuntimeAdapter CreateAuthoritySupplyProxy(CombatSimulationWorld world, bool visuals = false)
        {
            if (_root == null) _root = new GameObject("Authority supply proxy owner");
            var owner = _root.GetComponent<SpellCaster>(); if (owner == null) owner = _root.AddComponent<SpellCaster>();
            var create = typeof(MaterialPayloadRuntimeAdapter).GetMethod("CreateAuthority", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(MonoBehaviour), typeof(string), typeof(string), typeof(Vector3), typeof(float), typeof(Material),
                    typeof(CombatSimulationWorld), typeof(ulong), typeof(ulong), typeof(bool) }, null);
            Assert.That(create, Is.Not.Null, "原材料代理需在创建时取得Authority时钟/身份，且可显式不创建视觉。");
            var proxy = (MaterialPayloadRuntimeAdapter)create.Invoke(null, new object[]
                { owner, "authority-supply-inventory", "authority-supply-output", new Vector3(20, 30, 40), 0f, null, world, 1ul, 77ul, visuals });
            Assert.That(proxy, Is.Not.Null); _outputs.Add(proxy.gameObject); return proxy;
        }

        [Test]
        public void HeadlessAuthoritySupplyCreatesNoRendererAndPublishesOneCommittedBirth()
        {
            var world = new CombatSimulationWorld(); world.BeginTick(10);
            var proxy = CreateAuthoritySupplyProxy(world);
            Assert.That(proxy.GetComponentsInChildren<Renderer>(true), Is.Empty,
                "无图形路径不创建Shader/Material/Renderer，不能因DS没有URP图形资源取消真实预算。");
            Assert.That(proxy.GetComponentsInChildren<Collider>(true), Is.Empty);
            Assert.That(_born, Is.Zero, "Prepare阶段不能发布尚未提交的材料标记。");
            InvokeInputLifecycle(proxy, "Activate"); InvokeInputLifecycle(proxy, "Activate");
            Assert.That(_born, Is.EqualTo(1), "Activate提交后沿原Combat出生一次；重复激活不产生第二个网络输出。");
            Assert.That(proxy.OutputId, Is.EqualTo("authority-supply-output"));
            Assert.That(proxy.IsWaiting, Is.False);
        }

        [Test]
        public void AuthoritySupplyFallbackUsesUniqueTicksAndDoesNotAccumulateFrozenTime()
        {
            var world = new CombatSimulationWorld(); bool allowed = true;
            world.ConfigureAuthorityWriteGate(() => allowed); world.BeginTick(10);
            var proxy = CreateAuthoritySupplyProxy(world); int sends = 0;
            Action<SpellMaterialOutputEvent> listener = value => { if (!value.Ended) sends++; };
            EventBus<SpellMaterialOutputEvent>.Subscribe(listener);
            try
            {
                InvokeInputLifecycle(proxy, "Activate");
                world.StepAfterPhysics(.6f, 10); // 出生Tick没有经历此前的dt。
                world.StepAfterPhysics(.6f, 11); world.StepAfterPhysics(.6f, 11);
                Assert.That(sends, Is.Zero, "同Tick不能重复扣原1秒fallback等待。");
                allowed = false; world.StepAfterPhysics(5, 100); InvokeInputLifecycle(proxy, "Update");
                Assert.That(sends, Is.Zero); Assert.That(proxy.IsWaiting, Is.False);
                allowed = true; world.StepAfterPhysics(.3f, 101);
                Assert.That(sends, Is.Zero, "Frozen五秒不计入恢复后的等待债务。");
                world.StepAfterPhysics(.2f, 102);
                Assert.That(sends, Is.EqualTo(1)); Assert.That(proxy.IsWaiting, Is.True);
            }
            finally { EventBus<SpellMaterialOutputEvent>.Unsubscribe(listener); }
        }

        [Test]
        public void FrozenAuthoritySupplyCallbacksAndDisableRetainCommittedOutputWithoutEnded()
        {
            var world = new CombatSimulationWorld(); bool allowed = true;
            world.ConfigureAuthorityWriteGate(() => allowed); world.BeginTick(10);
            var proxy = CreateAuthoritySupplyProxy(world); int sends = 0, ended = 0;
            Vector3 observed = default;
            Action<SpellMaterialOutputEvent> listener = value =>
            { if (value.Ended) ended++; else { sends++; observed = value.Position; } };
            EventBus<SpellMaterialOutputEvent>.Subscribe(listener);
            try
            {
                InvokeInputLifecycle(proxy, "Activate"); allowed = false;
                var point = new Vector3(7, 8, 9);
                InvokeInputLifecycle(proxy, "ReleaseAt", point); InvokeInputLifecycle(proxy, "OnFinished");
                InvokeInputLifecycle(proxy, "OnDisable"); InvokeInputLifecycle(proxy, "OnEnable");
                Assert.That(sends, Is.Zero); Assert.That(ended, Is.Zero, "Authority暂挂不能把已提交预算转成Lost或退款。");
                Assert.That(proxy != null && proxy.gameObject.activeSelf, Is.True);
                Assert.That(proxy.OutputId, Is.EqualTo("authority-supply-output"));
                allowed = true; world.StepAfterPhysics(.2f, 11);
                Assert.That(sends, Is.EqualTo(1)); Assert.That(ended, Is.Zero); Assert.That(observed, Is.EqualTo(point));
            }
            finally { EventBus<SpellMaterialOutputEvent>.Unsubscribe(listener); }
        }

        // K的DTO先用反射构造，RED阶段不要求尚在根Agent落地的Core字段已能被静态引用。
        private static OnlineOwnedItemSnapshot SolidReleaseItem(string id, int x, ulong quantity)
        {
            var item = new OnlineOwnedItemSnapshot
            { InstanceId = id, ContentId = "containertest.ice", Revision = 1, LocationKind = 1, ParentInstanceId = "solid-release-bag", Slot = -1, X = x };
            var field = typeof(OnlineOwnedItemSnapshot).GetField("MaterialState");
            Assert.That(field, Is.Not.Null, "真实固体必须携带显式MaterialKey/Quantity，不能从模板或ContainerState补满。");
            var state = Activator.CreateInstance(field.FieldType);
            field.FieldType.GetField("SchemaVersion").SetValue(state, 1u);
            field.FieldType.GetField("MaterialKey").SetValue(state, (byte)5);
            field.FieldType.GetField("Quantity").SetValue(state, quantity);
            field.SetValue(item, state); return item;
        }

        private static void AddSolidReleaseBackpack(OnlineOwnedAssetSnapshot assets)
        {
            var items = new List<OnlineOwnedItemSnapshot>(assets.Items)
            {
                new OnlineOwnedItemSnapshot { InstanceId = "solid-release-bag", ContentId = "wandtest.bag", Revision = 1, LocationKind = 4 },
                SolidReleaseItem("z-first-ice", 0, 256), SolidReleaseItem("a-second-ice", 1, 40)
            };
            assets.Items = items.ToArray();
        }

        private OnlineCastDriver CreateSolidReleaseInput(out OnlineInventoryPresentationSnapshot snapshot)
        {
            var driver = CreateContainerInput(out snapshot);
            AddSolidReleaseBackpack(snapshot.Assets);
            snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.Assets.InventoryVersion++;
            Assert.That(driver.InstallClientInventory(ValidationIdentity(_containerDefinitionClone), snapshot, out var error), Is.True, error);
            return driver;
        }

        private string FirstMaterialInConfirmedBackpack()
        {
            var read = _root.GetComponent<OnlineInventoryReplica>().Source.ReadOnly;
            var items = new InventoryItemSnapshot[read.Count]; int count = read.CopyItemsTo(items); string bag = null;
            for (int i = 0; i < count; i++)
                if (items[i].Kind == ItemKind.Backpack && items[i].Location.Kind == ItemLocationKind.EquippedBackpack
                    && items[i].Location.OwnerId == read.ActorId) bag = items[i].InstanceId;
            for (int i = 0; i < count; i++)
                if (items[i].Kind == ItemKind.Material && items[i].Location.Kind == ItemLocationKind.Backpack
                    && items[i].Location.OwnerId == bag) return items[i].InstanceId;
            Assert.Fail("真实Owner夹具必须有原CopyItemsTo顺序的包内固体。"); return null;
        }

        private OnlineCastDriver CreateSolidReleaseAuthority()
        {
            var definition = CreateContainerDefinition(true); var assets = ValidationSnapshot(definition).Assets;
            assets.InventoryId = "solid-release-authority:" + Guid.NewGuid().ToString("N"); AddSolidReleaseBackpack(assets);
            var driver = (OnlineCastDriver)Create(out _, definitionOverride: definition, assetsOverride: assets);
            ConfigureContainerWorld(driver, "container-world"); return driver;
        }

        [Test]
        public void SolidReleasePressSendsOneZeroBudgetIntentForTheFirstConfirmedBackpackMaterial()
        {
            CreateSolidReleaseInput(out _); string first = FirstMaterialInConfirmedBackpack();
            InputState(false, UnityEngine.InputSystem.Key.K); StepContainerInput(.01f); StepContainerInput(2);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1), "K是一次按下的一次意图，按住不按10Hz重复投放。");
            var intent = _containerInputCommands[0];
            Assert.That((byte)intent.Kind, Is.EqualTo(10)); Assert.That(intent.ItemId, Is.EqualTo(first));
            Assert.That(intent.CommandId, Is.Not.Zero); Assert.That(intent.ConnectionEpoch, Is.EqualTo(7u));
            Assert.That(intent.ExpectedInventoryVersion, Is.EqualTo(10u)); Assert.That(intent.TransferBudget, Is.Zero);
            Assert.That(intent.AimOrigin, Is.EqualTo(Vector3.zero)); Assert.That(intent.AimDirection, Is.EqualTo(Vector3.zero));
            Assert.That(_root.GetComponent<OnlineInventoryReplica>().Source.ReadOnly.Version, Is.EqualTo(10u), "客户端不得乐观扣固体量。");
            InputState(false); StepContainerInput(.01f);
            InputState(false, UnityEngine.InputSystem.Key.K); StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(2));
            Assert.That(_containerInputCommands[1].CommandId, Is.GreaterThan(intent.CommandId));
        }

        [Test]
        public void SolidReleaseFocusLossAndReconnectDoNotResumeAnOldHeldPress()
        {
            var driver = CreateSolidReleaseInput(out var snapshot);
            InputState(false, UnityEngine.InputSystem.Key.K); StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            InvokeInputLifecycle(driver, "OnApplicationFocus", false); InvokeInputLifecycle(_containerInput, "OnApplicationFocus", false);
            StepContainerInput(2);
            InvokeInputLifecycle(driver, "OnApplicationFocus", true); InvokeInputLifecycle(_containerInput, "OnApplicationFocus", true);
            driver.SetBlocked(false); StepContainerInput(.1f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1), "回焦不继承旧K；原World资格恢复不等于新按下。");
            InputState(false); StepContainerInput(.01f);
            InputState(false, UnityEngine.InputSystem.Key.K); StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(2));
            driver.SetConnection(8, false); StepContainerInput(.2f); driver.SetConnection(8, true); StepContainerInput(.2f);
            snapshot.ConnectionEpoch = 8; snapshot.SnapshotSequence++; snapshot.ServerTick++;
            var identity = new OnlineInventoryPresentationIdentity(snapshot.Assets.RunId, snapshot.Assets.PlayerId, 1,
                snapshot.Assets.InventoryId, _containerDefinitionClone.GameplayConfigHash, 8, "validation-world");
            Assert.That(driver.InstallClientInventory(identity, snapshot, out var error), Is.True, error);
            StepContainerInput(.2f); Assert.That(_containerInputCommands.Count, Is.EqualTo(2));
            InputState(false); StepContainerInput(.01f);
            InputState(false, UnityEngine.InputSystem.Key.K); StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(3)); Assert.That(_containerInputCommands[2].ConnectionEpoch, Is.EqualTo(8u));
        }

        [Test]
        public void AuthoritySolidReleaseComputesOriginalForwardTargetWithoutAnyClientAim()
        {
            var driver = CreateSolidReleaseAuthority();
            driver.transform.SetPositionAndRotation(new Vector3(9000, 7000, 9000), Quaternion.Euler(0, 95, 0));
            var intent = new OnlineInventoryCommandIntent
            { CommandId = 901, ConnectionEpoch = 1, ExpectedInventoryVersion = driver.OwnedInventoryVersion,
                Kind = (OnlineInventoryCommandKind)10, ItemId = "z-first-ice", TransferBudget = 0, AimOrigin = Vector3.zero, AimDirection = Vector3.zero };
            var method = typeof(OnlineCastDriver).GetMethod("PrepareContainerContext", BindingFlags.Instance | BindingFlags.NonPublic);
            var context = (OnlineContainerAuthorityContext)method.Invoke(driver, new object[] { intent });
            Assert.That(context.CanAdvance, Is.True); Assert.That(context.PreparationFailure, Is.EqualTo(MaterialTransferFailure.None));
            Assert.That(context.ActorPosition, Is.EqualTo(driver.transform.position));
            var expected = driver.transform.position + driver.transform.forward * 1.5f + Vector3.up * .25f;
            Assert.That(Vector3.Distance(context.TargetPosition, expected), Is.LessThan(.001f), "K复用原Actor forward落点，不借用G/H射线或客户端落点。");
        }

        [Test]
        public void FrozenSolidReleaseRetainsQuantityAndItsRejectedReceiptAfterTheGateReopens()
        {
            var driver = CreateSolidReleaseAuthority(); bool allowed = false;
            var world = (CombatSimulationWorld)typeof(OnlineCastDriver).GetField("_world", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
            world.ConfigureAuthorityWriteGate(() => allowed);
            var workspace = (OnlineOwnedWorkspace)typeof(OnlineCastDriver).GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
            uint version = driver.OwnedInventoryVersion; int deposits = 0;
            Action<MaterialWorldDepositRequestedEvent> listener = _ => deposits++;
            EventBus<MaterialWorldDepositRequestedEvent>.Subscribe(listener);
            try
            {
                var intent = new OnlineInventoryCommandIntent { CommandId = 902, ConnectionEpoch = 1, ExpectedInventoryVersion = version,
                    Kind = (OnlineInventoryCommandKind)10, ItemId = "z-first-ice", TransferBudget = 0 };
                var first = driver.ExecuteInventoryCommand(intent);
                Assert.That(first.Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotReady)); Assert.That(first.Transferred, Is.Zero);
                allowed = true; var retry = driver.ExecuteInventoryCommand(intent);
                Assert.That(retry.Failure, Is.EqualTo(first.Failure)); Assert.That(retry.InventoryVersion, Is.EqualTo(first.InventoryVersion));
                Assert.That(driver.OwnedInventoryVersion, Is.EqualTo(version)); Assert.That(deposits, Is.Zero);
                Assert.That(workspace.TryGetItem("z-first-ice", out var material), Is.True);
                Assert.That(material.Material.Quantity, Is.EqualTo(256ul), "暂停与同ID重放都不能扣量或自动退货/重做。");
            }
            finally { EventBus<MaterialWorldDepositRequestedEvent>.Unsubscribe(listener); }
        }

        // G复用同一Owner、相机和真实InputAction夹具；测试只给输入意图，不在Client造飞瓶或改库存。
        [Test]
        public void HeldThrowShowsOriginalTrajectorySuppressesPourAndReleasesOneIntent()
        {
            var driver = CreateContainerInput(out _);
            _root.transform.SetPositionAndRotation(new Vector3(9000, 7000, 9000), Quaternion.identity);
            _containerInputCamera.transform.position = _root.transform.position + Vector3.up * 1.6f;
            _containerInputCamera.transform.rotation = Quaternion.LookRotation(new Vector3(1, .4f, 0).normalized);
            Physics.SyncTransforms();
            InputState(false, UnityEngine.InputSystem.Key.G, UnityEngine.InputSystem.Key.H); StepContainerInput(.2f);
            Assert.That(_containerInputCommands.Count, Is.Zero, "G瞄准阶段停止H，不能一边预览一边倾倒或提前投掷。");
            var lines = _containerInput.GetComponentsInChildren<LineRenderer>(true);
            Assert.That(lines.Length, Is.EqualTo(1));
            Assert.That(lines[0].enabled, Is.True, "按住G应显示原容器抛物线。");
            Assert.That(lines[0].positionCount, Is.EqualTo(48));
            var direction = _containerInputCamera.ViewportPointToRay(new Vector3(.5f, .5f, 0)).direction.normalized;
            var horizontal = new Vector3(direction.x, 0, direction.z).normalized;
            var origin = _root.transform.position + horizontal * .8f + Vector3.up * 1.2f;
            var use = _containerDefinitionClone.ContainerUse;
            float time = 47 * .065f;
            var endpoint = origin + (direction * use.ThrowSpeed + Vector3.up * use.ThrowLift) * time
                + Physics.gravity * (.5f * time * time);
            Assert.That(Vector3.Distance(lines[0].GetPosition(0), origin), Is.LessThan(.01f));
            Assert.That(Vector3.Distance(lines[0].GetPosition(47), endpoint), Is.LessThan(.01f),
                "G沿原ThrowSpeed/ThrowLift和48×.065秒采样；不能误用H的Pour参数。");
            InputState(false); StepContainerInput(.1f); StepContainerInput(.3f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1), "松开一次G只产生一条离散投掷意图。");
            var intent = _containerInputCommands[0];
            Assert.That((byte)intent.Kind, Is.EqualTo(9));
            Assert.That(intent.CommandId, Is.Not.Zero);
            Assert.That(intent.ConnectionEpoch, Is.EqualTo(7u));
            Assert.That(intent.ExpectedInventoryVersion, Is.EqualTo(9u));
            Assert.That(intent.ItemId, Is.EqualTo("validation-bottle"));
            Assert.That(intent.TargetSlot, Is.Zero);
            Assert.That(intent.TransferBudget, Is.Zero, "投掷整只既有容器，不借用F/H的数量预算。");
            Assert.That(Vector3.Distance(intent.AimDirection, direction), Is.LessThan(.0001f));
            Assert.That(lines[0].enabled, Is.False);
            Assert.That(driver.transform.rotation, Is.EqualTo(Quaternion.identity));
            Assert.That(_root.GetComponent<OnlineInventoryReplica>().Source.ReadOnly.Version, Is.EqualTo(9u),
                "Client只发意图，原瓶与组分等待权威确认。");
        }

        [Test]
        public void ThrowAimFocusLossAndConfirmedBottleReplacementCancelThePendingRelease()
        {
            var driver = CreateContainerInput(out var snapshot);
            var line = _containerInput.GetComponentInChildren<LineRenderer>(true);
            InputState(false, UnityEngine.InputSystem.Key.G); StepContainerInput(.1f);
            Assert.That(line.enabled, Is.True);
            InvokeInputLifecycle(driver, "OnApplicationFocus", false);
            InvokeInputLifecycle(_containerInput, "OnApplicationFocus", false);
            StepContainerInput(2);
            Assert.That(line.enabled, Is.False);
            InvokeInputLifecycle(driver, "OnApplicationFocus", true);
            InvokeInputLifecycle(_containerInput, "OnApplicationFocus", true);
            driver.SetBlocked(false); // EditMode没有World Update；按既有失焦夹具恢复World的实时资格，保留生产门。
            InputState(false); StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.Zero, "失焦取消后松开G不能误发旧瓶。");

            InputState(false, UnityEngine.InputSystem.Key.G); StepContainerInput(.1f);
            Assert.That(line.enabled, Is.True);
            snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.SelectionVersion++; snapshot.Assets.InventoryVersion++;
            snapshot.Assets.Items[2].InstanceId = "replacement-bottle"; snapshot.Assets.Items[2].Revision++;
            Assert.That(driver.InstallClientInventory(ValidationIdentity(_containerDefinitionClone), snapshot, out var error), Is.True, error);
            StepContainerInput(.1f);
            Assert.That(line.enabled, Is.False);
            InputState(false); StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.Zero, "当前绑定换瓶取消旧瞄准，释放不能偷偷改投新实例。");
            InputState(false, UnityEngine.InputSystem.Key.G); StepContainerInput(.1f);
            InputState(false); StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            Assert.That(_containerInputCommands[0].ItemId, Is.EqualTo("replacement-bottle"));
            Assert.That(_containerInputCommands[0].ExpectedInventoryVersion, Is.EqualTo(10u));
        }

        [Test]
        public void ThrowAimReconnectRequiresTheNewEpochSourceAndAFreshPress()
        {
            var driver = CreateContainerInput(out var snapshot);
            InputState(false, UnityEngine.InputSystem.Key.G); StepContainerInput(.1f);
            Assert.That(_containerInput.GetComponentInChildren<LineRenderer>(true).enabled, Is.True);
            driver.SetConnection(8, false); StepContainerInput(1);
            driver.SetConnection(8, true); StepContainerInput(.2f);
            Assert.That(_containerInputCommands.Count, Is.Zero);
            snapshot.ConnectionEpoch = 8; snapshot.SnapshotSequence++; snapshot.ServerTick++;
            var expected = new OnlineInventoryPresentationIdentity(snapshot.Assets.RunId, snapshot.Assets.PlayerId, 1,
                snapshot.Assets.InventoryId, _containerDefinitionClone.GameplayConfigHash, 8, "validation-world");
            Assert.That(driver.InstallClientInventory(expected, snapshot, out var error), Is.True, error);
            StepContainerInput(.2f);
            InputState(false); StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.Zero, "更高Epoch确认不能续接断线前G的释放。");
            InputState(false, UnityEngine.InputSystem.Key.G); StepContainerInput(.1f);
            InputState(false); StepContainerInput(.01f); StepContainerInput(.2f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            Assert.That((byte)_containerInputCommands[0].Kind, Is.EqualTo(9));
            Assert.That(_containerInputCommands[0].ConnectionEpoch, Is.EqualTo(8u));
        }

        [Test]
        public void AuthorityThrowContextUsesOriginalHorizontalOriginAndFullPitchVelocity()
        {
            var driver = CreateContainerAuthority(true);
            driver.transform.SetPositionAndRotation(new Vector3(9000, 7000, 9000), Quaternion.identity);
            var intent = ContainerAuthorityIntent(driver);
            intent.Kind = (OnlineInventoryCommandKind)9; intent.TransferBudget = 0;
            intent.AimOrigin = driver.transform.position + Vector3.up * 1.6f;
            intent.AimDirection = new Vector3(1, .5f, 0).normalized;
            var prepare = typeof(OnlineCastDriver).GetMethod("PrepareContainerContext", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(prepare, Is.Not.Null);
            object boxed = prepare.Invoke(driver, new object[] { intent });
            var context = (OnlineContainerAuthorityContext)boxed;
            Assert.That(context.CanAdvance, Is.True);
            Assert.That(context.PreparationFailure, Is.EqualTo(MaterialTransferFailure.None));
            var originField = typeof(OnlineContainerAuthorityContext).GetField("ThrowOrigin");
            var velocityField = typeof(OnlineContainerAuthorityContext).GetField("ThrowVelocity");
            Assert.That(originField, Is.Not.Null, "仅DSContext携带重算后的原投掷起点，Wire仍只传瞄准射线。");
            Assert.That(velocityField, Is.Not.Null);
            var horizontal = new Vector3(intent.AimDirection.x, 0, intent.AimDirection.z).normalized;
            var expectedOrigin = driver.transform.position + horizontal * .8f + Vector3.up * 1.2f;
            var use = _containerDefinitionClone.ContainerUse;
            var expectedVelocity = intent.AimDirection * use.ThrowSpeed + Vector3.up * use.ThrowLift;
            Assert.That(Vector3.Distance((Vector3)originField.GetValue(boxed), expectedOrigin), Is.LessThan(.01f));
            Assert.That(Vector3.Distance((Vector3)velocityField.GetValue(boxed), expectedVelocity), Is.LessThan(.001f));
            Assert.That(driver.transform.rotation, Is.EqualTo(Quaternion.identity), "权威投掷不旋转角色来补偿镜头方向。");
        }

        private sealed class PreparedContainerThrowProxy : IContainerThrowProxy
        {
            public int Launches, Cancellations;
            public void Launch() => Launches++;
            public void Cancel() => Cancellations++;
        }

        [Test]
        public void ThrowFactoryIsScopedRegisteredOnceAndRetainedAcrossConnectionChanges()
        {
            var driver = CreateContainerAuthority(true);
            var configure = typeof(OnlineCastDriver).GetMethod("ConfigureContainerThrowFactory", new[]
                { typeof(Func<ContainerThrowExchange, string, GameObject, IContainerThrowProxy>) });
            Assert.That(configure, Is.Not.Null, "Character必须让组合层预留输出槽并准备原代理，不能另造复制系统。");
            int preparations = 0; string content = null; GameObject model = null;
            var proxy = new PreparedContainerThrowProxy();
            Func<ContainerThrowExchange, string, GameObject, IContainerThrowProxy> factory = (exchange, contentId, worldModel) =>
            { preparations++; content = contentId; model = worldModel; return proxy; };
            Func<ContainerThrowExchange, string, GameObject, IContainerThrowProxy> replacement = (exchange, contentId, worldModel) => null;
            Assert.That(configure.Invoke(driver, new object[] { factory }), Is.EqualTo(true));
            Assert.That(configure.Invoke(driver, new object[] { factory }), Is.EqualTo(true));
            Assert.That(configure.Invoke(driver, new object[] { replacement }), Is.EqualTo(false), "同局活跃Workspace不能换代理工厂。");
            var foreign = new ContainerThrowExchange
            {
                FlightId = "foreign-flight", Request = new ContainerUseRequestedEvent
                { InventoryId = "another-owner", WorldId = "container-world", ItemId = "selection-authority-bottle", Kind = ContainerUseKind.Throw }
            };
            EventBus<ContainerThrownEvent>.Publish(new ContainerThrownEvent { Exchange = foreign });
            Assert.That(foreign.Proxy, Is.Null); Assert.That(preparations, Is.Zero, "全局Prepare事件不能替其它Owner准备输出。");
            driver.SetConnection(2, false);
            Assert.That(configure.Invoke(driver, new object[] { factory }), Is.EqualTo(true));
            Assert.That(configure.Invoke(driver, new object[] { replacement }), Is.EqualTo(false));
            driver.SetConnection(2, true);
            var intent = ContainerAuthorityIntent(driver);
            intent.ConnectionEpoch = 2; intent.Kind = (OnlineInventoryCommandKind)9; intent.TransferBudget = 0;
            var receipt = driver.ExecuteInventoryCommand(intent);
            Assert.That(receipt.Success, Is.True, "重连后真实Run Prepare仍须到达同一工厂，原瓶先提交在途再Launch。");
            Assert.That(preparations, Is.EqualTo(1));
            Assert.That(content, Is.EqualTo("containertest.bottle")); Assert.That(model, Is.Not.Null);
            Assert.That(proxy.Launches, Is.EqualTo(1)); Assert.That(proxy.Cancellations, Is.Zero);
            var retry = driver.ExecuteInventoryCommand(intent);
            Assert.That(retry.Success, Is.True); Assert.That(retry.InventoryVersion, Is.EqualTo(receipt.InventoryVersion));
            Assert.That(preparations, Is.EqualTo(1)); Assert.That(proxy.Launches, Is.EqualTo(1), "同ID重试不能再准备或发射第二只瓶。");
        }

        [TestCase("remote")] [TestCase("authority")]
        public void ContainerHeldInputIsOnlyBoundForTheLocalOwner(string role)
        {
            OnlineCastDriver driver = role == "authority" ? (OnlineCastDriver)Create(out _)
                : CreateSelectionClient(out _, localOwner: false, install: false);
            var input = driver.GetComponent(ContainerInputType()) as Behaviour;
            Assert.That(input == null || !input.enabled, Is.True);
        }

        [Test]
        public void RealHeldFAndHProduceIndependentTenHertzBudgetsWithoutLocalAssetWrites()
        {
            CreateContainerInput(out _);
            InputState(false, UnityEngine.InputSystem.Key.F, UnityEngine.InputSystem.Key.H);
            for (int i = 0; i < 10; i++) StepContainerInput(.1f);
            int collect = 0, pour = 0; ulong collectBudget = 0, pourBudget = 0;
            foreach (var intent in _containerInputCommands)
            {
                Assert.That(intent.ItemId, Is.EqualTo("validation-bottle"));
                if (intent.Kind == OnlineInventoryCommandKind.CollectMaterial) { collect++; collectBudget += intent.TransferBudget; }
                else if (intent.Kind == OnlineInventoryCommandKind.PourContainer) { pour++; pourBudget += intent.TransferBudget; }
                else Assert.Fail("持键容器组件不应发送其它玩法命令。");
            }
            Assert.That(collect, Is.EqualTo(10)); Assert.That(pour, Is.EqualTo(10));
            Assert.That(collectBudget, Is.EqualTo((ulong)_containerDefinitionClone.ContainerUse.CollectionRate));
            Assert.That(pourBudget, Is.EqualTo((ulong)_containerDefinitionClone.ContainerUse.PourRate));
            var source = _root.GetComponent<OnlineInventoryReplica>().Source;
            Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
            Assert.That(source.TryCopyContainer("validation-bottle", new MaterialAmount[8], out _, out ulong total, out _), Is.True);
            Assert.That(total, Is.EqualTo(150ul));
            InputState(false); StepContainerInput(.5f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(20));
        }

        [TestCase("h")] [TestCase("mouse")] [TestCase("both")]
        public void OriginalHAndLeftMouseShareOnePourStream(string controls)
        {
            CreateContainerInput(out _);
            InputState(controls != "h", controls == "mouse" ? Array.Empty<UnityEngine.InputSystem.Key>()
                : new[] { UnityEngine.InputSystem.Key.H });
            for (int i = 0; i < 5; i++) StepContainerInput(.1f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(5));
            foreach (var intent in _containerInputCommands)
                Assert.That(intent.Kind, Is.EqualTo(OnlineInventoryCommandKind.PourContainer));
        }

        private sealed class InputPortalTarget : IPortalInteractionTarget
        {
            public Transform PortalTransform { get; set; }
            public string InteractionLabel => "Temporary input priority portal";
            public bool CanInteract(Collider actor) => actor != null && actor.enabled && actor.gameObject.activeInHierarchy
                && (actor.transform.position - PortalTransform.position).sqrMagnitude < 4;
            public bool Interact(Collider actor) => CanInteract(actor);
        }
        private Action BlockFWithOriginalInteraction(string kind)
        {
            var frame = typeof(NearbyPickupInput).GetField("<LastPickupFrame>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(frame, Is.Not.Null);
            _containerPreviousPickupFrame = (int)frame.GetValue(null); _containerPickupFrameSaved = true;
            if (kind == "pickup-frame")
            {
                frame.SetValue(null, Time.frameCount);
                return () => frame.SetValue(null, Time.frameCount - 1);
            }
            if (kind == "portal")
            {
                var targetRoot = new GameObject("Temporary F portal"); _containerInputObjects.Add(targetRoot);
                targetRoot.transform.position = _root.transform.position + Vector3.forward;
                var target = new InputPortalTarget { PortalTransform = targetRoot.transform };
                PortalInteractionRegistry.Register(target);
                _containerPortalTarget = target;
                var input = _root.AddComponent<PortalInteractInput>();
                ActivatePriorityInput(input);
                Assert.That(PortalInteractInput.HasTarget, Is.True);
                return () => PortalInteractionRegistry.Unregister(target);
            }
            if (kind == "nearby")
            {
                var pickupRoot = new GameObject("Temporary nearby candidate state"); _containerInputObjects.Add(pickupRoot);
                var pickups = pickupRoot.AddComponent<NearbyPickupController>();
                SetInputField(pickups, "<Count>k__BackingField", 1);
                var input = _root.AddComponent<NearbyPickupInput>();
                SetInputField(input, "_pickups", pickups); ActivatePriorityInput(input);
                return () =>
                {
                    SetInputField(pickups, "<Count>k__BackingField", 0);
                    frame.SetValue(null, Time.frameCount - 1); // EditMode无自动帧推进，明确模拟原“上帧拾取”记号。
                };
            }
            // 仅构造HasStation所需的内存readmodel和HubReady状态；Session从未Awake，不触碰repository/存档。
            var stationRoot = new GameObject("Temporary memory-only F station"); stationRoot.SetActive(false);
            _containerInputObjects.Add(stationRoot);
            var session = stationRoot.AddComponent<SinglePlayerInventorySession>();
            Assert.That(_containerDefinitionClone.Catalog.TryCreateIndex(out var index, out var error), Is.True, error);
            var memory = new SinglePlayerProfile("input-priority", new ItemOwnershipStore(index));
            SetInputField(session, "_read", new InventoryReadModel(memory, "input-priority"));
            var coordinator = stationRoot.AddComponent<SinglePlayerRunCoordinator>();
            typeof(RaidState).GetProperty("Phase").SetValue(coordinator.State, RaidPhase.HubReady);
            typeof(SinglePlayerInventorySession).GetProperty("RunCoordinator").SetValue(session, coordinator);
            var definition = ScriptableObject.CreateInstance<HubDefinition>(); _containerInputObjects.Add(definition);
            definition.Stations = new[] { new HubDefinition.Station
                { Kind = HubStationKind.Warehouse, Position = _root.transform.position, DisplayName = "Temporary station" } };
            var hub = stationRoot.AddComponent<HubInteractionController>();
            SetInputField(hub, "_session", session); SetInputField(hub, "_definition", definition); SetInputField(hub, "_player", _root.transform);
            var stationInput = _root.AddComponent<HubInteractInput>();
            SetInputField(stationInput, "_hub", hub); ActivatePriorityInput(stationInput);
            return () => definition.Stations = Array.Empty<HubDefinition.Station>();
        }

        [TestCase("station")] [TestCase("portal")] [TestCase("nearby")] [TestCase("pickup-frame")]
        public void RealFDefersToOriginalInteractionsAndRequiresReleaseBeforeCollection(string kind)
        {
            CreateContainerInput(out _);
            var removeBlocker = BlockFWithOriginalInteraction(kind);
            InputState(false, UnityEngine.InputSystem.Key.F); StepContainerInput(.2f);
            Assert.That(_containerInputCommands.Count, Is.Zero);
            removeBlocker(); StepContainerInput(.2f);
            Assert.That(_containerInputCommands.Count, Is.Zero, "优先交互消失不能让同一次F尾部顺便采集。");
            InputState(false); StepContainerInput(.02f);
            InputState(false, UnityEngine.InputSystem.Key.F); StepContainerInput(.1f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            Assert.That(_containerInputCommands[0].Kind, Is.EqualTo(OnlineInventoryCommandKind.CollectMaterial));
        }

        [TestCase("blocked")] [TestCase("focus")] [TestCase("paused")] [TestCase("disconnected")]
        [TestCase("dead")] [TestCase("disabled")]
        public void HeldPourCancellationDiscardsDebtAndRequiresANewPress(string reason)
        {
            var driver = CreateContainerInput(out _);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            float oldScale = Time.timeScale;
            try
            {
                if (reason == "blocked") driver.SetBlocked(true);
                if (reason == "focus")
                {
                    InvokeInputLifecycle(driver, "OnApplicationFocus", false);
                    InvokeInputLifecycle(_containerInput, "OnApplicationFocus", false);
                }
                if (reason == "paused") Time.timeScale = 0;
                if (reason == "disconnected") driver.SetConnection(7, false);
                if (reason == "dead")
                {
                    var hit = new DamageRequest(99, 2, 10000, DamageType.True, _root.transform.position, Vector3.forward);
                    _root.GetComponent<HealthComponent>().ReceiveHit(in hit);
                }
                if (reason == "disabled")
                { InvokeInputLifecycle(_containerInput, "OnDisable"); ((Behaviour)_containerInput).enabled = false; }
                StepContainerInput(2);
                Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
                if (reason == "dead") return;
                if (reason == "focus")
                {
                    InvokeInputLifecycle(driver, "OnApplicationFocus", true);
                    InvokeInputLifecycle(_containerInput, "OnApplicationFocus", true);
                }
                Time.timeScale = oldScale;
                if (reason == "disconnected") driver.SetConnection(7, true);
                if (reason == "disabled")
                { ((Behaviour)_containerInput).enabled = true; InvokeInputLifecycle(_containerInput, "OnEnable"); }
                driver.SetBlocked(false);
                StepContainerInput(.3f);
                Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
                InputState(false); StepContainerInput(.001f);
                InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
                Assert.That(_containerInputCommands.Count, Is.EqualTo(2));
                Assert.That(_containerInputCommands[1].TransferBudget, Is.EqualTo(200ul));
            }
            finally { Time.timeScale = oldScale; }
        }

        [Test]
        public void RepeatedSameCameraBindingDoesNotInterruptHeldPour()
        {
            var driver = CreateContainerInput(out _);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.05f);
            Assert.That(_containerInputCommands.Count, Is.Zero);
            // 同一个表现相机的重复注册是幂等通知，不是失焦/换相机；真实绑定生命周期可重复到达。
            EventBus<LocalActorCameraBoundEvent>.Publish(new LocalActorCameraBoundEvent
            { Actor = driver.gameObject, Camera = _containerInputCamera });
            StepContainerInput(.05f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1), "重复通知不能清除已积累的持键时间或要求再松键。");
            for (int i = 0; i < 5; i++)
            {
                EventBus<LocalActorCameraBoundEvent>.Publish(new LocalActorCameraBoundEvent
                { Actor = driver.gameObject, Camera = _containerInputCamera });
                StepContainerInput(.1f);
            }
            Assert.That(_containerInputCommands.Count, Is.EqualTo(6));
        }

        [Test]
        public void SameBottleOwnerSnapshotVersionChangeDoesNotEraseTheStableHeldInterval()
        {
            var driver = CreateContainerInput(out var snapshot);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.05f);
            Assert.That(_containerInputCommands.Count, Is.Zero);
            snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.Assets.InventoryVersion++;
            snapshot.Assets.Items[2].Revision++; snapshot.Assets.Items[2].ContainerState.Components[0].Quantity = 99;
            Assert.That(driver.InstallClientInventory(ValidationIdentity(_containerDefinitionClone), snapshot, out var error), Is.True, error);
            StepContainerInput(.05f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1), "同瓶确认库存更新不等于换选，不能每张Owner快照重置10Hz计时。");
            Assert.That(_containerInputCommands[0].TransferBudget, Is.EqualTo(200ul));
            Assert.That(_containerInputCommands[0].ExpectedInventoryVersion, Is.EqualTo(10u));
        }

        [Test]
        public void ConfirmedBindingReplacementRequiresReleaseAndTargetsOnlyTheNewInstance()
        {
            var driver = CreateContainerInput(out var snapshot);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
            snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.SelectionVersion++; snapshot.Assets.InventoryVersion++;
            snapshot.Assets.Items[2].InstanceId = "replacement-bottle"; snapshot.Assets.Items[2].Revision++;
            Assert.That(driver.InstallClientInventory(ValidationIdentity(_containerDefinitionClone), snapshot, out var error), Is.True, error);
            StepContainerInput(.2f); Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            InputState(false); StepContainerInput(.001f);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(2));
            Assert.That(_containerInputCommands[1].ItemId, Is.EqualTo("replacement-bottle"));
            Assert.That(_containerInputCommands[1].TransferBudget, Is.EqualTo(200ul));
        }

        [Test]
        public void HigherEpochReconnectWaitsForItsConfirmedSourceAndANewPress()
        {
            var driver = CreateContainerInput(out var snapshot);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
            driver.SetConnection(8, false); StepContainerInput(1);
            driver.SetConnection(8, true); StepContainerInput(.2f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            snapshot.ConnectionEpoch = 8; snapshot.SnapshotSequence++; snapshot.ServerTick++;
            var expected = new OnlineInventoryPresentationIdentity(snapshot.Assets.RunId, snapshot.Assets.PlayerId, 1,
                snapshot.Assets.InventoryId, _containerDefinitionClone.GameplayConfigHash, 8, "validation-world");
            Assert.That(driver.InstallClientInventory(expected, snapshot, out var error), Is.True, error);
            StepContainerInput(.2f); Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            InputState(false); StepContainerInput(.001f);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(2));
            Assert.That(_containerInputCommands[1].ConnectionEpoch, Is.EqualTo(8u));
            Assert.That(_containerInputCommands[1].TransferBudget, Is.EqualTo(200ul));
        }

        [Test]
        public void HeldInputWaitsForConfirmedSelectionAndThenARelease()
        {
            var driver = CreateContainerInput(out var snapshot, selected: false);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.2f);
            Assert.That(_containerInputCommands.Count, Is.Zero);
            snapshot.SelectedToolSlotPlusOne = 1; snapshot.SelectionVersion++;
            snapshot.SnapshotSequence++; snapshot.ServerTick++;
            Assert.That(driver.InstallClientInventory(ValidationIdentity(_containerDefinitionClone), snapshot, out var error), Is.True, error);
            StepContainerInput(.2f); Assert.That(_containerInputCommands.Count, Is.Zero);
            InputState(false); StepContainerInput(.001f);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(1));
            Assert.That(_containerInputCommands[0].ItemId, Is.EqualTo("validation-bottle"));
        }

        [Test]
        public void LongHeldFrameHasOneBoundedBatchInsteadOfCatchUpRequests()
        {
            CreateContainerInput(out _);
            InputState(false, UnityEngine.InputSystem.Key.F, UnityEngine.InputSystem.Key.H); StepContainerInput(5);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(2));
            foreach (var intent in _containerInputCommands)
                Assert.That(intent.TransferBudget, Is.InRange(1ul,
                    intent.Kind == OnlineInventoryCommandKind.CollectMaterial ? 4096ul : _containerDefinitionClone.ContainerUse.MaximumPourBatchGmu));
            StepContainerInput(.01f);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(2));
        }

        [Test]
        public void SceneActorDestructionUnbindsHeldInputAndDestroysItsPreview()
        {
            CreateContainerInput(out _);
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
            int before = _containerInputCommands.Count;
            var lines = _containerInput.GetComponentsInChildren<LineRenderer>(true);
            Assert.That(lines.Length, Is.EqualTo(1));
            InvokeInputLifecycle(_containerInput, "OnDisable");
            InvokeInputLifecycle(_containerInput, "OnDestroy");
            InvokeInputLifecycle(_root.GetComponent<OnlineCastDriver>(), "OnDestroy");
            Object.DestroyImmediate(_root); _root = null;
            InputState(false); InputState(false, UnityEngine.InputSystem.Key.H);
            Assert.That(_containerInputCommands.Count, Is.EqualTo(before));
            Assert.That(lines[0] == null, Is.True);
            _containerInput = null;
        }

        [TestCase(false)] [TestCase(true)]
        public void ActualPourPreviewEndpointMatchesTheAuthorityTrajectory(bool collision)
        {
            var driver = CreateContainerInput(out _);
            _root.transform.SetPositionAndRotation(new Vector3(9000, 7000, 9000), Quaternion.identity);
            _containerInputCamera.transform.position = _root.transform.position + Vector3.up * 1.6f;
            _containerInputCamera.transform.rotation = Quaternion.LookRotation(new Vector3(1, -.2f, 0).normalized);
            if (collision)
            {
                var wall = new GameObject("Temporary authoritative pour collision"); _containerInputObjects.Add(wall);
                wall.transform.position = _root.transform.position + new Vector3(4, 1, 0);
                wall.AddComponent<BoxCollider>().size = new Vector3(.3f, 8, 8);
            }
            Physics.SyncTransforms();
            InputState(false, UnityEngine.InputSystem.Key.H); StepContainerInput(.1f);
            var lines = _containerInput.GetComponentsInChildren<LineRenderer>(true);
            Assert.That(lines.Length, Is.EqualTo(1));
            Assert.That(lines[0].enabled, Is.True); Assert.That(lines[0].positionCount, Is.GreaterThan(1));
            Vector3 preview = lines[0].GetPosition(lines[0].positionCount - 1);
            Vector3 direction = _containerInputCommands[0].AimDirection;
            var clientRoot = _root; var clientDefinition = _containerDefinitionClone;
            try
            {
                var authority = CreateContainerAuthority(true);
                authority.transform.SetPositionAndRotation(clientRoot.transform.position, clientRoot.transform.rotation);
                authority.GetComponent<CharacterController>().enabled = false;
                Physics.SyncTransforms();
                object[] args = { direction, Vector3.zero };
                var prepare = typeof(OnlineCastDriver).GetMethod("PreparePourTarget", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That((MaterialTransferFailure)prepare.Invoke(authority, args), Is.EqualTo(MaterialTransferFailure.None));
                Assert.That(Vector3.Distance(preview, (Vector3)args[1]), Is.LessThan(.01f),
                    "读取真实LineRenderer并与DS入口比较，不能用测试复写的轨迹公式冒充预览一致。");
                Assert.That(clientRoot.transform.rotation, Is.EqualTo(Quaternion.identity));
            }
            finally
            {
                if (_root != null && _root != clientRoot)
                {
                    var authority = _root.GetComponent<OnlineCastDriver>();
                    if (authority != null) InvokeInputLifecycle(authority, "OnDestroy");
                    Object.DestroyImmediate(_root);
                }
                if (_containerDefinitionClone != clientDefinition) Object.DestroyImmediate(_containerDefinitionClone);
                _root = clientRoot; _containerDefinitionClone = clientDefinition;
            }
        }


        [TestCase(false, 0)] [TestCase(false, 2)] [TestCase(true, 0)] [TestCase(true, 6)]
        public void AuthorityDiagnosticReportsTheActualHeldItemSlotAndSelectionVersionWithoutAdvancingState(bool tool, int slot)
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var source = ValidationSnapshot(definition);
            source.Assets.Items[0].Slot = tool ? 0 : slot;
            source.Assets.Items[2].Slot = tool ? slot : 0;
            source.SelectedToolSlotPlusOne = tool ? (uint)slot + 1 : 0;
            Assert.That(OnlineCastInventoryDiagnosticSnapshot.TryFromAuthoritySnapshot(source, 90, out var diagnostic), Is.True);
            AssertDiagnosticHeldSelection(diagnostic, tool ? "validation-bottle" : Wand,
                tool ? HeldItemSlots.WandCount + slot : slot, 5);
            Assert.That(diagnostic.SelectedWandInstanceId, Is.EqualTo(Wand), "保留最后法杖ID，另报真实Held选择。");
            Assert.That(OnlineCastInventoryDiagnosticSnapshot.TryFromAuthoritySnapshot(source, 91, out var again), Is.True);
            AssertDiagnosticHeldSelection(again, tool ? "validation-bottle" : Wand,
                tool ? HeldItemSlots.WandCount + slot : slot, 5);
            Assert.That(source.SnapshotSequence, Is.EqualTo(1ul));
            Assert.That(source.Assets.InventoryVersion, Is.EqualTo(9u));
            Assert.That(source.SelectionVersion, Is.EqualTo(5u));
            Assert.That(source.Assets.Items.Length, Is.EqualTo(3));
            Assert.That(source.Assets.Items[1].RemainingCharges, Is.EqualTo(3));
            Assert.That(source.Assets.Items[2].ContainerState.Components[0].Quantity, Is.EqualTo(100ul));
            Assert.That(source.Assets.Items[2].ContainerState.Components[1].Quantity, Is.EqualTo(50ul));
        }

        [TestCase(false, 0)] [TestCase(false, 2)] [TestCase(true, 0)] [TestCase(true, 6)]
        public void ClientDiagnosticReadsTheConfirmedHeldSelectionWithoutAdvancingReplicaOrAmounts(bool tool, int slot)
        {
            var driver = CreateSelectionClient(out var snapshot, install: false, toolSlot: tool ? slot : 0, toolSelected: tool);
            snapshot.Assets.Items[0].Slot = tool ? 0 : slot;
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            var source = _root.GetComponent<OnlineInventoryReplica>().Source;
            Assert.That(driver.TryCaptureClientInventoryDiagnostic(90, out var diagnostic), Is.True);
            AssertDiagnosticHeldSelection(diagnostic, tool ? "validation-bottle" : Wand,
                tool ? HeldItemSlots.WandCount + slot : slot, 5);
            Assert.That(diagnostic.SelectedWandInstanceId, Is.EqualTo(Wand));
            Assert.That(driver.TryCaptureClientInventoryDiagnostic(91, out var again), Is.True);
            AssertDiagnosticHeldSelection(again, tool ? "validation-bottle" : Wand,
                tool ? HeldItemSlots.WandCount + slot : slot, 5);
            Assert.That(driver.ClientInventorySequence, Is.EqualTo(1ul));
            Assert.That(driver.ClientInventoryVersion, Is.EqualTo(9u));
            Assert.That(source.ToolbeltReadOnly.SelectionVersion, Is.EqualTo(5u));
            Assert.That(source.ReadOnly.Count, Is.EqualTo(3));
            Assert.That(source.ReadOnly.TryGetView(Card, out var card), Is.True);
            Assert.That(card.RemainingCharges, Is.EqualTo(3));
            var components = new MaterialAmount[8];
            Assert.That(source.TryCopyContainer("validation-bottle", components, out int count, out ulong total, out _), Is.True);
            Assert.That(count, Is.EqualTo(2)); Assert.That(total, Is.EqualTo(150ul));
            Assert.That(components[0].Quantity, Is.EqualTo(100ul)); Assert.That(components[1].Quantity, Is.EqualTo(50ul));
        }

        private static void AssertDiagnosticHeldSelection(OnlineCastInventoryDiagnosticSnapshot diagnostic, string instance, int heldSlot, uint version)
        {
            // 新字段通过现有反射断言检查：缺实现是行为RED，不让诊断ABI尚未落盘造成测试编译阻断。
            Assert.That(DiagnosticValue(diagnostic, "SelectedItemInstanceId"), Is.EqualTo(instance));
            Assert.That(DiagnosticValue(diagnostic, "SelectedHeldSlot"), Is.EqualTo(heldSlot));
            Assert.That(DiagnosticValue(diagnostic, "SelectionVersion"), Is.EqualTo(version));
            Assert.That(diagnostic.TotalItemCount, Is.EqualTo(3));
            Assert.That(diagnostic.InventoryVersion, Is.EqualTo(9u));
        }


        private OnlineCastDriver CreateGroundAuthority(out OnlineOwnedAssetSnapshot assets)
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            assets = ValidationSnapshot(definition).Assets;
            assets.InventoryId = "ground-character:" + Guid.NewGuid().ToString("N");
            assets.Items[2].InstanceId = new string('c', 32);
            var items = new List<OnlineOwnedItemSnapshot>(assets.Items)
            { new OnlineOwnedItemSnapshot { InstanceId = new string('d', 32), ContentId = "wandtest.bag", Revision = 1, LocationKind = 4 } };
            assets.Items = items.ToArray();
            var driver = (OnlineCastDriver)Create(out _, definitionOverride: definition, assetsOverride: assets);
            driver.transform.position = new Vector3(9000, 7000, 9000);
            return driver;
        }

        [Test]
        public void GroundDropWithoutWorldOrSupportDoesNotRemoveTheCarriedInstance()
        {
            var driver = CreateGroundAuthority(out var assets);
            var command = new OnlineInventoryCommandIntent
            { CommandId = 101, ConnectionEpoch = 1, ExpectedInventoryVersion = driver.OwnedInventoryVersion,
                Kind = OnlineInventoryCommandKind.DropContainer, ItemId = new string('c', 32) };
            Assert.That(driver.ExecuteInventoryCommand(command).Failure, Is.EqualTo(OnlineInventoryCommandFailure.NotReady));
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            using (var world = new OnlineGroundItemAuthority(definition, driver.gameObject.scene))
            {
                Assert.That(driver.ConfigureGroundWorld(world), Is.True);
                command.CommandId++;
                Assert.That(driver.ExecuteInventoryCommand(command).Success, Is.False);
                Assert.That(driver.OwnedInventoryVersion, Is.EqualTo(assets.InventoryVersion));
                Assert.That(world.Capture().Items, Is.Empty);
                Assert.That(driver.TryCaptureInventory(1, 1, 1, out var snapshot), Is.True);
                Assert.That(Array.Find(snapshot.Assets.Items, item => item.InstanceId == command.ItemId).ContainerState.Components[0].Quantity, Is.EqualTo(100ul));
            }
        }

        [Test]
        public void GroundDriverPreservesInstanceComponentsAndReplayDoesNotRemoveALaterDrop()
        {
            var driver = CreateGroundAuthority(out _);
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var floor = new GameObject("Temporary original drop support");
            floor.transform.position = driver.transform.position + Vector3.down * .5f;
            floor.AddComponent<BoxCollider>().size = new Vector3(20, 1, 20);
            Physics.SyncTransforms();
            var world = new OnlineGroundItemAuthority(definition, driver.gameObject.scene);
            int registered = WorldItemPickup.RegisteredCount;
            try
            {
                Assert.That(driver.ConfigureGroundWorld(world), Is.True);
                var drop = new OnlineInventoryCommandIntent
                { CommandId = 101, ConnectionEpoch = 1, ExpectedInventoryVersion = driver.OwnedInventoryVersion,
                    Kind = OnlineInventoryCommandKind.DropContainer, ItemId = new string('c', 32) };
                Assert.That(driver.ExecuteInventoryCommand(drop).Success, Is.True);
                var first = world.Capture();
                Assert.That(first.Items.Length, Is.EqualTo(1));
                Assert.That(first.Items[0].InstanceId, Is.EqualTo(drop.ItemId));
                Assert.That(first.Items[0].ContainerState.Components[0].Quantity, Is.EqualTo(100ul));
                Assert.That(first.Items[0].ContainerState.Components[1].Quantity, Is.EqualTo(50ul));
                Assert.That(WorldItemPickup.RegisteredCount, Is.EqualTo(registered + 1));
                Assert.That(driver.ExecuteInventoryCommand(drop).Success, Is.True);
                Assert.That(world.Version, Is.EqualTo(first.Version));
                var pickup = new OnlineInventoryCommandIntent
                { CommandId = 102, ConnectionEpoch = 1, ExpectedInventoryVersion = driver.OwnedInventoryVersion,
                    Kind = OnlineInventoryCommandKind.PickupContainer, ItemId = drop.ItemId, WorldOwnerId = first.Items[0].WorldOwnerId, AutoPlace = true };
                Assert.That(driver.ExecuteInventoryCommand(pickup).Success, Is.True);
                Assert.That(world.Capture().Items, Is.Empty);
                Assert.That(WorldItemPickup.RegisteredCount, Is.EqualTo(registered));
                Assert.That(driver.TryCaptureInventory(1, 2, 2, out var owner), Is.True);
                var item = Array.Find(owner.Assets.Items, value => value.InstanceId == drop.ItemId);
                Assert.That(item.LocationKind, Is.EqualTo((int)ItemLocationKind.Backpack));
                Assert.That(item.ParentInstanceId, Is.EqualTo(new string('d', 32)));
                Assert.That(item.ContainerState.Components[0].Quantity, Is.EqualTo(100ul));
                drop.CommandId = 103; drop.ExpectedInventoryVersion = driver.OwnedInventoryVersion;
                Assert.That(driver.ExecuteInventoryCommand(drop).Success, Is.True);
                var later = world.Capture();
                Assert.That(driver.ExecuteInventoryCommand(pickup).Success, Is.True);
                Assert.That(world.Version, Is.EqualTo(later.Version));
                Assert.That(world.Capture().Items.Length, Is.EqualTo(1));
                Assert.That(WorldItemPickup.RegisteredCount, Is.EqualTo(registered + 1));
                InvokeInputLifecycle(driver, "OnDestroy");
                Assert.That(world.Capture().Items.Length, Is.EqualTo(1), "单Actor不能Dispose房间共享地面。");
            }
            finally { world.Dispose(); Object.DestroyImmediate(floor); }
        }

        [Test]
        public void ClientGroundProjectionRebindsCurrentStoreWithoutOptimisticPickupOrOwnerProbeInflation()
        {
            var driver = CreateSelectionClient(out var snapshot, install: false);
            snapshot.Ground = new OnlineGroundInventorySnapshot
            {
                Version = 1, Items = new[] { new OnlineGroundItemSnapshot
                { InstanceId = new string('e', 32), ContentId = "containertest.bottle", WorldOwnerId = new string('f', 32), Revision = 1,
                    Position = _root.transform.position + Vector3.forward, Rotation = Quaternion.identity,
                    ContainerState = new OnlineOwnedContainerStateSnapshot { SchemaVersion = 1, Components = Array.Empty<OnlineOwnedMaterialAmountSnapshot>() } } }
            };
            // 完整实例是地面根唯一事实；外层身份/组分别名须与它一致，不能再只造旧版瓶别名。
            var ground = snapshot.Ground.Items[0];
            ground.Item = new OnlineOwnedItemSnapshot
            { InstanceId = ground.InstanceId, ContentId = ground.ContentId, Revision = ground.Revision,
                LocationKind = (int)ItemLocationKind.WorldItem, ParentInstanceId = string.Empty, Slot = -1, ContainerState = ground.ContainerState };
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            int registered = WorldItemPickup.RegisteredCount;
            Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out var error), Is.True, error);
            var source = _root.GetComponent<OnlineInventoryReplica>().Source;
            string id = snapshot.Ground.Items[0].InstanceId;
            var proxy = WorldItemPickup.FindNearby(source.ReadOnly.InventoryId, id, _root.transform, 3);
            Assert.That(proxy, Is.Not.Null);
            int requests = 0; OnlineInventoryCommandRequestedEvent observed = default;
            Action<OnlineInventoryCommandRequestedEvent> listener = value => { requests++; observed = value; };
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(listener);
            try
            {
                Assert.That(proxy.RequestOnlinePickup(), Is.True);
                Assert.That(requests, Is.EqualTo(1));
                Assert.That(observed.CallerId, Is.EqualTo(proxy.GetInstanceID()));
                Assert.That(observed.Intent.Kind, Is.EqualTo(OnlineInventoryCommandKind.PickupContainer));
                Assert.That(observed.Intent.WorldOwnerId, Is.EqualTo(new string('f', 32)));
                Assert.That(observed.Intent.AutoPlace, Is.True);
                Assert.That(proxy.gameObject.activeSelf, Is.True);
                Assert.That(proxy.TryCollect(new ItemLocation(ItemLocationKind.SpellStorage, "validation-owner", 0, 0, 0, 0), 9).Success, Is.False);
                Assert.That(source.ReadOnly.Version, Is.EqualTo(9u));
                Assert.That(driver.TryCaptureClientInventoryDiagnostic(90, out var diagnostic), Is.True);
                Assert.That(diagnostic.TotalItemCount, Is.EqualTo(3), "Owner probe不能把共享地面算作随身。");
                snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.Assets.InventoryVersion++;
                Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out error), Is.True, error);
                Assert.That(WorldItemPickup.FindNearby(source.ReadOnly.InventoryId, id, _root.transform, 3), Is.SameAs(proxy));
                Assert.That(WorldItemPickup.RegisteredCount, Is.EqualTo(registered + 1));
                snapshot.SnapshotSequence++; snapshot.ServerTick++; snapshot.Ground = new OnlineGroundInventorySnapshot { Version = 2 };
                Assert.That(driver.InstallClientInventory(ValidationIdentity(definition), snapshot, out error), Is.True, error);
                Assert.That(WorldItemPickup.FindNearby(source.ReadOnly.InventoryId, id, _root.transform, 3), Is.Null);
                Assert.That(WorldItemPickup.RegisteredCount, Is.EqualTo(registered));
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(listener); }
        }

        [Test] public void OnlineSelectionReferencesReuseTheOriginalWandInputActions()
        {
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            var get = typeof(OnlineCombatDefinition).GetMethod("GetWandSelectionAction");
            Assert.That(get, Is.Not.Null, "动态创建的OwnedAdapter没有Inspector引用，必须从原输入资产显式装配。");
            for (int slot = 0; slot < 3; slot++)
            {
                var original = AssetDatabase.LoadMainAssetAtPath(
                    "Assets/_Project/ScriptableObjects/Input/Wands/Action_Select" + (slot + 1) + ".asset");
                Assert.That(get.Invoke(definition, new object[] { slot }), Is.SameAs(original));
            }
        }
    }
}
