using System.Collections.Generic;
using Game.ElementField;
using Game.Materials;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// Rendering 只读液量；完整同高平面复用 Greedy Meshing，其余形体使用局部连续外壳。
    /// 实际含材料的 Chunk 才拥有 Mesh，空邻页只提供 Halo 采样。
    /// </summary>
    internal sealed class LiquidSurfaceMeshBuilder
    {
        private readonly ILiquidSurfaceReadOnly _source;
        private readonly MaterialId _material;
        private readonly ElementChunkKey[] _keys;
        private readonly LiquidSmoothSurfaceMesher _smooth;
        internal int LastBuildSourceSamples { get; private set; }
        internal int LastBuildMergedTopCells { get; private set; }
        internal int LastBuildFlatPatches { get; private set; }

        internal LiquidSurfaceMeshBuilder(ILiquidSurfaceReadOnly source, MaterialId material, int maximumKeys,
            float minimumPuddleDepthCells=.35f)
        {
            _source = source; _material = material;
            _keys = new ElementChunkKey[maximumKeys];
            _smooth = new LiquidSmoothSurfaceMesher(source, material,minimumPuddleDepthCells);
        }

        internal int CopyCandidateKeys()
        {
            int count = _source.CopySurfaceChunkKeys(_material, _keys), kept = 0;
            for (int i = 0; i < count; i++)
                if (_source.HasDynamicMaterialInChunk(_keys[i], _material)) _keys[kept++] = _keys[i];
            return kept;
        }
        internal ElementChunkKey KeyAt(int index) => _keys[index];

        internal void BuildWorldChunk(ElementChunkKey key, List<Vector3> vertices,
            List<Vector3> normals, List<Vector2> uvs, List<int> indices,
            List<Vector4> motionData = null)
        {
            vertices.Clear(); normals.Clear(); uvs.Clear(); indices.Clear();
            motionData?.Clear();
            bool flat = _smooth.IsCompleteFlatPlane(key);
            _smooth.Build(key, vertices, normals, uvs, indices, flat, motionData);
            LastBuildSourceSamples = _smooth.SourceSamples;
            LastBuildMergedTopCells = flat ? _source.ChunkSize * _source.ChunkSize : 0;
            LastBuildFlatPatches = flat ? 1 : 0;
        }
    }
}
