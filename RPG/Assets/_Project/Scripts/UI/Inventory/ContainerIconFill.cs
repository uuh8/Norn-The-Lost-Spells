using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>只读容量表现：在作者 Icon 内叠加同 Sprite，alpha 保留轮廓，底部填充不改变原图布局。</summary>
    public static class ContainerIconFill
    {
        public const string ChildName = "CapacityFill";
        private static Material _material;

        // 只在初始化或对象池预热调用；Prefab 已有叠层时保留作者的 tint。
        public static Image Ensure(Image source)
        {
            if (source == null) return null;
            var child = source.transform.Find(ChildName);
            var fill = child != null ? child.GetComponent<Image>() : null;
            if (fill == null)
            {
                var go = new GameObject(ChildName, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(source.transform, false);
                fill = go.GetComponent<Image>();
                fill.color = new Color(.15f, .75f, 1f, 1f);
                var rect = fill.rectTransform;
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
            }
            fill.raycastTarget = false;
            if(_material==null)_material=Resources.Load<Material>("ContainerIconFill");
            if(_material!=null)fill.material=_material;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Vertical;
            fill.fillOrigin = (int)Image.OriginVertical.Bottom;
            fill.fillAmount = 0; fill.enabled = false;
            // 装饰 Frame 已是 Icon 的子对象：色层放在它前面，避免染色覆盖边框。
            fill.transform.SetAsFirstSibling();
            return fill;
        }

        public static void Set(Image fill, Image source, bool container, ulong amount, ulong capacity)
        {
            if (fill == null || source == null) return;
            float ratio = container && capacity > 0 ? Mathf.Clamp01((float)((double)amount / capacity)) : 0;
            if (fill.sprite != source.sprite) fill.sprite = source.sprite;
            if (fill.preserveAspect != source.preserveAspect) fill.preserveAspect = source.preserveAspect;
            if (fill.fillAmount != ratio) fill.fillAmount = ratio;
            bool visible = ratio > 0 && source.enabled && source.sprite != null;
            if (fill.enabled != visible) fill.enabled = visible;
        }
    }
}
