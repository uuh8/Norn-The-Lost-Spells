using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// 原状态栏中单个 Icon 的纯表现节点；状态是否有效、强度及衰减仍由 Combat 的事实决定。
    /// 正常强度被 Authority/Replica 的 StatusController 限定在 0..100，显示层只保留原取整规则。
    /// </summary>
    public class StatusIconView : MonoBehaviour
    {
        private const int MaximumCachedPercent = 100;
        // 同一个整数百分比会被多个角色/状态反复显示。101个标题只在类型冷初始化时生成一次，
        // 所有Icon共享；不在Set的持续刷新路径插值/装箱，也不靠降低状态同步频率掩盖分配。
        private static readonly string[] PercentCaptions = CreatePercentCaptions();

        [SerializeField] private Image _icon;
        [SerializeField] private Text _percentageText;

        /// <summary>
        /// 安装只读状态事实。Mathf.RoundToInt保留原先中点取偶数的规则，例如0.5→0、1.5→2。
        /// 缓存只覆盖Gameplay的有效域；异常/诊断越界值继续沿旧格式显示，不能在UI偷偷Clamp成另一事实。
        /// </summary>
        public void Set(Sprite icon, float intensity)
        {
            if (_icon != null)
            {
                _icon.sprite = icon;
                _icon.enabled = icon != null;
            }

            if (_percentageText != null)
            {
                int percent = Mathf.RoundToInt(intensity);
                _percentageText.text = percent >= 0 && percent <= MaximumCachedPercent
                    ? PercentCaptions[percent] : $"{percent}%";
            }

            gameObject.SetActive(true);
        }

        public void Clear()
        {
            if (_icon != null)
            {
                _icon.sprite = null;
                _icon.enabled = false;
            }

            if (_percentageText != null)
                _percentageText.text = "";

            gameObject.SetActive(false);
        }

        private static string[] CreatePercentCaptions()
        {
            var captions = new string[MaximumCachedPercent + 1];
            for (int percent = 0; percent < captions.Length; percent++) captions[percent] = percent.ToString() + "%";
            return captions;
        }
    }
}
