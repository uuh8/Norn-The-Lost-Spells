using System.Collections.Generic;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Profiling;

namespace Game.Rendering
{
    /// <summary>
    /// 气体密度上传到带一格 Halo 的 3D Texture；Fragment Ray Marching 表现真正的体积，
    /// 从俯视看也有厚度。只上传可见且局部版本变化的 Chunk，冷场复用同一 GPU 数据。
    /// </summary>
    public sealed class GasFieldVolumeRenderer : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private Material _material;
        [SerializeField, Min(1)] private float _fadeStart = 32f;
        [SerializeField, Min(1)] private float _fadeEnd = 56f;
        private Texture3D[] _textures;
        private ElementChunkKey[] _keys;
        private bool[] _uploaded;
        private bool[] _visibleDensity;
        private uint[,] _versions;
        private Color32[] _pixels;
        private uint[] _steam,_toxic;
        private Dictionary<ElementChunkKey,int> _textureSlots;
        private bool[] _bound;
        private int[] _freeSlots;
        private int _freeCount;
        private bool _reportedPoolMismatch;
        private Plane[] _planes;
        private MaterialPropertyBlock _properties;
        private Mesh _cube;
        private Camera _camera;
        private int _dimension;
        private static readonly ProfilerMarker UploadMarker = new ProfilerMarker("GridGas.UploadDensity");
        public int UploadedChunksLastFrame { get; private set; }
        public int DrawnChunksLastFrame { get; private set; }
        public int UnrepresentedChunksLastFrame { get; private set; }
        public int CopiedDensityChunksLastFrame { get; private set; }
        public long UploadedDensityBytesLastFrame { get; private set; }
        public int BoundTextureCount => _textures != null ? _textures.Length - _freeCount : 0;
        // 这是 RGBA32 无 Mipmap 的 Texel 字节数；不冒称驱动实际显存或 Unity CPU 副本。
        public long DensityTextureBytes => _textures != null ? (long)_textures.Length * _pixels.Length * 4 : 0;
        private static readonly int DensityId = Shader.PropertyToID("_GasDensity");
        private static readonly int LayoutId = Shader.PropertyToID("_GasLayout");
        private static readonly int FadeId = Shader.PropertyToID("_Visibility");
        private void Start()
        {
            if (_world == null) _world = GetComponent<ElementWorldRuntime>();
            IGasVolumeReadOnly source = _world != null ? _world.GasVolumes : null;
            if (source == null || source.MaximumChunks == 0 || _material == null)
            { GameLog.Error("Gas Renderer 缺少初始化世界或 Material。", "Rendering"); enabled = false; return; }
            _dimension = source.ChunkSize + 2;
            _textures = new Texture3D[source.MaximumChunks]; _keys = new ElementChunkKey[source.MaximumChunks];
            _uploaded = new bool[source.MaximumChunks]; _versions = new uint[source.MaximumChunks, 27];
            _visibleDensity = new bool[source.MaximumChunks];
            _pixels = new Color32[_dimension * _dimension * _dimension];
            _steam=new uint[_pixels.Length]; _toxic=new uint[_pixels.Length];
            _textureSlots=new Dictionary<ElementChunkKey,int>(source.MaximumChunks);
            _bound=new bool[source.MaximumChunks]; _freeSlots=new int[source.MaximumChunks];
            for(int i=source.MaximumChunks-1;i>=0;i--) _freeSlots[_freeCount++]=i;
            _properties = new MaterialPropertyBlock(); _planes = new Plane[6]; _camera = Camera.main;
            _cube = CreateCube();
            for (int i = 0; i < _textures.Length; i++)
                _textures[i] = new Texture3D(_dimension, _dimension, _dimension, TextureFormat.RGBA32, false)
                { name = "Gas Density " + i, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        }
        private void LateUpdate()
        {
            UploadedChunksLastFrame = DrawnChunksLastFrame = UnrepresentedChunksLastFrame = CopiedDensityChunksLastFrame = 0;
            UploadedDensityBytesLastFrame = 0;
            IGasVolumeReadOnly source = _world != null ? _world.GasVolumes : null;
            if (source == null || _textures == null || _material == null) return;
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;
            GeometryUtility.CalculateFrustumPlanes(_camera, _planes);
            int size = source.ChunkSize; float meters = size * _world.CellSize;
            // Resident 索引是压紧列表，回收会交换位置。Texture 按 World Key 绑定，
            // 否则删除一处气体就会让不变的远处气团重新采集、上传与分配缓存。
            for(int slot=0;slot<_bound.Length;slot++) if(_bound[slot] && source.GetChunkVersion(_keys[slot])==0)
            { _textureSlots.Remove(_keys[slot]); _bound[slot]=false; _uploaded[slot]=false; _freeSlots[_freeCount++]=slot; }
            for (int i = 0; i < source.ResidentChunkCount; i++)
            {
                ElementChunkKey key = source.GetChunkKey(i);
                Vector3 position = _world.Origin + new Vector3(key.X, key.Y, key.Z) * meters;
                var bounds = new Bounds(position + Vector3.one * meters * .5f, Vector3.one * meters);
                float distance = Vector3.Distance(_world.InterestPosition, bounds.ClosestPoint(_world.InterestPosition));
                if (distance >= _fadeEnd || !GeometryUtility.TestPlanesAABB(_planes, bounds)) continue;
                if(!_textureSlots.TryGetValue(key,out int textureSlot))
                {
                    if(_freeCount==0)
                    {
                        // 正常池与 Owner 容量相同，不应到达这里。保留 Gameplay 真量并公开异常，
                        // 避免池配置错误再次成为“存在且有伤害、却无法看见”的静默截断。
                        UnrepresentedChunksLastFrame++;
                        if(!_reportedPoolMismatch)
                        { _reportedPoolMismatch=true; GameLog.Error("Gas Texture 池不足：可见气体未显示，请检查 Owner/Renderer 初始化容量。", "Rendering"); }
                        continue;
                    }
                    textureSlot=_freeSlots[--_freeCount]; _bound[textureSlot]=true; _keys[textureSlot]=key;
                    _uploaded[textureSlot]=false; _textureSlots.Add(key,textureSlot);
                }
                bool changed = !_uploaded[textureSlot];
                // Trilinear Filtering 会读取角点与棱边 Halo，因此必须检查全部 26 个邻 Chunk，
                // 仅检查六个面邻居会让拐角浓度变化后复用旧 Texture，产生接缝。
                int side = 0;
                for (int z = -1; z <= 1; z++) for (int y = -1; y <= 1; y++) for (int x = -1; x <= 1; x++)
                {
                    var neighbor = new ElementChunkKey(key.X + x, key.Y + y, key.Z + z);
                    uint version = source.GetChunkVersion(neighbor);
                    changed |= _versions[textureSlot, side] != version; _versions[textureSlot, side++] = version;
                }
                if (changed)
                {
                    _visibleDensity[textureSlot] = Upload(source, key, _textures[textureSlot]); _uploaded[textureSlot] = true;
                    UploadedChunksLastFrame++;
                    CopiedDensityChunksLastFrame+=54; UploadedDensityBytesLastFrame+=(long)_pixels.Length*4;
                }
                // Texture 量化后完全为 0 的 Chunk 原本就是全透明；跳过 Draw 保持同一画面，
                // 避免大量极稀薄残留仍执行覆盖整块 Bounds 的 Fragment Ray Marching。
                if (!_visibleDensity[textureSlot]) continue;
                float fade = 1f - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(_fadeStart, _fadeEnd, distance));
                _properties.SetTexture(DensityId, _textures[textureSlot]);
                _properties.SetVector(LayoutId, new Vector4(size, _dimension, meters, 0));
                _properties.SetFloat(FadeId, fade);
                Graphics.DrawMesh(_cube, Matrix4x4.TRS(position, Quaternion.identity, Vector3.one * meters),
                    _material, 0, _camera, 0, _properties, ShadowCastingMode.Off, false);
                DrawnChunksLastFrame++;
            }
        }
        private bool Upload(IGasVolumeReadOnly source, ElementChunkKey key, Texture3D texture)
        {
            using var uploadScope = UploadMarker.Auto();
            bool visible=GasDensitySnapshot.Pack(source,key,_pixels,_steam,_toxic);
            texture.SetPixelData(_pixels, 0); texture.Apply(false, false);
            return visible;
        }
        private static Mesh CreateCube()
        {
            var mesh = new Mesh { name = "Gas Ray March Bounds" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, new Vector3(1,1,0), Vector3.up,
                Vector3.forward, new Vector3(1,0,1), Vector3.one, new Vector3(0,1,1) };
            mesh.triangles = new[] { 0,2,1,0,3,2,4,5,6,4,6,7,0,4,7,0,7,3,
                1,2,6,1,6,5,0,1,5,0,5,4,3,7,6,3,6,2 };
            mesh.RecalculateBounds(); return mesh;
        }
        private void OnDestroy()
        {
            if (_cube != null) DestroyOwned(_cube);
            if (_textures != null) for (int i = 0; i < _textures.Length; i++) DestroyOwned(_textures[i]);
        }
        private static void DestroyOwned(UnityEngine.Object value)
        { if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
    }
}
