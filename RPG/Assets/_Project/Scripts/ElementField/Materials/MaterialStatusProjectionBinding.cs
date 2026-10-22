using System;
using Game.Combat;
using Game.Materials;

namespace Game.ElementField
{
    /// <summary>把环境 Material 的占用量投影为角色 Status；它不改变 Simulation Truth。</summary>
    [Serializable]
    public struct MaterialStatusProjectionBinding
    {
        public MaterialId Material;
        public StatusKind Status;
        public float MaximumApplyPerExposureTick;
    }
}
