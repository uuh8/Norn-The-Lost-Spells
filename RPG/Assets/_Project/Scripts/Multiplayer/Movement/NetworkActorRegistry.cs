using Game.Character;
using Game.Combat;
using Game.Core;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Multiplayer
{
    /// <summary>固定八席：1..4 玩家，5 近战，6 远程，7 Boss；身份不因死亡/断线压缩或复用。</summary>
    public sealed class NetworkActorRegistry : ICombatActorRosterReadOnly
    {
        public sealed class Slot
        {
            public PlayerControllerBase Player;
            public EnemyControllerBase Enemy;
            public WizardBossController Boss;
            public OnlineCastDriver Cast;
            public HealthComponent Health;
            public OnlineActorBodyLifetime BodyLifetime;
            public GameObject Root;
            public uint Epoch, LastSnapshot;
            public int ConnectionId;
            public bool Connected, SimulationReady;
            // 认证只确认身份；首个合法输入证明对应 Epoch 的客户端已建立玩法世界。
            public bool Targetable => Connected && SimulationReady && (Health == null || Health.IsAlive);
            public Transform Body => Player != null ? Player.transform : Enemy != null ? Enemy.transform : Boss != null ? Boss.transform : null;
            public readonly AuthorityInputBuffer Input = new AuthorityInputBuffer();
            public readonly RemoteMovementInterpolation Interpolation = new RemoteMovementInterpolation();
        }
        private readonly Slot[] _slots = new Slot[8];
        private readonly OnlineMovementProfile _profile;
        private readonly OnlineCombatDefinition _combat;
        private readonly CombatSimulationWorld _combatWorld;
        private readonly Transform _parent;
        public int Count => 4; // 固定容量；未出生的槽由TryGetActor返回false，不压缩ActorId。
        public int Capacity => _slots.Length;
        public Slot this[int index] => _slots[index];
        public NetworkActorRegistry(OnlineMovementProfile profile, Transform parent, OnlineCombatDefinition combat = null, CombatSimulationWorld combatWorld = null)
        {
            _profile = profile; _parent = parent; _combat = combat; _combatWorld = combatWorld;
            for (int i = 0; i < _slots.Length; i++) _slots[i] = new Slot();
        }
        public Slot EnsurePlayer(int seat, PlayerExecutionMode mode)
        {
            var slot = _slots[seat]; if (slot.Player != null) return slot;
            var container = new GameObject("OnlinePlayer_" + (seat + 1)); container.SetActive(false); container.transform.SetParent(_parent, false);
            var instance = Object.Instantiate(_profile.PlayerPrefab, _profile.PlayerSpawn(seat), Quaternion.identity, container.transform);
            slot.Player = instance.GetComponent<PlayerControllerBase>(); slot.Root = container; slot.Health = instance.GetComponent<HealthComponent>();
            // inactive hierarchy保证Configure发生于Awake之前，远端从一开始就没有本机Input/Current。
            slot.Player.ConfigureExecutionMode(mode);
            if (_combatWorld != null) _combatWorld.ConfigureHierarchy(instance, mode == PlayerExecutionMode.Authority ? CombatExecutionMode.Authority : CombatExecutionMode.Replica);
            slot.BodyLifetime = new OnlineActorBodyLifetime(instance);
            PrepareAnimators(instance, mode == PlayerExecutionMode.Authority);
            // Replica 仍需胶囊供准星/遮挡查询；Move/Simulate 权限由角色模式拒绝，互穿由 IgnoreActorCollisions 管理。
            // 关闭 CC 会把真实可见目标从 Raycast 中移除，让近距离瞄准错误回退到远处背景。
            container.SetActive(true); IgnoreActorCollisions(); return slot;
        }
        public Slot EnsureEnemy(bool authority)
            => EnsureEnemy(5, authority);
        public Slot EnsureEnemy(ulong actorId, bool authority)
        {
            if (actorId < 5 || actorId > 7) throw new System.ArgumentOutOfRangeException(nameof(actorId));
            var slot = _slots[(int)actorId - 1]; if (slot.Body != null) return slot;
            GameObject prefab = actorId == 5 ? _profile.EnemyPrefab : _combat != null ? actorId == 6 ? _combat.RangedEnemyPrefab : _combat.BossPrefab : null;
            if (prefab == null) throw new System.InvalidOperationException("在线敌人生产 Prefab 缺失。");
            var container = new GameObject("OnlineEnemy_" + actorId); container.SetActive(false); container.transform.SetParent(_parent, false);
            var instance = Object.Instantiate(prefab, actorId == 5 ? _profile.EnemySpawn : _combat.EnemySpawn(actorId), Quaternion.identity, container.transform);
            slot.Enemy = instance.GetComponent<EnemyControllerBase>(); slot.Boss = instance.GetComponent<WizardBossController>();
            slot.Health = instance.GetComponent<HealthComponent>(); slot.Root = container; slot.Epoch = 1; slot.Connected = true;
            if (slot.Enemy == null && slot.Boss == null) throw new System.InvalidOperationException("敌人 Prefab 没有对应 Controller。");
            if (authority)
            {
                if (slot.Enemy != null)
                {
                    slot.Enemy.ConfigureOnlineAuthority(this);
                    if (_combat != null && _combatWorld != null) slot.Enemy.ConfigureOnlineCombat(_combatWorld, actorId, _combat.EnemyClipDuration(actorId));
                }
                if (slot.Boss != null) slot.Boss.ConfigureAuthority(this, _combatWorld, actorId);
            }
            else
            {
                if (slot.Enemy != null) slot.Enemy.ConfigureOnlineReplica();
                if (slot.Boss != null) slot.Boss.ConfigureReplica();
                var agent = instance.GetComponent<NavMeshAgent>(); if (agent != null) agent.enabled = false;
                // 保留 CharacterController 的查询形状；Replica 不调用 Move，NavMeshAgent 仍保持禁用。
            }
            if (_combatWorld != null) _combatWorld.ConfigureHierarchy(instance, authority ? CombatExecutionMode.Authority : CombatExecutionMode.Replica);
            slot.BodyLifetime = new OnlineActorBodyLifetime(instance);
            PrepareAnimators(instance, authority); container.SetActive(true); IgnoreActorCollisions(); return slot;
        }
        private static void PrepareAnimators(GameObject root, bool headless)
        {
            var animators = root.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                animators[i].applyRootMotion = false;
                animators[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
                if (headless) animators[i].enabled = false; // N3无骨骼命中，服务器运动不能依赖动画求值。
            }
        }
        private void IgnoreActorCollisions()
        {
            // N3合作角色相互穿行；预测不回滚动态角色碰撞，世界/坡面碰撞仍真实执行。
            for (int i = 0; i < _slots.Length; i++)
            {
                var a = ColliderAt(i); if (a == null) continue;
                for (int j = i + 1; j < _slots.Length; j++) { var b = ColliderAt(j); if (b != null) Physics.IgnoreCollision(a, b, true); }
            }
        }
        private CharacterController ColliderAt(int i)
        {
            var slot = _slots[i];
            if (slot.Player != null) return slot.Player.CharacterController;
            return slot.Body != null ? slot.Body.GetComponent<CharacterController>() : null;
        }
        public bool TryGetActor(int index, out CombatActorSnapshot actor)
        {
            actor = default; if (index < 0 || index >= 4) return false;
            var slot = _slots[index]; if (slot.Player == null) return false;
            actor = new CombatActorSnapshot { ActorId = (ulong)(index + 1), Position = slot.Player.transform.position,
                TeamId = slot.Health != null ? slot.Health.TeamId : 1, IsAlive = slot.Health == null || slot.Health.IsAlive,
                IsConnected = slot.Connected, Targetable = slot.Targetable, Transform = slot.Player.transform };
            return true;
        }
        public void Neutralize()
        { for (int i = 0; i < 4; i++) { _slots[i].Input.Neutralize(); if (_slots[i].Player != null) _slots[i].Player.ClearOnlineInput(); } }
        public void Dispose()
        { for (int i = 0; i < _slots.Length; i++) if (_slots[i].Root != null) Object.Destroy(_slots[i].Root); }
    }
}
