using Game.Character;
using Game.Combat;
using Game.Core;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Multiplayer
{
    /// <summary>四个玩家席位与一个N3敌人的本地映射；不是客户端可任意写入的资产/HP仓库。</summary>
    public sealed class NetworkActorRegistry : ICombatActorRosterReadOnly
    {
        public sealed class Slot
        {
            public PlayerControllerBase Player;
            public EnemyControllerBase Enemy;
            public HealthComponent Health;
            public GameObject Root;
            public uint Epoch, LastSnapshot;
            public int ConnectionId;
            public bool Connected;
            public readonly AuthorityInputBuffer Input = new AuthorityInputBuffer();
            public readonly RemoteMovementInterpolation Interpolation = new RemoteMovementInterpolation();
        }
        private readonly Slot[] _slots = new Slot[5];
        private readonly OnlineMovementProfile _profile;
        private readonly Transform _parent;
        public int Count => 4; // 固定容量；未出生的槽由TryGetActor返回false，不压缩ActorId。
        public Slot this[int index] => _slots[index];
        public NetworkActorRegistry(OnlineMovementProfile profile, Transform parent)
        {
            _profile = profile; _parent = parent;
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
            PrepareAnimators(instance, mode == PlayerExecutionMode.Authority);
            if (mode == PlayerExecutionMode.Replica) slot.Player.GetComponent<CharacterController>().enabled = false;
            container.SetActive(true); IgnoreActorCollisions(); return slot;
        }
        public Slot EnsureEnemy(bool authority)
        {
            var slot = _slots[4]; if (slot.Enemy != null) return slot;
            var container = new GameObject("OnlineEnemy_5"); container.SetActive(false); container.transform.SetParent(_parent, false);
            var instance = Object.Instantiate(_profile.EnemyPrefab, _profile.EnemySpawn, Quaternion.identity, container.transform);
            slot.Enemy = instance.GetComponent<EnemyControllerBase>(); slot.Root = container; slot.Epoch = 1; slot.Connected = true;
            if (authority) slot.Enemy.ConfigureOnlineAuthority(this);
            else
            {
                slot.Enemy.ConfigureOnlineReplica();
                var agent = instance.GetComponent<NavMeshAgent>(); if (agent != null) agent.enabled = false;
                instance.GetComponent<CharacterController>().enabled = false;
            }
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
            for (int i = 0; i < 5; i++)
            {
                var a = ColliderAt(i); if (a == null) continue;
                for (int j = i + 1; j < 5; j++) { var b = ColliderAt(j); if (b != null) Physics.IgnoreCollision(a, b, true); }
            }
        }
        private CharacterController ColliderAt(int i)
        {
            var slot = _slots[i];
            if (slot.Player != null) return slot.Player.CharacterController;
            return slot.Enemy != null ? slot.Enemy.GetComponent<CharacterController>() : null;
        }
        public bool TryGetActor(int index, out CombatActorSnapshot actor)
        {
            actor = default; if (index < 0 || index >= 4) return false;
            var slot = _slots[index]; if (slot.Player == null) return false;
            actor = new CombatActorSnapshot { ActorId = (ulong)(index + 1), Position = slot.Player.transform.position,
                TeamId = slot.Health != null ? slot.Health.TeamId : 1, IsAlive = slot.Health == null || slot.Health.IsAlive,
                IsConnected = slot.Connected, Targetable = slot.Connected && (slot.Health == null || slot.Health.IsAlive), Transform = slot.Player.transform };
            return true;
        }
        public void Neutralize()
        { for (int i = 0; i < 4; i++) { _slots[i].Input.Neutralize(); if (_slots[i].Player != null) _slots[i].Player.ClearOnlineInput(); } }
        public void Dispose()
        { for (int i = 0; i < 5; i++) if (_slots[i].Root != null) Object.Destroy(_slots[i].Root); }
    }
}
