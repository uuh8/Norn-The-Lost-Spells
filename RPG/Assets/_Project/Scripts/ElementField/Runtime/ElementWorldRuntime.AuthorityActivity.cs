using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime : IWorldAuthorityActivity
    {
        private readonly ulong[] _authorityActivityActors = new ulong[4];
        private readonly Vector3[] _authorityActivityPositions = new Vector3[4];
        private readonly ElementWorldActivityRegions _authorityActivityCandidate = new ElementWorldActivityRegions();
        public WorldActivityFailure LastActivityFailure { get; private set; }

        /// <summary>
        /// 可信装配层按固定角色槽更新位置。不能借来源轮转来代表玩家：四名玩家即使相距很远，
        /// 每轮也必须同时参与接触查询。此接口不推时钟，所以Frozen期间更新位置不会积累模拟债务。
        /// </summary>
        public bool TrySetActor(int slot, ulong actorId, Vector3 position)
        {
            if (_executionMode != WorldExecutionMode.Authority || _simulationFaulted || IsWorldStepInProgress
                || (uint)slot >= 4 || actorId == 0 || !float.IsFinite(position.x)
                || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return false;
            for (int i = 0; i < 4; i++)
                if (i != slot && _authorityActivityActors[i] == actorId) return false;
            _authorityActivityActors[slot] = actorId;
            _authorityActivityPositions[slot] = position;
            return true;
        }

        public void ClearActor(int slot)
        {
            if ((uint)slot >= 4 || IsWorldStepInProgress) return;
            _authorityActivityActors[slot] = 0;
            _authorityActivityPositions[slot] = default;
        }

        /// <summary>
        /// Authority的CPU规则推进全部有界resident/pending，独立于客户端发送Interest。
        /// 这里的32区域只服务Exposure等空间查询，不给每个Fire Chunk花名额；因此大量远处火源
        /// 可以继续反应，且不会把两个Actor之间的空白填成Envelope。代价是服务器不能靠距离休眠
        /// 节省Solver工作，仍受各Solver既有每Tick预算和Store容量约束，需另做目标机器压测。
        /// </summary>
        private void RefreshAuthorityActivity()
        {
            if (_simulationFaulted) return;
            _authorityActivityCandidate.Clear();
            float size = CellSize * ChunkSize;
            for (int i = 0; i < 4; i++)
            {
                if (_authorityActivityActors[i] == 0) continue;
                if (!ElementWorldActivitySources.TryBuildBounds(_authorityActivityPositions[i], _profile.UnitActivityRadius,
                    _origin, size, out Bounds bounds) || !_authorityActivityCandidate.TryAdd(bounds))
                { FailAuthorityActivity(WorldActivityFailure.RegionCapacity); return; }
            }
            // 同一位置的Actor和StatusController使用同一量化/radius，TryAdd精确去重。
            // 不用单机12/6轮转预算：漏一帧活动资格会改变权威玩法，而不是单纯降低远处画质。
            if (_activitySources.RejectedCount != 0)
            { FailAuthorityActivity(WorldActivityFailure.SourceCapacity); return; }
            if (!_activitySources.TryAppendAll(_authorityActivityCandidate, _profile.UnitActivityRadius,
                _profile.ProjectileActivityRadius, _origin, size))
            { FailAuthorityActivity(WorldActivityFailure.RegionCapacity); return; }
            _activityRegions.CopyFrom(_authorityActivityCandidate);
            LastActivityFailure = WorldActivityFailure.None;
            // 原Legacy调度需要一个有效anchor才遍历Store。Authority不借此anchor裁剪，
            // IsGameplayChunk对全部resident返回true；没有角色时仍必须结算在途火与液体。
            _hasInterestChunk = true;
        }

        private void FailAuthorityActivity(WorldActivityFailure failure)
        {
            LastActivityFailure = failure;
            _simulationFaulted = true;
            // 保留上一份完整集合，绝不发布半份；Source/写入口/后续Step统一拒绝此Faulted世界。
        }
    }
}
