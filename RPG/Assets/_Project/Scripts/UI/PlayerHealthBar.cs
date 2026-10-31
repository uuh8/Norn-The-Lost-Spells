using UnityEngine;
using UnityEngine.UI;
using Game.Core;
using Game.Combat;

namespace Game.UI
{
    /// <summary>
    /// 玩家屏幕底部 HUD 血条。挂在屏幕空间 Canvas 上：
    /// Inspector 拖入玩家 HealthComponent（拿 Max + 识别 id），订阅 DamageReceivedEvent 更新"目标填充"，
    /// Update 用 MoveTowards 平滑滑动到目标值（类原神掉血手感）。
    /// </summary>
    public class PlayerHealthBar : MonoBehaviour, ICombatHudElement
    {
        public GameObject PresentationRoot => gameObject;
        [SerializeField] private Image _fill;                    // 红填充（HP_line，Image Type=Filled, Horizontal）
        [SerializeField] private HealthComponent _playerHealth;  // 玩家血量（Inspector 拖入）
        [SerializeField] private Text _valueLabel;               // 显示“当前值/最大值”的 UGUI Text
        [SerializeField] private float _lerpSpeed = 3f;          // 平滑速度（填充比例/秒；越大越快）

        private float _targetFill = 1f;   // 事件设定的目标比例
        private float _displayFill = 1f;  // 当前显示比例（向目标逼近）
        private int _lastCurrentValue = int.MinValue;
        private int _lastMaxValue = int.MinValue;

        /// <summary>换数据源而不换 UGUI：单机仍保留 Inspector 引用，在线绑定本机 Replica 的只读 HP。</summary>
        public void Bind(HealthComponent health)
        {
            _playerHealth = health;
            _lastCurrentValue = _lastMaxValue = int.MinValue;
            RefreshFromSource(true);
        }

        private void RefreshFromSource(bool immediate)
        {
            _targetFill = _playerHealth != null && _playerHealth.MaxHp > 0f
                ? Mathf.Clamp01(_playerHealth.CurrentHp / _playerHealth.MaxHp) : 0f;
            RefreshValueLabel(_playerHealth != null ? _playerHealth.CurrentHp : 0f,
                _playerHealth != null ? _playerHealth.MaxHp : 0f);
            if (immediate) { _displayFill = _targetFill; ApplyFill(); }
        }

        private void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<HealthStateChangedEvent>.Subscribe(OnHealthState);
        }
        private void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<HealthStateChangedEvent>.Unsubscribe(OnHealthState);
        }

        private void Start()
        {
            if (_playerHealth == null)
            {
                GameLog.Warn("PlayerHealthBar 未指定玩家 HealthComponent，血条不会更新", "UI");
            }
            else if (_playerHealth.MaxHp > 0f)
            {
                _targetFill = _displayFill = Mathf.Clamp01(_playerHealth.CurrentHp / _playerHealth.MaxHp);
                RefreshValueLabel(_playerHealth.CurrentHp, _playerHealth.MaxHp);
            }
            ApplyFill();
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (_playerHealth == null || e.TargetId != _playerHealth.Id || _playerHealth.MaxHp <= 0f) return;
            _targetFill = Mathf.Clamp01(e.RemainingHp / _playerHealth.MaxHp);
            // 数字反映权威生命值；红条保留平滑掉血表现，两者不互相等待。
            RefreshValueLabel(e.RemainingHp, _playerHealth.MaxHp);
        }

        private void OnHealthState(HealthStateChangedEvent state)
        {
            if (_playerHealth == null || state.TargetId != _playerHealth.Id || state.MaxHp <= 0f) return;
            _targetFill = Mathf.Clamp01(state.CurrentHp / state.MaxHp);
            RefreshValueLabel(state.CurrentHp, state.MaxHp);
        }

        private void Update()
        {
            // 数值标签在离散事实中刷新；每帧只平滑原 Image，不格式化 string 或建立另一套联机条。
            if (Mathf.Approximately(_displayFill, _targetFill)) return; // 到位后不再计算
            _displayFill = Mathf.MoveTowards(_displayFill, _targetFill, _lerpSpeed * Time.deltaTime);
            ApplyFill();
        }

        private void ApplyFill()
        {
            if (_fill != null) _fill.fillAmount = _displayFill;
        }

        private void RefreshValueLabel(float currentValue, float maxValue)
        {
            // 资源内部可为 float，但 HUD 按 RPG 常见的整数口径展示，避免每帧回复产生字符串和 Canvas 重建。
            int current = Mathf.CeilToInt(Mathf.Max(0f, currentValue));
            int maximum = Mathf.CeilToInt(Mathf.Max(0f, maxValue));
            if (current == _lastCurrentValue && maximum == _lastMaxValue)
                return;

            _lastCurrentValue = current;
            _lastMaxValue = maximum;
            if (_valueLabel != null)
                _valueLabel.text = $"{current}/{maximum}";
        }
    }
}
