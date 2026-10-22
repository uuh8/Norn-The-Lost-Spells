namespace Game.Materials
{
    /// <summary>
    /// 物质的宏观语义类别。Behavior 不等于当前 Representation 或 Simulation Backend：
    /// 同一种 Liquid 未来可以由 PBF、Cell 或其他 Backend 表示。
    /// </summary>
    public enum MaterialBehaviorKind : byte
    {
        None = 0,
        Liquid = 1,
        ReactiveField = 2,
        Solid = 3,
        Gas = 4,
    }
}
