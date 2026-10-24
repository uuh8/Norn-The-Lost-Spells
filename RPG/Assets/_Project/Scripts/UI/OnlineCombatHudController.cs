using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// OnlineRoom 的只读战斗 HUD。事实来自 Core EventBus，UI 不持有 Character、Mirror 或资源扣除入口；
    /// HP/Mana 的平滑只改变 Image.fillAmount，不反写服务器状态。
    /// </summary>
    public sealed class OnlineCombatHudController : MonoBehaviour
    {
        [SerializeField] private GameObject _localRoot, _bossRoot;
        [SerializeField] private Image _hpFill, _manaFill, _bossFill;
        [SerializeField] private Text _hpValue, _manaValue, _bossValue;
        [SerializeField] private Text _chargesValue, _cooldownValue, _phaseValue;
        [SerializeField, Min(0f)] private float _hpLerpSpeed = 3f, _manaLerpSpeed = 4f;

        private DigitLabel _hpDigits, _manaDigits, _bossDigits, _chargesDigits, _cooldownDigits, _phaseDigits;
        private float _hpTarget, _manaTarget, _bossTarget, _cooldown;
        private bool _initialized, _hasLocal, _hasBoss, _frozen;

        private void Awake()
        {
            if (_initialized) return;
            _initialized = true;
            // 只在首次装配分配固定容量 Text；持续接收快照和冷却倒数均复用这批对象及数字 string 常量。
            _hpDigits = new DigitLabel(_hpValue, 21); _manaDigits = new DigitLabel(_manaValue, 21);
            _bossDigits = new DigitLabel(_bossValue, 21); _chargesDigits = new DigitLabel(_chargesValue, 10);
            _cooldownDigits = new DigitLabel(_cooldownValue, 12); _phaseDigits = new DigitLabel(_phaseValue, 10);
            SetVisible(_localRoot, false); SetVisible(_bossRoot, false);
        }

        private void OnEnable()
        {
            EventBus<OnlineCombatHudStateEvent>.Subscribe(ApplyState);
            EventBus<OnlineMovementPresentationEvent>.Subscribe(OnPresentation);
        }
        private void OnDisable()
        {
            EventBus<OnlineCombatHudStateEvent>.Unsubscribe(ApplyState);
            EventBus<OnlineMovementPresentationEvent>.Unsubscribe(OnPresentation);
        }

        /// <summary>只读的服务器事实入口；远端普通玩家的快照不会覆盖自己的 HP/Mana。</summary>
        public void ApplyState(OnlineCombatHudStateEvent state)
        {
            if (!_initialized) Awake();
            if (state.ActorId == 0) return;
            if (state.IsLocal)
            {
                if (!Finite(state.CurrentHp) || !Finite(state.MaxHp) || !Finite(state.CurrentMana)
                    || !Finite(state.MaxMana) || !Finite(state.Cooldown)) return;
                _hpTarget = Fraction(state.CurrentHp, state.MaxHp);
                _manaTarget = Fraction(state.CurrentMana, state.MaxMana);
                if (!_hasLocal) { SetFill(_hpFill, _hpTarget); SetFill(_manaFill, _manaTarget); }
                _hasLocal = true; SetVisible(_localRoot, true);
                _hpDigits.Ratio(Ceil(state.CurrentHp), Ceil(state.MaxHp));
                _manaDigits.Ratio(Ceil(state.CurrentMana), Ceil(state.MaxMana));
                if (state.Unlimited) _chargesDigits.Unlimited(); else _chargesDigits.Integer((uint)Mathf.Max(0, state.Charges));
                _cooldown = Mathf.Max(0f, state.Cooldown); _cooldownDigits.Seconds(_cooldown);
            }
            if (state.IsBoss)
            {
                // 实体存在与 encounter 激活是两个事实。Phase 0 表示未参战；未知 Phase 也不能提前显示。
                // 撤销显示资格后停止平滑/布局刷新，保留旧数字但不消费 inactive 快照中的 HP。
                if (state.BossPhase == 0 || state.BossPhase > 3 || state.CurrentHp <= 0)
                {
                    _hasBoss = false; SetVisible(_bossRoot, false); return;
                }
                if (!Finite(state.CurrentHp) || !Finite(state.MaxHp)) return;
                _bossTarget = Fraction(state.CurrentHp, state.MaxHp);
                if (!_hasBoss) SetFill(_bossFill, _bossTarget);
                _hasBoss = true; SetVisible(_bossRoot, true);
                _bossDigits.Ratio(Ceil(state.CurrentHp), Ceil(state.MaxHp));
                _phaseDigits.Integer(state.BossPhase);
            }
        }

        private void OnPresentation(OnlineMovementPresentationEvent state)
        {
            _frozen = state.Frozen;
            if (state.Active) return;
            // 离开 OnlineRoom 时清除显示资格；下一局要等待新事实，不能把旧角色的血量当作新局初值。
            _hasLocal = _hasBoss = false; _cooldown = 0f;
            SetVisible(_localRoot, false); SetVisible(_bossRoot, false);
        }

        private void Update()
        {
            if (!_initialized || _frozen) return;
            float dt = Time.deltaTime;
            if (_hasLocal)
            {
                Smooth(_hpFill, _hpTarget, _hpLerpSpeed * dt); Smooth(_manaFill, _manaTarget, _manaLerpSpeed * dt);
                // 这是 UI 预测倒数，仍以每个服务器事实校正；不据此授予施法权限。Frozen 时连显示倒数也停止。
                if (_cooldown > 0f) { _cooldown = Mathf.Max(0f, _cooldown - dt); _cooldownDigits.Seconds(_cooldown); }
                _hpDigits.RefreshLayout(); _manaDigits.RefreshLayout(); _chargesDigits.RefreshLayout(); _cooldownDigits.RefreshLayout();
            }
            if (_hasBoss) { Smooth(_bossFill, _bossTarget, _hpLerpSpeed * dt); _bossDigits.RefreshLayout(); _phaseDigits.RefreshLayout(); }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static uint Ceil(float value) => value <= 0f ? 0u : value >= uint.MaxValue ? uint.MaxValue : (uint)System.Math.Ceiling(value);
        private static float Fraction(float current, float maximum) => maximum > 0f ? Mathf.Clamp01(current / maximum) : 0f;
        private static void SetVisible(GameObject root, bool visible) { if (root != null && root.activeSelf != visible) root.SetActive(visible); }
        private static void SetFill(Image image, float value) { if (image != null) image.fillAmount = value; }
        private static void Smooth(Image image, float target, float delta)
        { if (image != null && !Mathf.Approximately(image.fillAmount, target)) image.fillAmount = Mathf.MoveTowards(image.fillAmount, target, delta); }

        /// <summary>
        /// UGUI Text 只接受不可变 string。用固定数字子 Text 复用 "0".."9"，避免每帧 ToString 或按最大 HP 建巨大缓存。
        /// 所有布局运算都是值类型；仅在数值、字体或 Rect 变化时写 Graphic，以减少 Canvas rebuild。
        /// </summary>
        private sealed class DigitLabel
        {
            private static readonly string[] Glyphs = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "/", ".", "∞" };
            private readonly Text _label;
            private readonly Text[] _digits;
            private readonly int[] _characters;
            private int _count, _kind = -1, _fontSize;
            private uint _lastA, _lastB;
            private Vector2 _size;

            internal DigitLabel(Text label, int capacity)
            {
                _label = label; _digits = new Text[capacity]; _characters = new int[capacity];
                if (label == null) return;
                label.text = string.Empty; label.raycastTarget = false;
                for (int i = 0; i < capacity; i++)
                {
                    var child = new GameObject("Digit" + i, typeof(RectTransform), typeof(Text));
                    child.transform.SetParent(label.transform, false);
                    var digit = child.GetComponent<Text>(); _digits[i] = digit;
                    digit.font = label.font; digit.fontStyle = label.fontStyle; digit.color = label.color;
                    digit.fontSize = label.fontSize; digit.alignment = TextAnchor.MiddleCenter; digit.raycastTarget = false;
                    digit.horizontalOverflow = HorizontalWrapMode.Overflow; digit.verticalOverflow = VerticalWrapMode.Overflow;
                    digit.rectTransform.anchorMin = digit.rectTransform.anchorMax = new Vector2(.5f, .5f);
                    digit.rectTransform.pivot = new Vector2(.5f, .5f); digit.enabled = false;
                    // 与单机 HUD 相同的浅色数字，细暗描边在亮地面上也能读清；创建成本只发生在 Awake。
                    var outline = child.AddComponent<Outline>(); outline.effectColor = new Color(0f, 0f, 0f, .85f);
                    outline.effectDistance = new Vector2(1f, -1f); outline.useGraphicAlpha = true;
                }
            }
            internal void Ratio(uint current, uint maximum)
            {
                if (!Changed(0, current, maximum)) return;
                _count = Number(current, 0); _characters[_count++] = 10; _count = Number(maximum, _count); Apply();
            }
            internal void Integer(uint value)
            { if (Changed(1, value, 0)) { _count = Number(value, 0); Apply(); } }
            internal void Unlimited()
            { if (Changed(2, 0, 0)) { _characters[0] = 12; _count = 1; Apply(); } }
            internal void Seconds(float value)
            {
                uint tenths = Ceil(value * 10f);
                if (!Changed(3, tenths, 0)) return;
                _count = Number(tenths / 10, 0); _characters[_count++] = 11; _characters[_count++] = (int)(tenths % 10); Apply();
            }
            private bool Changed(int kind, uint a, uint b)
            { if (_kind == kind && _lastA == a && _lastB == b) return false; _kind = kind; _lastA = a; _lastB = b; return true; }
            private int Number(uint value, int offset)
            {
                int start = offset;
                do { _characters[offset++] = (int)(value % 10); value /= 10; } while (value > 0);
                for (int a = start, b = offset - 1; a < b; a++, b--) { int temp = _characters[a]; _characters[a] = _characters[b]; _characters[b] = temp; }
                return offset;
            }
            private void Apply()
            {
                if (_label == null) return;
                for (int i = 0; i < _digits.Length; i++)
                {
                    _digits[i].enabled = i < _count;
                    if (i < _count) _digits[i].text = Glyphs[_characters[i]];
                }
                Layout();
            }
            internal void RefreshLayout()
            { if (_label != null && (_size != _label.rectTransform.rect.size || _fontSize != _label.fontSize)) Layout(); }
            private void Layout()
            {
                _size = _label.rectTransform.rect.size; _fontSize = _label.fontSize;
                float width = _fontSize * .58f, total = width * _count;
                float scale = _size.x > 0f && total > _size.x ? _size.x / total : 1f;
                width *= scale; total *= scale;
                var rect = _label.rectTransform.rect;
                int alignment = (int)_label.alignment % 3;
                float start = alignment == 0 ? rect.xMin : alignment == 2 ? rect.xMax - total : rect.center.x - total * .5f;
                for (int i = 0; i < _count; i++)
                {
                    var digit = _digits[i]; digit.fontSize = Mathf.Max(1, (int)(_fontSize * scale));
                    digit.rectTransform.anchoredPosition = new Vector2(start + (i + .5f) * width, rect.center.y);
                    digit.rectTransform.sizeDelta = new Vector2(width + 2f, _fontSize * 1.5f);
                }
            }
        }
    }
}
