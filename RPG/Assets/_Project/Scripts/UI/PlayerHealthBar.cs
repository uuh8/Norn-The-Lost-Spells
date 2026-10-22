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
    public class PlayerHealthBar : MonoBehaviour
    {
        [SerializeField] private Image _fill;                    // 红填充（HP_line，Image Type=Filled, Horizontal）
        [SerializeField] private HealthComponent _playerHealth;  // 玩家血量（Inspector 拖入）
        [SerializeField] private Text _valueLabel;               // 显示“当前值/最大值”的 UGUI Text
        [SerializeField] private float _lerpSpeed = 3f;          // 平滑速度（填充比例/秒；越大越快）

        private float _targetFill = 1f;   // 事件设定的目标比例
        private float _displayFill = 1f;  // 当前显示比例（向目标逼近）
        private int _lastCurrentValue = int.MinValue;
        private int _lastMaxValue = int.MinValue;

        private void OnEnable()  => EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
        private void OnDisable() => EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);

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

        private void Update()
        {
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
