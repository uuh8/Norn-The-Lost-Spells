using System;
using System.Collections;
using System.Reflection;
using Game.Core;
using Game.NetworkProtocol;
using Game.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 账号HTTP命令的真实接收/确认边界，不启动游戏、业务服或改本地档。
    /// UI和业务各有正式入口测试；这里防止“POST成功”或另一账号Profile被误当成已安装成功。
    /// </summary>
    public sealed class AccountInventoryCommandClientTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Player = "11111111111111111111111111111111";
        private const string Other = "22222222222222222222222222222222";
        private const string Item = "33333333333333333333333333333333";
        private const string Command = "44444444444444444444444444444444";
        private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private GameObject _root;
        private OnlineAccountClient _client;

        [SetUp] public void PrepareCurrentAccountScope()
        {
            ResetFixtureScope();
            _root = new GameObject("Account HTTP command fixture"); _root.SetActive(false);
            _client = _root.AddComponent<OnlineAccountClient>();
            _root.AddComponent<AccountCommandConsumerProbe>();
            SetClient("_token", "test-session-not-a-real-credential");
            SetClient("_expiresUnixMilliseconds", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());
            SetClient("_manifest", new ContentManifest { Hash = Hash, ProtocolVersion = ProtocolCompatibility.CurrentVersion });
            SetClient("_view", Profile());
            Assert.That(OnlineAccountInventoryHubScope.TryPublish(_client.GetInstanceID(), Snapshot(), out var error), Is.True, error);
            Assert.That(OnlineAccountInventoryHubScope.TryRequireScene(_client.GetInstanceID(), _root.scene.handle, out error), Is.True, error);
        }
        [TearDown] public void ReleaseOnlyFixture()
        {
            if (_client != null) OnlineAccountInventoryHubScope.Clear(_client.GetInstanceID());
            if (_root != null) Object.DestroyImmediate(_root);
            // 生产Clear故意保留Scene禁写标记；本测试借用Runner内存Scene，不能把该标记传给后续DS夹具。
            ResetFixtureScope();
        }
        private static void ResetFixtureScope()
            => typeof(OnlineAccountInventoryHubScope).GetMethod("Reset", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);
        private OnlineProfileReply Profile(uint version = 7, string player = Player, bool locked = false)
            => new OnlineProfileReply { PlayerId = player, Version = version, Gold = 10000,
                ContentManifestHash = Hash, AssetsLocked = locked, ActiveRunId = locked ? "active-run" : "",
                Items = new[] { new OnlineItemReply { InstanceId = Item, ContentId = "existing-item", Revision = version,
                    LocationKind = 0, ParentInstanceId = "", Area = "Warehouse", Slot = version == 7 ? 0 : 4 } } };
        private OnlineAccountInventorySnapshot Snapshot() => new OnlineAccountInventorySnapshot
        {
            SchemaVersion = OnlineAccountInventorySnapshot.CurrentSchemaVersion, PlayerId = Player, Version = 7,
            ContentManifestHash = Hash, Gold = 10000, Items = new[] { new Game.Core.OnlineOwnedItemSnapshot
                { InstanceId = Item, ContentId = "existing-item", Revision = 7, LocationKind = 0,
                    ParentInstanceId = "", Slot = 0 } }
        };
        private static Type Core(string name)
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core." + name);
            Assert.That(type, Is.Not.Null, "缺少账号命令合同，不能伪造Actor/Epoch冒充DS命令。"); return type;
        }
        private static void Set(object value, string name, object fieldValue)
        { var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, name); field.SetValue(value, fieldValue); }
        private void SetClient(string name, object value)
        { var field = typeof(OnlineAccountClient).GetField(name, Private); Assert.That(field, Is.Not.Null, name); field.SetValue(_client, value); }
        private object Request(string player = Player, uint version = 7, string command = Command, string item = Item, int? scene = null)
        {
            var intent = Activator.CreateInstance(Core("OnlineAccountInventoryCommandIntent"));
            Set(intent, "CommandId", command); Set(intent, "PlayerId", player); Set(intent, "SceneHandle", scene ?? _root.scene.handle);
            Set(intent, "ExpectedVersion", version); Set(intent, "ItemId", item);
            Set(intent, "Kind", Enum.Parse(Core("OnlineAccountInventoryCommandKind"), "Move"));
            Set(intent, "TargetLocationKind", 0); Set(intent, "TargetParentInstanceId", ""); Set(intent, "TargetSlot", -1);
            var request = Activator.CreateInstance(Core("OnlineAccountInventoryCommandRequestedEvent"));
            Set(request, "CallerId", 31); Set(request, "Intent", intent); return request;
        }
        private bool Capture(object request)
        {
            var method = typeof(OnlineAccountClient).GetMethod("TryCaptureAccountInventoryCommand", Private);
            Assert.That(method, Is.Not.Null, "需要正式账号入口冻结一次命令，不能让UI发布本地写事件。");
            var args = new[] { request, null }; return (bool)method.Invoke(_client, args);
        }
        private object Pending()
        {
            var field = typeof(OnlineAccountClient).GetField("_accountInventoryPending", Private);
            Assert.That(field, Is.Not.Null); return field.GetValue(_client);
        }
        private InventoryMoveReply Receipt(string command = Command, uint version = 8)
        {
            var reply = new InventoryMoveReply { CommandId = command, CommittedVersion = version, InstanceId = Item, Slot = 4 };
            Set(reply, "OperationKind", 1); Set(reply, "TargetLocationKind", 0); Set(reply, "TargetParentInstanceId", "");
            return reply;
        }
        [Test]
        public void RealHttpMoveReceiptWithNullWorldPoseCanConfirmTheOriginalCommand()
        {
            Assert.That(Capture(Request()), Is.True);
            var entry = typeof(OnlineAccountClient).GetMethod("CanBeginServerRun", Private);
            Assert.That((bool)entry.Invoke(_client, new object[] { null }), Is.False,
                "未确认时继续保留原入局保护，不靠清锁绕过。");
            // MySQL中实际成功收据的字段形状：普通移动的WorldPose是真正JSON null。
            // 直接new DTO会遗漏Unity反序列化边界，正是旧测试未能发现现场200后永久pending的原因。
            string json = "{\"CommandId\":\"" + Command + "\",\"CommittedVersion\":8,\"InstanceId\":\"" + Item
                + "\",\"OperationKind\":1,\"TargetLocationKind\":0,\"TargetParentInstanceId\":\"\",\"Slot\":4,"
                + "\"GridX\":0,\"GridY\":0,\"QuarterTurns\":0,\"WholeStack\":false,\"Transferred\":0,\"WorldOwnerId\":\"\",\"WorldPose\":null}";
            var parser = typeof(OnlineAccountClient).GetMethod("ParseAccountInventoryReceipt", Private | BindingFlags.Static);
            var receipt = (InventoryMoveReply)parser.Invoke(null, new object[] { json });
            Assert.That(receipt.WorldPose, Is.Null, "普通移动收据的JSON null不能被Unity转换为非null空姿态。");
            Assert.That(Confirm(receipt, Profile(8)), Is.True,
                "真实HTTP成功正文经Unity边界后也必须解除原pending并安装同版本Profile。");
            Assert.That(Pending(), Is.Null);
            Assert.That((bool)entry.Invoke(_client, new object[] { null }), Is.True,
                "同一成功收据和完整Profile确认后必须恢复单机入口，不能永久留在未确认状态。");
        }

        [TestCase("{}")]
        [TestCase("{\"SceneKey\":\"hub.map1\",\"WorldSceneKey\":\"hub.map1\",\"RotationW\":1}")]
        public void NonNullWirePoseCannotMasqueradeAsAnOrdinaryMove(string pose)
        {
            Assert.That(Capture(Request()), Is.True);
            string json = "{\"CommandId\":\"" + Command + "\",\"CommittedVersion\":8,\"InstanceId\":\"" + Item
                + "\",\"OperationKind\":1,\"TargetLocationKind\":0,\"TargetParentInstanceId\":\"\",\"Slot\":4,\"WorldPose\":" + pose + "}";
            var parser = typeof(OnlineAccountClient).GetMethod("ParseAccountInventoryReceipt", Private | BindingFlags.Static);
            Assert.That(Confirm((InventoryMoveReply)parser.Invoke(null, new object[] { json }), Profile(8)), Is.False);
            Assert.That(Pending(), Is.Not.Null, "非法正文不能借null兼容逻辑解锁未知命令。");
        }

        [TestCase("\"WorldPose\":null,\"WorldPose\":null")]
        [TestCase("\"WorldPose\":null,\"World\\u0050ose\":{}")]
        [TestCase("\"Destination\":\"WorldPose:null\"")]
        public void AmbiguousOrMissingWirePoseCannotUnlockTheAccount(string fields)
        {
            var parser = typeof(OnlineAccountClient).GetMethod("ParseAccountInventoryReceipt", Private | BindingFlags.Static);
            Assert.That(parser.Invoke(null, new object[] { "{" + fields + "}" }), Is.Null);
        }

        private bool Confirm(InventoryMoveReply reply, OnlineProfileReply profile)
        {
            var method = typeof(OnlineAccountClient).GetMethod("TryConfirmAccountInventoryCommand", Private);
            Assert.That(method, Is.Not.Null, "POST收据不能代替同账号完整Profile安装。");
            var args = new object[] { reply, profile, null }; return (bool)method.Invoke(_client, args);
        }

        [Test] public void PrivateHubDropFreezesRealPoseAndRequiresMatchingWorldProfileBeforeCommit()
        {
            // 原Move不能携带Unity inline空姿态壳；真实编码器必须给业务一个JSON null。
            Assert.That(InstanceCheckpointJson.TrySerializeInventoryMove(new InventoryMoveRequest { OperationKind = 1 }, out var ordinary, out _), Is.True);
            Assert.That(ordinary, Does.Contain("\"WorldPose\":null"));
            var pose = new OnlineAccountWorldItemPose { SceneKey = "hub.map1", WorldSceneKey = "hub.map1",
                PositionX = 3, PositionY = 1, PositionZ = -2, PoseKind = 2 };
            var request = new OnlineAccountInventoryCommandRequestedEvent { CallerId = 31,
                Intent = new OnlineAccountInventoryCommandIntent { CommandId = Command, PlayerId = Player,
                    SceneHandle = _root.scene.handle, ExpectedVersion = 7, ItemId = Item,
                    Kind = OnlineAccountInventoryCommandKind.Drop, TargetLocationKind = 5, TargetParentInstanceId = "",
                    TargetSlot = -1, WorldOwnerId = "", WorldPose = pose } };
            Assert.That(Capture(request), Is.True);
            pose.PositionX = 999; // UI持有的可变对象不能改固定HTTP正文或后续收据关联。
            var pending = Pending(); var body = (string)pending.GetType().GetField("Json", Private | BindingFlags.Public).GetValue(pending);
            var frozen = JsonUtility.FromJson<InventoryMoveRequest>(body);
            Assert.That(frozen.WorldPose.PositionX, Is.EqualTo(3));
            const string spawn = "55555555555555555555555555555555";
            var receipt = new InventoryMoveReply { CommandId = Command, InstanceId = Item, CommittedVersion = 8,
                OperationKind = 4, TargetLocationKind = 5, TargetParentInstanceId = "", Slot = -1,
                WorldOwnerId = spawn, WorldPose = frozen.WorldPose };
            var profile = Profile(8); profile.Items[0].LocationKind = 5; profile.Items[0].Slot = -1;
            profile.WorldItems = new[] { new OnlineHubWorldItemReply { InstanceId = Item, WorldOwnerId = Other, Pose = frozen.WorldPose } };
            Assert.That(Confirm(receipt, profile), Is.False, "只有World位置却缺同一次落地身份，不能消除未知候选。");
            Assert.That(Pending(), Is.SameAs(pending));
            profile.WorldItems[0].WorldOwnerId = spawn;
            Assert.That(Confirm(receipt, profile), Is.True);
            Assert.That(Pending(), Is.Null);
            Assert.That(OnlineAccountInventoryHubScope.TryCopyForScene(_root.scene.handle, out var installed), Is.True);
            Assert.That(installed.Items[0].LocationKind, Is.EqualTo(5));
            Assert.That(installed.WorldItems[0].WorldOwnerId, Is.EqualTo(spawn));
            Assert.That(installed.WorldItems[0].Pose.PositionX, Is.EqualTo(3));
            Assert.That(installed.Gold, Is.EqualTo(10000));
        }

        [Test] public void RealAccountRequestFreezesOneCandidateAndDoesNotMutateProfile()
        {
            Assert.That(Capture(Request()), Is.True);
            Assert.That(Pending(), Is.Not.Null);
            Assert.That(OnlineAccountInventoryHubScope.TryCopyForScene(_root.scene.handle, out var snapshot), Is.True);
            Assert.That(snapshot.Version, Is.EqualTo(7)); Assert.That(snapshot.Items[0].Slot, Is.Zero);
        }
        [TestCase(Other, 7u)] [TestCase(Player, 6u)] [TestCase(Player, 8u)]
        public void DifferentAccountOrStaleAndFutureUiVersionsCannotWrite(string player, uint version)
        { Assert.That(Capture(Request(player, version)), Is.False); Assert.That(Pending(), Is.Null); }
        [Test] public void WrongSceneAndUnownedItemCannotReachHttp()
        {
            Assert.That(Capture(Request(scene: int.MaxValue)), Is.False);
            Assert.That(Capture(Request(item: "55555555555555555555555555555555")), Is.False);
            Assert.That(Pending(), Is.Null);
        }
        [Test] public void UnknownFirstCandidateCannotBeReplacedByFreshCommand()
        {
            Assert.That(Capture(Request()), Is.True); var first = Pending();
            Assert.That(Capture(Request(command: "66666666666666666666666666666666")), Is.False);
            Assert.That(Pending(), Is.SameAs(first));
        }
        [TestCase(Other, 8u, false)] [TestCase(Player, 7u, false)] [TestCase(Player, 8u, true)]
        public void ReplyCannotUnlockWithWrongOldOrLeasedProfile(string player, uint version, bool locked)
        {
            Assert.That(Capture(Request()), Is.True);
            Assert.That(Confirm(Receipt(), Profile(version, player, locked)), Is.False);
            Assert.That(Pending(), Is.Not.Null);
            OnlineAccountInventoryHubScope.TryCopyForScene(_root.scene.handle, out var snapshot);
            Assert.That(snapshot.Version, Is.EqualTo(7));
        }
        [Test] public void DifferentCommandCannotConfirmAndSameAccountCannotBeReplacedMidRequest()
        {
            Assert.That(Capture(Request()), Is.True);
            Assert.That(Confirm(Receipt("77777777777777777777777777777777"), Profile(8)), Is.False);
            SetClient("_view", Profile(7, Other));
            Assert.That(Confirm(Receipt(), Profile(8)), Is.False); Assert.That(Pending(), Is.Not.Null);
        }
        [Test] public void CompleteMatchingReceiptAndProfileInstallBeforePendingIsCleared()
        {
            Assert.That(Capture(Request()), Is.True);
            Assert.That(Confirm(Receipt(), Profile(8)), Is.True);
            Assert.That(Pending(), Is.Null);
            Assert.That(OnlineAccountInventoryHubScope.TryCopyForScene(_root.scene.handle, out var snapshot), Is.True);
            Assert.That(snapshot.Version, Is.EqualTo(8)); Assert.That(snapshot.Items[0].Slot, Is.EqualTo(4));
            Assert.That(_root.GetComponent<AccountCommandConsumerProbe>().InstalledVersion, Is.EqualTo(8));
        }
        [Test] public void EqualCommittedVersionWithOldPositionCannotPretendThatMoveWasInstalled()
        {
            Assert.That(Capture(Request()), Is.True);
            var inconsistent = Profile(8); inconsistent.Items[0].Slot = 0;
            Assert.That(Confirm(Receipt(), inconsistent), Is.False,
                "同版本正文必须包含收据已确认的位置；不能仅靠版本号声称完成。");
            Assert.That(Pending(), Is.Not.Null);
            OnlineAccountInventoryHubScope.TryCopyForScene(_root.scene.handle, out var old);
            Assert.That(old.Version, Is.EqualTo(7));
        }
        [Test] public void FrozenHttpBodyDoesNotFollowSubsequentUiMutation()
        {
            var request = Request(); Assert.That(Capture(request), Is.True);
            var intentField = request.GetType().GetField("Intent"); var changed = intentField.GetValue(request);
            Set(changed, "TargetSlot", 12); intentField.SetValue(request, changed);
            var pending = Pending(); var bodyField = pending.GetType().GetField("Json", Private | BindingFlags.Public);
            Assert.That(bodyField, Is.Not.Null);
            var captured = JsonUtility.FromJson<InventoryMoveRequest>((string)bodyField.GetValue(pending));
            Assert.That(captured.Slot, Is.EqualTo(-1)); Assert.That(captured.CommandId, Is.EqualTo(Command));
        }
        [Test] public void ReceiptWithoutInstalledHubConsumerRetainsTheCandidate()
        {
            Assert.That(Capture(Request()), Is.True); Object.DestroyImmediate(_root.GetComponent<AccountCommandConsumerProbe>());
            Assert.That(Confirm(Receipt(), Profile(8)), Is.False); Assert.That(Pending(), Is.Not.Null);
        }
        [Test] public void ServerConfirmedSamePositionNoOpKeepsItsOriginalVersionAndMayFinish()
        {
            var request = Request(); var intentField = request.GetType().GetField("Intent");
            var intent = intentField.GetValue(request); Set(intent, "TargetSlot", 0); intentField.SetValue(request, intent);
            Assert.That(Capture(request), Is.True);
            var receipt = Receipt(version: 7); receipt.Slot = 0;
            Assert.That(Confirm(receipt, Profile()), Is.True,
                "原位置无变化的已确认事务不应凭空增版本，也不能永远卡在未知。");
            Assert.That(Pending(), Is.Null);
        }
        [Test] public void DifferentLoginCannotTakeOverAnUnconfirmedOriginalAccountCommand()
        {
            Assert.That(Capture(Request()), Is.True);
            var method = typeof(OnlineAccountClient).GetMethod("TryAdoptRefreshedAccountProfile", Private);
            Assert.That(method, Is.Not.Null, "重登Profile必须与未确认候选身份关联。");
            var args = new object[] { Profile(8, Other), null };
            Assert.That((bool)method.Invoke(_client, args), Is.False);
            Assert.That(_client.Authenticated, Is.False); Assert.That(Pending(), Is.Not.Null);
        }
        [Test] public void SameAccountMayRecoverAfterItsOriginalHubWasUnloadedWithoutConfirmingStaleUi()
        {
            var original = _root.scene; var temporary = EditorSceneManager.NewPreviewScene();
            temporary.name = "AccountCommandOrigin";
            int committed = 0;
            Action<OnlineAccountInventoryCommandResultEvent> observe = result =>
            { if (result.State == OnlineAccountInventoryCommandState.Committed) committed++; };
            EventBus<OnlineAccountInventoryCommandResultEvent>.Subscribe(observe);
            try
            {
                ResetFixtureScope(); SceneManager.MoveGameObjectToScene(_root, temporary);
                Assert.That(OnlineAccountInventoryHubScope.TryPublish(_client.GetInstanceID(), Snapshot(), out _), Is.True);
                Assert.That(OnlineAccountInventoryHubScope.TryArmHubEntry(_client.GetInstanceID(), temporary.name, out _), Is.True);
                Assert.That(OnlineAccountInventoryHubScope.IsRequiredForScene(temporary), Is.True);
                OnlineAccountInventoryHubScope.CancelPendingHubEntry(_client.GetInstanceID());
                Assert.That(Capture(Request()), Is.True);
                SceneManager.MoveGameObjectToScene(_root, original); EditorSceneManager.ClosePreviewScene(temporary);
                Assert.That(Confirm(Receipt(), Profile(8)), Is.True,
                    "原Scene已结束不能让同账号已确认候选永久卡住下一次登录进入Hub。");
                Assert.That(Pending(), Is.Null); Assert.That(committed, Is.Zero, "不能向已卸载界面伪报安装完成。");
                Assert.That(_root.GetComponent<AccountCommandConsumerProbe>().InstalledVersion, Is.Zero);
            }
            finally
            {
                EventBus<OnlineAccountInventoryCommandResultEvent>.Unsubscribe(observe);
                if (temporary.IsValid() && temporary.isLoaded) EditorSceneManager.ClosePreviewScene(temporary);
            }
        }
        [TestCase(OnlineLobbyAction.Login)] [TestCase(OnlineLobbyAction.Refresh)]
        public void MalformedHttpSuccessCannotPermanentlyLockTheRealAccountCoroutine(OnlineLobbyAction action)
        {
            var method = typeof(OnlineAccountClient).GetMethod("Execute", Private);
            var routine = (IEnumerator)method.Invoke(_client, new object[] { new OnlineLobbyRequestEvent { Action = action } });
            Assert.That(routine.MoveNext(), Is.True);
            IEnumerator refresh = null;
            if (action == OnlineLobbyAction.Refresh)
            { refresh = (IEnumerator)routine.Current; Assert.That(refresh.MoveNext(), Is.True); }
            SetClient("_lastSuccess", true); SetClient("_lastBody", "{invalid-json");
            if (refresh != null) Assert.DoesNotThrow(() => refresh.MoveNext());
            Assert.DoesNotThrow(() => routine.MoveNext());
            Assert.That(_client.Busy, Is.False);
        }

        [Test]
        public void ShopMappingCombinesControlledQuotesWithCompleteAuthoritativeStockAndGold()
        {
            ConfigureShop();
            var reply = ShopReply();
            Array.Reverse(reply.Shop.Stocks); // 业务数组顺序不决定报价身份，必须按OfferId完整合并。
            var value = MapShop(reply);
            Assert.That(value.PlayerId, Is.EqualTo(Player)); Assert.That(value.InventoryId, Is.EqualTo("account." + Player));
            Assert.That(value.InventoryVersion, Is.EqualTo(7)); Assert.That(value.Gold, Is.EqualTo(10000));
            Assert.That(value.Stocks.Length, Is.EqualTo(2));
            Assert.That(value.Stocks[0].BuyPrice, Is.EqualTo(100)); Assert.That(value.Stocks[0].Remaining, Is.EqualTo(5));
            Assert.That(value.Stocks[1].MaterialKey, Is.EqualTo(1)); Assert.That(value.Stocks[1].Remaining, Is.EqualTo(100));
        }

        [TestCase("hash")] [TestCase("duplicate")] [TestCase("missing")] [TestCase("foreign")]
        [TestCase("gold")] [TestCase("excess-stock")] [TestCase("scene")]
        public void ShopMappingRejectsUntrustedOrIncompleteBusinessValues(string bad)
        {
            ConfigureShop(); var reply = ShopReply(); var scene = (int)_root.scene.handle;
            if (bad == "hash") reply.Shop.ConfigHash = Hash;
            if (bad == "duplicate") reply.Shop.Stocks[1].OfferId = reply.Shop.Stocks[0].OfferId;
            if (bad == "missing") reply.Shop.Stocks = new[] { reply.Shop.Stocks[0] };
            if (bad == "foreign") reply.Shop.PlayerId = Other;
            if (bad == "gold") reply.Profile.Gold = -1;
            if (bad == "excess-stock") reply.Shop.Stocks[0].Remaining = 11;
            if (bad == "scene") scene = int.MaxValue;
            var args = new object[] { reply, scene, null, null };
            Assert.That((bool)ShopMethod("TryMapAccountShopReply").Invoke(_client, args), Is.False);
            Assert.That(args[2], Is.Null);
        }

        [Test]
        public void ShopReplyPublishesOnlyAfterTheOriginalHubConsumersHaveInstalledItsProfile()
        {
            ConfigureShop(); int changed = 0;
            Action<OnlineAccountShopChangedEvent> observe = value =>
            {
                if (!value.Success) return;
                changed++; Assert.That(_root.GetComponent<AccountCommandConsumerProbe>().InstalledVersion, Is.EqualTo(8));
                Assert.That(OnlineAccountInventoryHubScope.TryCopyForScene(_root.scene.handle, out var profile), Is.True);
                Assert.That(profile.Gold, Is.EqualTo(9900)); Assert.That(value.Snapshot.Gold, Is.EqualTo(9900));
            };
            EventBus<OnlineAccountShopChangedEvent>.Subscribe(observe);
            try { Assert.That(InstallShop(ShopReply(8, 12, 9900)), Is.True); Assert.That(changed, Is.EqualTo(1)); }
            finally { EventBus<OnlineAccountShopChangedEvent>.Unsubscribe(observe); }
        }

        [Test]
        public void ShopTradeFreezesItsOriginalCommandVersionsHashAndTargetWithoutUploadingPrices()
        {
            ConfigureShop(); Assert.That(InstallShop(ShopReply()), Is.True);
            var request = ShopRequest(); Assert.That(CaptureShop(request), Is.True);
            var pending = ShopPending(); var json = (string)pending.GetType().GetField("Json", Private | BindingFlags.Public).GetValue(pending);
            request.Request.OfferId = "changed-by-ui"; request.Request.Quantity = 999;
            var frozen = JsonUtility.FromJson<AccountShopTradeRequest>(json);
            Assert.That(frozen.CommandId, Is.EqualTo(Command)); Assert.That(frozen.ExpectedVersion, Is.EqualTo(7));
            Assert.That(frozen.ExpectedShopVersion, Is.EqualTo(11)); Assert.That(frozen.ConfigHash, Is.EqualTo(ShopDefinition().Hash));
            Assert.That(frozen.OfferId, Is.EqualTo("test.offer.item")); Assert.That(frozen.Quantity, Is.EqualTo(1));
            Assert.That(json, Does.Not.Contain("Price")); Assert.That(json, Does.Not.Contain("Currency"));
            Assert.That(CaptureShop(ShopRequest("66666666666666666666666666666666")), Is.False);
            Assert.That(ShopPending(), Is.SameAs(pending));
        }

        [Test]
        public void ShopAndInventoryCandidatesAreMutuallyExclusiveAndBlockNewServerEntry()
        {
            ConfigureShop(); Assert.That(InstallShop(ShopReply()), Is.True);
            Assert.That(CaptureShop(ShopRequest()), Is.True);
            Assert.That(Capture(Request()), Is.False);
            var args = new object[] { null };
            Assert.That((bool)typeof(OnlineAccountClient).GetMethod("CanBeginServerRun", Private).Invoke(_client, args), Is.False);
            var hub = new object[] { "SinglePlayer_Hub", null };
            Assert.That((bool)typeof(OnlineAccountClient).GetMethod("TryBeginHubEntry", Private).Invoke(_client, hub), Is.False);
            SetClient("_accountShopPending", null);
            Assert.That(Capture(Request()), Is.True); Assert.That(CaptureShop(ShopRequest()), Is.False);
        }

        [Test]
        public void AnotherLoginCannotAdoptOrReplaceAnUnknownShopTrade()
        {
            ConfigureShop(); Assert.That(InstallShop(ShopReply()), Is.True); Assert.That(CaptureShop(ShopRequest()), Is.True);
            var pending = ShopPending();
            var args = new object[] { Profile(8, Other), null };
            Assert.That((bool)typeof(OnlineAccountClient).GetMethod("TryAdoptRefreshedAccountProfile", Private).Invoke(_client, args), Is.False);
            Assert.That(_client.Authenticated, Is.False); Assert.That(ShopPending(), Is.SameAs(pending));
        }

        [TestCase("receipt-player")] [TestCase("command")] [TestCase("old-profile")] [TestCase("old-shop")]
        public void ShopTradeReceiptCannotFinishUntilFreshMatchingShopAndProfileAreConfirmed(string bad)
        {
            ConfigureShop(); Assert.That(InstallShop(ShopReply()), Is.True); Assert.That(CaptureShop(ShopRequest()), Is.True);
            var receipt = ShopReceipt(); var fresh = ShopReply(8, 12, 9900);
            if (bad == "receipt-player") receipt.PlayerId = Other;
            if (bad == "command") receipt.CommandId = Other;
            if (bad == "old-profile") fresh = ShopReply(7, 12, 9900);
            if (bad == "old-shop") fresh = ShopReply(8, 11, 9900);
            Assert.That(ConfirmShop(receipt, fresh), Is.False); Assert.That(ShopPending(), Is.Not.Null);
        }

        [Test]
        public void ShopTradeConfirmsConsumersBeforePublishingTheOriginalResultAndClearingItsCandidate()
        {
            ConfigureShop(); Assert.That(InstallShop(ShopReply()), Is.True); Assert.That(CaptureShop(ShopRequest()), Is.True);
            int completed = 0;
            Action<ShopTradeResultEvent> observe = result =>
            {
                completed++; Assert.That(result.Success, Is.True); Assert.That(result.RequestId, Is.EqualTo(Command));
                Assert.That(result.Currency, Is.EqualTo(9900)); Assert.That(result.CurrencyDelta, Is.EqualTo(-100));
                Assert.That(_root.GetComponent<AccountCommandConsumerProbe>().InstalledVersion, Is.EqualTo(8));
                Assert.That(ShopPending(), Is.Null);
            };
            EventBus<ShopTradeResultEvent>.Subscribe(observe);
            try { Assert.That(ConfirmShop(ShopReceipt(), ShopReply(8, 12, 9900)), Is.True); Assert.That(completed, Is.EqualTo(1)); }
            finally { EventBus<ShopTradeResultEvent>.Unsubscribe(observe); }
        }

        [Test]
        public void ShopReceiptWithoutAConfirmedHubConsumerRemainsRecoverable()
        {
            ConfigureShop(); Assert.That(InstallShop(ShopReply()), Is.True); Assert.That(CaptureShop(ShopRequest()), Is.True);
            Object.DestroyImmediate(_root.GetComponent<AccountCommandConsumerProbe>());
            Assert.That(ConfirmShop(ShopReceipt(), ShopReply(8, 12, 9900)), Is.False);
            Assert.That(ShopPending(), Is.Not.Null);
        }

        private ContentShopDefinition ShopDefinition()
        {
            var shop = new ContentShopDefinition { ShopId = "test.shop", PurchaseBudget = 500, RefreshEveryProgress = 3,
                Offers = new[]
                {
                    new ContentShopOffer { OfferId = "test.offer.item", ContentId = "existing-item", BuyPrice = 100, SellPrice = 50, InitialStock = 10 },
                    new ContentShopOffer { OfferId = "test.offer.water", MaterialKey = 1, BuyPrice = 20, SellPrice = 10, InitialStock = 100 }
                } };
            shop.Hash = ContentShopRules.CalculateHash(shop); return shop;
        }
        private void ConfigureShop()
            => SetClient("_manifest", new ContentManifest { SchemaVersion = 3, Hash = Hash,
                ProtocolVersion = ProtocolCompatibility.CurrentVersion, Shop = ShopDefinition() });
        private AccountShopReply ShopReply(uint inventory = 7, long shop = 11, long gold = 10000)
        {
            var profile = Profile(inventory); profile.Gold = gold;
            return new AccountShopReply { Profile = profile, Shop = new AccountShopSnapshot { PlayerId = Player,
                ShopId = "test.shop", ConfigHash = ShopDefinition().Hash, InventoryVersion = inventory, Version = shop,
                RemainingBudget = 500, ProgressUntilRefresh = 3,
                Stocks = new[] { new AccountShopStock { OfferId = "test.offer.item", Remaining = 5 },
                    new AccountShopStock { OfferId = "test.offer.water", Remaining = 100 } } } };
        }
        private OnlineAccountShopOpenRequestedEvent ShopOpen() => new OnlineAccountShopOpenRequestedEvent
        { CallerId = 41, SceneHandle = _root.scene.handle, InventoryId = "account." + Player };
        private OnlineAccountShopTradeRequestedEvent ShopRequest(string command = Command) => new OnlineAccountShopTradeRequestedEvent
        { CallerId = 41, SceneHandle = _root.scene.handle, Request = new ShopTradeRequestedEvent
            { InventoryId = "account." + Player, RequestId = command, OfferId = "test.offer.item", ItemInstanceId = "",
                ContainerId = "", BackpackId = "", InventoryVersion = 7, ShopVersion = 11, Quantity = 1, Kind = ShopTradeKind.BuyItem } };
        private AccountShopTradeReply ShopReceipt() => new AccountShopTradeReply { CommandId = Command, PlayerId = Player,
            Success = true, InventoryVersion = 8, ShopVersion = 12, Currency = 9900, CurrencyDelta = -100, Quantity = 1 };
        private MethodInfo ShopMethod(string name)
        { var method = typeof(OnlineAccountClient).GetMethod(name, Private); Assert.That(method, Is.Not.Null, "账号商店桥缺少真实接缝：" + name); return method; }
        private OnlineAccountShopPresentationSnapshot MapShop(AccountShopReply reply)
        { var args = new object[] { reply, (int)_root.scene.handle, null, null }; Assert.That((bool)ShopMethod("TryMapAccountShopReply").Invoke(_client, args), Is.True, args[3] as string); return (OnlineAccountShopPresentationSnapshot)args[2]; }
        private bool InstallShop(AccountShopReply reply)
        { var args = new object[] { ShopOpen(), reply, null }; return (bool)ShopMethod("TryInstallAccountShopReply").Invoke(_client, args); }
        private bool CaptureShop(OnlineAccountShopTradeRequestedEvent request)
        { var args = new object[] { request, null }; return (bool)ShopMethod("TryCaptureAccountShopTrade").Invoke(_client, args); }
        private bool ConfirmShop(AccountShopTradeReply receipt, AccountShopReply fresh)
        { var args = new object[] { receipt, fresh, null }; return (bool)ShopMethod("TryConfirmAccountShopTrade").Invoke(_client, args); }
        private object ShopPending()
        { var field = typeof(OnlineAccountClient).GetField("_accountShopPending", Private); Assert.That(field, Is.Not.Null); return field.GetValue(_client); }
    }

    /// <summary>仅替代UI消费者的确认端口，不读旧档、不创建角色或真实资产。</summary>
    public sealed class AccountCommandConsumerProbe : MonoBehaviour, IAccountInventoryHubConsumer
    {
        public uint InstalledVersion;
        public bool TryPrepareAccountInventory(OnlineAccountInventorySnapshot snapshot, out string error)
        { InstalledVersion = snapshot.Version; error = null; return true; }
        public bool HasPreparedAccountInventory(string playerId, uint minimumVersion) => InstalledVersion >= minimumVersion;
    }
}
