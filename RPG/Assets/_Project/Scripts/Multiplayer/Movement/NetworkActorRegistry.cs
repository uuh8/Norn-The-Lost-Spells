using Game.Character;
using Game.Combat;
using Game.Core;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Multiplayer
{
    /// <summary>
    /// 固定十六席：1..4玩家，后续NPC由当前可信副本配置逐项定义，身份不因死亡/断线压缩或复用。
    /// Registry负责对象身份、执行模式和激活边界；遭遇顺序由Character的Director裁定，不在这里猜场景名称。
    /// </summary>
    public sealed class NetworkActorRegistry : ICombatActorRosterReadOnly, IOnlineDungeonNpcReadOnly
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
        private readonly Slot[] _slots = new Slot[OnlineActorLimits.MaximumActors];
        private readonly OnlineMovementProfile _profile;
        private readonly OnlineCombatDefinition _combat;
        private readonly CombatSimulationWorld _combatWorld;
        private readonly Transform _parent;
        private readonly OnlineDungeonDefinition _dungeon;
        public int Count => OnlineActorLimits.PlayerCount; // AI目标Roster只枚举玩家，不把NPC误当作队友目标。
        public int Capacity => _slots.Length;
        public Slot this[int index] => _slots[index];
        public NetworkActorRegistry(OnlineMovementProfile profile, Transform parent, OnlineCombatDefinition combat = null,
            CombatSimulationWorld combatWorld = null, OnlineDungeonDefinition dungeon = null)
        {
            _profile = profile; _parent = parent; _combat = combat; _combatWorld = combatWorld; _dungeon = dungeon;
            if (_dungeon != null && !_dungeon.TryValidate(out string error))
                throw new System.ArgumentException("副本Actor表无效：" + error, nameof(dungeon));
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = new Slot();
                // 每个远端Actor有自己的有界历史；配置显式来自同一Profile，不从首包到达时间猜网络频率。
                _slots[i].Interpolation.Configure(profile.SnapshotInterval, profile.InterpolationDelay, profile.MaximumInterpolationDelay);
            }
        }
        public Slot EnsurePlayer(int seat, PlayerExecutionMode mode)
        {
            if (seat < 0 || seat >= OnlineActorLimits.PlayerCount) throw new System.ArgumentOutOfRangeException(nameof(seat));
            var slot = _slots[seat]; if (slot.Player != null) return slot;
            var container = new GameObject("OnlinePlayer_" + (seat + 1)); container.SetActive(false); container.transform.SetParent(_parent, false);
            var position = _dungeon != null ? _dungeon.PlayerSpawn(seat) : _profile.PlayerSpawn(seat);
            var instance = Object.Instantiate(_profile.PlayerPrefab, position, Quaternion.identity, container.transform);
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
            if (actorId < OnlineActorLimits.FirstNpcActorId || actorId > OnlineActorLimits.MaximumActors)
                throw new System.ArgumentOutOfRangeException(nameof(actorId));
            GameObject prefab; Vector3 position; Quaternion rotation; float attackDuration; bool initiallyVisible;
            if (_dungeon != null)
            {
                // 正式地图从可信白名单取得每个NPC。未注册Actor不得回落为“默认近战”或把Actor7当作Boss。
                if (!_dungeon.TryGetNpc(actorId, out var spawn)) throw new System.ArgumentOutOfRangeException(nameof(actorId));
                prefab = spawn.Prefab; position = spawn.Position; rotation = spawn.Rotation;
                attackDuration = spawn.AttackClipDuration; initiallyVisible = spawn.InitiallyVisible;
            }
            else
            {
                // 仅历史运输/战斗lab保留原三席；正式OnlineWorld必须显式传入DungeonDefinition。
                if (actorId > 7) throw new System.ArgumentOutOfRangeException(nameof(actorId));
                prefab = actorId == 5 ? _profile.EnemyPrefab : _combat != null ? actorId == 6 ? _combat.RangedEnemyPrefab : _combat.BossPrefab : null;
                position = actorId == 5 ? _profile.EnemySpawn : _combat != null ? _combat.EnemySpawn(actorId) : Vector3.zero;
                rotation = Quaternion.identity; attackDuration = _combat != null ? _combat.EnemyClipDuration(actorId) : 0;
                initiallyVisible = true;
            }
            var slot = _slots[(int)actorId - 1]; if (slot.Body != null) return slot;
            if (prefab == null) throw new System.InvalidOperationException("在线敌人生产 Prefab 缺失。");
            var container = new GameObject("OnlineEnemy_" + actorId); container.SetActive(false); container.transform.SetParent(_parent, false);
            var instance = Object.Instantiate(prefab, position, rotation, container.transform);
            // 原遭遇中的NPC常以inactive保存；子实例先置activeSelf，外层container仍inactive，
            // 因此配置完成前不会执行Awake，后面的受控激活也不会被Prefab的旧开关吞掉。
            instance.SetActive(true);
            slot.Enemy = instance.GetComponent<EnemyControllerBase>(); slot.Boss = instance.GetComponent<WizardBossController>();
            slot.Health = instance.GetComponent<HealthComponent>(); slot.Root = container; slot.Epoch = 1; slot.Connected = initiallyVisible;
            if ((slot.Enemy != null) == (slot.Boss != null)) throw new System.InvalidOperationException("敌人 Prefab 必须有唯一对应 Controller。");
            if (authority)
            {
                if (slot.Enemy != null)
                {
                    slot.Enemy.ConfigureOnlineAuthority(this);
                    if (_combat != null && _combatWorld != null) slot.Enemy.ConfigureOnlineCombat(_combatWorld, actorId, attackDuration);
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
            PrepareAnimators(instance, authority);
            // Health.Awake会初始化满HP，必须在首个Replica事实到达前完成一次真实Awake。
            // 同帧激活再关闭不会渲染一帧Dormant敌人；后续遭遇激活仅OnEnable，不重建HP/施法工作区。
            container.SetActive(true);
            if (!initiallyVisible) container.SetActive(false);
            IgnoreActorCollisions(); return slot;
        }

        /// <summary>遭遇/可信快照只改变对应NPC的活动资格，不回收ActorId、不重建Prefab或重置资源。</summary>
        public bool SetNpcActive(ulong actorId, bool active)
        {
            if (actorId < OnlineActorLimits.FirstNpcActorId || actorId > OnlineActorLimits.MaximumActors) return false;
            if (_dungeon != null && !_dungeon.TryGetNpc(actorId, out _)) return false;
            var slot = _slots[(int)actorId - 1];
            if (slot.Root == null || slot.Body == null) return false;
            slot.Connected = active;
            if (slot.Root.activeSelf != active) slot.Root.SetActive(active);
            return true;
        }
        public bool TryGetNpcState(ulong actorId, out bool alive, out bool active)
        {
            alive = active = false;
            if (actorId < OnlineActorLimits.FirstNpcActorId || actorId > OnlineActorLimits.MaximumActors
                || _dungeon != null && !_dungeon.TryGetNpc(actorId, out _)) return false;
            var slot = _slots[(int)actorId - 1];
            if (slot.Root == null || slot.Body == null || slot.Health == null) return false;
            alive = slot.Health.IsAlive; active = slot.Connected && slot.Root.activeInHierarchy; return true;
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
            actor = default; if (index < 0 || index >= OnlineActorLimits.PlayerCount) return false;
            var slot = _slots[index]; if (slot.Player == null) return false;
            actor = new CombatActorSnapshot { ActorId = (ulong)(index + 1), Position = slot.Player.transform.position,
                TeamId = slot.Health != null ? slot.Health.TeamId : 1, IsAlive = slot.Health == null || slot.Health.IsAlive,
                IsConnected = slot.Connected, Targetable = slot.Targetable, Transform = slot.Player.transform };
            return true;
        }
        public void Neutralize()
        { for (int i = 0; i < OnlineActorLimits.PlayerCount; i++) { _slots[i].Input.Neutralize(); if (_slots[i].Player != null) _slots[i].Player.ClearOnlineInput(); } }
        public void Dispose()
        { for (int i = 0; i < _slots.Length; i++) if (_slots[i].Root != null) Object.Destroy(_slots[i].Root); }
    }
}
