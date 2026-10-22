using Game.Materials;

namespace Game.Combat
{
    /// <summary>
    /// 一条 Material Pair 到 Reaction Id 的不可变绑定。Pair 的无序语义由 Catalog Snapshot 统一处理。
    /// </summary>
    public readonly struct MaterialReactionBinding
    {
        public MaterialReactionBinding(
            MaterialId first,
            MaterialId second,
            ElementReactionId reaction)
        {
            First = first;
            Second = second;
            Reaction = reaction;
        }

        public MaterialId First { get; }
        public MaterialId Second { get; }
        public ElementReactionId Reaction { get; }
    }
}
