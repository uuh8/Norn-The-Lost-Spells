using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 表面和 Collider 共用同一高度描述。相邻侧面只保留未被邻格覆盖的高度，
    /// Chunk 边缘读取邻 Chunk；材质 ID 写入 Color.r，颜色本身由 Rendering 决定。
    /// </summary>
    public sealed class FixedSolidSurfaceBuilder
    {
        public Vector3[] Vertices { get; }
        public Vector3[] Normals { get; }
        public Color32[] MaterialIds { get; }
        public int[] Indices { get; }
        public Bounds[] NavigationBoxes { get; }
        private readonly bool[] _navigationVisited;
        private readonly FixedSolidVolumeStore _store;
        private readonly Func<MaterialId, bool> _blocks;
        private readonly int _size;
        public int VertexCount { get; private set; }
        public int IndexCount { get; private set; }
        public int NavigationBoxCount { get; private set; }
        public FixedSolidSurfaceBuilder(FixedSolidVolumeStore store, Func<MaterialId, bool> blocks, int maximumBoxes)
        {
            _store = store; _blocks = blocks; _size = store.ChunkSize;
            Vertices = new Vector3[checked(store.CellsPerChunk * 24)]; Normals = new Vector3[Vertices.Length];
            MaterialIds = new Color32[Vertices.Length]; Indices = new int[checked(store.CellsPerChunk * 36)];
            NavigationBoxes = new Bounds[maximumBoxes]; _navigationVisited = new bool[store.CellsPerChunk];
        }
        public void Build(ElementChunkKey key, float cellSize, bool blockingOnly)
        {
            VertexCount = 0; IndexCount = 0;
            Vector3Int origin = new Vector3Int(key.X, key.Y, key.Z) * _size;
            for (int z = 0; z < _size; z++) for (int y = 0; y < _size; y++) for (int x = 0; x < _size; x++)
            {
                var cell = origin + new Vector3Int(x, y, z);
                float h = Height(cell, blockingOnly, out MaterialId material); if (h <= 0) continue;
                Vector3 p = new Vector3(x, y, z) * cellSize; float top = p.y + h * cellSize;
                var id = new Color32((byte)material, 0, 0, 255);
                if (h < 1f || Height(cell + Vector3Int.up, blockingOnly, out _) == 0)
                    Quad(new Vector3(p.x, top, p.z + cellSize), new Vector3(p.x + cellSize, top, p.z + cellSize),
                        new Vector3(p.x + cellSize, top, p.z), new Vector3(p.x, top, p.z), Vector3.up, id);
                if (Height(cell + Vector3Int.down, blockingOnly, out _) < 1f)
                    Quad(p, p + Vector3.right * cellSize, p + new Vector3(cellSize, 0, cellSize),
                        p + Vector3.forward * cellSize, Vector3.down, id);
                Side(cell, p, top, h, Vector3Int.right, cellSize, blockingOnly, id);
                Side(cell, p, top, h, Vector3Int.left, cellSize, blockingOnly, id);
                Side(cell, p, top, h, Vector3Int.forward, cellSize, blockingOnly, id);
                Side(cell, p, top, h, Vector3Int.back, cellSize, blockingOnly, id);
            }
        }
        public bool BuildNavigationBoxes(ElementChunkKey key, float cellSize)
        {
            NavigationBoxCount = 0; Array.Clear(_navigationVisited, 0, _navigationVisited.Length);
            Vector3Int origin = new Vector3Int(key.X, key.Y, key.Z) * _size;
            // 先合并 X/Z 等高矩形，再合并满格的 Y 层；访问标记防止重复段，真实洞口保持开放。
            for (int z = 0; z < _size; z++) for (int y = 0; y < _size; y++) for (int x = 0; x < _size; x++)
            {
                int index = x + _size * (y + _size * z); if (_navigationVisited[index]) continue;
                float height = Height(origin + new Vector3Int(x, y, z), true, out _); if (height == 0) continue;
                int end = x + 1;
                while (end < _size && !_navigationVisited[end + _size * (y + _size * z)]
                    && Height(origin + new Vector3Int(end, y, z), true, out _) == height) end++;
                int endZ = z + 1;
                while (endZ < _size && SameRectangle(origin, x, end, y, endZ, endZ + 1, height)) endZ++;
                int endY = y + 1;
                if (height == 1f)
                    while (endY < _size && SameRectangle(origin, x, end, endY, z, endZ, height)) endY++;
                if (NavigationBoxCount == NavigationBoxes.Length) return false;
                for (int ny = y; ny < endY; ny++) for (int nz = z; nz < endZ; nz++) for (int nx = x; nx < end; nx++)
                    _navigationVisited[nx + _size * (ny + _size * nz)] = true;
                var size = new Vector3(end - x, endY - y - 1 + height, endZ - z) * cellSize;
                NavigationBoxes[NavigationBoxCount++] = new Bounds(new Vector3(x, y, z) * cellSize + size * .5f, size);
            }
            return true;
        }
        private bool SameRectangle(Vector3Int origin, int x, int end, int y, int z, int endZ, float height)
        {
            for (int nz = z; nz < endZ; nz++) for (int nx = x; nx < end; nx++)
                if (_navigationVisited[nx + _size * (y + _size * nz)]
                    || Height(origin + new Vector3Int(nx, y, nz), true, out _) != height) return false;
            return true;
        }
        private float Height(Vector3Int cell, bool blockingOnly, out MaterialId material)
        {
            material = MaterialId.Empty;
            for (int m = 0; m < _store.MaterialCount; m++)
            {
                var id = _store.GetMaterial(m);
                if (blockingOnly && !_blocks(id)) continue;
                if (_store.TryGetQuantity(cell, id, out uint quantity))
                { material = id; return quantity / (float)_store.CellCapacity; }
            }
            return 0;
        }
        private void Side(Vector3Int cell, Vector3 p, float top, float height, Vector3Int direction,
            float size, bool blockingOnly, Color32 id)
        {
            float neighbor = Height(cell + direction, blockingOnly, out _);
            if (neighbor >= height) return;
            float low = p.y + neighbor * size;
            if (direction.x > 0)
                Quad(new Vector3(p.x + size, low, p.z), new Vector3(p.x + size, top, p.z),
                    new Vector3(p.x + size, top, p.z + size), new Vector3(p.x + size, low, p.z + size), Vector3.right, id);
            else if (direction.x < 0)
                Quad(new Vector3(p.x, low, p.z + size), new Vector3(p.x, top, p.z + size),
                    new Vector3(p.x, top, p.z), new Vector3(p.x, low, p.z), Vector3.left, id);
            else if (direction.z > 0)
                Quad(new Vector3(p.x, low, p.z + size), new Vector3(p.x + size, low, p.z + size),
                    new Vector3(p.x + size, top, p.z + size), new Vector3(p.x, top, p.z + size), Vector3.forward, id);
            else
                Quad(new Vector3(p.x + size, low, p.z), new Vector3(p.x, low, p.z),
                    new Vector3(p.x, top, p.z), new Vector3(p.x + size, top, p.z), Vector3.back, id);
        }
        private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Color32 id)
        {
            int start = VertexCount;
            Vertices[VertexCount++] = a; Vertices[VertexCount++] = b;
            Vertices[VertexCount++] = c; Vertices[VertexCount++] = d;
            for (int i = start; i < VertexCount; i++) { Normals[i] = normal; MaterialIds[i] = id; }
            Indices[IndexCount++] = start; Indices[IndexCount++] = start + 1; Indices[IndexCount++] = start + 2;
            Indices[IndexCount++] = start; Indices[IndexCount++] = start + 2; Indices[IndexCount++] = start + 3;
        }
    }
}
