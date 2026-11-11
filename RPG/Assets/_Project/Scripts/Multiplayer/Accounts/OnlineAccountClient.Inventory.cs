using System;
using System.Collections;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Multiplayer
{
    public sealed partial class OnlineAccountClient
    {
        /// <summary>
        /// 账号Hub的离散HTTP事务，不伪造DS Actor/Run/Epoch。原UI只提出位置意图，
        /// 业务端从真实实例重算；本机保留一个固定正文，确认完整Profile安装后才报告成功。
        /// </summary>
        private sealed class AccountInventoryPending
        {
            internal OnlineAccountInventoryCommandRequestedEvent Request;
            internal string Json;
            internal uint CommittedVersion;
            internal OnlineAccountInventorySnapshot Before;
        }
        private AccountInventoryPending _accountInventoryPending;

        /// <summary>只有已确认的账号可申请新的服务器副本；重连沿原Room/Ticket恢复，不借此解锁旧Run。</summary>
        internal bool CanBeginServerRun(out string error)
        {
            error = null;
            if (_busy || _accountInventoryPending != null || _accountShopPending != null)
            { error = "账号资产操作尚未确认，请先保存或刷新恢复。"; return false; }
            if (!Authenticated || _manifest == null || _view == null || string.IsNullOrEmpty(_view.PlayerId)
                || _view.Version == 0 || _view.ContentManifestHash != _manifest.Hash)
            { error = "请先登录并读取当前账号资产。"; return false; }
            if (_view.AssetsLocked || !string.IsNullOrEmpty(_view.ActiveRunId))
            { error = "账号资产仍属于原副本，请先恢复或完成该副本，不能重复入场。"; return false; }
            return true;
        }

        internal bool TryBeginHubEntry(string scenePathOrName, out string error)
        {
            error = null;
            if (_accountInventoryPending != null || _accountShopPending != null)
            { error = "上一笔账号资产操作结果尚未确认，请先刷新恢复。"; return false; }
            if (!Authenticated || _manifest == null || _view == null || _view.AssetsLocked
                || !string.IsNullOrEmpty(_view.ActiveRunId))
            { error = "账号资产尚未读取或仍被副本占用，请先恢复原副本状态。"; return false; }
            if (!OnlineAccountInventoryMapping.TryMap(_view, _manifest.Hash, out var snapshot, out error)
                || !OnlineAccountInventoryHubScope.TryPublish(GetInstanceID(), snapshot, out error)) return false;
            // 必须早于LoadScene，保证Session.Awake/OnEnable第一条资产入口已被账号作用域保护。
            return OnlineAccountInventoryHubScope.TryArmHubEntry(GetInstanceID(), scenePathOrName, out error);
        }

        internal void ReportHubEntryPreparationFailure(string error)
            => Publish(error ?? "据点账号资产尚未安装；已保持保护状态，请刷新重试。", false);

        private void OnAccountWandSelection(OnlineAccountWandSelectionRequestedEvent request)
        {
            if (!OnlineAccountInventoryHubScope.TryGetSummaryForScene(request.SceneHandle, out var summary)
                || summary.InventoryId != request.InventoryId || summary.Version != request.ExpectedVersion
                || summary.AssetsLocked || !string.IsNullOrEmpty(summary.ActiveRunId)
                || request.SlotIndex < 0 || request.SlotIndex >= 3
                || !OnlineAccountInventoryHubScope.TryCopyForScene(request.SceneHandle, out var snapshot)) return;
            // 按原数字键对应的真实装备槽解析，不把查看/空槽当新选择，也不借新Profile重写旧请求版本。
            string wand = null;
            foreach (var item in snapshot.Items)
                if (item != null && item.LocationKind == 3 && item.Slot == request.SlotIndex)
                { wand = item.InstanceId; break; }
            if (string.IsNullOrEmpty(wand)) { Publish("该装备槽没有法杖，保持原选择。", false); return; }
            if (_accountInventoryPending != null)
            {
                var prior = _accountInventoryPending.Request;
                if (!_busy && prior.Intent.Kind == OnlineAccountInventoryCommandKind.SelectWand
                    && prior.Intent.PlayerId == summary.PlayerId && prior.Intent.SceneHandle == request.SceneHandle
                    && prior.Intent.ItemId == wand && prior.Intent.TargetSlot == request.SlotIndex)
                    OnAccountInventoryCommand(prior); // 同槽重试仅重发被冻结的原JSON/CommandId。
                else Publish("上一笔账号操作仍待确认，请先恢复。", false);
                return;
            }
            OnAccountInventoryCommand(new OnlineAccountInventoryCommandRequestedEvent
            { CallerId = request.CallerId, Intent = new OnlineAccountInventoryCommandIntent
                { CommandId = Guid.NewGuid().ToString("N"), PlayerId = summary.PlayerId, SceneHandle = request.SceneHandle,
                    ExpectedVersion = request.ExpectedVersion, Kind = OnlineAccountInventoryCommandKind.SelectWand,
                    ItemId = wand, TargetLocationKind = 3, TargetParentInstanceId = "", TargetSlot = request.SlotIndex } });
        }

        private bool TryCaptureAccountInventoryCommand(OnlineAccountInventoryCommandRequestedEvent request, out string error)
        {
            error = null;
            var value = request.Intent;
            if (_accountInventoryPending != null || _accountShopPending != null || _busy)
            { error = "已有账号操作等待确认，请勿重复拖放。"; return false; }
            if (!Authenticated || _manifest == null || _view == null || request.CallerId == 0
                || value.PlayerId != _view.PlayerId || value.ExpectedVersion == 0 || value.ExpectedVersion != _view.Version
                || _view.AssetsLocked || !string.IsNullOrEmpty(_view.ActiveRunId)
                || !OnlineAccountInventoryHubScope.TryCopyForScene(value.SceneHandle, out var snapshot)
                || snapshot.PlayerId != value.PlayerId || snapshot.Version != value.ExpectedVersion
                || snapshot.ContentManifestHash != _manifest.Hash || snapshot.AssetsLocked || !string.IsNullOrEmpty(snapshot.ActiveRunId))
            { error = "账号、据点、资产版本或租约已变化，请刷新后重试。"; return false; }
            if (!Guid.TryParseExact(value.CommandId, "N", out var command) || command == Guid.Empty
                || command.ToString("N") != value.CommandId || string.IsNullOrEmpty(value.ItemId)
                || value.Kind < OnlineAccountInventoryCommandKind.Move || value.Kind > OnlineAccountInventoryCommandKind.Pickup
                || value.TargetParentInstanceId == null
                || !(IsAccountMoveTarget(value.TargetLocationKind) || value.Kind == OnlineAccountInventoryCommandKind.Drop && value.TargetLocationKind == 5)
                || value.QuarterTurns < 0 || value.QuarterTurns > 3)
            { error = "账号移动意图非法。"; return false; }
            Game.Core.OnlineOwnedItemSnapshot owned = null;
            foreach (var item in snapshot.Items)
                if (item != null && item.InstanceId == value.ItemId) { owned = item; break; }
            if (owned == null) { error = "当前账号不拥有这个物品实例。"; return false; }
            if (!ValidAccountWorldCommand(value, owned, snapshot, out error)) return false;
            if (value.Kind == OnlineAccountInventoryCommandKind.SelectWand)
            {
                bool equipped = false;
                foreach (var item in snapshot.Items)
                    if (item != null && item.InstanceId == value.ItemId && item.LocationKind == 3 && item.Slot == value.TargetSlot)
                    { equipped = true; break; }
                if (!equipped || value.TargetLocationKind != 3 || value.TargetParentInstanceId != ""
                    || value.GridX != 0 || value.GridY != 0 || value.QuarterTurns != 0 || value.WholeStack)
                { error = "只能选择当前真实装备槽中的法杖。"; return false; }
            }
            var move = new InventoryMoveRequest { CommandId = value.CommandId, ExpectedVersion = value.ExpectedVersion,
                InstanceId = value.ItemId, OperationKind = (int)value.Kind, TargetLocationKind = value.TargetLocationKind,
                TargetParentInstanceId = value.TargetParentInstanceId, Slot = value.TargetSlot, GridX = value.GridX,
                GridY = value.GridY, QuarterTurns = value.QuarterTurns, WholeStack = value.WholeStack,
                WorldOwnerId = value.WorldOwnerId ?? string.Empty, WorldPose = OnlineAccountInventoryMapping.ToSharedPose(value.WorldPose) };
            if (!InstanceCheckpointJson.TrySerializeInventoryMove(move, out var json, out error)) return false;
            // 固定正文和用于核对材料守恒的原完整快照同时冻结，调用者随后改pose对象不能改变收据关联。
            value.WorldOwnerId = value.WorldOwnerId ?? string.Empty; value.WorldPose = value.WorldPose?.CopyValue(); request.Intent = value;
            _accountInventoryPending = new AccountInventoryPending { Request = request, Json = json, Before = snapshot.CopyValue() };
            return true;
        }

        private static bool ValidAccountWorldCommand(in OnlineAccountInventoryCommandIntent value, Game.Core.OnlineOwnedItemSnapshot item,
            OnlineAccountInventorySnapshot snapshot, out string error)
        {
            error = "账号地面命令与原实例或落点不一致。";
            if (value.Kind == OnlineAccountInventoryCommandKind.Drop)
            {
                if (item.EntryBoundBackpack || value.TargetLocationKind != 5 || value.TargetSlot != -1 || value.TargetParentInstanceId != ""
                    || value.GridX != 0 || value.GridY != 0 || value.QuarterTurns != 0 || !string.IsNullOrEmpty(value.WorldOwnerId)
                    || value.WorldPose == null || !value.WorldPose.IsValid() || value.WorldPose.PoseKind != 2
                    || value.WorldPose.SceneKey != "hub.map1" || value.WorldPose.WorldSceneKey != "hub.map1") return false;
            }
            else if (value.Kind == OnlineAccountInventoryCommandKind.Pickup)
            {
                if (item.LocationKind != 5 || value.WorldPose != null || !AccountWorldId(value.WorldOwnerId)) return false;
                bool found = false;
                foreach (var ground in snapshot.WorldItems)
                    if (ground != null && ground.InstanceId == item.InstanceId && ground.WorldOwnerId == value.WorldOwnerId) { found = true; break; }
                if (!found) return false;
                error = null; return true;
            }
            else if (value.WorldPose != null || !string.IsNullOrEmpty(value.WorldOwnerId)) return false;
            // 结构Move/整理不能绕过地面拾取身份门；父卡跟随真实根链，同样不能隔空从地上杖抽卡。
            var root = item;
            for (int depth = 0; !string.IsNullOrEmpty(root.ParentInstanceId); depth++)
            {
                if (depth >= snapshot.Items.Length) return false;
                Game.Core.OnlineOwnedItemSnapshot parent = null;
                foreach (var candidate in snapshot.Items) if (candidate != null && candidate.InstanceId == root.ParentInstanceId) { parent = candidate; break; }
                if (parent == null) return false; root = parent;
            }
            if (root.LocationKind == 5) return false;
            if (value.Kind == OnlineAccountInventoryCommandKind.OrganizeBackpack
                && (item.LocationKind != 4 || value.TargetLocationKind != 4 || value.TargetSlot != 0
                    || value.TargetParentInstanceId != "" || value.GridX != 0 || value.GridY != 0 || value.QuarterTurns != 0 || value.WholeStack)) return false;
            error = null; return true;
        }
        private static bool AccountWorldId(string value)
            => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty && id.ToString("N") == value;

        private static bool IsAccountMoveTarget(int kind)
            => kind == 0 || kind == 1 || kind == 2 || kind == 3 || kind == 4 || kind == 7 || kind == 8;

        private static bool SameAccountIntent(OnlineAccountInventoryCommandIntent a, OnlineAccountInventoryCommandIntent b)
            => a.CommandId == b.CommandId && a.PlayerId == b.PlayerId && a.SceneHandle == b.SceneHandle
                && a.ExpectedVersion == b.ExpectedVersion && a.Kind == b.Kind && a.ItemId == b.ItemId
                && a.TargetLocationKind == b.TargetLocationKind && a.TargetParentInstanceId == b.TargetParentInstanceId
                && a.TargetSlot == b.TargetSlot && a.GridX == b.GridX && a.GridY == b.GridY
                && a.QuarterTurns == b.QuarterTurns && a.WholeStack == b.WholeStack
                && (a.WorldOwnerId ?? "") == (b.WorldOwnerId ?? "") && OnlineAccountWorldItemPose.Same(a.WorldPose, b.WorldPose);

        private void OnAccountInventoryCommand(OnlineAccountInventoryCommandRequestedEvent request)
        {
            if (_accountInventoryPending != null)
            {
                if (!_busy && SameAccountIntent(_accountInventoryPending.Request.Intent, request.Intent))
                { StartCoroutine(DeliverAccountInventoryCommand()); return; }
                PublishAccountCommand(request, OnlineAccountInventoryCommandState.Rejected, "已有未确认操作，请使用保存/刷新恢复原请求。", OnlineInventoryCommandFailure.Busy);
                return;
            }
            if (!TryCaptureAccountInventoryCommand(request, out var error))
            { PublishAccountCommand(request, OnlineAccountInventoryCommandState.Rejected, error, OnlineInventoryCommandFailure.NotReady); return; }
            StartCoroutine(DeliverAccountInventoryCommand());
        }

        private bool MatchesAccountCandidate(AccountInventoryPending candidate)
            => ReferenceEquals(candidate, _accountInventoryPending) && Authenticated && _view != null
                && _view.PlayerId == candidate.Request.Intent.PlayerId
                && (OnlineAccountInventoryHubScope.IsRequiredForScene(candidate.Request.Intent.SceneHandle)
                    || !FindAccountScene(candidate.Request.Intent.SceneHandle).IsValid());

        private IEnumerator DeliverAccountInventoryCommand()
        {
            var candidate = _accountInventoryPending;
            if (candidate == null || !MatchesAccountCandidate(candidate)) yield break;
            _busy = true;
            PublishAccountCommand(candidate.Request, OnlineAccountInventoryCommandState.Pending, "正在保存账号资产…");
            try
            {
                // 离散操作三次有界恢复；每次使用同一字符串正文/CommandId，不能随重试偷换版本或目标。
                for (int attempt = 0; attempt < 3 && MatchesAccountCandidate(candidate); attempt++)
                {
                    yield return Send("POST", "/api/online/inventory/move", candidate.Json);
                    if (!MatchesAccountCandidate(candidate)) yield break;
                    if (_lastSuccess)
                    {
                        InventoryMoveReply receipt = null;
                        try { receipt = ParseAccountInventoryReceipt(_lastBody); } catch (ArgumentException) { }
                        if (!MatchesAccountReceipt(candidate, receipt)) continue;
                        candidate.CommittedVersion = receipt.CommittedVersion;
                        yield return Send("GET", "/api/online/profile", null);
                        if (!MatchesAccountCandidate(candidate)) yield break;
                        if (_lastSuccess)
                        {
                            OnlineProfileReply profile = null;
                            try { profile = JsonUtility.FromJson<OnlineProfileReply>(_lastBody); } catch (ArgumentException) { }
                            if (TryConfirmAccountInventoryCommand(receipt, profile, out _)) yield break;
                        }
                    }
                    else if (LastHttpStatus >= 400 && LastHttpStatus < 500 && LastHttpStatus != 408 && LastHttpStatus != 429)
                    {
                        // 业务明确拒绝在角色行锁和收据查询之后发生；不触碰本机位置，允许读取真实最新Profile。
                        // 401由原Send失效Session，且上面的身份栅栏会保留未确认候选而不会假报成功。
                        var rejected = candidate.Request; _accountInventoryPending = null;
                        yield return Refresh();
                        PublishAccountCommand(rejected, OnlineAccountInventoryCommandState.Rejected,
                            "业务服未接受此移动，物品按已确认账号状态保留，请刷新后重新操作。", OnlineInventoryCommandFailure.Rejected);
                        yield break;
                    }
                }
                if (ReferenceEquals(candidate, _accountInventoryPending))
                    PublishAccountCommand(candidate.Request, OnlineAccountInventoryCommandState.Unknown,
                        "保存结果尚未完整确认；物品未在本机移动，请按保存/刷新重试原请求。", OnlineInventoryCommandFailure.NotReady);
            }
            finally { _busy = _accountShopLobbyRestoring; Publish(LastStatus, _busy); if (!_busy) PumpQueuedAccountShopRead(); }
        }

        /// <summary>
        /// HTTP正文是确认入口的一部分，不能用直接new的DTO代替此边界测试。
        /// Unity把Serializable inline class的JSON null还原成默认实例；普通Move因而被误判带姿态，
        /// 三次200也不读Profile，最终同时锁住拖放和入局。只有原始顶层字段明确null时才恢复null。
        /// 地面Drop仍保留真实pose交给原匹配规则，未知/重复/缺失字段不能借此变成合法空值。
        /// </summary>
        private static InventoryMoveReply ParseAccountInventoryReceipt(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 16384) return null;
            var value = JsonUtility.FromJson<InventoryMoveReply>(json);
            if (value == null || !TryReadReceiptPoseNull(json, out bool isNull)) return null;
            if (isNull) value.WorldPose = null;
            return value;
        }

        private static bool TryReadReceiptPoseNull(string json, out bool isNull)
        {
            // 这是null语义修复，不是第二套DTO解析器。其余JSON仍由JsonUtility解析；
            // 此有限扫描只识别顶层属性，跳过字符串中的引号/花括号，避免Contains/Regex误认值里的字段名。
            // 服务端字段名固定为ASCII；拒绝顶层转义名称和重复WorldPose，防止另一个拼写覆盖已确认null。
            isNull = false; bool found = false; int depth = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '{' || ch == '[') { depth++; continue; }
                if (ch == '}' || ch == ']') { if (--depth < 0) return false; continue; }
                if (ch != '"') continue;
                int start = ++i; bool escaped = false;
                for (; i < json.Length && json[i] != '"'; i++)
                    if (json[i] == '\\') { escaped = true; if (++i >= json.Length) return false; }
                if (i >= json.Length) return false;
                int after = i + 1;
                while (after < json.Length && char.IsWhiteSpace(json[after])) after++;
                if (depth != 1 || after >= json.Length || json[after] != ':') continue;
                if (escaped) return false;
                if (i - start != 9 || string.CompareOrdinal(json, start, "WorldPose", 0, 9) != 0) continue;
                if (found) return false;
                found = true; after++;
                while (after < json.Length && char.IsWhiteSpace(json[after])) after++;
                if (after + 4 <= json.Length && string.CompareOrdinal(json, after, "null", 0, 4) == 0)
                {
                    int end = after + 4;
                    while (end < json.Length && char.IsWhiteSpace(json[end])) end++;
                    if (end >= json.Length || json[end] != ',' && json[end] != '}') return false;
                    isNull = true;
                }
            }
            return found && depth == 0;
        }

        private static bool MatchesAccountReceipt(AccountInventoryPending candidate, InventoryMoveReply reply)
        {
            if (candidate == null || reply == null) return false;
            var intent = candidate.Request.Intent;
            return MatchesAccountWorldReceipt(intent, reply)
                && reply.CommandId == intent.CommandId && reply.InstanceId == intent.ItemId && reply.OperationKind == (int)intent.Kind
                && reply.CommittedVersion >= intent.ExpectedVersion && reply.TargetLocationKind == intent.TargetLocationKind
                && reply.TargetParentInstanceId == intent.TargetParentInstanceId && reply.GridX == intent.GridX
                && reply.GridY == intent.GridY && reply.QuarterTurns == intent.QuarterTurns && reply.WholeStack == intent.WholeStack
                && (reply.Slot == intent.TargetSlot || intent.TargetLocationKind == 0 && intent.TargetSlot == -1
                    && reply.Slot >= 0 && reply.Slot < OnlineInventoryAreas.WarehouseCapacity);
        }

        private static bool MatchesAccountWorldReceipt(in OnlineAccountInventoryCommandIntent intent, InventoryMoveReply receipt)
        {
            if (intent.Kind == OnlineAccountInventoryCommandKind.Drop)
                return AccountWorldId(receipt.WorldOwnerId) && receipt.WorldPose != null && receipt.Transferred == 0
                    && OnlineAccountWorldItemPose.Same(intent.WorldPose, OnlineAccountInventoryMapping.ToCorePose(receipt.WorldPose));
            if (intent.Kind == OnlineAccountInventoryCommandKind.Pickup)
                return receipt.WorldOwnerId == intent.WorldOwnerId && receipt.WorldPose == null;
            return string.IsNullOrEmpty(receipt.WorldOwnerId) && receipt.WorldPose == null && receipt.Transferred == 0;
        }

        private bool TryConfirmAccountInventoryCommand(InventoryMoveReply receipt, OnlineProfileReply profile, out string error)
        {
            error = null;
            var candidate = _accountInventoryPending;
            if (candidate == null || !MatchesAccountCandidate(candidate) || !MatchesAccountReceipt(candidate, receipt)
                || profile == null || profile.PlayerId != candidate.Request.Intent.PlayerId || profile.Version < receipt.CommittedVersion
                || profile.AssetsLocked || !string.IsNullOrEmpty(profile.ActiveRunId))
            { error = "移动收据与当前账号完整资产尚未匹配。"; return false; }
            if (profile.Version == receipt.CommittedVersion && !ProfileConfirmsAccountCommand(candidate, profile, receipt))
            { error = "同版本账号资产与移动收据位置不一致，未报告成功。"; return false; }
            if (profile.Version == receipt.CommittedVersion && candidate.Request.Intent.Kind == OnlineAccountInventoryCommandKind.SelectWand
                && (profile.CombatAssets == null || profile.CombatAssets.SelectedWandInstanceId != candidate.Request.Intent.ItemId))
            { error = "同版本账号资产尚未确认选中的法杖。"; return false; }
            var scene = FindAccountScene(candidate.Request.Intent.SceneHandle);
            if (!TryAdoptVerifiedHubProfile(profile, receipt.CommittedVersion, out error)) return false;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                // 原Hub已卸载：同账号最终收据和完整Profile仍能恢复账号缓存，但没有旧UI可安装。
                // 不向旧Caller伪报Committed；下一次正常EnterHub重新预arm并安装这份已确认账号Source。
                _accountInventoryPending = null;
                Publish("原界面已结束，账号资产已确认；可正常进入据点。", false); return true;
            }
            if (!TryPrepareVerifiedHubScene(scene, out error)
                || !IsVerifiedHubSceneReady(scene, receipt.CommittedVersion))
            { error = error ?? "账号资产尚未安装到原据点界面。"; return false; }
            var completed = candidate.Request;
            _accountInventoryPending = null; // 只有Source/消费者已确认后才允许下一笔UI操作。
            PublishAccountCommand(completed, OnlineAccountInventoryCommandState.Committed, "账号资产已保存。",
                OnlineInventoryCommandFailure.None, receipt.CommittedVersion);
            return true;
        }

        private static bool ProfileContainsAccountMove(OnlineProfileReply profile, InventoryMoveReply receipt)
        {
            if (profile.Items == null) return false;
            foreach (var item in profile.Items)
                if (item != null && item.InstanceId == receipt.InstanceId)
                    return item.LocationKind == receipt.TargetLocationKind && item.ParentInstanceId == receipt.TargetParentInstanceId
                        && item.Slot == receipt.Slot && item.X == receipt.GridX && item.Y == receipt.GridY
                        && item.QuarterTurns == receipt.QuarterTurns;
            return false;
        }

        private static bool ProfileConfirmsAccountCommand(AccountInventoryPending candidate, OnlineProfileReply profile, InventoryMoveReply receipt)
        {
            if (candidate.Request.Intent.Kind == OnlineAccountInventoryCommandKind.Drop)
            {
                if (!ProfileContainsAccountMove(profile, receipt) || profile.WorldItems == null) return false;
                foreach (var world in profile.WorldItems)
                    if (world != null && world.InstanceId == receipt.InstanceId && world.WorldOwnerId == receipt.WorldOwnerId
                        && OnlineAccountWorldItemPose.Same(OnlineAccountInventoryMapping.ToCorePose(world.Pose), candidate.Request.Intent.WorldPose)) return true;
                return false;
            }
            if (candidate.Request.Intent.Kind != OnlineAccountInventoryCommandKind.Pickup) return ProfileContainsAccountMove(profile, receipt);
            if (candidate.Before == null || profile.WorldItems == null) return false;
            Game.Core.OnlineOwnedItemSnapshot before = null;
            foreach (var item in candidate.Before.Items) if (item.InstanceId == receipt.InstanceId) { before = item; break; }
            if (before == null) return false;
            if (before.MaterialState == null)
            {
                if (receipt.Transferred != 0 || !ProfileContainsAccountMove(profile, receipt)) return false;
                foreach (var world in profile.WorldItems) if (world.InstanceId == receipt.InstanceId) return false;
                return true;
            }
            // 原材料拾取可能只补一部分到旧堆，或整份并入旧ID。不能把“原ID未到目标”误报失败，
            // 必须用真实Transferred、同Spawn余量以及目标已有GMU的增量共同确认数量守恒。
            ulong original = before.MaterialState.Quantity;
            if (receipt.Transferred == 0 || receipt.Transferred > original) return false;
            ulong remaining = original - receipt.Transferred;
            bool hasGround = false;
            foreach (var world in profile.WorldItems)
                if (world.InstanceId == receipt.InstanceId)
                { if (world.WorldOwnerId != receipt.WorldOwnerId) return false; hasGround = true; break; }
            if (hasGround != (remaining != 0)) return false;
            Game.NetworkProtocol.OnlineItemReply after = null;
            foreach (var item in profile.Items) if (item.InstanceId == receipt.InstanceId) { after = item; break; }
            if (remaining != 0 && (after == null || after.LocationKind != 5 || after.MaterialState == null
                || after.MaterialState.MaterialKey != before.MaterialState.MaterialKey || after.MaterialState.Quantity != remaining)) return false;
            return TryAccountTargetMaterialTotal(candidate.Before.Items, before.ContentId, before.MaterialState.MaterialKey, receipt, out var oldTotal)
                && TryProfileTargetMaterialTotal(profile, before.ContentId, before.MaterialState.MaterialKey, receipt, out var newTotal)
                && oldTotal <= ulong.MaxValue - receipt.Transferred && newTotal == oldTotal + receipt.Transferred;
        }
        private static bool TryAccountTargetMaterialTotal(Game.Core.OnlineOwnedItemSnapshot[] items, string content, byte key, InventoryMoveReply receipt, out ulong total)
        {
            total = 0;
            foreach (var item in items)
                if (item.ContentId == content && item.LocationKind == receipt.TargetLocationKind && item.ParentInstanceId == receipt.TargetParentInstanceId
                    && item.MaterialState != null && item.MaterialState.MaterialKey == key)
                { if (total > ulong.MaxValue - item.MaterialState.Quantity) return false; total += item.MaterialState.Quantity; }
            return true;
        }
        private static bool TryProfileTargetMaterialTotal(OnlineProfileReply profile, string content, byte key, InventoryMoveReply receipt, out ulong total)
        {
            total = 0;
            foreach (var item in profile.Items)
                if (item.ContentId == content && item.LocationKind == receipt.TargetLocationKind && item.ParentInstanceId == receipt.TargetParentInstanceId
                    && item.MaterialState != null && item.MaterialState.MaterialKey == key)
                { if (total > ulong.MaxValue - item.MaterialState.Quantity) return false; total += item.MaterialState.Quantity; }
            return true;
        }

        private static Scene FindAccountScene(int handle)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            { var scene = SceneManager.GetSceneAt(i); if (scene.handle == handle) return scene; }
            return default;
        }

        private void PublishAccountCommand(OnlineAccountInventoryCommandRequestedEvent request,
            OnlineAccountInventoryCommandState state, string message,
            OnlineInventoryCommandFailure failure = OnlineInventoryCommandFailure.None, uint version = 0)
        {
            LastStatus = message;
            EventBus<OnlineAccountInventoryCommandResultEvent>.Publish(new OnlineAccountInventoryCommandResultEvent
            { CallerId = request.CallerId, SceneHandle = request.Intent.SceneHandle, PlayerId = request.Intent.PlayerId,
                CommandId = request.Intent.CommandId, State = state, Message = message, Failure = failure, CommittedVersion = version });
        }
    }
}
