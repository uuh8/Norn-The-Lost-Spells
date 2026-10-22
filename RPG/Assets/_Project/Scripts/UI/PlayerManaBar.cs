using UnityEngine;
using UnityEngine.UI;
using Game.Core;
using Game.Combat;

namespace Game.UI
{
    /// <summary>
    /// 玩家屏幕 HUD 蓝条。Inspector 拖入玩家 ManaComponent 和填充 Image。
    /// Image 需要设置为 Type=Filled，Fill Method=Horizontal。
    /// </summary>
    public class PlayerManaBar : MonoBehaviour
    {
        [SerializeField] private Image _fill;
        [SerializeField] private ManaComponent _playerMana;
        [SerializeField] private Text _valueLabel;
        [SerializeField] private float _lerpSpeed = 4f;

        private float _targetFill = 1f;
        private float _displayFill = 1f;
        private int _lastCurrentValue = int.MinValue;
        private int _lastMaxValue = int.MinValue;

        private void Start()
        {
            if (_playerMana == null)
            {
                GameLog.Warn("PlayerManaBar 未指定玩家 ManaComponent，蓝条不会更新", "UI");
            }
            else
            {
                _targetFill = _displayFill = _playerMana.Normalized;
                RefreshValueLabel(_playerMana.CurrentMana, _playerMana.MaxMana);
            }

            ApplyFill();
        }

        private void Update()
        {
            if (_playerMana != null)
            {
                _targetFill = _playerMana.Normalized;
                // Mana 自动回复没有事件；每帧只比较整数快照，文本实际变化时才写入 UGUI。
                RefreshValueLabel(_playerMana.CurrentMana, _playerMana.MaxMana);
            }

            if (Mathf.Approximately(_displayFill, _targetFill))
                return;

            _displayFill = Mathf.MoveTowards(_displayFill, _targetFill, _lerpSpeed * Time.deltaTime);
            ApplyFill();
        }

        private void ApplyFill()
        {
            if (_fill != null)
                _fill.fillAmount = _displayFill;
        }

        private void RefreshValueLabel(float currentValue, float maxValue)
        {
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
