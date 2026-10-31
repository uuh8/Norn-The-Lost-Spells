using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Character
{
    /// <summary>
    /// 原地图的Scene级照明表现。Prefab不会自动携带RenderSettings，因此在线加载静态地图后显式应用
    /// 原天空盒、雾与环境光，并在离开时恢复此前Hub的值；本组件不拥有玩法，也不在DS修改渲染环境。
    /// </summary>
    public sealed class OnlineDungeonEnvironment : MonoBehaviour
    {
        [Serializable] public sealed class BakedMap
        { public Texture2D Color, Direction, ShadowMask; }
        [Serializable] public sealed class BakedRenderer
        { public Renderer Target; public int Index; public Vector4 ScaleOffset; }

        [SerializeField] private bool _fog;
        [SerializeField] private int _fogMode, _ambientMode, _reflectionMode, _reflectionResolution, _reflectionBounces;
        [SerializeField] private float _fogDensity, _fogStart, _fogEnd, _ambientIntensity, _reflectionIntensity;
        [SerializeField] private float _haloStrength, _flareStrength, _flareFadeSpeed;
        [SerializeField] private Color _fogColor, _ambientSky, _ambientEquator, _ambientGround, _subtractiveShadow;
        [SerializeField] private Material _skybox;
        [SerializeField] private Cubemap _customReflection;
        [SerializeField] private Light _sun;
        [SerializeField] private BakedMap[] _lightmaps = Array.Empty<BakedMap>();
        [SerializeField] private BakedRenderer[] _lightmappedRenderers = Array.Empty<BakedRenderer>();
        [SerializeField] private string _sourceLightingDataPath;
        [SerializeField] private bool _sourceLightingDataMissing;
        private static int _owner;
        private static Previous _baseline;
        public bool SourceLightingDataMissing => _sourceLightingDataMissing;
        public string SourceLightingDataPath => _sourceLightingDataPath;

        private sealed class Previous
        {
            internal bool Fog;
            internal FogMode FogMode;
            internal AmbientMode AmbientMode;
            internal DefaultReflectionMode ReflectionMode;
            internal int ReflectionResolution, ReflectionBounces;
            internal float FogDensity, FogStart, FogEnd, AmbientIntensity, ReflectionIntensity, Halo, Flare, FlareFade;
            internal Color FogColor, Sky, Equator, Ground, Shadow;
            internal Material Skybox;
            internal Texture Reflection;
            internal Light Sun;
            internal SphericalHarmonicsL2 Probe;
            internal LightmapData[] Lightmaps;
            internal static Previous Capture() => new Previous
            {
                Fog = RenderSettings.fog, FogMode = RenderSettings.fogMode, FogDensity = RenderSettings.fogDensity,
                FogStart = RenderSettings.fogStartDistance, FogEnd = RenderSettings.fogEndDistance, FogColor = RenderSettings.fogColor,
                AmbientMode = RenderSettings.ambientMode, AmbientIntensity = RenderSettings.ambientIntensity,
                Sky = RenderSettings.ambientSkyColor, Equator = RenderSettings.ambientEquatorColor, Ground = RenderSettings.ambientGroundColor,
                Shadow = RenderSettings.subtractiveShadowColor, Skybox = RenderSettings.skybox, Sun = RenderSettings.sun,
                ReflectionMode = RenderSettings.defaultReflectionMode, ReflectionResolution = RenderSettings.defaultReflectionResolution,
                ReflectionBounces = RenderSettings.reflectionBounces, ReflectionIntensity = RenderSettings.reflectionIntensity,
                Reflection = RenderSettings.customReflectionTexture, Halo = RenderSettings.haloStrength,
                Flare = RenderSettings.flareStrength, FlareFade = RenderSettings.flareFadeSpeed,
                Probe = RenderSettings.ambientProbe, Lightmaps = LightmapSettings.lightmaps
            };
            internal void Restore()
            {
                RenderSettings.fog = Fog; RenderSettings.fogMode = FogMode; RenderSettings.fogDensity = FogDensity;
                RenderSettings.fogStartDistance = FogStart; RenderSettings.fogEndDistance = FogEnd; RenderSettings.fogColor = FogColor;
                RenderSettings.ambientMode = AmbientMode; RenderSettings.ambientIntensity = AmbientIntensity;
                RenderSettings.ambientSkyColor = Sky; RenderSettings.ambientEquatorColor = Equator; RenderSettings.ambientGroundColor = Ground;
                RenderSettings.subtractiveShadowColor = Shadow; RenderSettings.skybox = Skybox != null ? Skybox : null;
                RenderSettings.sun = Sun != null ? Sun : null; RenderSettings.defaultReflectionMode = ReflectionMode;
                RenderSettings.defaultReflectionResolution = ReflectionResolution; RenderSettings.reflectionBounces = ReflectionBounces;
                RenderSettings.reflectionIntensity = ReflectionIntensity; RenderSettings.customReflectionTexture = Reflection != null ? Reflection : null;
                RenderSettings.haloStrength = Halo; RenderSettings.flareStrength = Flare; RenderSettings.flareFadeSpeed = FlareFade;
                RenderSettings.ambientProbe = Probe; LightmapSettings.lightmaps = Lightmaps;
            }
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOwners() { _owner = 0; _baseline = null; }
        private void OnEnable()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (!Application.isPlaying) return; // Editor导出/Preview绝不能改作者当前Scene的全局渲染设置。
            if (_owner == 0) _baseline = Previous.Capture();
            _owner = GetInstanceID(); Apply();
#endif
        }
        private void Apply()
        {
            RenderSettings.skybox = _skybox; RenderSettings.sun = _sun;
            RenderSettings.fog = _fog; RenderSettings.fogMode = (FogMode)_fogMode; RenderSettings.fogColor = _fogColor;
            RenderSettings.fogDensity = _fogDensity; RenderSettings.fogStartDistance = _fogStart; RenderSettings.fogEndDistance = _fogEnd;
            RenderSettings.ambientMode = (AmbientMode)_ambientMode; RenderSettings.ambientIntensity = _ambientIntensity;
            RenderSettings.ambientSkyColor = _ambientSky; RenderSettings.ambientEquatorColor = _ambientEquator; RenderSettings.ambientGroundColor = _ambientGround;
            RenderSettings.subtractiveShadowColor = _subtractiveShadow;
            RenderSettings.defaultReflectionMode = (DefaultReflectionMode)_reflectionMode;
            RenderSettings.defaultReflectionResolution = _reflectionResolution; RenderSettings.reflectionBounces = _reflectionBounces;
            RenderSettings.reflectionIntensity = _reflectionIntensity; RenderSettings.customReflectionTexture = _customReflection;
            RenderSettings.haloStrength = _haloStrength; RenderSettings.flareStrength = _flareStrength; RenderSettings.flareFadeSpeed = _flareFadeSpeed;
            var maps = new LightmapData[_lightmaps.Length];
            for (int i = 0; i < maps.Length; i++) maps[i] = new LightmapData { lightmapColor = _lightmaps[i].Color,
                lightmapDir = _lightmaps[i].Direction, shadowMask = _lightmaps[i].ShadowMask };
            LightmapSettings.lightmaps = maps;
            foreach (var entry in _lightmappedRenderers)
                if (entry.Target != null) { entry.Target.lightmapIndex = entry.Index; entry.Target.lightmapScaleOffset = entry.ScaleOffset; }
            // 原Scene没有独立可读AmbientProbe时，根据原天空盒请求一次环境更新；这不是每帧GPU读回。
            // 若源LightingData丢失会保留诊断标志，不能用这次请求声称恢复了缺失的烘焙结果。
            DynamicGI.UpdateEnvironment();
        }
        private void OnDisable()
        {
#if !UNITY_SERVER || UNITY_EDITOR
            if (!Application.isPlaying || _owner != GetInstanceID()) return;
            // 新世界先接管、旧世界随后卸载时，旧OnDisable没有恢复权限。最后Owner退出恢复最初基线，
            // 不能恢复成已经销毁的旧世界；所以基线只在第一个Owner进入时保存一次。
            _baseline?.Restore(); _owner = 0; _baseline = null;
#endif
        }
    }
}
