using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>同Density材料共享的真实纵向区间；Fill相对于一格容量，绝不包含艺术圆冠。</summary>
    public readonly struct LiquidLayerSample
    {
        public readonly int DensityGroup;
        public readonly float BaseFill, ThicknessFill;
        public float TopFill => BaseFill + ThicknessFill;
        public LiquidLayerSample(int group, float baseFill, float thicknessFill)
        { DensityGroup = group; BaseFill = baseFill; ThicknessFill = thicknessFill; }
    }

    /// <summary>Presentation与角色查询共享分层规则，消费者不能更改数量或唤醒求解器。</summary>
    public interface ILiquidLayeredSurfaceReadOnly
    {
        int MaterialCount { get; }
        int DensityGroupCount { get; }
        MaterialId GetLiquidMaterial(int slot);
        int GetDensityGroup(MaterialId material);
        bool TryGetLayer(Vector3Int cell, int densityGroup, out LiquidLayerSample layer);
        float GetGroupMaterialShare(Vector3Int cell, MaterialId material);
    }
}
