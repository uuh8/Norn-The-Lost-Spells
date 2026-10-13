using System;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>只读三杖 HUD；数值字符串在初始化缓存，恢复每帧不进行插值/格式化。</summary>
    public sealed class WandResourceHud : MonoBehaviour
    {
        [Serializable] private sealed class Row
        {
            public Text Label;
            public Image Mana, Cooldown, Selection;
            [NonSerialized] public string[] Numbers;
            [NonSerialized] public string[] Percentages;
            [NonSerialized] public float Maximum;
        }
        [SerializeField] private SinglePlayerInventorySession _session;
        [SerializeField] private Row[] _rows = new Row[3];
        private Font _font;
        private bool _ready;
        private static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("Norn.WandHud.Update");
        private void Start()
        {
            _font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Arial" }, 18);
            Text[] labels = GetComponentsInChildren<Text>(true);
            for (int i = 0; i < labels.Length; i++) labels[i].font = _font;
            _font.RequestCharactersInTexture("0123456789/% —法杖冷却切换左键施法整备移动", 18);
        }
        private void Update()
        {
            IWandLoadoutReadOnly read = _session != null ? _session.WandsReadOnly : null;
            if (read == null) return;
            if (!_ready)
            {
                // 一次初始化可离散分配；通过 Coroutine Start 建表，避免将分配藏进 Update。
                return;
            }
            using var sample = UpdateMarker.Auto();
            for (int i = 0; i < _rows.Length; i++)
            {
                Row row = _rows[i]; bool exists = read.TryGetSlot(i, out WandStateSnapshot state);
                row.Selection.enabled = exists && state.Selected;
                row.Mana.fillAmount = exists ? state.CurrentMana / state.MaxMana : 0;
                row.Cooldown.fillAmount = exists && state.CastCooldown > 0 ? state.CooldownRemaining / state.CastCooldown : 0;
                bool exact = exists && state.MaxMana == row.Maximum;
                string[] labels = exact ? row.Numbers : row.Percentages;
                int number = exists ? Mathf.FloorToInt(exact ? state.CurrentMana : Mathf.Clamp01(state.CurrentMana / state.MaxMana) * 100) : 0;
                // 新装备上限变化时仍使用百分比缓存，不因换物品在热路径构造字符串。
                row.Label.text = exists ? labels[Mathf.Clamp(number, 0, labels.Length - 1)] : "空装备位";
            }
        }
        private void Awake() => StartCoroutine(InitializeLabels());
        private System.Collections.IEnumerator InitializeLabels()
        {
            while (_session == null || _session.WandsReadOnly == null) yield return null;
            for (int i = 0; i < _rows.Length; i++)
            {
                Row row = _rows[i]; row.Percentages = new string[101];
                for (int number = 0; number <= 100; number++) row.Percentages[number] = (i + 1) + "  法杖   Mana " + number + "%";
                if (_session.WandsReadOnly.TryGetSlot(i, out WandStateSnapshot state) && state.MaxMana <= 2048)
                {
                    row.Maximum = state.MaxMana; row.Numbers = new string[Mathf.CeilToInt(state.MaxMana) + 1];
                    for (int number = 0; number < row.Numbers.Length; number++) row.Numbers[number] = (i + 1) + "  法杖   Mana " + number + "/" + state.MaxMana;
                }
                else row.Numbers = row.Percentages;
            }
            _ready = true;
        }
        private void OnDestroy() { if (_font != null) Destroy(_font); }
    }
}
