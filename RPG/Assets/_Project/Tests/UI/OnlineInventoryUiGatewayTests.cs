using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.UI.Tests
{
    /// <summary>
    /// 使用原库存 Controller 与真实线上 Source，验证 UI 只发意图、不改资产。
    /// UI 测试程序集不依赖 Run/Skills；Reflection 仅用于测试装配和新合同 RED，生产不使用这种查找。
    /// </summary>
    public sealed class OnlineInventoryUiGatewayTests : InputTestFixture
    {
        private readonly List<Object> _objects = new List<Object>();
        private readonly List<Action> _unsubscribers = new List<Action>();
        private SinglePlayerInventoryController _ui;
        private Text _status;
        private object _source;
        private object _read;
        private object _request;
        private int _requests, _localWrites;
        private bool _enabled;
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [SetUp]
        public void CreateRealReadSourceAndInactiveOriginalController()
        {
            _request = null; _requests = _localWrites = 0; _enabled = false; _source = _read = null;
            var wand = Definition("ui.online.wand", "Wand");
            var card = Definition("ui.online.spell", "Spell");
            var catalog = Asset("Game.Run.ItemCatalog");
            var catalogData = new SerializedObject(catalog);
            var entries = catalogData.FindProperty("_definitions"); entries.arraySize = 2;
            entries.GetArrayElementAtIndex(0).objectReferenceValue = wand;
            entries.GetArrayElementAtIndex(1).objectReferenceValue = card;
            catalogData.ApplyModifiedPropertiesWithoutUndo();
            var indexArgs = new object[] { null, null };
            Assert.That((bool)catalog.GetType().GetMethod("TryCreateIndex").Invoke(catalog, indexArgs), Is.True, indexArgs[1] as string);
            var identity = Activator.CreateInstance(Type("Game.Core.OnlineInventoryPresentationIdentity"), "run", "player", 1ul,
                "inventory", Hash, 7u, "DungeonWorld");
            var args = new[] { indexArgs[0], identity, Snapshot(), null, null };
            Assert.That((bool)Type("Game.Run.OnlineInventoryPresentationSource").GetMethod("TryCreate").Invoke(null, args), Is.True, args[4] as string);
            _source = args[3]; _read = Get(_source, "ReadOnly");
            var root = Track(new GameObject("OriginalInventoryGateway")); root.SetActive(false);
            _ui = root.AddComponent<SinglePlayerInventoryController>();
            var statusRoot = new GameObject("Status", typeof(RectTransform), typeof(Text));
            statusRoot.transform.SetParent(root.transform); _status = statusRoot.GetComponent<Text>();
            Set(_ui, "_status", _status);
            _ui.GetType().GetMethod("BindPresentationSource").Invoke(_ui, new[] { _source });
            Set(_ui, "_open", true); _status.text = "正在查看";
            Invoke(_ui, "OnEnable"); _enabled = true;
            Subscribe(typeof(InventoryCommandEvent), _ => _localWrites++);
        }

        [TearDown]
        public void DisposeOnlyTestObjectsAndSubscriptions()
        {
            if (_enabled && _ui != null) Invoke(_ui, "OnDisable");
            for (int i = _unsubscribers.Count - 1; i >= 0; i--) _unsubscribers[i]();
            _unsubscribers.Clear();
            (_source as IDisposable)?.Dispose();
            for (int i = _objects.Count - 1; i >= 0; i--) if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public void RepeatedWandAndCardInspectionDoesNotReportARejectedWrite()
        {
            _ui.SelectItem("wand"); _ui.ClickItem("wand");
            Assert.That(_status.text, Is.EqualTo("正在查看"), "重复查看法杖不应先撞上写权限检查。");
            _ui.SelectItem("card"); _ui.ClickItem("card"); _ui.InspectWandSlot(0);
            Assert.That(_status.text, Is.EqualTo("正在查看")); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void OwnedCardDragPublishesVersionedPlaceIntentWithoutEditingReadModel()
        {
            CaptureRequests(); Begin("card");
            Assert.That(_ui.IsDragging, Is.True, "线上自有法术卡可使用原拖拽表现；提交仍必须交给 DS。");
            Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1)); Assert.That(_ui.IsDragging, Is.False);
            Assert.That(Get(_request, "ActorId"), Is.EqualTo(1ul)); Assert.That(Get(_request, "CallerId"), Is.EqualTo(_ui.GetInstanceID()));
            var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "CommandId"), Is.Not.EqualTo(0ul));
            Assert.That(Get(intent, "ConnectionEpoch"), Is.EqualTo(7u));
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(9u));
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(17u));
            Assert.That(Get(intent, "TargetProgramRevision"), Is.EqualTo(17u));
            Assert.That(Get(intent, "ItemId"), Is.EqualTo("card")); Assert.That(Get(intent, "SourceWandId"), Is.EqualTo("wand"));
            Assert.That(Get(intent, "TargetWandId"), Is.EqualTo("wand")); Assert.That(Get(intent, "TargetSlot"), Is.EqualTo(1));
            Assert.That(Get(intent, "ToSpellStorage"), Is.False);
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void UnsupportedGroundDestinationCancelsWithoutSendingEitherAuthorityPath()
        {
            CaptureRequests(); Begin("card"); Submit("Ground", null, -1);
            Assert.That(_ui.IsDragging, Is.False); Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void CardToSpellStorageUsesRealStorageTargetAndKeepsAssetsUntilSnapshot()
        {
            CaptureRequests(); Begin("card"); Submit("SpellStorage", "player", 0);
            Assert.That(_requests, Is.EqualTo(1)); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "ToSpellStorage"), Is.True);
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(17u));
            Assert.That(Get(intent, "TargetProgramRevision"), Is.EqualTo(0u));
            Assert.That(Get(intent, "TargetWandId"), Is.Null.Or.Empty);
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void MidDragSnapshotDoesNotRewriteCapturedSourceVersionButTargetUsesCurrentProgram()
        {
            CaptureRequests(); Begin("card");
            var newer = Snapshot(); Set(newer, "SnapshotSequence", 2ul);
            ((OnlineOwnedAssetSnapshot)Get(newer, "Assets")).InventoryVersion = 10u;
            Set(((Array)Get(newer, "Wands")).GetValue(0), "ProgramRevision", 21u);
            var args = new[] { newer, null };
            Assert.That((bool)_source.GetType().GetMethod("TryInstall").Invoke(_source, args), Is.True, args[1] as string);
            Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1)); var intent = Get(_request, "Intent");
            Assert.That(Get(intent, "ExpectedInventoryVersion"), Is.EqualTo(9u));
            Assert.That(Get(intent, "SourceProgramRevision"), Is.EqualTo(17u));
            Assert.That(Get(intent, "TargetProgramRevision"), Is.EqualTo(21u));
            Assert.That(InventoryVersion(), Is.EqualTo(10u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void EquippedWandDragCannotBecomeEquipOrLocalInventoryCommand()
        {
            CaptureRequests(); Begin("wand");
            Assert.That(_ui.IsDragging, Is.False); Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.Zero); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void ReceiptsMustMatchActorCallerEpochAndLatestCommandAndNeverMutateAssets()
        {
            CaptureRequests(); Begin("card"); Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1));
            ulong id = (ulong)Get(Get(_request, "Intent"), "CommandId"); string pending = _status.text;
            Receipt(id, 7u, _ui.GetInstanceID() + 1, 1ul); Assert.That(_status.text, Is.EqualTo(pending));
            Receipt(id, 6u, _ui.GetInstanceID(), 1ul); Assert.That(_status.text, Is.EqualTo(pending));
            Receipt(id, 7u, _ui.GetInstanceID(), 2ul); Assert.That(_status.text, Is.EqualTo(pending));
            Receipt(id - 1, 7u, _ui.GetInstanceID(), 1ul); Assert.That(_status.text, Is.EqualTo(pending));
            Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            Assert.That(_status.text, Is.Not.EqualTo(pending), "有效收据仅反馈已受理/失败，资产仍等完整快照。");
            Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void EscapeAfterMenuThenOriginalTabClosesInventoryWithoutClosingConnectionMenu()
        {
            var cursor = Cursor.lockState; bool visible = Cursor.visible;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var root = Object.Instantiate(Resources.Load<GameObject>("Online/DungeonEntryDialog"));
            var dialog = root.GetComponent<DungeonEntryDialogController>();
            bool menuOpen = false, inventoryOpen = false;
            int menuRequests = 0;
            Action publishView = () => typeof(DungeonEntryDialogController).GetMethod("OnGameplay", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(dialog, new object[] { new OnlineMovementPresentationEvent
                { Active = true, Connected = true, MenuOpen = menuOpen, InputCaptured = menuOpen || inventoryOpen } });
            Action<OnlineMovementMenuRequestEvent> menu = change => { menuOpen = change.Open; menuRequests++; publishView(); };
            Action<InventoryPresentationVisibilityEvent> inventory = change =>
            { if (change.OwnerId == _ui.GetInstanceID()) { inventoryOpen = change.Open; publishView(); } };
            try
            {
                // 使用原 Controller、真实线上读模型及项目原 Tab/Cancel Action。仅用只读视图回放代替
                // 不属于 UI 单元测试的网络 World；按键仍走 InputSystem，而非直接 Invoke OnCancel。
                _ui.Close(); Invoke(_ui, "OnDisable"); _enabled = false;
                Set(_ui, "_toggleAction", AssetDatabase.LoadAssetAtPath<InputActionReference>("Assets/_Project/ScriptableObjects/Input/Inventory/Action_Toggle.asset"));
                Set(_ui, "_cancelAction", AssetDatabase.LoadAssetAtPath<InputActionReference>("Assets/_Project/ScriptableObjects/Input/Inventory/Action_Cancel.asset"));
                Invoke(_ui, "OnEnable"); _enabled = true;
                Invoke(dialog, "Awake"); Invoke(dialog, "OnDisable"); Invoke(dialog, "OnEnable");
                EventBus<OnlineMovementMenuRequestEvent>.Subscribe(menu);
                EventBus<InventoryPresentationVisibilityEvent>.Subscribe(inventory);
                publishView();
                PressKeys(Key.Escape); Invoke(dialog, "Update"); PressKeys();
                Assert.That(menuOpen, Is.True); Assert.That(menuRequests, Is.EqualTo(1));
                PressKeys(Key.Tab); PressKeys();
                Assert.That(_ui.IsOpen && inventoryOpen, Is.True, "连接菜单打开后，原 Tab 功能仍可打开库存。");
                PressKeys(Key.Escape); Invoke(dialog, "Update");
                Assert.That(_ui.IsOpen || inventoryOpen, Is.False, "第一下 Esc 由原库存 Cancel Action 正常关闭库存。");
                Assert.That(menuOpen, Is.True, "同一 Esc 不能从库存穿透并关闭背后的连接菜单。");
                Assert.That(menuRequests, Is.EqualTo(1));
                Assert.That(Time.timeScale, Is.EqualTo(1f));
            }
            finally
            {
                EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(menu);
                EventBus<InventoryPresentationVisibilityEvent>.Unsubscribe(inventory);
                Invoke(dialog, "OnDisable"); Object.DestroyImmediate(root);
                InputSystem.RemoveDevice(keyboard);
                Cursor.lockState = cursor; Cursor.visible = visible;
            }
            void PressKeys(params Key[] keys)
            { InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys)); InputSystem.Update(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LateDialogRecoversAlreadyOpenOriginalInventoryBeforeEscape(bool menuOpen)
        { ExerciseDialogResubscriptionWithOpenInventory(menuOpen, false); }

        [TestCase(false)]
        [TestCase(true)]
        public void RestartedDialogRecoversAlreadyOpenOriginalInventoryBeforeEscape(bool menuOpen)
        { ExerciseDialogResubscriptionWithOpenInventory(menuOpen, true); }

        private void ExerciseDialogResubscriptionWithOpenInventory(bool menuOpen, bool restart)
        {
            var cursor = Cursor.lockState; bool visible = Cursor.visible;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var root = Object.Instantiate(Resources.Load<GameObject>("Online/DungeonEntryDialog"));
            var dialog = root.GetComponent<DungeonEntryDialogController>();
            bool inventoryOpen = false; int menuRequests = 0;
            Action publishView = () => EventBus<OnlineMovementPresentationEvent>.Publish(new OnlineMovementPresentationEvent
            { Active = true, Connected = true, MenuOpen = menuOpen, InputCaptured = menuOpen || inventoryOpen });
            Action<InventoryPresentationVisibilityEvent> inventory = change =>
            { if (change.OwnerId == _ui.GetInstanceID()) { inventoryOpen = change.Open; publishView(); } };
            Action<OnlineMovementMenuRequestEvent> menu = change => { menuOpen = change.Open; menuRequests++; publishView(); };
            try
            {
                Invoke(dialog, "Awake"); Invoke(dialog, "OnDisable");
                _ui.Close(); Invoke(_ui, "OnDisable"); _enabled = false;
                Set(_ui, "_cancelAction", AssetDatabase.LoadAssetAtPath<InputActionReference>("Assets/_Project/ScriptableObjects/Input/Inventory/Action_Cancel.asset"));
                // 用真正活跃的原 Controller 和真实只读 Source：恢复必须从已存在的 UI 事实取得，
                // 不能让测试在 Dialog 重启后再补发一次 Visibility，掩盖生产中的漏订阅。
                _ui.gameObject.SetActive(true); Invoke(_ui, "OnEnable"); _enabled = true;
                EventBus<InventoryPresentationVisibilityEvent>.Subscribe(inventory);
                EventBus<OnlineMovementMenuRequestEvent>.Subscribe(menu);
                if (restart) { Invoke(dialog, "OnEnable"); publishView(); }
                _ui.OpenBackpack(); Assert.That(_ui.IsOpen && inventoryOpen, Is.True);
                if (restart) Invoke(dialog, "OnDisable");
                Invoke(dialog, "OnEnable"); publishView();
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape)); InputSystem.Update();
                Invoke(dialog, "Update");
                Assert.That(_ui.IsOpen || inventoryOpen, Is.False, "Esc 仍由真实原库存 Cancel Action 关闭面板。");
                Assert.That(menuRequests, Is.Zero, "晚启用或重启 Dialog 必须恢复旧库存 Owner，同次 Esc 不能切换连接菜单。");
            }
            finally
            {
                EventBus<InventoryPresentationVisibilityEvent>.Unsubscribe(inventory);
                EventBus<OnlineMovementMenuRequestEvent>.Unsubscribe(menu);
                Invoke(dialog, "OnDisable"); Object.DestroyImmediate(root);
                InputSystem.RemoveDevice(keyboard); Cursor.lockState = cursor; Cursor.visible = visible;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SuccessfulReceiptAndInstalledSnapshotFinishStatusInEitherArrivalOrder(bool snapshotFirst)
        {
            ulong id = SubmitCardMove();
            if (snapshotFirst) InstallSnapshot(10u, 2ul, 1);
            Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            if (!snapshotFirst)
            {
                Assert.That(_status.text, Does.Contain("等待"));
                Assert.That(InventoryVersion(), Is.EqualTo(9u)); Assert.That(CardSlot(), Is.EqualTo(0), "收据不能乐观移动资产。");
                InstallSnapshot(10u, 2ul, 1);
            }
            Assert.That(_status.text, Is.EqualTo("库存已更新。"), "成功收据与对应真实库存均已到达后，不能永久显示等待快照。");
            Assert.That(InventoryVersion(), Is.EqualTo(10u)); Assert.That(CardSlot(), Is.EqualTo(1)); Assert.That(_localWrites, Is.Zero);
        }

        [Test]
        public void InventoryChangedNotificationCannotFinishReceiptBeforeRealReadModelReachesVersion()
        {
            ulong id = SubmitCardMove(); Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = "inventory", Version = 10u });
            Invoke(_ui, "LateUpdate");
            Assert.That(_status.text, Does.Contain("等待"), "事件仅为刷新通知，不能取代真实只读模型中的版本。");
            Assert.That(InventoryVersion(), Is.EqualTo(9u));
            InstallSnapshot(10u, 2ul, 1);
            Assert.That(_status.text, Is.EqualTo("库存已更新。"));
        }

        [Test]
        public void FailedReceiptIsNotTurnedIntoSuccessByAnUnrelatedInventorySnapshot()
        {
            ulong id = SubmitCardMove(); Receipt(id, 7u, _ui.GetInstanceID(), 1ul, OnlineInventoryCommandFailure.NoCapacity);
            string failure = _status.text;
            InstallSnapshot(10u, 2ul, 0);
            Assert.That(_status.text, Is.EqualTo(failure)); Assert.That(_status.text, Does.Contain("目标槽位不可用"));
            Assert.That(CardSlot(), Is.EqualTo(0)); Assert.That(_localWrites, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ClosingAndReopeningInventoryDiscardsOldReceiptAndSnapshotStatus(bool receiptBeforeClose)
        {
            ulong id = SubmitCardMove();
            if (receiptBeforeClose) Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            _ui.Close(); _ui.Open(); string reopened = _status.text;
            if (!receiptBeforeClose) Receipt(id, 7u, _ui.GetInstanceID(), 1ul);
            InstallSnapshot(10u, 2ul, 1);
            Assert.That(_status.text, Is.EqualTo(reopened), "关闭结束本次交互；重开后的提示不接收上次操作迟到的收据/完成状态。");
            Assert.That(CardSlot(), Is.EqualTo(1), "关闭 UI 不阻止安装真实资产快照，只结束旧提示关联。");
        }

        private ulong SubmitCardMove()
        {
            CaptureRequests(); Begin("card"); Submit("ProgramSlot", "wand", 1);
            Assert.That(_requests, Is.EqualTo(1));
            return (ulong)Get(Get(_request, "Intent"), "CommandId");
        }
        private void InstallSnapshot(uint version, ulong sequence, int cardSlot)
        {
            var snapshot = Snapshot(); Set(snapshot, "SnapshotSequence", sequence);
            var assets = (OnlineOwnedAssetSnapshot)Get(snapshot, "Assets"); assets.InventoryVersion = version; assets.Items[1].Slot = cardSlot;
            var args = new[] { snapshot, null };
            Assert.That((bool)_source.GetType().GetMethod("TryInstall").Invoke(_source, args), Is.True, args[1] as string);
            EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent { InventoryId = "inventory", Version = version });
            Invoke(_ui, "LateUpdate");
        }
        private int CardSlot()
        {
            var args = new object[] { "card", null };
            Assert.That((bool)_read.GetType().GetMethod("TryGetView").Invoke(_read, args), Is.True);
            return (int)Get(Get(args[1], "Location"), "SlotIndex");
        }

        private void CaptureRequests() => Subscribe(Type("Game.Core.OnlineInventoryCommandRequestedEvent"), e => { _request = e; _requests++; });
        private uint InventoryVersion() => (uint)Type("Game.Run.IInventoryReadOnly").GetProperty("Version").GetValue(_read);
        private void Begin(string itemId)
        {
            var args = new object[] { itemId, null };
            Assert.That((bool)_read.GetType().GetMethod("TryGetView").Invoke(_read, args), Is.True);
            _ui.GetType().GetMethod("BeginDrag").Invoke(_ui, new[] { args[1], (object)Vector2.zero, null, false,
                default(Vector2Int), -1, null, null, 0f });
        }
        private void Submit(string kind, string owner, int slot) => _ui.GetType().GetMethod("SubmitToTarget").Invoke(_ui,
            new[] { Enum.Parse(Type("Game.UI.InventoryDropKind"), kind), owner, (object)slot });
        private void Receipt(ulong id, uint epoch, int caller, ulong actor, OnlineInventoryCommandFailure failure = OnlineInventoryCommandFailure.None)
        {
            var result = Activator.CreateInstance(Type("Game.Core.OnlineInventoryCommandResultEvent"));
            var receipt = Activator.CreateInstance(Type("Game.Core.OnlineInventoryCommandReceipt"));
            Set(receipt, "CommandId", id); Set(receipt, "ConnectionEpoch", epoch); Set(receipt, "InventoryVersion", 10u);
            Set(receipt, "Failure", failure);
            Set(result, "ActorId", actor); Set(result, "CallerId", caller); Set(result, "Receipt", receipt);
            typeof(EventBus<>).MakeGenericType(result.GetType()).GetMethod("Publish").Invoke(null, new[] { result });
        }
        private void Subscribe(System.Type type, Action<object> receive)
        {
            var helper = new Capture(receive);
            var callback = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(type), helper,
                typeof(Capture).GetMethod(nameof(Capture.Receive)).MakeGenericMethod(type));
            var bus = typeof(EventBus<>).MakeGenericType(type); bus.GetMethod("Subscribe").Invoke(null, new object[] { callback });
            _unsubscribers.Add(() => bus.GetMethod("Unsubscribe").Invoke(null, new object[] { callback }));
        }
        private sealed class Capture
        {
            private readonly Action<object> _receive;
            public Capture(Action<object> receive) => _receive = receive;
            public void Receive<T>(T value) where T : struct, IGameEvent => _receive(value);
        }
        private Object Definition(string id, string kind)
        {
            var asset = Asset("Game.Run.ItemDefinition"); var data = new SerializedObject(asset);
            data.FindProperty("_contentId").stringValue = id;
            data.FindProperty("_kind").intValue = Convert.ToInt32(Enum.Parse(Type("Game.Run.ItemKind"), kind));
            string childType = kind == "Wand" ? "Game.Run.WandDefinition" : "Game.Skills.SpellDefinition";
            var child = Asset(childType); var childData = new SerializedObject(child);
            if (kind == "Wand") childData.FindProperty("_programCapacity").intValue = 2;
            else childData.FindProperty("Kind").intValue = Convert.ToInt32(Enum.Parse(Type("Game.Skills.SpellKind"), "Modify"));
            childData.ApplyModifiedPropertiesWithoutUndo();
            data.FindProperty(kind == "Wand" ? "_wandDefinition" : "_spellDefinition").objectReferenceValue = child;
            data.ApplyModifiedPropertiesWithoutUndo(); return asset;
        }
        private object Snapshot()
        {
            var value = Activator.CreateInstance(Type("Game.Core.OnlineInventoryPresentationSnapshot"));
            Set(value, "SchemaVersion", 1u); Set(value, "ConnectionEpoch", 7u); Set(value, "SnapshotSequence", 1ul);
            Set(value, "ResourceVersion", 12u); Set(value, "SelectionVersion", 5u); Set(value, "SpellUseVersion", 4u);
            Set(value, "Assets", new OnlineOwnedAssetSnapshot { SchemaVersion = 1, ActorId = 1, RunId = "run", PlayerId = "player",
                InventoryId = "inventory", GameplayConfigHash = Hash, InventoryVersion = 9, SelectedWandInstanceId = "wand", Items = new[] {
                    new OnlineOwnedItemSnapshot { InstanceId = "wand", ContentId = "ui.online.wand", Revision = 1, LocationKind = 3, Slot = 0, UseTemplateWandStats = true },
                    new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "ui.online.spell", Revision = 1, LocationKind = 2, Slot = 0, ParentInstanceId = "wand", RemainingCharges = 3 } } });
            var wand = Activator.CreateInstance(Type("Game.Core.OnlineInventoryWandPresentationSnapshot"));
            Set(wand, "InstanceId", "wand"); Set(wand, "ProgramRevision", 17u); Set(wand, "CurrentMana", 37f); Set(wand, "CooldownRemaining", 1.25f);
            SetArray(value, "Wands", wand);
            var use = Activator.CreateInstance(Type("Game.Core.OnlineInventorySpellUsePresentationSnapshot"));
            Set(use, "InstanceId", "card"); Set(use, "Eligible", true); SetArray(value, "SpellUses", use); return value;
        }
        private static void SetArray(object target, string name, object item)
        { var array = Array.CreateInstance(item.GetType(), 1); array.SetValue(item, 0); Set(target, name, array); }
        private T Track<T>(T value) where T : Object { _objects.Add(value); return value; }
        private Object Asset(string type) => Track(ScriptableObject.CreateInstance(Type(type)));
        private static System.Type Type(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var found = assembly.GetType(name); if (found != null) return found; }
            Assert.Fail("缺少测试所需合同/类型：" + name); return null;
        }
        private static object Get(object target, string name)
        {
            var type = target.GetType(); var property = type.GetProperty(name);
            if (property != null) return property.GetValue(target);
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);
            foreach (var contract in type.GetInterfaces())
            { property = contract.GetProperty(name); if (property != null) return property.GetValue(target); }
            Assert.Fail("缺少字段/属性：" + type.FullName + "." + name); return null;
        }
        private static void Set(object target, string name, object value) => target.GetType().GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(target, value);
        private static void Invoke(object target, string name) => target.GetType().GetMethod(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(target, null);
    }
}
