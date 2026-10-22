using Game.Combat;
using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Boss Phase 的纯表现层 Adapter。Gameplay 只发布 BossPhaseChangedEvent，
    /// 本组件据此切换预先放在 Boss 下的 VFX Root，避免 Boss AI 反向依赖具体 ParticleSystem。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BossPhaseVfxController : MonoBehaviour
    {
        [SerializeField] private HealthComponent _bossHealth;
        [SerializeField] private GameObject _phaseOneVfxRoot;
        [SerializeField] private GameObject _phaseTwoVfxRoot;
        [SerializeField] private GameObject _phaseThreeVfxRoot;

        private void Awake()
        {
            if (_bossHealth == null)
            {
                _bossHealth = GetComponentInParent<HealthComponent>();
            }

            ApplyPhase(1);
        }

        private void OnEnable()
        {
            EventBus<BossPhaseChangedEvent>.Subscribe(OnPhaseChanged);
        }

        private void OnDisable()
        {
            EventBus<BossPhaseChangedEvent>.Unsubscribe(OnPhaseChanged);
        }

        private void OnPhaseChanged(BossPhaseChangedEvent phaseEvent)
        {
            if (_bossHealth == null || phaseEvent.BossId != _bossHealth.Id)
            {
                return;
            }

            ApplyPhase(phaseEvent.CurrentPhase);
        }

        private void ApplyPhase(byte phase)
        {
            SetActiveIfAssigned(_phaseOneVfxRoot, phase == 1);
            SetActiveIfAssigned(_phaseTwoVfxRoot, phase == 2);
            SetActiveIfAssigned(_phaseThreeVfxRoot, phase >= 3);
        }

        private static void SetActiveIfAssigned(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active)
            {
                target.SetActive(active);
            }
        }
    }
}
