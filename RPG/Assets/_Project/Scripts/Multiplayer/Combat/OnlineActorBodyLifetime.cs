using System;
using Game.Combat;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 在线尸体保留原 Prefab 的死亡移除窗口，但到期只撤销碰撞/视觉，不销毁 Run 内的角色身份。
    /// Collider 在出生时缓存，Renderer 在首个死亡边沿缓存以包含后续子特效；持续 Tick 不扫描层级或分配。
    /// </summary>
    public sealed class OnlineActorBodyLifetime
    {
        private readonly Collider[] _colliders;
        private readonly StatusController[] _statuses;
        private readonly GameObject _root;
        private Renderer[] _renderers;
        private readonly float _prefabDelay;
        private uint _deathTick;
        private float _remainingAtDeath;

        public bool DeathMarked { get; private set; }
        public bool Retired { get; private set; }
        public float PrefabDelay => _prefabDelay;

        public OnlineActorBodyLifetime(GameObject root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            _root = root;
            _colliders = root.GetComponentsInChildren<Collider>(true);
            _statuses = root.GetComponentsInChildren<StatusController>(true);
            var feedback = root.GetComponent<CharacterCombatFeedback>();
            _prefabDelay = feedback != null ? feedback.DeathRemovalDelay : 0f;
        }

        /// <summary>
        /// 首次死亡事实决定本次生命的结束窗口。重复快照和新 ConnectionEpoch 都不能重启它；
        /// 晚加入使用服务器给出的剩余秒数，零表示服务器上的尸体已完成清理。
        /// </summary>
        public void MarkDeath(uint deathTick, float remainingSeconds)
        {
            if (Retired) return;
            float remaining = float.IsFinite(remainingSeconds) ? Mathf.Max(0f, remainingSeconds) : 0f;
            if (!DeathMarked)
            {
                DeathMarked = true; _deathTick = deathTick; _remainingAtDeath = remaining;
                // Boss Awake 与受击流血会晚于角色出生创建子 Renderer；只在死亡这一次离散边沿补齐。
                _renderers = _root.GetComponentsInChildren<Renderer>(true);
            }
            else
            {
                // 两个通道到达顺序不一致。比较同一 Tick 锚点下的 deadline，只允许权威事实缩短，不能延长。
                float candidate = unchecked((int)(deathTick - _deathTick)) * FixedStepClock.DeltaTime + remaining;
                _remainingAtDeath = Mathf.Min(_remainingAtDeath, Mathf.Max(0f, candidate));
            }
            // 事实本身已证明经过的 ServerTick 可以清尸体，无需等待另一条运动通道追上来。
            Step(deathTick);
        }

        public float RemainingSeconds(uint serverTick)
        {
            if (!DeathMarked || Retired) return 0f;
            // 同一 Run 的 uint Tick 允许正常回卷；客户端运动快照暂落后死亡事实时，不倒扣尚未经过的时间。
            int elapsedTicks = unchecked((int)(serverTick - _deathTick));
            return Mathf.Max(0f, _remainingAtDeath - Mathf.Max(0, elapsedTicks) * FixedStepClock.DeltaTime);
        }

        public void Step(uint serverTick)
        {
            if (!DeathMarked || Retired || RemainingSeconds(serverTick) > 0f) return;
            Retired = true;
            // Collider 与 Renderer 同一 Deadline 退休，匹配单机 DestroyDelay 的两秒遮挡窗口。
            // Root/Health/Cast 继续存在；未来复活必须走显式 NewLife，连接重入不得自行开启这些组件。
            for (int i = 0; i < _colliders.Length; i++) if (_colliders[i] != null) _colliders[i].enabled = false;
            for (int i = 0; i < _renderers.Length; i++) if (_renderers[i] != null) _renderers[i].enabled = false;
            // 状态快照可能晚于首次死亡，甚至晚于清理到达；停现有VFX并封住后续新生，不修改状态强度。
            for (int i = 0; i < _statuses.Length; i++) if (_statuses[i] != null) _statuses[i].RetireReplicaPresentation();
        }
    }
}
