using System.Collections.Generic;
using Game.Core;
using UnityEngine;
namespace Game.Rendering
{
    /// <summary>只消费真实代理的绑定事件，地图装饰不会被遍历或赋予拾取能力。</summary>
    public sealed class WorldItemHighlightBinder:MonoBehaviour
    {
        [SerializeField]private WorldItemHighlightProfile _profile;
        private void OnEnable()=>EventBus<WorldItemVisualBoundEvent>.Subscribe(OnBound);
        private void OnDisable()=>EventBus<WorldItemVisualBoundEvent>.Unsubscribe(OnBound);
        private void OnBound(WorldItemVisualBoundEvent e)
        {if(e.Added && e.Root!=null && e.Root.gameObject.scene==gameObject.scene && _profile!=null)
            WorldItemHighlightBinding.Bind(e.Root,_profile);}
    }

    /// <summary>
    /// Client 进程内共用的视觉绑定接线。Hub 在进入单人 DS/合作副本时会整根挂起，
    /// 所以真实地面代理的表现不能由 Hub Scene 的 Binder 存活与否决定。
    /// 这里只消费 Core 已发布的视觉事实并复用原高亮，不读取库存、不授予拾取资格，也不保存 Actor。
    /// </summary>
    internal static class WorldItemHighlightBinding
    {
        // 只暂存早于 Renderer 准备的真实绑定；Profile 就绪后立即排空，不在每帧扫描场景。
        // HashSet 同时合并同一代理的重复快照，Added=false 则撤回尚未安装的绑定。
        private static readonly HashSet<Transform> Pending = new HashSet<Transform>();
        private static WorldItemHighlightProfile _profile;
        private static bool _subscribed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntime()
        {
            // Enter Play Mode 可关闭 Domain Reload，静态订阅必须按进程会话显式重置。
            // 仅撤销自己的委托，不能 Clear 全局事件而误删库存/附近物的其他合法订阅者。
            EventBus<WorldItemVisualBoundEvent>.Unsubscribe(OnBound);
            Application.quitting -= ResetRuntime;
            _subscribed = false; _profile = null; Pending.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BeginRuntime()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return; // Dedicated Server 保留地面权威，不订阅 Client 表现或创建任何附加材质。
#else
            if (!Application.isPlaying || _subscribed) return;
            EventBus<WorldItemVisualBoundEvent>.Subscribe(OnBound);
            Application.quitting += ResetRuntime;
            _subscribed = true;
#endif
        }

        /// <summary>
        /// Profile 来自现有 RendererFeature 的序列化引用；不复制/改写用户的 SO，也不猜默认颜色强度。
        /// Create 可能早于 Play Mode，AddRenderPasses 再补一次幂等准备，覆盖 Renderer 被 Editor 预先缓存的情况。
        /// </summary>
        internal static void PrepareProfile(WorldItemHighlightProfile profile)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            BeginRuntime();
            if (!Application.isPlaying || !IsReady(profile)) return;
            _profile = profile;
            if (Pending.Count == 0) return;
            foreach (var root in Pending) if (root != null) Bind(root, profile);
            // 异常会原样传播，失败时保留候选，不能把未成功准备的绑定伪装成已安装。
            Pending.Clear();
#endif
        }

        private static void OnBound(WorldItemVisualBoundEvent change)
        {
            if (change.Root == null) return;
            if (!change.Added) { Pending.Remove(change.Root); return; }
            if (IsReady(_profile)) Bind(change.Root, _profile);
            else Pending.Add(change.Root);
        }

        private static bool IsReady(WorldItemHighlightProfile profile)
            => profile != null && profile.MaskShader != null && profile.FallbackShader != null;

        internal static void Bind(Transform root, WorldItemHighlightProfile profile)
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (root == null || !IsReady(profile)) return;
            // Bind 可以同时收到兼容的 Scene Binder 与共用订阅。Initialize 已按自有材质幂等，
            // 因而一次代理重绑不会嵌套包装新增的 Mask/Outline Renderer。
            var highlight = root.GetComponent<WorldItemHighlight>();
            if (highlight == null) highlight = root.gameObject.AddComponent<WorldItemHighlight>();
            highlight.Initialize(profile);
#endif
        }
    }

    /// <summary>只增加无色遮罩和像素轮廓，原表面材质完全保留；资源在离散绑定创建、代理销毁释放。</summary>
    public sealed class WorldItemHighlight:MonoBehaviour
    {
        public static int ActiveCount{get;private set;}
        private static readonly List<WorldItemHighlight> Active=new List<WorldItemHighlight>();
        private readonly List<Renderer> _sources=new List<Renderer>();
        private bool _counted;
        private void OnEnable(){if(!_counted){ActiveCount++;Active.Add(this);_counted=true;}}
        private void OnDisable(){if(_counted){ActiveCount--;Active.Remove(this);_counted=false;}}
        public static bool HasVisibleSource(Camera camera,Plane[] planes)
        {
            // 缓存原Renderer和Frustum数组，避免每摄像机/每帧Find、new数组；普通装饰不入注册表。
            for(int i=0;i<Active.Count;i++)for(int j=0;j<Active[i]._sources.Count;j++)
            {var source=Active[i]._sources[j];if(source!=null && source.enabled && source.gameObject.activeInHierarchy && (camera.cullingMask & (1<<source.gameObject.layer))!=0
                && GeometryUtility.TestPlanesAABB(planes,source.bounds))return true;}
            return false;
        }
        private readonly List<Material> _owned=new List<Material>();
        public int MaterialCount=>_owned.Count;
        public void Initialize(WorldItemHighlightProfile profile)
        {
            if(_owned.Count>0 || profile.MaskShader==null || profile.FallbackShader==null)return;
            var attached=new Dictionary<Renderer,Renderer[]>();
            // 先快照原Renderer，新增的子Renderer不会被再次递归包裹。
            foreach(var renderer in GetComponentsInChildren<MeshRenderer>(true))
            {
                if(!renderer.TryGetComponent<MeshFilter>(out var filter) || filter.sharedMesh==null)continue;
                _sources.Add(renderer);
                var materials=renderer.sharedMaterials;
                var masks=new Material[materials.Length];var shells=new Material[materials.Length];
                for(int i=0;i<materials.Length;i++)
                {
                    var source=materials[i];masks[i]=CreateLayer(profile.MaskShader,source);shells[i]=CreateLayer(profile.FallbackShader,source);
                    shells[i].SetColor("_Color",profile.Color*profile.Strength);shells[i].SetFloat("_OutlinePixels",profile.OutlinePixels);
                    shells[i].SetVector("_CenterOS",filter.sharedMesh.bounds.center);
                }
                var mask=AddLayer(renderer,"Pickup silhouette mask",filter.sharedMesh,masks);
                var shell=AddLayer(renderer,"Pickup outline",profile.GetShell(filter.sharedMesh),shells);
                attached.Add(renderer,new Renderer[]{mask,shell});
            }
            foreach(var group in GetComponentsInChildren<LODGroup>(true))
            {
                var lods=group.GetLODs();
                for(int i=0;i<lods.Length;i++)
                {
                    var renderers=new List<Renderer>();foreach(var original in lods[i].renderers)
                    {renderers.Add(original);if(original!=null && attached.TryGetValue(original,out var layers))renderers.AddRange(layers);}
                    lods[i].renderers=renderers.ToArray();
                }
                // 附加层必须进入同一LOD；否则被隐藏的低精度网格仍描边，产生叶片外的幽灵线。
                group.SetLODs(lods);
            }
        }
        private Material CreateLayer(Shader shader,Material source)
        {
            var copy=new Material(shader);_owned.Add(copy);
            if(source!=null)
            {
                bool grass=source.shader.name=="ANGRYMESH/Stylized Pack/Grass";
                // 用户草药来自ANGRYMESH，透明轮廓来自_BaseAlbedo.a；不能用白纹理包住叶片矩形。
                string map=grass?"_BaseAlbedo":source.HasProperty("_BaseMap")?"_BaseMap":source.HasProperty("_MainTex")?"_MainTex":null;
                if(map!=null){copy.SetTexture("_BaseMap",source.GetTexture(map));copy.SetTextureScale("_BaseMap",source.GetTextureScale(map));copy.SetTextureOffset("_BaseMap",source.GetTextureOffset(map));}
                bool cut=grass || source.IsKeywordEnabled("_ALPHATEST_ON") || source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip")>.5f;
                copy.SetFloat("_AlphaClip",cut?1:0);
                if(grass)copy.SetFloat("_Cutoff",source.GetFloat("_BaseOpacityCutoff"));
                else if(source.HasProperty("_Cutoff"))copy.SetFloat("_Cutoff",source.GetFloat("_Cutoff"));
                if(grass)
                {
                    copy.SetFloat("_PickupGrassWind",source.IsKeywordEnabled("_ENABLEWIND_ON")?1:0);
                    foreach(string property in new[]{"_WindGrassSpeed","_WindGrassAmplitude","_WindGrassFlexibility","_WindGrassScale","_WindGrassTurbulence"})copy.SetFloat(property,source.GetFloat(property));
                }
            }
            return copy;
        }
        private static MeshRenderer AddLayer(MeshRenderer source,string name,Mesh mesh,Material[] materials)
        {
            var node=new GameObject(name,typeof(MeshFilter),typeof(MeshRenderer));node.layer=source.gameObject.layer;node.transform.SetParent(source.transform,false);
            node.GetComponent<MeshFilter>().sharedMesh=mesh;var renderer=node.GetComponent<MeshRenderer>();renderer.sharedMaterials=materials;
            renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;renderer.receiveShadows=false;
            renderer.lightProbeUsage=UnityEngine.Rendering.LightProbeUsage.Off;renderer.reflectionProbeUsage=UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.enabled=source.enabled;return renderer;
        }
        private void OnDestroy(){foreach(var material in _owned)if(material!=null)Destroy(material);_owned.Clear();}
    }
}
