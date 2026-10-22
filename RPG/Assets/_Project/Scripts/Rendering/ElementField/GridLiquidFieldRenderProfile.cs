using UnityEngine;

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
        [SerializeField,Range(0,1),Tooltip("浅液团圆冠塑形强度。0.15为体积校准，0.8为厚圆冠；天然湖泊保持实际水位，Gameplay GMU不变。")] private float _cartoonCrownStrength=.8f;
        [SerializeField,Range(1,2.5f),Tooltip("动态液体的卡通显示体积倍率。地面和空中共用，1为原体积，1.8形成厚液团；不改Gameplay GMU或天然湖泊水位。")] private float _cartoonVolumeScale=1.8f;
        public ComputeShader Compute=>_compute;
        public Shader Shader=>_shader;
        public bool CullByCamera=>_cullByCamera;
        public float VisibilityFadeStart=>_visibilityFadeStart;
        public float VisibilityFadeEnd=>Mathf.Max(_visibilityFadeStart+1,_visibilityFadeEnd);
        public float ViewPadding=>_viewPadding;
        public float CartoonCrownStrength=>_cartoonCrownStrength;
        public float CartoonVolumeScale=>_cartoonVolumeScale;
        public Material GetMaterial(int slot)=>_palette!=null && slot<_palette.Length?_palette[slot]:null;
        public GridLiquidFieldBudget Budget(int chunkSize,int materials,long reservedBytes=0)=>new GridLiquidFieldBudget(chunkSize,materials,_inputChunks,_views,_fineViews,_budgetMiB,reservedBytes);
    }
}
