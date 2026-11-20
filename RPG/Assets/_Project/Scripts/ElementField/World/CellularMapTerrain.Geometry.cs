using System.Collections.Generic;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField
{
    internal sealed partial class CellularMapTerrain
    {
        internal sealed partial class PatchView
        {
            // 原生 Terrain 只替换被修改的 holes texel；索引随该区域的高度图采样，而不是把整座山烘成方块。
            // 可开挖的平地则按稀疏编辑递归切长方体，挖第一个格不会生成数万个并排 Collider。
            private GameObject BakePatch(Rect rect, Vector2Int key)
            {
                var mesh = new SurfaceBuilder(_origin);
                Vector3Int lo = Vector3Int.FloorToInt((Source.Bounds.min - _origin) / _size);
                Vector3Int hi = Vector3Int.CeilToInt((Source.Bounds.max - _origin) / _size);
                if (Source.Collider is BoxCollider) BuildBox(mesh, BoxTileCells(key));
                else
                {
                    lo.x = Mathf.FloorToInt((rect.xMin - _origin.x) / _size); lo.z = Mathf.FloorToInt((rect.yMin - _origin.z) / _size);
                    hi.x = Mathf.CeilToInt((rect.xMax - _origin.x) / _size); hi.z = Mathf.CeilToInt((rect.yMax - _origin.z) / _size);
                    var cuts = new List<int>();
                    for (int z = lo.z; z < hi.z; z++)
                    for (int x = lo.x; x < hi.x; x++)
                    {
                        var p = new Vector3Int(x, 0, z);
                        if (!Source.Column(p, _origin, _size, out var bottom, out var top)) continue;
                        float a = Mathf.Max(rect.xMin, _origin.x + x * _size), b = Mathf.Min(rect.xMax, _origin.x + (x + 1) * _size);
                        float c = Mathf.Max(rect.yMin, _origin.z + z * _size), d = Mathf.Min(rect.yMax, _origin.z + (z + 1) * _size);
                        if (a >= b || c >= d) continue;
                        cuts.Clear();
                        int first = Mathf.FloorToInt((bottom - _origin.y) / _size), last = Mathf.CeilToInt((top - _origin.y) / _size);
                        cuts.Add(first); cuts.Add(last);
                        if (_columnEdits.TryGetValue(new Vector2Int(x,z), out var levels))
                            foreach (var y in levels)
                                if (y >= first && y < last) { cuts.Add(y); cuts.Add(y + 1); }
                        cuts.Sort();
                        for (int i = 1; i < cuts.Count; i++)
                        {
                            if (cuts[i] <= cuts[i-1]) continue;
                            p.y = cuts[i-1];
                            MaterialId material = Edits.TryGetValue(p, out var value) ? value : Source.Material;
                            if (material == MaterialId.Empty) continue;
                            float floor = Mathf.Max(bottom, _origin.y + p.y * _size);
                            float ceil = _origin.y + cuts[i] * _size;
                            // 顶层的四个角仍采原高度；地下空洞上方的薄层也有真实下表面。
                            bool surface = cuts[i] == last && Source.Heightfield != null;
                            mesh.Prism(a, b, c, d, floor, surface ? Mathf.Max(floor, Source.Height(a,c)) : Mathf.Min(top, ceil),
                                surface ? Mathf.Max(floor, Source.Height(a,d)) : Mathf.Min(top, ceil),
                                surface ? Mathf.Max(floor, Source.Height(b,d)) : Mathf.Min(top, ceil),
                                surface ? Mathf.Max(floor, Source.Height(b,c)) : Mathf.Min(top, ceil), material);
                        }
                    }
                }
                // 全部挖空也保留一个没有 Collider 的局部标记，Native Terrain holes 仍须保持挖通。
                var go = new GameObject("Excavated " + Source.Material);
                go.SetActive(false); go.layer = Source.Collider.gameObject.layer; go.transform.SetParent(_root, false);
                try
                {
                    if (mesh.Vertices.Count == 0) return go;
                    var data = mesh.Create();
                    var collider = go.AddComponent<MeshCollider>(); collider.sharedMesh = data; collider.sharedMaterial = Source.Collider.sharedMaterial;
                    if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
                    {
                        go.AddComponent<MeshFilter>().sharedMesh = data;
                        go.AddComponent<MeshRenderer>().sharedMaterials = new[] { Surface(MaterialId.Rock), Surface(MaterialId.Soil) };
                    }
                    return go;
                }
                catch { ReleasePatch(go); throw; }
            }

            private Material Surface(MaterialId material)
            {
                Material configured = _profile.Surface(material);
                if (material == Source.Material && Source.Surface != null) return Source.Surface;
                if (configured != null) return configured;
                var fallback = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.defaultMaterial : null;
                if (Source.Heightfield == null || material != Source.Material || fallback == null) return fallback;
                if (_terrainSurface == null)
                {
                    _terrainSurface = new Material(fallback) { hideFlags = HideFlags.DontSave };
                    var layers = Source.Heightfield.terrainLayers;
                    if (layers.Length > 0 && layers[0] != null && layers[0].diffuseTexture != null)
                        _terrainSurface.mainTexture = layers[0].diffuseTexture;
                }
                return _terrainSurface;
            }

            private void BuildBox(SurfaceBuilder mesh, BoundsInt box)
            {
                var size = box.size;
                bool changed = _boxEdits.ContainsKey(box);
                if (!changed || size == Vector3Int.one)
                {
                    var material = changed ? Edits[box.min] : Source.Material;
                    if (material == MaterialId.Empty) return;
                    var min = Vector3.Max(Source.Bounds.min, _origin + (Vector3)box.min * _size);
                    var max = Vector3.Min(Source.Bounds.max, _origin + (Vector3)box.max * _size);
                    mesh.Prism(min.x, max.x, min.z, max.z, min.y, max.y, max.y, max.y, max.y, material);
                    return;
                }
                SplitBox(box, out var first, out var second);
                BuildBox(mesh, first); BuildBox(mesh, second);
            }

            private static void SplitBox(BoundsInt box, out BoundsInt first, out BoundsInt second)
            {
                var size = box.size;
                int axis = size.x >= size.z && size.x >= size.y ? 0 : size.z >= size.y ? 2 : 1;
                var low = size; low[axis] = size[axis] / 2;
                var right = box.min; right[axis] += low[axis];
                first = new BoundsInt(box.min, low); second = new BoundsInt(right, box.max - right);
            }
        }

        private sealed class SurfaceBuilder
        {
            internal readonly List<Vector3> Vertices = new List<Vector3>();
            private readonly List<Vector2> _uv = new List<Vector2>();
            private readonly List<int>[] _triangles = { new List<int>(), new List<int>() };
            private readonly Vector3 _origin;
            internal SurfaceBuilder(Vector3 origin) { _origin = origin; }
            internal void Prism(float x0, float x1, float z0, float z1, float bottom, float h0, float h1, float h2, float h3, MaterialId material)
            {
                int m = material == MaterialId.Rock ? 0 : 1;
                var a = new Vector3(x0, bottom, z0); var b = new Vector3(x0, bottom, z1);
                var c = new Vector3(x1, bottom, z1); var d = new Vector3(x1, bottom, z0);
                var A = new Vector3(x0,h0,z0); var B = new Vector3(x0,h1,z1); var C = new Vector3(x1,h2,z1); var D = new Vector3(x1,h3,z0);
                Quad(A,B,C,D,m); Quad(d,c,b,a,m); Quad(a,b,B,A,m); Quad(b,c,C,B,m); Quad(c,d,D,C,m); Quad(d,a,A,D,m);
            }
            private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int m)
            {
                int n = Vertices.Count;
                Vertices.Add(a-_origin); Vertices.Add(b-_origin); Vertices.Add(c-_origin); Vertices.Add(d-_origin);
                float u = Mathf.Max(.01f, Vector3.Distance(a,b)), v = Mathf.Max(.01f, Vector3.Distance(a,d));
                _uv.Add(Vector2.zero); _uv.Add(new Vector2(u,0)); _uv.Add(new Vector2(u,v)); _uv.Add(new Vector2(0,v));
                var indices = _triangles[m];
                indices.Add(n); indices.Add(n+1); indices.Add(n+2); indices.Add(n); indices.Add(n+2); indices.Add(n+3);
            }
            internal Mesh Create()
            {
                var mesh = new Mesh { name = "Editable map terrain", hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(Vertices); mesh.SetUVs(0,_uv); mesh.subMeshCount = 2;
                mesh.SetTriangles(_triangles[0],0); mesh.SetTriangles(_triangles[1],1);
                mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
            }
        }
    }
}
