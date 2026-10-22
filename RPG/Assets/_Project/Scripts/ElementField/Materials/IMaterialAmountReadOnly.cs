using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>按 Global Cell 与 Canonical Material 查询 Gameplay Amount 的统一只读 Contract。</summary>
    public interface IMaterialAmountReadOnly
    {
        bool TryGetAmount(Vector3Int globalCell, MaterialId material, out byte amount);
    }
}
