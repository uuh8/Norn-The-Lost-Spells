using System.Collections.Generic;
using Game.ElementField;
using UnityEngine;

namespace Game.Rendering
{
    public sealed partial class CellularMaterialRenderer
    {
        // 8³ 体素绘制页覆盖 29 位有符号页坐标。每个非空父节点只保存八个孩子的存在位；
        // 插入或回收一页只改到最近仍有兄弟的祖先。相机无需逐一访问地图远端的非可见页。
        private const int PageTreeDepth = 32 - PageShift;
        private const int PageBias = 1 << (PageTreeDepth - 1);
        private readonly Dictionary<Vector3Int, byte>[] _pageLevels = new Dictionary<Vector3Int, byte>[PageTreeDepth];
        private Matrix4x4 _visibleMatrix;
        private Vector3 _visibleOrigin;
        private float _visibleMeters;
        private bool _haveFrustum, _visibleSetValid;
        private int _visibleDirtyPages;

        private void IndexPage(ElementChunkKey key, bool present)
        {
            var child = new Vector3Int(key.X + PageBias, key.Y + PageBias, key.Z + PageBias);
            for (int level = 0; level < PageTreeDepth; level++)
            {
                int mask = 1 << ((child.x & 1) | ((child.y & 1) << 1) | ((child.z & 1) << 2));
                var parent = new Vector3Int(child.x >> 1, child.y >> 1, child.z >> 1);
                var nodes = _pageLevels[level] ??= new Dictionary<Vector3Int, byte>();
                nodes.TryGetValue(parent, out var before);
                int after = present ? before | mask : before & ~mask;
                if (after == before) return;
                if (after == 0) nodes.Remove(parent); else nodes[parent] = (byte)after;
                if (present && before != 0 || !present && after != 0) return;
                child = parent;
            }
        }

        private void CollectVisiblePages(float meters)
        {
            Matrix4x4 matrix = _camera.cullingMatrix;
            if (!_haveFrustum || !matrix.Equals(_visibleMatrix))
            {
                GeometryUtility.CalculateFrustumPlanes(matrix,_frustum);
                _visibleMatrix = matrix; _haveFrustum = true; _visibleSetValid = false;
            }
            Vector3 origin = _world.Origin;
            if (_visibleSetValid && origin.Equals(_visibleOrigin) && meters == _visibleMeters) return;
            var old = _visiblePages; _visiblePages = _previousVisiblePages; _previousVisiblePages = old;
            foreach (var page in old) page.Visible = false;
            _visiblePages.Clear();
            if (_pages.Count <= 32)
            {
                foreach (var page in _pages.Values) if (IsVisible(page, meters)) AddVisiblePage(page);
            }
            else CollectNode(PageTreeDepth, Vector3Int.zero, meters);
            // 可见集合只随相机、世界坐标或页的生灭重算。其余帧保持同一批 Draw，
            // 显式记录从 clean 到 dirty 的过渡，无脏页时不再为查找重建工作扫描全部可见页。
            foreach (var page in old) if(!page.Visible) DequeueVisibleDirty(page);
            old.Clear();
            _visibleOrigin = origin; _visibleMeters = meters; _visibleSetValid = true;
        }

        private void CollectNode(int level, Vector3Int node, float meters)
        {
            int span = 1 << level;
            var minimum = new Vector3(node.x * span - PageBias, node.y * span - PageBias, node.z * span - PageBias);
            float edge = span * meters;
            var center = _world.Origin + (minimum + Vector3.one * (.5f * span)) * meters;
            // 包含跨页液滴一个格距的视觉插值；给远坐标的 float AABB 留出舍入余量。
            var bounds = new Bounds(center, Vector3.one * (edge + 2f * _world.CellSize + edge * .000001f));
            if (!GeometryUtility.TestPlanesAABB(_frustum, bounds)) return;
            if (level == 0)
            {
                var key = new ElementChunkKey(node.x - PageBias, node.y - PageBias, node.z - PageBias);
                if (_pages.TryGetValue(key, out var page)) AddVisiblePage(page);
                return;
            }
            if (!_pageLevels[level - 1].TryGetValue(node, out var children)) return;
            for (int i = 0; i < 8; i++)
                if ((children & (1 << i)) != 0)
                    CollectNode(level - 1, new Vector3Int(node.x * 2 + (i & 1), node.y * 2 + ((i >> 1) & 1),
                        node.z * 2 + ((i >> 2) & 1)), meters);
        }

        private void ClearPageIndex()
        {
            for (int i = 0; i < PageTreeDepth; i++) _pageLevels[i]?.Clear();
            _visibleDirtyPages = 0; _haveFrustum = _visibleSetValid = false;
            _firstDirty = _lastDirty = null; _frameStart = 0; _previousVisiblePages.Clear();
        }

        private void AddVisiblePage(Page page)
        {
            page.Visible=true; page.VisibleIndex=_visiblePages.Count; _visiblePages.Add(page);
            QueueVisibleDirty(page);
        }

        private void RemoveVisiblePage(Page page)
        {
            if (!page.Visible) return;
            int last=_visiblePages.Count-1;
            var moved=_visiblePages[last]; _visiblePages[page.VisibleIndex]=moved; moved.VisibleIndex=page.VisibleIndex;
            _visiblePages.RemoveAt(last); page.Visible=false;
            DequeueVisibleDirty(page);
        }

        private void QueueVisibleDirty(Page page)
        {
            if(!page.Visible || page.Queued || !page.SurfaceDirty && !page.GasDirty)return;
            // 每页只保留最早尚未处理的排队位置。同页每 tick 重复改量不会重新排队，换视角也保留仍可见页的先后。
            // 授权提交可能在 LateUpdate 前发生。复用上一可见帧的采样时刻，误差通常不超过一帧，
            // 避免给每次格写增加原生时钟读取。移出视锥后再次进入会获得新的时间起点。
            page.Queued=true; page.QueuedAt=_frameStart; page.PreviousDirty=_lastDirty; page.NextDirty=null;
            if(_lastDirty==null)_firstDirty=page;else _lastDirty.NextDirty=page;
            _lastDirty=page;_visibleDirtyPages++;
        }

        private void DequeueVisibleDirty(Page page)
        {
            if(!page.Queued)return;
            if(page.PreviousDirty==null)_firstDirty=page.NextDirty;else page.PreviousDirty.NextDirty=page.NextDirty;
            if(page.NextDirty==null)_lastDirty=page.PreviousDirty;else page.NextDirty.PreviousDirty=page.PreviousDirty;
            page.PreviousDirty=page.NextDirty=null;page.Queued=false;_visibleDirtyPages--;
        }

        // 大静水区域的边界交接也沿现有页树裁剪。坐标以页为单位作整数比较，
        // 因而不会在世界边缘因为 float AABB 精度而遗漏邻格。
        private void SeedRestingNode(int level, Vector3Int node, ElementChunkKey low, ElementChunkKey high,
            BoundsInt cells, HashSet<Vector3Int> touching)
        {
            int span = 1 << level;
            var min = new Vector3Int(node.x * span - PageBias, node.y * span - PageBias, node.z * span - PageBias);
            if (min.x > high.X || min.y > high.Y || min.z > high.Z || min.x + span <= low.X
                || min.y + span <= low.Y || min.z + span <= low.Z) return;
            if (level == 0)
            {
                if (_pages.TryGetValue(new ElementChunkKey(min.x,min.y,min.z), out var page)) SeedPage(page,cells,touching);
                return;
            }
            if (!_pageLevels[level-1].TryGetValue(node,out byte children)) return;
            for (int i = 0; i < 8; i++)
                if ((children & (1 << i)) != 0)
                    SeedRestingNode(level-1, new Vector3Int(node.x*2+(i&1), node.y*2+((i>>1)&1), node.z*2+((i>>2)&1)),
                        low, high, cells, touching);
        }
    }
}
