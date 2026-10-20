using UnityEngine;
namespace Game.Core
{
    // 气体没有 Collider；输入经 Core 请求材料 Owner 的只读体积查询，避免 Character 反向依赖 ElementField。
    public sealed class MaterialCollectionAimExchange
    {
        public string WorldId;
        public Vector3 RayOrigin, RayDirection, ActorPosition, Point;
        public float Reach, MaximumRayDistance, SampleRadius;
        public ushort AllowedMaterials;
        public byte MaterialKey;
        public bool Handled, Found;
    }
    public struct MaterialCollectionAimRequestedEvent : IGameEvent
    { public MaterialCollectionAimExchange Exchange; }
}
