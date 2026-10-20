using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 把 Unity Trigger 转换为“向本局法术背包授予奖励”的一次性 Scene Adapter。
    /// 奖励由携带库存裁定；本组件只判断玩家是否进入以及拾取物何时隐藏。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class SpellRewardPickup : MonoBehaviour
    {
        [SerializeField, Min(0)] private int _encounterIndex;
        [SerializeField] private LayerMask _playerLayers;
        [SerializeField] private GameObject _visualRoot;

        private Collider _trigger;
        private SpellDefinition _rewardSpell;
        private bool _available;
        private bool _consumed;
        private DungeonRewardProfile _randomProfile;
        private int _rewardCharges;
        public void ConfigureRandomRewards(DungeonRewardProfile profile)=>_randomProfile=profile;

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

            if (_randomProfile!=null)
            {
                var roll=_randomProfile.Roll(1)[0];_rewardSpell=roll.Spell;_rewardCharges=roll.Charges;
            }
            else if (e.RewardSpell == null)
            {
                GameLog.Warn(
                    $"Encounter {_encounterIndex} 已完成，但 DemoRunDefinition 没有配置 Reward Spell。",
                    "Run");
                return;
            }

            // 奖励定义来自本次 Encounter 的只读配置；真正的拥有关系由物品实例库存持有。
            else _rewardSpell = e.RewardSpell;
            // 关后确定奖励和普通地面法术共用图标表现，但仍保留原有奖励解锁/重试逻辑。
            WorldSpellIconPresentation.Apply(_visualRoot,_rewardSpell.Icon);
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
                bool grantedModern=_randomProfile!=null?modern.TryGrantFinite("encounter."+_encounterIndex,_rewardSpell,_rewardCharges)
                    :modern.TryGrant("encounter."+_encounterIndex,_rewardSpell);
                if(!grantedModern)return;
                _consumed=true;_available=false;SetPresentationActive(false);return;
            }
            // 缺少工作区时保留世界入口，不能把奖励写进已弃用的临时库。
            GameLog.Warn("法术奖励缺少携带库存工作区，保留领取入口。", "Run");
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
