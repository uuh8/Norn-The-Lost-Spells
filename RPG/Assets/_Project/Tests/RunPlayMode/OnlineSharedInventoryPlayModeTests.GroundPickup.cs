#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    public sealed partial class OnlineSharedInventoryPlayModeTests
    {
        private const string NewGroundSpell = "cccccccccccccccccccccccccccccc17";
        private const string GroundPickupBag = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb17";
        private const string GroundPickupWand = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa18";
        private const string GroundPickupTrigger = "dddddddddddddddddddddddddddddd18";
        private const string GroundPickupChild = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeee18";
        private const string GroundPickupCaster = "original-ground-input-caster";
        private OnlineGroundItemStore _pickupGround;
        private OnlineGroundItemPresentation _pickupPresentation;
        private EventSystem _pickupEvents;
        private EditorWindow _previousPickupWindow;
        private Mouse _pickupMouse;

        [UnityTest]
        public IEnumerator NewGroundSpellRealMouseDragIntoBackpackCommitsTheOriginalInstance()
            => CollectNewGroundSpellThroughOriginalInput(true);

        [UnityTest]
        public IEnumerator NewGroundSpellRealKeyboardFCommitsTheOriginalInstanceToSpellStorage()
            => CollectNewGroundSpellThroughOriginalInput(false);

        [UnityTest]
        public IEnumerator NewGroundSpellRealMouseDragSurvivesSameVersionOwnerSnapshotRebinding()
            => CollectNewGroundSpellThroughOriginalInput(true, true);

        [UnityTest]
        public IEnumerator NewGroundSpellRealMouseDragPreservesAnExecutableWaitingTriggerChild()
            => CollectNewGroundSpellThroughOriginalInput(true, waitingChild: true);

        [UnityTest]
        public IEnumerator NewGroundSpellRealKeyboardFPreservesAnExecutableWaitingTriggerChild()
            => CollectNewGroundSpellThroughOriginalInput(false, waitingChild: true);

        /// <summary>
        /// 补齐“自丢自捡”未覆盖的新来源：真正 Ground Store 登记新法术，随后由正式投影生成代理。
        /// Mouse/Keyboard 只排 Input System 状态，BeginDrag/Drop/F 均由生产组件自行处理。
        /// 仅 HTTP/KCP 传递用内存桥替代；桥仍执行真实 Owner/Ground 事务、收据和完整快照安装，
        /// 不手工 Publish 成功、不直接改 Store，也不把此局部用例宣称为 DS 或 NPC 掉落生成验收。
        /// </summary>
        private IEnumerator CollectNewGroundSpellThroughOriginalInput(bool drag, bool rebindWhileDragging = false,
            bool waitingChild = false)
        {
            _previousPickupWindow = EditorWindow.focusedWindow;
            // 焦点前置失败时TearDown也会执行，先保存原环境，避免尚未BeginIsolatedInput
            // 就把默认false/0写回runInBackground/Cursor或误报TimeScale恢复失败。
            _background=Application.runInBackground;_scale=Time.timeScale;_cursorMode=Cursor.lockState;_cursorVisible=Cursor.visible;
            var pickupGameView=EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor"));
            yield return RequireGroundPickupGameViewFocus(pickupGameView);
            Assert.That(Application.isFocused, Is.True, "真实输入正例需要 GameView 焦点，不能关闭产品 Focus 门。");
            BeginIsolatedInput();
            _pickupMouse = InputSystem.AddDevice<Mouse>();
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(definition, Is.Not.Null);
            Assert.That(definition.Catalog.TryCreateIndex(out var catalog, out var error), Is.True, error);
            var items = new List<OnlineOwnedItemSnapshot>
            {
                new OnlineOwnedItemSnapshot { InstanceId = GroundPickupBag, ContentId = "wandtest.bag",
                    Revision = 1, LocationKind = (int)ItemLocationKind.EquippedBackpack }
            };
            if (waitingChild)
            {
                // 全部来自正式 Catalog；有效 Termination + Fire 产生真实非空子层，不能用伪造计数器制造忙门。
                items.Add(new OnlineOwnedItemSnapshot { InstanceId = GroundPickupWand, ContentId = "norn.v2.wand.0",
                    Revision = 1, LocationKind = (int)ItemLocationKind.EquippedWand, UseTemplateWandStats = true });
                items.Add(new OnlineOwnedItemSnapshot { InstanceId = GroundPickupTrigger, ContentId = "norn.v2.termination",
                    Revision = 1, LocationKind = (int)ItemLocationKind.WandSlot, ParentInstanceId = GroundPickupWand,
                    Slot = 0, RemainingCharges = 8 });
                items.Add(new OnlineOwnedItemSnapshot { InstanceId = GroundPickupChild, ContentId = "wandtest.fire",
                    Revision = 1, LocationKind = (int)ItemLocationKind.WandSlot, ParentInstanceId = GroundPickupWand,
                    Slot = 1, RemainingCharges = 13 });
            }
            var seed = new OnlineOwnedAssetSnapshot
            {
                SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion, ActorId = 1,
                RunId = "original-ground-input-run", PlayerId = "original-ground-input-player",
                InventoryId = "original-ground-input-inventory", InventoryVersion = 1, CurrencyVersion = 1,
                GameplayConfigHash = definition.GameplayConfigHash,
                SelectedWandInstanceId = waitingChild ? GroundPickupWand : string.Empty, Items = items.ToArray()
            };
            Assert.That(OnlineOwnedWorkspaceFactory.TryCreate(catalog, seed, definition.GameplayConfigHash,
                out _authority, out error), Is.True, error);
            _pickupGround = new OnlineGroundItemStore(catalog);
            _authority.ConfigureGroundItems(_pickupGround);
            var replay = waitingChild ? PrepareGroundPickupWaitingChild() : null;
            WandExecutionSnapshot oldCast = default;
            if (waitingChild) oldCast = GroundPickupCastState();
            // 与 NPC 的计划注册入口相同；这个实例从未属于玩家，因此不能靠 self-drop 的旧 Transfer 掩盖新来源问题。
            Assert.That(_pickupGround.TryRegisterPlannedLoot("p7.input.new-spell",
                new RuntimeItemState(NewGroundSpell, "wandtest.fire", 17), Vector3.forward, Quaternion.identity,
                out var registered), Is.EqualTo(OnlineInventoryCommandFailure.None));
            Assert.That(registered, Is.EqualTo(NewGroundSpell));
            Assert.That(_pickupGround.TryGet(NewGroundSpell, out var groundBefore), Is.True);
            Assert.That(_authority.TryCapturePresentation(7, 1, 10, out var before), Is.True);
            before.Ground = _pickupGround.Capture();
            Assert.That(before.Assets.Items.Length, Is.EqualTo(items.Count), "新地面法术尚未进入 Owner。");

            _scene = SceneManager.CreateScene("OnlineOriginalGroundInputRuntime");
            var identity = new OnlineInventoryPresentationIdentity(seed.RunId, seed.PlayerId, 1, seed.InventoryId,
                definition.GameplayConfigHash, 7, _scene.name);
            Assert.That(OnlineInventoryPresentationSource.TryCreate(catalog, identity, before, out _source, out error), Is.True, error);
            var actor = new GameObject("Ground input local Actor"); actor.SetActive(false);
            SceneManager.MoveGameObjectToScene(actor, _scene); actor.AddComponent<HealthComponent>();
            var root = new GameObject("Ground input formal Scope"); root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, _scene); _scope = root.AddComponent<CombatPresentationScope>();
            typeof(CombatPresentationScope).GetField("_online", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(_scope, true);
            CreateGroundPickupInputModule();
            _pickupPresentation = new OnlineGroundItemPresentation(_source, actor.transform);
            Assert.That(_pickupPresentation.Sync(), Is.True);
            ActivateScopeWithIsolatedInventoryInput(root, actor);
            // Scope 只负责 Run Controller；正式 Character adapter 与 Driver 中使用的组件相同。
            // 这里显式装配 adapter，因此不声称测试 OnlineCastDriver 的自动装配/网络身份门。
            var nearby = actor.GetComponent<NearbyPickupController>();
            Assert.That(nearby, Is.Not.Null);
            var pickupInput = actor.AddComponent<NearbyPickupInput>(); pickupInput.ConfigureOnline(nearby);
            actor.SetActive(true);
            yield return null; yield return null; yield return null;
            var inventory = root.GetComponentInChildren<SinglePlayerInventoryController>(true);
            Assert.That(inventory.PresentationSource, Is.SameAs(_source));
            Assert.That(EventSystem.current, Is.SameAs(_pickupEvents));
            Assert.That(_pickupEvents.currentInputModule, Is.TypeOf<InputSystemUIInputModule>());
            nearby.RefreshCandidates();
            var proxy = WorldItemPickup.FindNearby(_source.ReadOnly.InventoryId, NewGroundSpell, actor.transform, 3);
            Assert.That(proxy, Is.Not.Null); Assert.That(nearby.FocusedPickup, Is.SameAs(proxy));
            Assert.That(proxy.WorldOwner, Is.EqualTo(groundBefore.WorldOwnerId));

            var requests = new List<OnlineInventoryCommandRequestedEvent>();
            Action<OnlineInventoryCommandRequestedEvent> observe = request =>
            { if (request.ActorId == 1 && request.Intent.ItemId == NewGroundSpell) requests.Add(request); };
            EventBus<OnlineInventoryCommandRequestedEvent>.Subscribe(observe);
            try
            {
                if (drag)
                {
                    yield return GroundPickupKey(Key.Tab);
                    Assert.That(inventory.IsOpen, Is.True); Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
                    yield return DragNewGroundSpellByMouse(inventory, actor, rebindWhileDragging);
                }
                else
                {
                    Assert.That(inventory.IsOpen, Is.False);
                    yield return GroundPickupKey(Key.F);
                    Assert.That(nearby.HasPendingOnlinePickup, Is.True);
                }
                Assert.That(requests.Count, Is.EqualTo(1), "一次真实输入只能形成一条原 PickupItem 意图。");
                var request = requests[0]; var intent = request.Intent;
                Assert.That(request.CallerId, Is.EqualTo(drag ? inventory.GetInstanceID() : proxy.GetInstanceID()));
                Assert.That(intent.Kind, Is.EqualTo(OnlineInventoryCommandKind.PickupItem));
                Assert.That(intent.ConnectionEpoch, Is.EqualTo(7u));
                Assert.That(intent.ExpectedInventoryVersion, Is.EqualTo(before.Assets.InventoryVersion));
                Assert.That(intent.WorldOwnerId, Is.EqualTo(groundBefore.WorldOwnerId));
                Assert.That(intent.AutoPlace, Is.EqualTo(!drag));
                if (drag)
                {
                    Assert.That(intent.TargetLocationKind, Is.EqualTo((int)ItemLocationKind.Backpack));
                    Assert.That(intent.TargetBackpackId, Is.EqualTo(GroundPickupBag));
                    Assert.That(intent.GridX, Is.EqualTo(2)); Assert.That(intent.GridY, Is.EqualTo(2));
                    Assert.That(intent.QuarterTurns, Is.Zero);
                }
                Assert.That(_source.ReadOnly.TryGetView(NewGroundSpell, out var waiting), Is.True);
                Assert.That(waiting.Location.Kind, Is.EqualTo(ItemLocationKind.WorldItem));
                Assert.That(_pickupGround.Count, Is.EqualTo(1));
                Assert.That(proxy.gameObject.activeInHierarchy, Is.True, "请求本身不能乐观消除地面模型。");
                if (waitingChild) AssertGroundPickupWaitingChild(replay, oldCast);

                var receipt = _authority.ExecuteInventoryCommand(intent, new OnlineContainerAuthorityContext
                { CanAdvance = true, WorldId = _pickupGround.WorldId, ActorPosition = actor.transform.position });
                Assert.That(receipt.Success, Is.True, "真实 Ground/Owner 拒绝：" + receipt.Failure);
                Assert.That(receipt.CommandId, Is.EqualTo(intent.CommandId));
                Assert.That(receipt.ConnectionEpoch, Is.EqualTo(intent.ConnectionEpoch));
                Assert.That(receipt.InventoryVersion, Is.GreaterThan(before.Assets.InventoryVersion));
                Assert.That(_pickupGround.Count, Is.Zero);
                if (waitingChild) AssertGroundPickupWaitingChild(replay, oldCast);
                EventBus<OnlineInventoryCommandResultEvent>.Publish(new OnlineInventoryCommandResultEvent
                { ActorId = 1, CallerId = request.CallerId, Receipt = receipt });
                Assert.That(_source.ReadOnly.TryGetView(NewGroundSpell, out waiting), Is.True);
                Assert.That(waiting.Location.Kind, Is.EqualTo(ItemLocationKind.WorldItem), "收据仍不能冒充完整 Owner 快照。");

                Assert.That(_authority.TryCapturePresentation(7, _source.SnapshotSequence + 1, 12, out var after), Is.True);
                after.Ground = _pickupGround.Capture();
                Assert.That(_source.TryInstall(after, out error), Is.True, error);
                Assert.That(_pickupPresentation.Sync(), Is.True);
                EventBus<InventoryChangedEvent>.Publish(new InventoryChangedEvent
                { InventoryId = _source.ReadOnly.InventoryId, Version = _source.ReadOnly.Version });
                yield return null; yield return null;
                nearby.RefreshCandidates();
                Assert.That(_source.ReadOnly.TryGetView(NewGroundSpell, out var collected), Is.True);
                Assert.That(collected.Location.Kind, Is.EqualTo(drag ? ItemLocationKind.Backpack : ItemLocationKind.SpellStorage));
                Assert.That(collected.Location.OwnerId, Is.EqualTo(drag ? GroundPickupBag : seed.PlayerId));
                Assert.That(collected.RemainingCharges, Is.EqualTo(17));
                if (drag)
                {
                    Assert.That(collected.Location.X, Is.EqualTo(intent.GridX));
                    Assert.That(collected.Location.Y, Is.EqualTo(intent.GridY));
                    Assert.That(collected.Location.QuarterTurns, Is.EqualTo(intent.QuarterTurns));
                }
                Assert.That(after.Assets.Items.Length, Is.EqualTo(before.Assets.Items.Length + 1));
                int matchingIds = 0;
                foreach (var item in after.Assets.Items) if (item.InstanceId == NewGroundSpell)
                { matchingIds++; Assert.That(item.Revision, Is.GreaterThanOrEqualTo(groundBefore.Revision)); }
                Assert.That(matchingIds, Is.EqualTo(1), "同一新来源实例只能进入 Owner 一次。");
                Assert.That(_source.TryGetGroundPose(NewGroundSpell, out _, out _), Is.False);
                Assert.That(WorldItemPickup.FindNearby(_source.ReadOnly.InventoryId, NewGroundSpell, actor.transform, 3), Is.Null);
                Assert.That(nearby.Count, Is.Zero); Assert.That(nearby.HasPendingOnlinePickup, Is.False);
                Assert.That(inventory.IsDragging, Is.False);
                if (drag)
                {
                    Assert.That(inventory.LastOnlineCommandReceipt.HasValue, Is.True);
                    Assert.That(inventory.LastOnlineCommandReceipt.Value.CommandId, Is.EqualTo(intent.CommandId));
                    Assert.That(inventory.LastOnlineCommandReceipt.Value.Success, Is.True);
                }
                if (waitingChild)
                {
                    AssertGroundPickupWaitingChild(replay, oldCast);
                    CompleteGroundPickupWaitingChild(replay, oldCast);
                }
            }
            finally { EventBus<OnlineInventoryCommandRequestedEvent>.Unsubscribe(observe); }
        }

        /// <summary>
        /// 真实 Owner 执行 Prepare/Complete，只有世界观察由 fixture 通过原 EventBus 提供。
        /// 此处没有创建 Projectile；测试证明原输入拾取与活票据并存，不宣称验证碰撞或 Trigger 的 Physics 时机。
        /// 根观察后仍保留可执行的 Unattempted 子层，不能 Cancel/改 ActiveReservations 让拾取虚假通过。
        /// </summary>
        private WandExecutionReplay PrepareGroundPickupWaitingChild()
        {
            var initial = GroundPickupCastState();
            var prepared = _authority.Execute(GroundPickupCastRequest(CastResourceOperation.PrepareRoot, 1));
            Assert.That(prepared.Success, Is.True, prepared.Reason.ToString());
            var complete = GroundPickupCastRequest(CastResourceOperation.Complete, 2);
            complete.TicketId = prepared.TicketId;
            var committed = _authority.Execute(complete);
            Assert.That(committed.Success, Is.True, committed.Reason.ToString());
            Assert.That(_authority.TryGetLatestReplay(GroundPickupWand, out var replay), Is.True);
            Assert.That(replay.TicketId, Is.EqualTo(prepared.TicketId));
            Assert.That(replay.Graph.Scopes.Count, Is.EqualTo(2));
            Assert.That(replay.Graph.Outputs.Count, Is.EqualTo(2));
            Assert.That(replay.Graph.Scopes[1].OutputIds.Count, Is.EqualTo(1), "必须是真正可执行的子 Fire，不能退化为空 Trigger 正例。");
            Assert.That(replay.Graph.Outputs[0].PayloadScopeId, Is.EqualTo(1));
            Assert.That(replay.GetLayerStatus(0), Is.EqualTo(WandLayerResourceStatus.Committed));
            Assert.That(GroundPickupCastState().Mana, Is.LessThan(initial.Mana), "先由原事务真实支付施法资源。");
            ObserveGroundPickupLayer(replay, 0);
            AssertGroundPickupWaitingChild(replay, GroundPickupCastState());
            return replay;
        }

        private WandExecutionSnapshot GroundPickupCastState()
        {
            Assert.That(_authority.TryGetWandExecution(GroundPickupWand, out var state), Is.True);
            return state;
        }

        private CastResourceRequestedEvent GroundPickupCastRequest(CastResourceOperation operation, ulong requestId,
            WandExecutionReplay replay = null, int layer = 0)
        {
            var state = GroundPickupCastState();
            return new CastResourceRequestedEvent { InventoryId = _authority.InventoryId,
                SourceWandId = GroundPickupWand, CallerId = GroundPickupCaster, RequestId = requestId,
                UseV2 = true, Operation = operation, TicketId = replay?.TicketId, LayerIndex = layer,
                ProgramRevision = (uint)(replay == null ? state.Program.Revision : replay.Graph.Program.Revision),
                ExecutionRevision = state.Draw.ExecutionRevision, ResourceVersion = state.ResourceVersion,
                InventoryVersion = _authority.InventoryVersion };
        }

        private void AssertGroundPickupCastResources(in WandExecutionSnapshot before)
        {
            var current = GroundPickupCastState();
            Assert.That(current.Program, Is.SameAs(before.Program), "新增收纳不能重编旧杖程序。");
            Assert.That(current.Draw, Is.SameAs(before.Draw), "新增收纳/子层提交不能重置已经支付的抽牌状态。");
            Assert.That(current.Mana, Is.EqualTo(before.Mana));
            Assert.That(current.CastRemaining, Is.EqualTo(before.CastRemaining));
            Assert.That(current.ReloadRemaining, Is.EqualTo(before.ReloadRemaining));
            Assert.That(_authority.WandsReadOnly.SelectedWandId, Is.EqualTo(GroundPickupWand));
            Assert.That(_authority.TryGetItem(GroundPickupTrigger, out var trigger), Is.True);
            Assert.That(trigger.RemainingCharges, Is.EqualTo(7));
            Assert.That(_authority.TryGetItem(GroundPickupChild, out var child), Is.True);
            Assert.That(child.RemainingCharges, Is.EqualTo(12), "根事务只支付一次子 Fire；拾取和子层释放不再次扣费。");
            Assert.That(_authority.OwnershipStore.TryGetLocation(GroundPickupTrigger, out var triggerLocation), Is.True);
            Assert.That(triggerLocation.Kind, Is.EqualTo(ItemLocationKind.WandSlot));
            Assert.That(triggerLocation.OwnerId, Is.EqualTo(GroundPickupWand)); Assert.That(triggerLocation.SlotIndex, Is.Zero);
            Assert.That(_authority.OwnershipStore.TryGetLocation(GroundPickupChild, out var childLocation), Is.True);
            Assert.That(childLocation.Kind, Is.EqualTo(ItemLocationKind.WandSlot));
            Assert.That(childLocation.OwnerId, Is.EqualTo(GroundPickupWand)); Assert.That(childLocation.SlotIndex, Is.EqualTo(1));
        }

        private void AssertGroundPickupWaitingChild(WandExecutionReplay replay, in WandExecutionSnapshot before)
        {
            Assert.That(_authority.V2CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1));
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Unattempted));
            Assert.That(replay.ReleasedOutputs[replay.Graph.Scopes[1].OutputIds[0]], Is.False);
            AssertGroundPickupCastResources(before);
        }

        private void CompleteGroundPickupWaitingChild(WandExecutionReplay replay, in WandExecutionSnapshot before)
        {
            var prepared = _authority.Execute(GroundPickupCastRequest(CastResourceOperation.PrepareLayer, 3, replay, 1));
            Assert.That(prepared.Success, Is.True, "拾取后的旧子层 Prepare 失败：" + prepared.Reason);
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Prepared));
            var committed = _authority.Execute(GroundPickupCastRequest(CastResourceOperation.Complete, 4, replay, 1));
            Assert.That(committed.Success, Is.True, committed.Reason.ToString());
            Assert.That(replay.GetLayerStatus(1), Is.EqualTo(WandLayerResourceStatus.Committed));
            Assert.That(_authority.V2CastResourcesReadOnly.ActiveReservations, Is.EqualTo(1), "资源提交不能冒充世界输出已观察。");
            ObserveGroundPickupLayer(replay, 1);
            Assert.That(_authority.V2CastResourcesReadOnly.ActiveReservations, Is.Zero, "旧票据只能在实际终结条件满足后收集。");
            AssertGroundPickupCastResources(before);
            Assert.That(_authority.TryGetItem(NewGroundSpell, out var collected), Is.True);
            Assert.That(collected.RemainingCharges, Is.EqualTo(17), "子层提交不能覆盖或消费刚拾取的独立实例。");
        }

        private void ObserveGroundPickupLayer(WandExecutionReplay replay, int layer)
        {
            foreach (int output in replay.Graph.Scopes[layer].OutputIds)
            {
                EventBus<SpellExecutionObservedEvent>.Publish(new SpellExecutionObservedEvent {
                    InventoryId = _authority.InventoryId, SourceWandId = GroundPickupWand, CallerId = GroundPickupCaster,
                    TicketId = replay.TicketId, ProgramRevision = (uint)replay.Graph.Program.Revision,
                    LayerId = layer, OutputId = output, SimulationTime = layer + 1,
                    Kind = SpellExecutionObservationKind.OutputReleased });
                Assert.That(replay.ReleasedOutputs[output], Is.True, "由正式 Owner 接受匹配世界观察，不直接调用 Collect。");
            }
        }

        private void CreateGroundPickupInputModule()
        {
            var root = new GameObject("Original ground input EventSystem"); root.SetActive(false);
            SceneManager.MoveGameObjectToScene(root, _scene);
            _pickupEvents = root.AddComponent<FocusAwareEventSystem>();
            var module = root.AddComponent<InputSystemUIInputModule>();
            // 不使用包内 static defaultActions，避免 InputTestFixture 切换 runtime 后复用旧 device state。
            // 绑定采用原 UI InputModule 的 Mouse position/leftButton，仍由它执行 Raycast/阈值/拖放派发。
            var actions = ScriptableObject.CreateInstance<InputActionAsset>();
            var map = actions.AddActionMap("UI");
            var point = map.AddAction("Point", InputActionType.PassThrough, "<Mouse>/position", expectedControlLayout: "Vector2");
            var click = map.AddAction("Click", InputActionType.PassThrough, "<Mouse>/leftButton", expectedControlLayout: "Button");
            var pointReference = InputActionReference.Create(point); var clickReference = InputActionReference.Create(click);
            _inputAssets.Add(actions, actions); _inputReferences.Add(pointReference); _inputReferences.Add(clickReference);
            module.actionsAsset = actions; module.point = pointReference; module.leftClick = clickReference;
            root.SetActive(true); EventSystem.current = _pickupEvents;
        }

        private IEnumerator GroundPickupKey(Key key)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key)); InputSystem.Update(); yield return null; yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState()); InputSystem.Update(); yield return null;
        }

        private IEnumerator GroundPickupMouse(Vector2 position, bool pressed)
        {
            var state = new MouseState { position = position };
            if (pressed) state = state.WithButton(MouseButton.Left);
            InputSystem.QueueStateEvent(_pickupMouse, state); InputSystem.Update(); yield return null; yield return null;
        }

        private IEnumerator DragNewGroundSpellByMouse(SinglePlayerInventoryController inventory, GameObject actor, bool rebindWhileDragging)
        {
            var nearby = inventory.GetComponentInChildren<NearbyItemGridView>(true);
            Assert.That(nearby, Is.Not.Null);
            InventoryItemView item = null;
            foreach (var view in nearby.GetComponentsInChildren<InventoryItemView>(false))
                if (view.Snapshot.InstanceId == NewGroundSpell) { item = view; break; }
            Assert.That(item, Is.Not.Null, "正式 Nearby 网格必须显示同一新来源实例。");
            Canvas.ForceUpdateCanvases();
            Vector2 start = ScreenCenter(item.Rect);
            AssertGroundPickupRaycast<IBeginDragHandler>(start, item.gameObject, "Nearby source");
            yield return GroundPickupMouse(start, false);
            yield return GroundPickupMouse(start, true);
            Vector2 threshold = start + Vector2.right * (_pickupEvents.pixelDragThreshold + 8);
            yield return GroundPickupMouse(threshold, true);
            Assert.That(inventory.IsDragging, Is.True, "真实 InputModule 尚未进入 BeginDrag；AppFocus="
                + Application.isFocused + ", EventFocus=" + _pickupEvents.isFocused + ", Mouse=" + _pickupMouse.position.ReadValue());
            if (rebindWhileDragging)
            {
                var oldStore = _source.PresentationItems;
                uint inventoryVersion = _source.ReadOnly.Version, groundVersion = _source.GroundVersion;
                var proxy = WorldItemPickup.FindNearby(_source.ReadOnly.InventoryId, NewGroundSpell, actor.transform, 3);
                Assert.That(proxy, Is.Not.Null);
                Assert.That(Field(proxy, "_store"), Is.SameAs(oldStore));
                Assert.That(_authority.TryCapturePresentation(7, _source.SnapshotSequence + 1, 11, out var refresh), Is.True);
                refresh.Ground = _pickupGround.Capture();
                Assert.That(refresh.Assets.InventoryVersion, Is.EqualTo(inventoryVersion));
                Assert.That(refresh.Ground.Version, Is.EqualTo(groundVersion));
                Assert.That(_source.TryInstall(refresh, out var error), Is.True, error);
                Assert.That(_source.PresentationItems, Is.Not.SameAs(oldStore), "必须实际替换投影 Store，不能只重复绑定旧数据冒充复制更新。");
                // 与 OnlineInventoryReplica.Install 的同 Owner 同版本分支一致：重放来源绑定，
                // 不伪造 InventoryChanged。随后 Driver 原本会 Sync 代理到新 Store。
                OnlineInventoryPresentation.Bind(70177, actor, _source);
                Assert.That(_pickupPresentation.Sync(), Is.True);
                Assert.That(Field(proxy, "_store"), Is.SameAs(_source.PresentationItems));
                Assert.That(WorldItemPickup.FindNearby(_source.ReadOnly.InventoryId, NewGroundSpell, actor.transform, 3), Is.SameAs(proxy));
                yield return null; yield return null;
                Assert.That(_pickupMouse.leftButton.isPressed, Is.True);
                Assert.That(inventory.IsDragging, Is.True, "同版 Owner/Store 更新不能取消正在进行的真实 Mouse 拖动。");
                Assert.That(item != null && item.isActiveAndEnabled, Is.True, "Pointer 所引用的原 Nearby View 不应被同版快照停用或销毁。");
                Assert.That(item.Snapshot.InstanceId, Is.EqualTo(NewGroundSpell));
                TestContext.Progress.WriteLine("DuringDragOwnerRefresh sequence=" + _source.SnapshotSequence
                    + ", inventoryVersion=" + inventoryVersion + ", groundVersion=" + groundVersion
                    + ", newStore=true, sameProxy=true, originalView=" + item.GetInstanceID());
            }
            var grid = (InventoryGridView)Field(inventory, "_gridView");
            var rect = (RectTransform)grid.transform;
            Vector2 end = RectTransformUtility.WorldToScreenPoint(null,
                rect.TransformPoint(new Vector3(rect.rect.xMin + 2.5f * grid.CellSize, rect.rect.yMax - 2.5f * grid.CellSize, 0)));
            for (int i = 1; i <= 3; i++) yield return GroundPickupMouse(Vector2.Lerp(threshold, end, i / 3f), true);
            AssertGroundPickupRaycast<IDropHandler>(end, grid.gameObject, "Backpack target");
            yield return GroundPickupMouse(end, false);
        }

        private void AssertGroundPickupRaycast<T>(Vector2 point, GameObject expected, string stage) where T : IEventSystemHandler
        {
            var hits = new List<RaycastResult>();
            _pickupEvents.RaycastAll(new PointerEventData(_pickupEvents) { position = point }, hits);
            var paths = new List<string>();
            foreach (var hit in hits)
            {
                string path = hit.gameObject.name;
                for (var parent = hit.gameObject.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
                paths.Add(path);
            }
            TestContext.Progress.WriteLine(stage + " point=" + point + " raycasts=" + string.Join(" | ", paths));
            Assert.That(hits, Is.Not.Empty, stage + "没有命中正式 Canvas。");
            Assert.That(ExecuteEvents.GetEventHandler<T>(hits[0].gameObject), Is.SameAs(expected),
                stage + "被遮挡，实际 Raycast 已记录；仅查询 handler，不人工分发输入。");
        }

        private void ReleaseGroundPickupFixture()
        {
            _pickupPresentation?.Dispose(); _pickupPresentation = null;
            _pickupGround?.Dispose(); _pickupGround = null;
            // EventSystem.current 的 setter 只接受已注册实例，null/停用/销毁对象都会记录 Error。
            // OnDisable 会从注册表删除本 fixture 实例，保留其他系统的原相对顺序并自然恢复 current；
            // 无旧系统时 current 自然为 null，不能把保存的过期引用强塞回包内注册表。
            if (_pickupEvents != null) _pickupEvents.enabled = false;
            _pickupEvents = null; _pickupMouse = null;
            if (_previousPickupWindow != null) _previousPickupWindow.Focus();
            _previousPickupWindow = null;
        }
    }
}
#endif
