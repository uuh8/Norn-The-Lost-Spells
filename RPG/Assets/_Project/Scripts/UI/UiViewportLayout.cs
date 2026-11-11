using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// 原 UGUI 界面的视口策略：完整容纳设计面积，背景则覆盖整张 Canvas。
    /// 由各界面的生命周期显式调用，只处理该界面所属 Canvas；不扫描场景、不接管 Gameplay，
    /// 也不在 Update 重排。实际分辨率变化继续由已有 CanvasScaler/RectTransform 负责。
    /// </summary>
    internal static class UiViewportLayout
    {
        internal static CanvasScaler FitCanvas(Transform content)
        {
            if (content == null) return null;
            var canvas = content.GetComponentInParent<Canvas>(true);
            if (canvas == null || canvas.rootCanvas.renderMode == RenderMode.WorldSpace) return null;
            canvas = canvas.rootCanvas;
            var scaler = canvas.GetComponent<CanvasScaler>();
            if (scaler == null) return null;
            // 保留各原界面的设计分辨率（登录1280×800、背包1920×1080）。
            // Match .5采用两轴比例的几何平均，超宽时高度可能放大到屏外；Expand取较小比例，
            // 保证设计宽和高同时放得下，多出来的区域交给anchors分配，绝不分别拉伸内容两轴。
            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            return scaler;
        }

        internal static void FitFixedPanel(GameObject panel)
        {
            if (panel == null || !(panel.transform is RectTransform rect)) return;
            var scaler = FitCanvas(rect);
            if (scaler == null || rect.parent != scaler.transform || rect.anchorMin != rect.anchorMax) return;
            // 整块作者设计面板居中，子控件仍保持原相对位置。转换anchor时补偿设计坐标，
            // 不直接将anchoredPosition归零：左上pivot的1920×1080整块根会因此移出半个屏幕。
            // 已居中的第二次调用差值为0，商店与内嵌库存共用同一根也不会累计漂移。
            var center = Vector2.one * .5f;
            rect.anchoredPosition += Vector2.Scale(rect.anchorMin - center, scaler.referenceResolution);
            rect.anchorMin = rect.anchorMax = center;
        }

        internal static void CoverCanvas(GameObject background)
        {
            if (background == null) return;
            var scaler = FitCanvas(background.transform);
            if (scaler == null) return;
            // 只给背景自身Stretch不够：原主菜单还有一个固定1920×1080祖先。
            // 沿明确的背景祖先链铺满，保留原Image/Sprite及兄弟Settings，不查找或修改其他Canvas。
            for (var current = background.transform; current != scaler.transform && current is RectTransform rect;
                 current = current.parent)
            {
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.anchoredPosition = Vector2.zero; rect.sizeDelta = Vector2.zero; rect.localScale = Vector3.one;
            }
            if (background.TryGetComponent<Image>(out var image) && image.sprite != null)
            {
                // 原美术是近16:9图片。只对背景叶层使用UGUI等比Cover，超宽/16:10裁掉多余边缘，
                // 不把画面横向拉胖，也不缩放独立的登录Canvas或祖先下的Settings兄弟。
                var fit = background.GetComponent<AspectRatioFitter>() ?? background.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = image.sprite.rect.width / image.sprite.rect.height;
            }
        }
    }
}
