using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>小容量缓存整行；大容量复用数字Text，避免按容量建立巨大字符串表或把真实余量改成百分比。</summary>
    internal sealed class HeldItemHudValues
    {
        private static readonly string[] Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "/" };
        private readonly Text _label;
        private string[] _values;
        private Text[] _digits;
        private readonly int[] _characters = new int[41];
        private ulong _maximum, _last;
        private bool _ratio, _hasValue;
        private string _quantity;
        private Vector2 _size;
        private int _fontSize;
        internal HeldItemHudValues(Text label)
        {
            _label = label;
            // 作者Value的Rect可能比fontSize还矮；Truncate会连首行都不生成。允许溢出显示两行，保留原Rect与字体。
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            EnsureContrast(_label);
        }
        internal void Bind(ulong maximum, int quantity, bool container, bool wand)
        {
            _maximum = maximum; _ratio = container || wand; _hasValue = false;
            _quantity = container ? quantity.ToString() : "";
            if (!_ratio) { HideDigits(); _label.text = quantity.ToString(); return; }
            if (maximum <= 4096)
            {
                HideDigits(); _values = new string[(int)maximum + 1];
                string prefix = container ? _quantity + "\n" : "";
                for (int i = 0; i < _values.Length; i++) _values[i] = prefix + i + "/" + maximum;
            }
            else
            {
                _values = null; EnsureDigits(); _label.text = _quantity;
            }
        }
        internal void Clear() { _hasValue = false; _ratio = false; _label.text = ""; HideDigits(); }
        internal void Set(ulong amount)
        {
            if (!_ratio) return; amount = Math.Min(amount, _maximum);
            var sizeNow = _label.rectTransform.rect.size;
            if (_hasValue && _last == amount && (_values != null || _size == sizeNow && _fontSize == _label.fontSize)) return;
            _last = amount; _hasValue = true; _size = sizeNow; _fontSize = _label.fontSize;
            if (_values != null) { _label.text = _values[(int)amount]; return; }
            // UGUI Text接收不可变string。按位复用常量数字，在连续倒料中不ToString、不new。
            int count = WriteNumber(amount, 0); _characters[count++] = 10; count = WriteNumber(_maximum, count);
            float size = _label.fontSize, width = size * .6f, total = count * width;
            var rect = _label.rectTransform.rect;
            int horizontal = (int)_label.alignment % 3;
            float start = horizontal == 0 ? rect.xMin : horizontal == 2 ? rect.xMax - total : rect.center.x - total * .5f;
            float y = _quantity.Length == 0 ? rect.center.y : rect.center.y - size * .6f;
            float fit = total > rect.width && rect.width > 0 ? rect.width / total : 1;
            width *= fit;
            if (fit < 1) start = rect.xMin;
            for (int i = 0; i < _digits.Length; i++)
            {
                var digit = _digits[i]; digit.enabled = i < count;
                if (i >= count) continue;
                digit.text = Digits[_characters[i]]; digit.fontSize = Math.Max(1, (int)(size * fit));
                digit.rectTransform.anchoredPosition = new Vector2(start + (i + .5f) * width, y);
                digit.rectTransform.sizeDelta = new Vector2(width + 4, size * 1.5f);
            }
        }
        private int WriteNumber(ulong value, int offset)
        {
            int first = offset;
            do { _characters[offset++] = (int)(value % 10); value /= 10; } while (value > 0);
            for (int a = first, b = offset - 1; a < b; a++, b--)
            { int temporary = _characters[a]; _characters[a] = _characters[b]; _characters[b] = temporary; }
            return offset;
        }
        private void EnsureDigits()
        {
            if (_digits != null) return; _digits = new Text[41];
            // 仅首次绑定大容量物品创建；子Text限制在作者Value内部，不动十槽外框和Frame。
            for (int i = 0; i < _digits.Length; i++)
            {
                var go = new GameObject("AmountDigit" + i, typeof(RectTransform), typeof(Text)); go.transform.SetParent(_label.transform, false);
                var digit = go.GetComponent<Text>(); digit.font = _label.font; digit.fontStyle = _label.fontStyle;
                digit.color = _label.color; digit.alignment = TextAnchor.MiddleCenter; digit.raycastTarget = false;
                digit.horizontalOverflow = HorizontalWrapMode.Overflow; digit.verticalOverflow = VerticalWrapMode.Overflow;
                digit.rectTransform.anchorMin = digit.rectTransform.anchorMax = _label.rectTransform.pivot;
                digit.rectTransform.pivot = new Vector2(.5f, .5f); digit.enabled = false; _digits[i] = digit;
                EnsureContrast(digit);
            }
        }
        private static void EnsureContrast(Text label)
        {
            // 数字可能叠在浅色图标或白色Frame上；细暗描边保留作者字体/颜色，也让数量可读。
            if (label.GetComponent<Shadow>() != null) return;
            var outline = label.gameObject.AddComponent<Outline>(); outline.effectColor = new Color(0, 0, 0, .85f);
            outline.effectDistance = new Vector2(1, -1); outline.useGraphicAlpha = true;
        }
        private void HideDigits() { if (_digits != null) for (int i = 0; i < _digits.Length; i++) _digits[i].enabled = false; }
    }
}
