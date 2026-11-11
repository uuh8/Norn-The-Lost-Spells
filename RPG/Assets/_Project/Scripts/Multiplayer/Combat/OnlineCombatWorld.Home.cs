using System;
using System.Collections.Generic;
using Game.Character;
using Game.Core;
using Game.Networking;
using UnityEngine;
using Shared = Game.NetworkProtocol;

namespace Game.Multiplayer
{
    public sealed partial class OnlineCombatWorld
    {
        private readonly bool[] _homeEntryRestored = new bool[4];
        private bool _homeGroundRestored;
        public bool ResearchPaused
        {
            get
            {
                if (_disposed || _world.Registry == null) return false;
                for (int i = 0; i < 4; i++) if (_world.Registry[i].Cast != null && _world.Registry[i].Cast.ResearchPaused) return true;
                return false;
            }
        }
        /// <summary>
        /// 只从业务服冻结的同Session Entry恢复。Client无此入口；重连复用存活Workspace，不能再应用一次旧余额。
        /// 外壳限量解压后由Character/Run验证程序和原状态；失败锁住现场，不退回新满蓝Owner。
        /// </summary>
        private bool RestoreHomeEntry(NetworkActorRegistry.Slot slot, JoinConnectionIdentity identity, out string error)
        {
            error = null;
            if (_world.DungeonDefinition == null || !_world.DungeonDefinition.IsPeacefulHome) return true;
            int index = (int)identity.ActorId - 1;
            if (_homeEntryRestored[index]) return true;
            Shared.OnlineProfileReply profile = null;
            for (int i = 0; i < _instance.Inventory.MemberCount; i++)
                if (_instance.Inventory.TryGetFrozenMember(i, out var member) && member.ActorId == identity.ActorId) profile = member.Profile;
            if (profile == null || profile.PlayerId != identity.PlayerId || !PracticeHex(profile.PracticeSessionScope, 64))
            { error = "Home缺少本次登录的可信和平Scope。"; return false; }
            OnlineWandResearchStateSnapshot continuity = null;
            if (!string.IsNullOrEmpty(profile.PracticeContinuityJson))
            {
                if (profile.PracticeContinuityJson.Length > Shared.PracticeContinuityEnvelopeRules.MaximumEnvelopeBytes)
                { error = "Home连续性外壳超过预算。"; return false; }
                try
                {
                    var envelope = JsonUtility.FromJson<Shared.OnlinePracticeContinuityEnvelope>(profile.PracticeContinuityJson);
                    if (!Shared.PracticeContinuityEnvelopeRules.TryExpand(envelope, identity.PlayerId, _definition.GameplayConfigHash, out var raw, out error)) return false;
                    continuity = JsonUtility.FromJson<OnlineWandResearchStateSnapshot>(raw);
                    if (continuity == null || !slot.Cast.TryRestorePracticeContinuity(continuity, out error)) return false;
                }
                catch (ArgumentException) { error = "Home连续性JSON无效，保留原资产。"; return false; }
            }
            if (!_homeGroundRestored)
            {
                if (!OnlineAccountInventoryMapping.TryMap(profile, profile.ContentManifestHash, out var mapped, out error)
                    || !TryMapHomeGround(mapped, out var ground, out error) || !EnsureGroundItems()
                    || !_groundItems.TryRestorePractice(ground, continuity, out error)) return false;
                _homeGroundRestored = true;
            }
            if (slot.Health != null) slot.Health.SetInvulnerable(true);
            _homeEntryRestored[index] = true; return true;
        }

        internal static bool TryMapHomeGround(OnlineAccountInventorySnapshot profile, out OnlineGroundInventorySnapshot ground, out string error)
        {
            ground = null; error = null;
            if (profile == null || profile.Items == null || profile.WorldItems == null || profile.Items.Length > OnlineAccountInventorySnapshot.MaximumItems)
            { error = "Home冻结Profile闭包缺失。"; return false; }
            var byId = new Dictionary<string, OnlineOwnedItemSnapshot>(StringComparer.Ordinal);
            foreach (var item in profile.Items)
                if (item == null || string.IsNullOrEmpty(item.InstanceId) || !byId.TryAdd(item.InstanceId, item))
                { error = "Home冻结物品ID重复或缺失。"; return false; }
            var roots = new List<OnlineGroundItemSnapshot>();
            foreach (var saved in profile.WorldItems)
            {
                if (saved == null || saved.Pose == null || !saved.Pose.IsValid()) { error = "Home冻结地面姿态非法。"; return false; }
                if (saved.Pose.SceneKey != "home.map1" || saved.Pose.WorldSceneKey != "home.map1") continue;
                if (!byId.TryGetValue(saved.InstanceId, out var item) || item.LocationKind != 5 || !PracticeHex(saved.WorldOwnerId, 32))
                { error = "Home地面缺少同ID真实物品。"; return false; }
                var children = new List<OnlineOwnedItemSnapshot>();
                foreach (var candidate in profile.Items) if (candidate.LocationKind == 2 && candidate.ParentInstanceId == item.InstanceId) children.Add(candidate);
                var pose = saved.Pose;
                roots.Add(new OnlineGroundItemSnapshot { InstanceId = item.InstanceId, ContentId = item.ContentId, Revision = item.Revision,
                    WorldOwnerId = saved.WorldOwnerId, Position = new Vector3(pose.PositionX, pose.PositionY, pose.PositionZ),
                    Rotation = new Quaternion(pose.RotationX, pose.RotationY, pose.RotationZ, pose.RotationW),
                    Item = item, Children = children.ToArray(), ContainerState = item.ContainerState, MaterialState = item.MaterialState });
            }
            ground = new OnlineGroundInventorySnapshot { Version = 1, Items = roots.ToArray() }; return true;
        }
        internal static bool PracticeHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }
        internal bool TryCaptureHomeContinuity(ulong actor, out OnlineWandResearchStateSnapshot continuity, out OnlineGroundInventorySnapshot ground, out string error)
        {
            continuity = null; ground = null; error = null;
            if (_world.DungeonDefinition == null || !_world.DungeonDefinition.IsPeacefulHome || actor < 1 || actor > 4 || _groundItems == null) return false;
            var driver = _world.Registry[(int)actor - 1].Cast;
            if (driver == null || !driver.TryCapturePracticeContinuity(out continuity, out error)
                || !_groundItems.TryCapturePracticeContinuity(continuity, out error)) return false;
            ground = _groundItems.Capture(); return ground != null;
        }
        internal void CompleteHomeHandoff()
        { if (_world.DungeonDefinition != null && _world.DungeonDefinition.IsPeacefulHome) _groundItems?.CompletePracticeHandoff(); }
    }
}
