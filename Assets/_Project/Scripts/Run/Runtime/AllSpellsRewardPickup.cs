using Game.Core;
using Game.Skills;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 地图一最终 Encounter 的毕业奖励。它只合并完整 Spell Library，
    /// 不直接操作 Palette/UI；下次打开 Wand Editor 时会读取同一 Runtime Clone。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class AllSpellsRewardPickup : MonoBehaviour
    {
        [SerializeField] private RunSpellSession _session;
        [SerializeField] private SpellLibrary _fullLibrary;
        [SerializeField, Min(0)] private int _encounterIndex;
        [SerializeField] private LayerMask _playerLayers;
        [SerializeField] private GameObject _visualRoot;

        private Collider _trigger;
        private bool _available;
        private bool _consumed;

        public bool IsAvailable => _available;
        public bool IsConsumed => _consumed;

        private void Awake()
        {
            _trigger = GetComponent<Collider>();
            if (_trigger != null && !_trigger.isTrigger)
            {
                GameLog.Warn("AllSpellsRewardPickup 的 Collider 应启用 Is Trigger。", "Run");
            }

            if (_visualRoot == gameObject)
            {
                GameLog.Warn(
                    "AllSpellsRewardPickup 的 Visual Root 不能指向组件自身。",
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

            RunSpellSession session = ResolveSession();
            if (session == null)
            {
                GameLog.Error("AllSpellsRewardPickup 缺少可用的 RunSpellSession。", "Run");
                return false;
            }

            SpellLibrary fullLibrary = _fullLibrary != null
                ? _fullLibrary
                : session.FullLibrary;
            if (fullLibrary == null)
            {
                GameLog.Error("AllSpellsRewardPickup 缺少 Full SpellLibrary。", "Run");
                return false;
            }

            var modern=PracticeSpellRewardAdapter.FindInScene(gameObject.scene);
            if(modern!=null && !modern.TryGrantLibrary("all."+_encounterIndex,fullLibrary))return false;
            // 旧Library仅保留其他教学场景的兼容；新工作区已提交真实实例才消费世界入口。
            session.EnsureAllSpellsUnlocked(fullLibrary);
            _consumed = true;
            _available = false;
            SetPresentationActive(false);
            return true;
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
            SetPresentationActive(true);
        }

        private RunSpellSession ResolveSession()
        {
            if (_session == null)
            {
                _session = RunSpellSession.Current;
            }

            return _session;
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
