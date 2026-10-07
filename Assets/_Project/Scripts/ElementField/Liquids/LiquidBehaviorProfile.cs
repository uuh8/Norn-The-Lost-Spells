using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    [CreateAssetMenu(menuName = "Game/Element Field/Grid Liquid Behavior", fileName = "GridLiquidBehavior")]
    public sealed class LiquidBehaviorProfile : ScriptableObject
    {
        [SerializeField] private MaterialId _material = MaterialId.Water;
        [SerializeField, Min(.01f), Tooltip("相对 Density。较轻材料拥有混合格的自由面；局部等体积交换使轻液体上浮。自由落体重力不受它影响。")]
        private float _relativeDensity = 1f;
        [SerializeField, Min(0f), Tooltip("地面速度衰减率(1/s)，与Density独立；Water低、Sticky高。")]
        private float _groundDrag = 1.5f;
        [SerializeField, Min(.01f), Tooltip("World Space坡面速度上限(m/s)。")]
        private float _maximumGroundSpeed = 2f;
        public float GroundDrag => _groundDrag;
        public float MaximumGroundSpeed => _maximumGroundSpeed;
        [SerializeField, Range(1, 255)] private int _maximumDownFlowPerTick = 96;
        [SerializeField, Range(1, 255)] private int _maximumSideFlowPerTick = 24;
        [SerializeField, Range(0, 32)] private int _sideFlowDeadbandGmu = 8;
        [SerializeField, Range(0f,.9f), Tooltip("向新的空格扩散时保留的填充比例。0维持旧扩散；较高值使水聚成有深度的团，减少极薄活动格。")]
        private float _sideSpreadReserveFill;
        [SerializeField] private bool _allowsStableRegion = true;
        [SerializeField, Tooltip("仅明确标记挥发性毒液时启用 Poison→ToxicVapor；旧毒液默认保持原行为。")]
        private bool _isVolatile;
        public bool IsVolatile => _isVolatile;

        public MaterialId Material => _material;
        public float RelativeDensity => _relativeDensity;
        public int MaximumDownFlowPerTick => _maximumDownFlowPerTick;
        public int MaximumSideFlowPerTick => _maximumSideFlowPerTick;
        public int SideFlowDeadbandGmu => _sideFlowDeadbandGmu;
        public float SideSpreadReserveFill => _sideSpreadReserveFill;
        public bool AllowsStableRegion => _allowsStableRegion;
    }
}
