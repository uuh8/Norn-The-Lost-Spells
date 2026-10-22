using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 副本最终 Encounter 的有限随机法术奖励。
    /// 逐项提交真实库存；容量不足保留未领取的结果，不重抽、不自动授予全集。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class DungeonFinalRewardPickup : MonoBehaviour
    {
        [SerializeField, Min(0)] private int _encounterIndex;
        [SerializeField] private LayerMask _playerLayers;
        [SerializeField] private GameObject _visualRoot;

        private Collider _trigger;
        private bool _available;
        private bool _consumed;
        private DungeonRewardProfile _randomProfile;
        private DungeonSpellReward[] _rewards;
        private int _nextReward;
        public void ConfigureRandomRewards(DungeonRewardProfile profile)=>_randomProfile=profile;

        public bool IsAvailable => _available;
        public bool IsConsumed => _consumed;

        private void Awake()
        {
            _trigger = GetComponent<Collider>();
            if (_trigger != null && !_trigger.isTrigger)
            {
                GameLog.Warn("DungeonFinalRewardPickup 的 Collider 应启用 Is Trigger。", "Run");
            }

            if (_visualRoot == gameObject)
            {
                GameLog.Warn(
                    "DungeonFinalRewardPickup 的 Visual Root 不能指向组件自身。",
                    "Run");
                _visualRoot = null;
            }

            SetPresentationActive(false);
        }

        private void OnEnable()
        {
            EventBus<EncounterCompletedEvent>.Subscribe(OnEncounterCompleted);
        }

        private void OnDisable()
        {
            EventBus<EncounterCompletedEvent>.Unsubscribe(OnEncounterCompleted);
        }

        public void BindEncounter(int encounterIndex)
        {
            _encounterIndex = encounterIndex;
        }

        public bool TryCollect(Collider other)
        {
            if (!_available || _consumed || !IsPlayerCollider(other))
            {
                return false;
            }
            if(_randomProfile!=null)
            {
                var adapter=PracticeSpellRewardAdapter.FindInScene(gameObject.scene);if(adapter==null||_rewards==null)return false;
                // 只推进成功的抽奖槽；容量不足保留其余结果，既不重抽也不重复发已领取的部分。
                while(_nextReward<_rewards.Length)
                {var reward=_rewards[_nextReward];if(!adapter.TryGrantFinite("final."+_encounterIndex+"."+_nextReward,reward.Spell,reward.Charges))return false;_nextReward++;}
                _consumed=true;_available=false;SetPresentationActive(false);return true;
            }

            // 正式奖励必须有随机奖励配置和真实库存，缺失时保留入口以便诊断。
            GameLog.Warn("关底奖励缺少奖励配置，未发放全集。", "Run");
            return false;
        }

        private void OnTriggerEnter(Collider other)
        {
            TryCollect(other);
        }

        private void OnEncounterCompleted(EncounterCompletedEvent e)
        {
            if (_consumed || _available || e.EncounterIndex != _encounterIndex)
            {
                return;
            }

            _available = true;
            if(_randomProfile!=null)
            {
                _rewards=_randomProfile.Roll(3);
                WorldSpellIconPresentation.Apply(_visualRoot,_rewards[0].Spell.Icon);
            }
            SetPresentationActive(true);
        }



        private bool IsPlayerCollider(Collider other)
        {
            if (other == null)
            {
                return false;
            }

            int layerBit = 1 << other.gameObject.layer;
            return (_playerLayers.value & layerBit) != 0;
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
