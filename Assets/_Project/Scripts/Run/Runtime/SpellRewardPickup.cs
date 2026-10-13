using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 把 Unity Trigger 转换为“向本局法术背包授予奖励”的一次性 Scene Adapter。
    /// 奖励规则属于 RunSpellSession；本组件只判断玩家是否进入以及拾取物何时隐藏。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class SpellRewardPickup : MonoBehaviour
    {
        [SerializeField] private RunSpellSession _session;
        [SerializeField, Min(0)] private int _encounterIndex;
        [SerializeField] private LayerMask _playerLayers;
        [SerializeField] private GameObject _visualRoot;

        private Collider _trigger;
        private SpellDefinition _rewardSpell;
        private bool _available;
        private bool _consumed;

        /// <summary>
        /// 奖励是否已经被对应 Encounter 解锁。公开只读状态主要用于测试和 Editor 诊断。
        /// </summary>
        public bool IsAvailable => _available;

        /// <summary>
        /// 奖励是否已经被玩家触发过。重复法术也会消耗拾取物，避免多 Collider 重复触发。
        /// </summary>
        public bool IsConsumed => _consumed;

        private void Awake()
        {
            _trigger = GetComponent<Collider>();
            if (_trigger != null && !_trigger.isTrigger)
            {
                GameLog.Warn("SpellRewardPickup 的 Collider 应启用 Is Trigger。", "Run");
            }

            if (_visualRoot == gameObject)
            {
                // 根物体必须保持启用才能继续接收 EventBus 事件，因此视觉应放在独立子物体上。
                GameLog.Warn(
                    "SpellRewardPickup 的 Visual Root 不能指向组件自身，请指定一个视觉子物体。",
                    "Run");
                _visualRoot = null;
            }

            SetPresentationActive(false);
        }

        /// <summary>
        /// 由所属战斗区域在一局游戏初始化时注入关卡编号。
        /// 运行时绑定会覆盖 Prefab 复制时遗留的序列化值，避免多个奖励错误监听同一关卡。
        /// </summary>
        public void BindEncounter(int encounterIndex)
        {
            _encounterIndex = encounterIndex;
        }

        private void OnEnable()
        {
            EventBus<EncounterCompletedEvent>.Subscribe(OnEncounterCompleted);
        }

        private void OnDisable()
        {
            EventBus<EncounterCompletedEvent>.Unsubscribe(OnEncounterCompleted);
        }

        private void OnEncounterCompleted(EncounterCompletedEvent e)
        {
            if (_consumed || _available || e.EncounterIndex != _encounterIndex)
            {
                return;
            }

            if (e.RewardSpell == null)
            {
                GameLog.Warn(
                    $"Encounter {_encounterIndex} 已完成，但 DemoRunDefinition 没有配置 Reward Spell。",
                    "Run");
                return;
            }

            // 奖励定义来自本次 Encounter 的只读配置；真正的可变库存仍由 RunSpellSession 持有。
            _rewardSpell = e.RewardSpell;
            _available = true;
            SetPresentationActive(true);
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!_available || _consumed || other == null)
            {
                return;
            }

            int enteringLayerBit = 1 << other.gameObject.layer;
            if ((_playerLayers.value & enteringLayerBit) == 0)
            {
                return;
            }

            var modern=PracticeSpellRewardAdapter.FindInScene(gameObject.scene);
            if(modern!=null)
            {
                if(!modern.TryGrant("encounter."+_encounterIndex,_rewardSpell))return;
                _consumed=true;_available=false;SetPresentationActive(false);return;
            }
            if (_session == null || _rewardSpell == null)
            {
                GameLog.Warn("SpellRewardPickup 缺少 RunSpellSession 或 Reward Spell。", "Run");
                return;
            }

            // 即使玩家已拥有该法术，也消耗这个世界拾取物；否则 Trigger 会在角色多个 Collider 下反复触发。
            bool granted = _session.TryGrantSpell(_rewardSpell);
            if (!granted)
            {
                GameLog.Info($"法术奖励 {_rewardSpell.DisplayName} 已存在，本次不重复加入。", "Run");
            }

            _consumed = true;
            _available = false;
            SetPresentationActive(false);
        }

        private void SetPresentationActive(bool active)
        {
            if (_trigger != null)
            {
                _trigger.enabled = active;
            }

            if (_visualRoot != null)
            {
                _visualRoot.SetActive(active);
            }
        }
    }
}
