using System.Collections.Generic;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>有可碰撞物质的 Chunk 才持有几何。每行相连的格共用一个 BoxCollider，
    /// 同一 Tick 内的一批改动只在提交时重建一次。液体、气体的运动不参与此工作。</summary>
    internal sealed class CellularSolidColliders : System.IDisposable
    {
        private sealed class Page
        {
            internal readonly bool[] Cells;
            internal readonly List<BoxCollider>[] Rows;
            internal readonly Stack<BoxCollider> SpareBoxes = new Stack<BoxCollider>();
            internal readonly bool[] RowMarked;
            internal readonly int[] DirtyRows;
            internal readonly GameObject Root;
            internal int Count;
            internal int DirtyRowCount;
            internal Page(int size, Transform parent, int layer)
            {
                Cells = new bool[checked(size * size * size)];
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

        internal CellularSolidColliders(Transform parent, float size, int side, int layer)
        { _parent = parent; _size = size; _side = side; _layer = layer; }

        internal bool Set(Vector3Int cell, bool solid)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _side, out var key, out var local);
            if (!_pages.TryGetValue(key, out var page))
            {
                if (!solid) return false;
                page = _spare.Count > 0 ? _spare.Pop() : new Page(_side, _parent, _layer);
                page.Root.transform.localPosition = new Vector3(key.X, key.Y, key.Z) * (_side * _size);
                page.Root.SetActive(true);
                _pages.Add(key, page);
            }
            int row = local.y + _side * local.z;
            int index = local.x + _side * row;
            if (page.Cells[index] == solid) return false;
            page.Cells[index] = solid;
            page.Count += solid ? 1 : -1;
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
                else Object.Destroy(page.Root);
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
                    if (!page.Cells[start + x]) { x++; continue; }
                    int first = x++;
                    while (x < _side && page.Cells[start + x]) x++;
                    if (boxes == null) page.Rows[row] = boxes = new List<BoxCollider>(1);
                    if (used == boxes.Count)
                        boxes.Add(page.SpareBoxes.Count > 0 ? page.SpareBoxes.Pop() : page.Root.AddComponent<BoxCollider>());
                    BoxCollider box = boxes[used++];
                    box.center = new Vector3((first + x) * .5f, y + .5f, z + .5f) * _size;
                    box.size = new Vector3(x - first, 1f, 1f) * _size;
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
            foreach (var page in _pages.Values) { page.Root.SetActive(false); Object.Destroy(page.Root); }
            foreach (var page in _spare) Object.Destroy(page.Root);
            _pages.Clear();
            _spare.Clear();
            _dirty.Clear();
        }
    }
}
