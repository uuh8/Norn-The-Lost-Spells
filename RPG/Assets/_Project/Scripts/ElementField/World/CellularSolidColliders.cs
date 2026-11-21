using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>有可碰撞物质的 Chunk 才持有几何。每行相连的格共用一个 BoxCollider，
    /// 同一 Tick 内的一批改动只在提交时重建一次。液体、气体的运动不参与此工作。</summary>
    internal sealed class CellularSolidColliders : System.IDisposable
    {
        private sealed class Page
        {
            internal readonly uint[] Cells;
            internal readonly List<BoxCollider>[] Rows;
            internal readonly Stack<BoxCollider> SpareBoxes = new Stack<BoxCollider>();
            internal readonly bool[] RowMarked;
            internal readonly int[] DirtyRows;
            internal readonly GameObject Root;
            internal int Count;
            internal int DirtyRowCount;
            internal Page(int size, Transform parent, int layer)
            {
                Cells = new uint[checked(size * size * size)];
                Rows = new List<BoxCollider>[size * size];
                RowMarked = new bool[Rows.Length];
                DirtyRows = new int[Rows.Length];
                Root = new GameObject("Material Collision");
                Root.transform.SetParent(parent, false);
                Root.layer = layer;
                Root.AddComponent<GeneratedMaterialSolidCollider>();
            }
        }

        private readonly Dictionary<ElementChunkKey, Page> _pages = new Dictionary<ElementChunkKey, Page>();
        private readonly List<ElementChunkKey> _dirty = new List<ElementChunkKey>();
        private readonly Stack<Page> _spare = new Stack<Page>(8);
        private readonly Transform _parent;
        private readonly int _side, _layer;
        private readonly float _size;
        private readonly uint _capacity;

        internal CellularSolidColliders(Transform parent, float size, int side, int layer, uint capacity)
        { _parent = parent; _size = size; _side = side; _layer = layer; _capacity = capacity; }

        internal bool Set(Vector3Int cell, MaterialId material, uint quantity)
        {
            // 整块岩石和冰沿用整格支撑。沙的规则仍能阻隔气体，但 Unity 的物理顶面要和玩家看到的沙量一致。
            uint fill = quantity == 0 || !ElementCellMaterialRules.OccupiesWholeCell(material) ? 0
                : material == MaterialId.Sand ? System.Math.Min(quantity, _capacity) : _capacity;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _side, out var key, out var local);
            if (!_pages.TryGetValue(key, out var page))
            {
                if (fill == 0) return false;
                page = _spare.Count > 0 ? _spare.Pop() : new Page(_side, _parent, _layer);
                page.Root.transform.localPosition = new Vector3(key.X, key.Y, key.Z) * (_side * _size);
                page.Root.SetActive(true);
                _pages.Add(key, page);
            }
            int row = local.y + _side * local.z;
            int index = local.x + _side * row;
            uint previous = page.Cells[index];
            if (previous == fill) return false;
            page.Cells[index] = fill;
            if (previous == 0) page.Count++;
            else if (fill == 0) page.Count--;
            if (page.DirtyRowCount == 0) _dirty.Add(key);
            if (!page.RowMarked[row])
            {
                page.RowMarked[row] = true;
                page.DirtyRows[page.DirtyRowCount++] = row;
            }
            return true;
        }

        internal void Commit()
        {
            if (_dirty.Count == 0) return;
            foreach (var key in _dirty)
            {
                Page page = _pages[key];
                Rebuild(page);
                if (page.Count != 0) continue;
                page.Root.SetActive(false);
                _pages.Remove(key);
                if (_spare.Count < 8) _spare.Push(page);
                else DestroyPage(page.Root);
            }
            _dirty.Clear();
            Physics.SyncTransforms();
        }

        private void Rebuild(Page page)
        {
            for (int i = 0; i < page.DirtyRowCount; i++)
            {
                int row = page.DirtyRows[i];
                page.RowMarked[row] = false;
                int y = row % _side, z = row / _side, start = row * _side;
                var boxes = page.Rows[row];
                int used = 0;
                for (int x = 0; x < _side;)
                {
                    uint fill = page.Cells[start + x];
                    if (fill == 0) { x++; continue; }
                    int first = x++;
                    // 只有接触且等高的面能合并；两团未铺满格底的沙之间留有真实间距。
                    if (!ElementCellMaterialRules.HasInsetGrainFootprint(fill, _capacity))
                        while (x < _side && page.Cells[start + x] == fill) x++;
                    if (boxes == null) page.Rows[row] = boxes = new List<BoxCollider>(1);
                    if (used == boxes.Count)
                        boxes.Add(page.SpareBoxes.Count > 0 ? page.SpareBoxes.Pop() : page.Root.AddComponent<BoxCollider>());
                    BoxCollider box = boxes[used++];
                    var extent = ElementCellMaterialRules.GrainDimensions(fill, _capacity);
                    box.center = new Vector3((first + x) * .5f, y + extent.y * .5f, z + .5f) * _size;
                    box.size = new Vector3(x - first - 1 + extent.x, extent.y, extent.z) * _size;
                    box.enabled = true;
                }
                if (boxes == null) continue;
                while (boxes.Count > used)
                {
                    int last = boxes.Count - 1;
                    BoxCollider box = boxes[last];
                    box.enabled = false;
                    page.SpareBoxes.Push(box);
                    boxes.RemoveAt(last);
                }
            }
            page.DirtyRowCount = 0;
        }

        public void Dispose()
        {
            foreach (var page in _pages.Values) { page.Root.SetActive(false); DestroyPage(page.Root); }
            foreach (var page in _spare) DestroyPage(page.Root);
            _pages.Clear();
            _spare.Clear();
            _dirty.Clear();
        }

        private static void DestroyPage(GameObject root)
        {
            if (Application.isPlaying) Object.Destroy(root);
            else Object.DestroyImmediate(root);
        }
    }
}
