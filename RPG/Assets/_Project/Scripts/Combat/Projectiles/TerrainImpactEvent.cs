using Game.Core;
using UnityEngine;

namespace Game.Combat
{
    /// <summary>一次权威物理碰撞的材料破坏请求。目标归属、材质抗性和几何提交由场景世界判断。</summary>
    public readonly struct TerrainImpactEvent : IGameEvent
    {
        public readonly Collider Surface;
        public readonly Vector3 Point, Normal;
        public readonly float Power;
        public TerrainImpactEvent(Collider surface, Vector3 point, Vector3 normal, float power)
        { Surface = surface; Point = point; Normal = normal; Power = power; }
    }
}
