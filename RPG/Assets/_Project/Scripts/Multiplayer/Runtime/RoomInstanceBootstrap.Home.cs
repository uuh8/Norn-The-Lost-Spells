using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using UnityEngine;
using Core = Game.Core;

namespace Game.Multiplayer
{
    public sealed partial class RoomInstanceBootstrap
    {
        /// <summary>
        /// Home仍走原个人稳定捕获与固定正文重试。附带的是DS真实运行事实，不接收Client的连续性或物品清单；
        /// wrapper、原地面闭包和实际退休Journal随同一最终交易入库，收据前不清现场。
        /// </summary>
        private bool PreparePracticeMemberExit(OnlineWorldBootstrap world, MemberExit pending, OnlineRunMemberExitRequest candidate,
            InstanceEntryMember entry, out string error)
        {
            error = null;
            if (pending.Reason != OnlineRunExitReason.VoluntaryExit || world.Combat == null || entry?.Profile == null
                || !MemberExitHex(entry.Profile.PracticeSessionScope, 64)
                || !world.Combat.TryCaptureHomeContinuity(pending.Origin.ActorId, out var continuity, out var ground, out error)) return false;
            string raw = JsonUtility.ToJson(continuity);
            if (!PracticeContinuityEnvelopeRules.TryPack(pending.Origin.PlayerId, candidate.Assets.GameplayConfigHash,
                raw, out var wrapper, out error)) return false;
            candidate.PracticeSessionScope = entry.Profile.PracticeSessionScope;
            candidate.PracticeContinuityJson = JsonUtility.ToJson(wrapper);
            using (var hash = SHA256.Create())
                candidate.PracticeContinuityHash = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(candidate.PracticeContinuityJson)))
                    .Replace("-", string.Empty).ToLowerInvariant();
            candidate.PracticeGroundItems = new OnlineRunAssetCheckpointGround[ground.Items.Length];
            for (int i = 0; i < ground.Items.Length; i++)
            {
                var root = ground.Items[i];
                // 原映射负责深拷贝roll/真实GMU与可空状态，不能把Entry旧值或UI值填进当前地面。
                var copied = OnlineOwnedAssetMapping.ToShared(new Core.OnlineOwnedAssetSnapshot { Items = JoinPracticeTree(root) });
                var children = new Game.NetworkProtocol.OnlineOwnedItemSnapshot[root.Children.Length];
                Array.Copy(copied.Items, 1, children, 0, children.Length);
                candidate.PracticeGroundItems[i] = new OnlineRunAssetCheckpointGround { WorldOwnerId = root.WorldOwnerId,
                    Item = copied.Items[0], Children = children,
                    Position = new OnlineRunAssetCheckpointVector3 { X = root.Position.x, Y = root.Position.y, Z = root.Position.z },
                    Rotation = new OnlineRunAssetCheckpointQuaternion { X = root.Rotation.x, Y = root.Rotation.y, Z = root.Rotation.z, W = root.Rotation.w } };
            }
            if (!world.RunCheckpointJournal.TryCapture(out var retired) || retired.Length > Game.NetworkProtocol.OnlineOwnedAssetSnapshot.MaximumItems)
            { error = "Home真实退休Journal暂未稳定或超过个人正文预算。"; return false; }
            var facts = new List<OnlineRunAssetCheckpointRetired>(retired.Length);
            foreach (var fact in retired)
            {
                if (fact == null || fact.Reason < OnlineItemRetirementReason.Consumed || fact.Reason > OnlineItemRetirementReason.Destroyed)
                { error = "Home真实退休事实无效。"; return false; }
                // Home由业务保证只有本人；Journal同Run唯一，包含本人库存与该Home地面真实合并/消耗。
                facts.Add(new OnlineRunAssetCheckpointRetired { InstanceId = fact.InstanceId, ContentId = fact.ContentId,
                    Revision = fact.Revision, Reason = (OnlineRunAssetRetiredReason)(byte)fact.Reason });
            }
            candidate.FinalRetiredItems = facts.ToArray(); return true;
        }
        private static Core.OnlineOwnedItemSnapshot[] JoinPracticeTree(OnlineGroundItemSnapshot root)
        {
            var result = new Core.OnlineOwnedItemSnapshot[root.Children.Length + 1]; result[0] = root.Item;
            Array.Copy(root.Children, 0, result, 1, root.Children.Length); return result;
        }
    }
}
