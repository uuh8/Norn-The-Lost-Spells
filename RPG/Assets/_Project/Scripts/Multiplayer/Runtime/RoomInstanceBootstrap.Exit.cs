using System;
using System.Collections;
using System.Collections.Generic;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using UnityEngine;
using UnityEngine.Networking;

namespace Game.Multiplayer
{
    public sealed partial class RoomInstanceBootstrap
    {
        /// <summary>每个Actor最多一份固定候选；HTTP与Heartbeat/全Run检查点各持自己的Request。</summary>
        private sealed class MemberExit
        {
            public JoinConnectionIdentity Origin;
            public string Command, Body, Failure;
            public OnlineRunMemberExitRequest Candidate;
            public OnlineRunMemberExitReceipt Receipt;
            public OnlineExitedMemberLossSnapshot Losses;
            public UnityWebRequest Request;
            public OnlineRunExitPhase Phase;
            public OnlineRunExitReason Reason;
            public int Attempts;
            public bool Sending;
            public double NextAttempt;
            public bool Replaceable;
            public MemberExit Previous;
            public bool ThroughReturnPortal, SuccessfulExtraction;
            public uint DefeatedEnemies;
        }
        private readonly MemberExit[] _memberExits = new MemberExit[4];

        /// <summary>
        /// 仅权威死亡回调调用：身份取受信Entry与最后真实认证Epoch，不向客户端索要死亡声明。
        /// 先Fence/自然drain再捕获。还未固定Body的ESC可升级；一旦固定正文则不能用同ID重写资产。
        /// </summary>
        internal bool RequestFinalDeath(OnlineWorldBootstrap world, ulong actor)
        {
            if (!Ready || _launch == null || Inventory == null || world == null || !world.IsAuthority || !world.CanAdvance
                || world.Registry == null || world.Combat == null || !world.Combat.IsFinalPlayerDeath(actor)
                || Inventory.IsMemberExited(actor)) return false;
            if (world.DungeonDefinition != null && world.DungeonDefinition.IsPeacefulHome) return false;
            int index = (int)actor - 1;
            var pending = _memberExits[index];
            if (pending != null)
            {
                if (pending.Body == null && pending.Phase == OnlineRunExitPhase.Draining)
                { pending.Reason = OnlineRunExitReason.FinalDeath; SendMemberExitStatus(pending); }
                return true; // 已固定的终态交易不能被第二个死亡通知替换。
            }
            InstanceEntryMember entry = null;
            for (int i = 0; i < Inventory.MemberCount; i++)
                if (Inventory.TryGetFrozenMember(i, out var member) && member.ActorId == actor) { entry = member; break; }
            uint epoch = world.Registry[index].Epoch;
            if (entry == null || entry.Profile == null || epoch == 0 || !Inventory.Contains(actor, entry.Profile.PlayerId)) return false;
            var identity = new JoinConnectionIdentity(entry.Profile.PlayerId, _launch.RoomId, _launch.RunId,
                _launch.RoomEpoch, epoch, actor, _launch.DungeonId);
            string command = epoch.ToString("x8", System.Globalization.CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N").Substring(8);
            if (!world.TryBeginMemberExit(identity, command)) return false;
            pending = new MemberExit { Origin = identity, Command = command, Phase = OnlineRunExitPhase.Draining,
                Reason = OnlineRunExitReason.FinalDeath };
            _memberExits[index] = pending; SendMemberExitStatus(pending); return true;
        }

        internal void RequestMemberExit(JoinConnectionIdentity identity, in OnlineRunExitIntent intent)
        {
            if (!Ready || _launch == null || Inventory == null || identity == null || identity.ActorId < 1 || identity.ActorId > 4
                || intent.ConnectionEpoch != identity.ConnectionEpoch || !MemberExitHex(intent.CommandId, 32)) return;
            int index = (int)identity.ActorId - 1;
            var pending = _memberExits[index];
            if (!CanAddressMemberExitCommand(pending != null ? pending.Command : null,
                pending != null && pending.Previous != null ? pending.Previous.Command : null,
                pending != null ? pending.Origin.ConnectionEpoch : 0, intent.CommandId, identity.ConnectionEpoch)) return;
            if (pending != null)
            {
                if (pending.Origin.PlayerId != identity.PlayerId || pending.Origin.RunId != identity.RunId || pending.Origin.RoomEpoch != identity.RoomEpoch) return;
                if (pending.Command != intent.CommandId)
                {
                    if (pending.Previous != null && pending.Previous.Command == intent.CommandId)
                    { SendMemberExitStatus(pending.Previous); return; }
                    var currentWorld = OnlineWorldBootstrap.Active;
                    if (!pending.Replaceable || pending.Sending || pending.Phase != OnlineRunExitPhase.Rejected
                        || identity.ConnectionEpoch <= pending.Origin.ConnectionEpoch || currentWorld == null
                        || !currentWorld.TryReplaceRejectedMemberExit(identity, pending.Command, intent.CommandId)) return;
                    // 最多保留当前＋上一份已证拒绝，避免每次重连形成无界链；更旧ID不能再激活Owner。
                    pending.Previous = null;
                    // 旧候选已被证明永不提交；只丢它的非正文损耗副本，新候选仍从同一存活Owner重捕获。
                    // 不改旧Body，也不沿Previous保留多代损耗，四Actor缓存总计最多两来源各8行。
                    pending.Losses = null;
                    var next = new MemberExit { Origin = identity, Command = intent.CommandId, Phase = OnlineRunExitPhase.Draining,
                        Reason = pending.Reason, Previous = pending, ThroughReturnPortal = pending.ThroughReturnPortal,
                        SuccessfulExtraction = pending.SuccessfulExtraction, DefeatedEnemies = pending.DefeatedEnemies };
                    _memberExits[index] = next; SendMemberExitStatus(next); return;
                }
                // 新transport只改变回复地址，未知候选的origin epoch/正文绝不跟着重写。
                if (pending.Phase == OnlineRunExitPhase.Uncertain && !pending.Sending)
                { pending.Attempts = 0; pending.NextAttempt = Time.realtimeSinceStartupAsDouble; }
                SendMemberExitStatus(pending); return;
            }
            var world = OnlineWorldBootstrap.Active;
            if (identity.RunId != _launch.RunId || identity.RoomEpoch != _launch.RoomEpoch
                || !Inventory.Contains(identity.ActorId, identity.PlayerId) || world == null) return;
            if (intent.ThroughReturnPortal && !world.CanUseReturnPortal(identity.ActorId))
            {
                world.SendMemberExitStatus(identity, new OnlineRunExitStatus { CommandId = intent.CommandId,
                    ConnectionEpoch = identity.ConnectionEpoch, Phase = OnlineRunExitPhase.Rejected,
                    Reason = OnlineRunExitReason.VoluntaryExit, Failure = "ReturnPortalUnavailable" }); return;
            }
            uint defeated = 0;
            bool extracted = intent.ThroughReturnPortal && world.TryCaptureSuccessfulExtraction(identity.ActorId, out _, out defeated);
            if (!world.TryBeginMemberExit(identity, intent.CommandId)) return;
            pending = new MemberExit { Origin = identity, Command = intent.CommandId, Phase = OnlineRunExitPhase.Draining,
                ThroughReturnPortal = intent.ThroughReturnPortal, SuccessfulExtraction = extracted, DefeatedEnemies = defeated,
                Reason = world.Combat != null && world.Combat.IsFinalPlayerDeath(identity.ActorId)
                    ? OnlineRunExitReason.FinalDeath : OnlineRunExitReason.VoluntaryExit };
            _memberExits[index] = pending; SendMemberExitStatus(pending);
        }

        internal void CaptureMemberExits(OnlineWorldBootstrap world, uint tick)
        {
            if (!Ready || world == null || !world.IsAuthority || !world.CanAdvance || tick == 0 || world.Registry == null) return;
            for (int i = 0; i < _memberExits.Length; i++)
            {
                var pending = _memberExits[i];
                if (pending == null || pending.Phase != OnlineRunExitPhase.Draining) continue;
                // 同Tick内伤害可能晚于ESC按键；以捕获固定正文之前的真实权威生命状态线性化。
                if (world.Combat != null && world.Combat.IsFinalPlayerDeath(pending.Origin.ActorId))
                    pending.Reason = OnlineRunExitReason.FinalDeath;
                if (pending.Reason == OnlineRunExitReason.FinalDeath
                    && (world.Combat == null || !world.Combat.IsFinalPlayerDeath(pending.Origin.ActorId))) continue;
                var driver = world.Registry[i].Cast;
                if (driver == null || !driver.TryCaptureMemberExitFacts(pending.Command, out var owned, out var losses)) continue;
                InstanceEntryMember entry = null;
                for (int m = 0; m < Inventory.MemberCount; m++)
                    if (Inventory.TryGetFrozenMember(m, out var member) && member.ActorId == pending.Origin.ActorId) { entry = member; break; }
                if (entry == null || entry.Profile == null || entry.Profile.PlayerId != pending.Origin.PlayerId)
                { RejectMemberExit(pending, "MemberExitEntryUnavailable"); continue; }
                if (losses == null || losses.RunId != _launch.RunId || losses.PlayerId != pending.Origin.PlayerId || losses.ActorId != pending.Origin.ActorId)
                { RejectMemberExit(pending, "MemberExitLossIdentityRejected"); continue; }
                var candidate = new OnlineRunMemberExitRequest
                {
                    SchemaVersion = OnlineRunMemberExitRequest.CurrentSchemaVersion, SettlementId = pending.Command,
                    InstanceId = _launch.InstanceId, RunId = _launch.RunId, RoomEpoch = _launch.RoomEpoch,
                    PlayerId = pending.Origin.PlayerId, ActorId = pending.Origin.ActorId,
                    ConnectionEpoch = pending.Origin.ConnectionEpoch, ExpectedPersistentVersion = entry.Profile.Version,
                    Assets = OnlineOwnedAssetMapping.ToShared(owned),
                    Reason = (OnlineRunMemberExitReason)(byte)pending.Reason,
                    FinalRetiredItems = Array.Empty<OnlineRunAssetCheckpointRetired>(),
                    FinalDeathLostMaterials = Array.Empty<Game.NetworkProtocol.OnlineOwnedMaterialAmountSnapshot>(),
                    PracticeGroundItems = Array.Empty<OnlineRunAssetCheckpointGround>()
                };
                if (pending.SuccessfulExtraction && candidate.Reason == OnlineRunMemberExitReason.VoluntaryExit)
                { candidate.SuccessfulExtraction = true; candidate.CollectedGmu = driver.CollectedGmu; candidate.DefeatedEnemies = pending.DefeatedEnemies; }
                if (_launch.DungeonId == "home" && !PreparePracticeMemberExit(world, pending, candidate, entry, out var practiceError))
                { RejectMemberExit(pending, "PracticeExitFactsRejected"); GameLog.Warn(practiceError, "OnlineExit"); continue; }
                if (pending.Reason == OnlineRunExitReason.FinalDeath
                    && !TryBuildFinalDeathFacts(candidate.Assets, out candidate.FinalRetiredItems, out candidate.FinalDeathLostMaterials))
                { RejectMemberExit(pending, "FinalDeathFactsRejected"); continue; }
                if (!InstanceCheckpointJson.TrySerializeMemberExit(candidate, out var json, out var error))
                { RejectMemberExit(pending, "MemberExitEncodingRejected"); GameLog.Warn(error, "OnlineExit"); continue; }
                pending.Candidate = candidate; pending.Body = json; pending.Losses = losses; pending.Phase = OnlineRunExitPhase.Submitting;
                pending.NextAttempt = Time.realtimeSinceStartupAsDouble; SendMemberExitStatus(pending);
            }
        }
        internal void RefreshMemberExitStatus(JoinConnectionIdentity identity)
        {
            if (identity == null || identity.ActorId < 1 || identity.ActorId > 4) return;
            var pending = _memberExits[(int)identity.ActorId - 1];
            if (pending == null || pending.Origin.PlayerId != identity.PlayerId || pending.Origin.RunId != identity.RunId
                || pending.Origin.RoomEpoch != identity.RoomEpoch || identity.ConnectionEpoch < pending.Origin.ConnectionEpoch) return;
            if (pending.Phase == OnlineRunExitPhase.Uncertain && !pending.Sending)
            { pending.Attempts = 0; pending.NextAttempt = Time.realtimeSinceStartupAsDouble; }
            SendMemberExitStatus(pending);
        }
        private void PumpMemberExits()
        {
            if (!Ready || _launch == null) return;
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < _memberExits.Length; i++)
            {
                var pending = _memberExits[i];
                if (pending == null || pending.Body == null || pending.Sending || pending.Attempts >= 3
                    || pending.Phase == OnlineRunExitPhase.Committed || pending.Phase == OnlineRunExitPhase.Rejected
                    || now < pending.NextAttempt) continue;
                pending.Sending = true; StartCoroutine(DeliverMemberExit(pending));
            }
        }

        /// <summary>
        /// 仅最终Receipt确认的Exited成员可提供历史损耗。它不是Member/库存导出，不能重授资源资格。
        /// 每个Exited必须有明确记录（含双空的已知零）；缺失/错身份整次拒绝，保留原Head，不能默补零。
        /// </summary>
        public int CopyExitedMemberLossesTo(OnlineExitedMemberLossSnapshot[] destination)
        {
            if (destination == null || destination.Length < 4) return -1;
            Array.Clear(destination, 0, 4);
            if (Inventory == null) return -1;
            int count = 0;
            for (int i = 0; i < _memberExits.Length; i++)
            {
                if (!Inventory.IsMemberExited((ulong)i + 1)) continue;
                var pending = _memberExits[i];
                if (pending == null || pending.Phase != OnlineRunExitPhase.Committed || pending.Receipt == null || !pending.Receipt.Exited
                    || pending.Losses == null || pending.Losses.RunId != RunId
                    || pending.Losses.PlayerId != pending.Receipt.PlayerId || pending.Losses.PlayerId != pending.Origin.PlayerId
                    || pending.Losses.ActorId != (ulong)i + 1 || pending.Losses.ActorId != pending.Receipt.ActorId
                    || !OnlineExitedMemberLossSnapshot.TryCopy(pending.Losses, out var copy))
                { Array.Clear(destination, 0, 4); return -1; }
                destination[count++] = copy;
            }
            return count;
        }
        private IEnumerator DeliverMemberExit(MemberExit pending)
        {
            long postStatus = 0, queryStatus = 0; string postFailure = null;
            try
            {
                pending.Attempts++;
                // 首先查询：上次POST可能已经提交，或新连接已经失去Mint资格；404本身不是成功。
                if (!CreateMemberExitRequest(pending, true)) yield break;
                yield return pending.Request.SendWebRequest();
                queryStatus = pending.Request.responseCode;
                if (TryAcceptMemberExitReceipt(pending)) yield break;
                ReleaseMemberExitRequest(pending);
                if (queryStatus == 401 || queryStatus == 403) { RejectMemberExit(pending, "MemberExitAuthenticationRejected"); yield break; }
                if (queryStatus != 404) { UncertainMemberExit(pending); yield break; }
                if (!CreateMemberExitRequest(pending, false)) yield break;
                yield return pending.Request.SendWebRequest();
                postStatus = pending.Request.responseCode;
                if (TryAcceptMemberExitReceipt(pending)) yield break;
                if (postStatus == 409)
                    try { postFailure = JsonUtility.FromJson<OnlineErrorReply>(pending.Request.downloadHandler.text)?.Code; }
                    catch (ArgumentException) { postFailure = null; }
                ReleaseMemberExitRequest(pending);
                // 丢响应后必须查相同ID，不能新建候选或用HTTP失败回滚已经提交的库存。
                if (!CreateMemberExitRequest(pending, true)) yield break;
                yield return pending.Request.SendWebRequest();
                queryStatus = pending.Request.responseCode;
                if (TryAcceptMemberExitReceipt(pending)) yield break;
                if (CanReplaceRejectedMemberExit(postStatus, postFailure, queryStatus))
                {
                    pending.Replaceable = true;
                    RejectMemberExit(pending, "RunMemberExitConnectionSuperseded");
                }
                else if (queryStatus == 404 && (postStatus == 400 || postStatus == 401 || postStatus == 403 || postStatus == 409))
                    RejectMemberExit(pending, "MemberExitCandidateRejected");
                else UncertainMemberExit(pending);
            }
            finally
            {
                ReleaseMemberExitRequest(pending); pending.Sending = false;
                pending.NextAttempt = Time.realtimeSinceStartupAsDouble + 2;
                if (pending.Attempts >= 3 && pending.Phase != OnlineRunExitPhase.Committed && pending.Phase != OnlineRunExitPhase.Rejected)
                    UncertainMemberExit(pending);
            }
        }
        private bool CreateMemberExitRequest(MemberExit pending, bool query)
        {
            try
            {
                pending.Request = query ? RunMemberExitHttp.Query(_launch, _secret, pending.Command)
                    : RunMemberExitHttp.Create(_launch, _secret, pending.Body);
                return true;
            }
            catch (ArgumentException) { RejectMemberExit(pending, "MemberExitControlConfigurationRejected"); return false; }
        }
        private bool TryAcceptMemberExitReceipt(MemberExit pending)
        {
            if (pending.Request == null || pending.Request.result != UnityWebRequest.Result.Success) return false;
            OnlineRunMemberExitReceipt receipt;
            try { receipt = JsonUtility.FromJson<OnlineRunMemberExitReceipt>(pending.Request.downloadHandler.text); }
            catch (ArgumentException) { return false; }
            if (!MatchesMemberExitReceipt(pending.Candidate, receipt)) return false;
            if (pending.Receipt != null && (pending.Receipt.PayloadHash != receipt.PayloadHash || pending.Receipt.CommittedVersion != receipt.CommittedVersion)) return false;
            // 只有最终收据确认后，死亡销毁量才进入后续整Run的第三来源累计损耗。
            // 不调用全Run退休Journal重复退休；个人事务已经持久化精确终止集合。
            if (pending.Losses == null) return false;
            var death = receipt.FinalDeathLostMaterials;
            if (receipt.SchemaVersion == OnlineRunMemberExitRequest.LegacySchemaVersion) death = Array.Empty<Game.NetworkProtocol.OnlineOwnedMaterialAmountSnapshot>();
            var copiedDeath = new Game.Core.OnlineOwnedMaterialAmountSnapshot[death.Length];
            for (int i = 0; i < death.Length; i++) copiedDeath[i] = new Game.Core.OnlineOwnedMaterialAmountSnapshot
                { MaterialKey = death[i].MaterialKey, Quantity = death[i].Quantity };
            pending.Losses.FinalDeathLost = copiedDeath;
            pending.Receipt = receipt; pending.Phase = OnlineRunExitPhase.Committed; pending.Failure = null;
            // 最终业务事实先进入实例身份簿；不能依赖场景仍存在才阻止旧Entry回灌。
            if (Inventory != null) Inventory.MarkMemberExited(receipt.ActorId, receipt.PlayerId);
            var completedWorld = OnlineWorldBootstrap.Active;
            if (_launch != null && _launch.DungeonId == "home" && completedWorld != null) completedWorld.Combat?.CompleteHomeHandoff();
            if (completedWorld != null) completedWorld.CompleteMemberExit(receipt.ActorId, receipt.PlayerId);
            SendMemberExitStatus(pending); return true;
        }
        internal static bool MatchesMemberExitReceipt(OnlineRunMemberExitRequest candidate, OnlineRunMemberExitReceipt receipt)
        {
            return candidate != null && candidate.Assets != null && receipt != null && receipt.Exited
                && MatchesMemberExitReasonAndFacts(candidate, receipt)
                && receipt.SettlementId == candidate.SettlementId && receipt.InstanceId == candidate.InstanceId
                && receipt.RunId == candidate.RunId && receipt.PlayerId == candidate.PlayerId && receipt.ActorId == candidate.ActorId
                && receipt.RoomEpoch == candidate.RoomEpoch && receipt.ConnectionEpoch == candidate.ConnectionEpoch
                && receipt.CommittedVersion > candidate.ExpectedPersistentVersion && MemberExitHex(receipt.PayloadHash, 64)
                && receipt.Gold == candidate.Assets.Gold && receipt.CurrencyVersion == candidate.Assets.CurrencyVersion;
        }
        internal static bool CanReplaceRejectedMemberExit(long postStatus, string code, long authenticatedQueryStatus)
            => postStatus == 409 && code == "RunMemberExitConnectionSuperseded" && authenticatedQueryStatus == 404;
        internal static bool CanAddressMemberExitCommand(string current, string previous, uint originEpoch, string incoming, uint authenticatedEpoch)
        {
            if (!MemberExitHex(incoming, 32) || authenticatedEpoch == 0 || authenticatedEpoch < originEpoch) return false;
            // 先匹配当前/上一份固定候选，跨连接仍保留原ID；不能因新transport而改已捕获正文。
            if (incoming == current || incoming == previous) return true;
            uint prefix = 0;
            for (int i = 0; i < 8; i++) prefix = (prefix << 4) | (uint)(incoming[i] <= '9' ? incoming[i] - '0' : incoming[i] - 'a' + 10);
            // 新ID仅能属于当前真实认证epoch。初次也校验，阻止预埋未来ID后在历史淘汰时回用。
            return prefix == authenticatedEpoch && (originEpoch == 0 || authenticatedEpoch > originEpoch);
        }
        private void SendMemberExitStatus(MemberExit pending)
        {
            var connection = _manager != null ? _manager.ActorConnection((int)pending.Origin.ActorId - 1) : null;
            var current = connection != null ? connection.authenticationData as JoinConnectionIdentity : null;
            if (current == null || current.PlayerId != pending.Origin.PlayerId || current.RunId != pending.Origin.RunId) return;
            var receipt = pending.Receipt;
            var status = new OnlineRunExitStatus { CommandId = pending.Command, ConnectionEpoch = current.ConnectionEpoch,
                Phase = pending.Phase, Failure = pending.Failure ?? string.Empty,
                Reason = pending.Reason,
                CommittedVersion = receipt != null ? receipt.CommittedVersion : 0, PayloadHash = receipt != null ? receipt.PayloadHash : string.Empty };
            OnlineWorldBootstrap.Active?.SendMemberExitStatus(current, status);
        }
        /// <summary>
        /// 清理前快照本身保持原实例/随机属性/组分；只派生终止证据，不先改Owner再赌HTTP成功。
        /// 装备背包只保护外壳。其内容、工具与法杖内卡仍退休；已经丢地/转交的ID不在此Owned中。
        /// </summary>
        internal static bool TryBuildFinalDeathFacts(Game.NetworkProtocol.OnlineOwnedAssetSnapshot assets,
            out OnlineRunAssetCheckpointRetired[] retired, out Game.NetworkProtocol.OnlineOwnedMaterialAmountSnapshot[] lost)
        {
            retired = null; lost = null;
            if (assets == null || assets.Items == null || assets.Items.Length > Game.NetworkProtocol.OnlineOwnedAssetSnapshot.MaximumItems) return false;
            var entries = new List<OnlineRunAssetCheckpointRetired>(assets.Items.Length);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var quantities = new ulong[9];
            for (int i = 0; i < assets.Items.Length; i++)
            {
                var item = assets.Items[i];
                if (item == null || string.IsNullOrEmpty(item.InstanceId) || string.IsNullOrEmpty(item.ContentId)
                    || item.Revision == 0 || !ids.Add(item.InstanceId)) return false;
                if (item.LocationKind == 4) continue; // 沿原PracticeDeathSettlement保留已装备背包本体，不另造新包。
                entries.Add(new OnlineRunAssetCheckpointRetired { InstanceId = item.InstanceId, ContentId = item.ContentId,
                    Revision = item.Revision, Reason = OnlineRunAssetRetiredReason.Destroyed });
                if (item.ContainerState != null)
                {
                    if (item.ContainerState.SchemaVersion != 1 || item.ContainerState.Components == null
                        || item.ContainerState.Components.Length > 8) return false;
                    uint seen = 0;
                    foreach (var amount in item.ContainerState.Components)
                    {
                        if (amount == null || amount.MaterialKey < 1 || amount.MaterialKey > 8 || amount.Quantity == 0
                            || (seen & (1u << amount.MaterialKey)) != 0 || !AddFinalDeathAmount(quantities, amount.MaterialKey, amount.Quantity)) return false;
                        seen |= 1u << amount.MaterialKey;
                    }
                }
                if (item.MaterialState != null && (item.MaterialState.SchemaVersion != 1
                    || !AddFinalDeathAmount(quantities, item.MaterialState.MaterialKey, item.MaterialState.Quantity))) return false;
            }
            int count = 0;
            for (int i = 1; i <= 8; i++) if (quantities[i] != 0) count++;
            var amounts = new Game.NetworkProtocol.OnlineOwnedMaterialAmountSnapshot[count];
            int at = 0;
            for (byte i = 1; i <= 8; i++) if (quantities[i] != 0)
                amounts[at++] = new Game.NetworkProtocol.OnlineOwnedMaterialAmountSnapshot { MaterialKey = i, Quantity = quantities[i] };
            retired = entries.ToArray(); lost = amounts; return true;
        }
        private static bool AddFinalDeathAmount(ulong[] quantities, byte key, ulong quantity)
        {
            if (key < 1 || key > 8 || quantity == 0 || quantities[key] > ulong.MaxValue - quantity) return false;
            quantities[key] += quantity; return true;
        }
        private static bool MatchesMemberExitReasonAndFacts(OnlineRunMemberExitRequest candidate, OnlineRunMemberExitReceipt receipt)
        {
            if (candidate.SchemaVersion == OnlineRunMemberExitRequest.LegacySchemaVersion)
                return receipt.SchemaVersion == OnlineRunMemberExitRequest.LegacySchemaVersion
                    && candidate.Reason == OnlineRunMemberExitReason.None && receipt.Reason == OnlineRunMemberExitReason.None;
            if ((candidate.SchemaVersion != OnlineRunMemberExitRequest.LegacyFinalDeathSchemaVersion
                    && candidate.SchemaVersion != OnlineRunMemberExitRequest.CurrentSchemaVersion) || receipt.SchemaVersion != candidate.SchemaVersion
                || candidate.Reason < OnlineRunMemberExitReason.VoluntaryExit || candidate.Reason > OnlineRunMemberExitReason.FinalDeath
                || receipt.Reason != candidate.Reason || candidate.FinalRetiredItems == null || receipt.FinalRetiredItems == null
                || candidate.FinalRetiredItems.Length != receipt.FinalRetiredItems.Length
                || candidate.FinalDeathLostMaterials == null || receipt.FinalDeathLostMaterials == null
                || candidate.FinalDeathLostMaterials.Length != receipt.FinalDeathLostMaterials.Length) return false;
            if (candidate.SchemaVersion == OnlineRunMemberExitRequest.CurrentSchemaVersion
                && (receipt.SuccessfulExtraction != candidate.SuccessfulExtraction || receipt.CollectedGmu != candidate.CollectedGmu
                    || receipt.DefeatedEnemies != candidate.DefeatedEnemies || receipt.PracticeSessionScope != candidate.PracticeSessionScope
                    || receipt.PracticeContinuityHash != candidate.PracticeContinuityHash)) return false;
            var facts = new Dictionary<string, OnlineRunAssetCheckpointRetired>(candidate.FinalRetiredItems.Length, StringComparer.Ordinal);
            foreach (var fact in candidate.FinalRetiredItems)
                if (fact == null || string.IsNullOrEmpty(fact.InstanceId) || !facts.TryAdd(fact.InstanceId, fact)) return false;
            foreach (var fact in receipt.FinalRetiredItems)
            {
                if (fact == null || !facts.TryGetValue(fact.InstanceId, out var expected) || fact.ContentId != expected.ContentId
                    || fact.Revision != expected.Revision || fact.Reason != expected.Reason) return false;
                facts.Remove(fact.InstanceId);
            }
            if (facts.Count != 0) return false;
            var amounts = new ulong[9];
            foreach (var value in candidate.FinalDeathLostMaterials)
                if (value == null || value.MaterialKey < 1 || value.MaterialKey > 8 || value.Quantity == 0
                    || amounts[value.MaterialKey] != 0) return false;
                else amounts[value.MaterialKey] = value.Quantity;
            foreach (var value in receipt.FinalDeathLostMaterials)
                if (value == null || value.MaterialKey < 1 || value.MaterialKey > 8 || value.Quantity == 0
                    || amounts[value.MaterialKey] != value.Quantity) return false;
                else amounts[value.MaterialKey] = 0;
            for (int i = 1; i <= 8; i++) if (amounts[i] != 0) return false;
            return true;
        }

        private void RejectMemberExit(MemberExit pending, string reason)
        { pending.Phase = OnlineRunExitPhase.Rejected; pending.Failure = reason; SendMemberExitStatus(pending); }
        private void UncertainMemberExit(MemberExit pending)
        { pending.Phase = OnlineRunExitPhase.Uncertain; pending.Failure = "MemberExitReceiptUnconfirmed"; SendMemberExitStatus(pending); }
        private static void ReleaseMemberExitRequest(MemberExit pending)
        { var request = pending.Request; pending.Request = null; request?.Dispose(); }
        private void StopMemberExitPublisher()
        {
            for (int i = 0; i < _memberExits.Length; i++)
            { var pending = _memberExits[i]; if (pending == null) continue; pending.Request?.Abort(); ReleaseMemberExitRequest(pending); }
        }
        private static bool MemberExitHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            for (int i = 0; i < value.Length; i++) if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return false;
            return true;
        }
    }
}
