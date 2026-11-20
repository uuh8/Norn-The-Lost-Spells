using UnityEngine;
using Game.Materials;

namespace Game.Rendering
{
    /// <summary>显示层独立预算；不修改GMU、Density或Solver分辨率。</summary>
    [CreateAssetMenu(menuName="Game/Rendering/Grid Liquid Continuous Field")]
    public sealed class GridLiquidFieldRenderProfile : ScriptableObject
    {
        [SerializeField] private ComputeShader _compute;
        [SerializeField] private Shader _shader;
        [SerializeField] private Material[] _palette;
        [SerializeField,Min(16)] private int _budgetMiB=128;
        // 显示输入与Gameplay驻留上限独立：1024份全量GPU输入预留会挤掉实际显示页。
        // 256份输入配96个r2页仍在128MiB内；超过显示容量沿原有显式CPU回退路径处理。
        [SerializeField,Min(1)] private int _inputChunks=256;
        [SerializeField,Min(1)] private int _views=128;
        [SerializeField,Min(0)] private int _fineViews=96;
        [Header("Presentation Visibility (Gameplay Unchanged)")]
        [SerializeField] private bool _cullByCamera=true;
        [SerializeField,Min(0)] private float _visibilityFadeStart=32;
        [SerializeField,Min(1)] private float _visibilityFadeEnd=56;
        [SerializeField,Min(0),Tooltip("Frustum外预备范围（米），保留宽核与转头余量；不做墙体遮挡猜测。")]
        private float _viewPadding=8;
        [SerializeField,Range(0,1),Tooltip("连续外壳采样模式。0.15为校准档；大于0.15保留薄水厚代理再压回，地面高度由下面的材料视觉张力控制。")] private float _cartoonCrownStrength=.25f;
        [SerializeField,Range(1,2.5f),Tooltip("显示体积倍率。自然档使用1；旧厚圆冠对照可显式用1.8，不改Gameplay GMU或天然湖泊水位。")] private float _cartoonVolumeScale=1;
        [Header("Ground Surface Tension (Visual Only)")]
        [SerializeField,Tooltip("按材料控制地面收拢和圆冠，水浅而铺展、黏液较聚团。关闭仅用于旧美术对照；不影响Solver、数量或碰撞。")]
        private bool _useMaterialSurfaceTension=true;
        [SerializeField,Tooltip("平地非Stable小连通体使用同一实例路径的体积一致球冠；避免微量被宽核画成纸片。深水、大池与坡面继续连续场，数量/碰撞不变。")]
        private bool _useGroundContactCaps=true;
        [SerializeField,Range(0,1)] private float _waterSurfaceTension=.2f;
        [SerializeField,Range(0,1)] private float _poisonSurfaceTension=.18f;
        [SerializeField,Range(0,1)] private float _stickySurfaceTension=.65f;
        public ComputeShader Compute=>_compute;
        public Shader Shader=>_shader;
        public bool CullByCamera=>_cullByCamera;
        public float VisibilityFadeStart=>_visibilityFadeStart;
        public float VisibilityFadeEnd=>Mathf.Max(_visibilityFadeStart+1,_visibilityFadeEnd);
        public float ViewPadding=>_viewPadding;
        public float CartoonCrownStrength=>_cartoonCrownStrength;
        public float CartoonVolumeScale=>_cartoonVolumeScale;
        public bool UseMaterialSurfaceTension=>_useMaterialSurfaceTension;
        public bool UseGroundContactCaps=>_useMaterialSurfaceTension && _useGroundContactCaps;
        // 这里是无量纲的视觉张力，不是N/m物理参数。按MaterialId而非Palette槽选取，
        // 使副本/据点材料顺序变化时仍使用同一形态，不会把黏液误当水。
        public float GetSurfaceTension(MaterialId material)=>material==MaterialId.Water?Mathf.Clamp01(_waterSurfaceTension):
            material==MaterialId.Poison?Mathf.Clamp01(_poisonSurfaceTension):material==MaterialId.Sticky?Mathf.Clamp01(_stickySurfaceTension):.12f;
        public Material GetMaterial(int slot)=>_palette!=null && slot<_palette.Length?_palette[slot]:null;
        public GridLiquidFieldBudget Budget(int chunkSize,int materials,long reservedBytes=0)=>new GridLiquidFieldBudget(chunkSize,materials,_inputChunks,_views,_fineViews,_budgetMiB,reservedBytes);
    }
}
