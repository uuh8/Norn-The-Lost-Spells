using UnityEngine;

namespace Game.UI
{
    /// <summary>只转换拖拽的显示坐标；资产占格合法性仍由库存模块验证。</summary>
    internal static class InventoryDragGeometry
    {
        public static bool TryPlacement(RectTransform ghost, RectTransform grid, float cellSize,
            out Vector2Int anchor, out float coverage)
        {
            anchor = default; coverage = 0;
            if (ghost == null || grid == null || !grid.gameObject.activeInHierarchy || cellSize <= 0
                || !(grid.parent is RectTransform viewport)) return false;
            Rect rect = ghost.rect;
            // Canvas 缩放、ScrollRect 平移都包含在 Transform 中。物品左上角转换到目标网格 Local Space，
            // 再取最近的整数格；抓取点的小数已体现在 Ghost 位置，不能再按鼠标所在格减一次。
            Vector3 topLeft = ghost.TransformPoint(new Vector3(rect.xMin, rect.yMax));
            Vector3 local = grid.InverseTransformPoint(topLeft);
            anchor = new Vector2Int(Mathf.RoundToInt((local.x - grid.rect.xMin) / cellSize),
                Mathf.RoundToInt((grid.rect.yMax - local.y) / cellSize));

            // 用可见物品轮廓判断目标区域，鼠标无需先进入该区域。四个值类型角点避免每帧创建数组。
            Vector2 a = viewport.InverseTransformPoint(topLeft);
            Vector2 b = viewport.InverseTransformPoint(ghost.TransformPoint(new Vector3(rect.xMax, rect.yMax)));
            Vector2 c = viewport.InverseTransformPoint(ghost.TransformPoint(new Vector3(rect.xMin, rect.yMin)));
            Vector2 d = viewport.InverseTransformPoint(ghost.TransformPoint(new Vector3(rect.xMax, rect.yMin)));
            Vector2 min = Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d));
            Vector2 max = Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d));
            float area = (max.x - min.x) * (max.y - min.y);
            Rect visible = viewport.rect;
            float width = Mathf.Max(0, Mathf.Min(max.x, visible.xMax) - Mathf.Max(min.x, visible.xMin));
            float height = Mathf.Max(0, Mathf.Min(max.y, visible.yMax) - Mathf.Max(min.y, visible.yMin));
            coverage = area > 0 ? width * height / area : 0;
            return coverage > 0;
        }
    }
}
