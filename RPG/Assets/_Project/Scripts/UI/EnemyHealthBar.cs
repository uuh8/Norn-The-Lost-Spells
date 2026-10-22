using UnityEngine;
using UnityEngine.UI;
using Game.Core;
using Game.Combat;

namespace Game.UI
{
    /// <summary>
    /// 敌人头顶世界空间血条。挂在敌人预制体的世界空间 Canvas 子物体上：
    /// 直接引用同体 HealthComponent 拿血量（自包含、随敌人生灭、零路由）；
    /// 订阅 DamageReceivedEvent 按 id 把填充瞬间设为 RemainingHp/MaxHp；
    /// LateUpdate 做告示牌正对相机；DeathEvent 命中自己 id 时隐藏（敌人对象随后被销毁，血条自动消失）。
    /// </summary>
    public class EnemyHealthBar : MonoBehaviour
    {
        [SerializeField] private Image _fill;               // 填充图（HP_line，Image Type=Filled, Horizontal）
        [SerializeField] private Transform _billboardRoot;  // 需朝向相机的根（一般是血条 Canvas 自身）；空 → 用自身 transform
        [SerializeField] private HealthComponent _health;   // 自己敌人的血量；空 → Awake 自动 GetComponentInParent

        private Camera _camera;

        private void Awake()
        {
            if (_health == null) _health = GetComponentInParent<HealthComponent>();
            if (_billboardRoot == null) _billboardRoot = transform;
            if (_health == null)
                GameLog.Warn("EnemyHealthBar 未找到 HealthComponent，血条不会更新", "UI");
        }

        private void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void Start()
        {
            _camera = Camera.main;
            if (_health != null) SetFill(_health.CurrentHp); // 初始按当前血量
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (_health == null || e.TargetId != _health.Id) return;
            SetFill(e.RemainingHp); // 敌人条瞬变，不平滑
        }

        private void OnDeath(DeathEvent e)
        {
            if (_health == null || e.TargetId != _health.Id) return;
            gameObject.SetActive(false); // 立刻隐藏空血条；敌人对象随后被 CharacterCombatFeedback 销毁
        }

        private void SetFill(float currentHp)
        {
            if (_fill == null || _health == null || _health.MaxHp <= 0f) return;
            _fill.fillAmount = Mathf.Clamp01(currentHp / _health.MaxHp);
        }

        private void LateUpdate()
        {
            if (_camera == null)
            {
                _camera = Camera.main; // 相机可能晚于敌人生成，重试缓存
                if (_camera == null) return;
            }
            // 告示牌：血条朝向与相机一致（正对屏幕，避免边缘透视扭曲）
            _billboardRoot.forward = _camera.transform.forward;
        }
    }
}
