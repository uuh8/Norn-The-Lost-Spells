namespace Game.Combat
{
    /// <summary>
    /// 反应公式的纯结果。First/Second 始终对应输入快照的两侧，而不是固定对应 Water/Fire，
    /// 这样无论空间遍历方向如何，调用方都能把扣除量写回正确的数据拥有者。
    /// </summary>
    public readonly struct MaterialReactionResult
    {
        public MaterialReactionResult(
            ElementReactionId reaction,
            int firstConsumedGmu,
            int secondConsumedGmu,
            float areaDamage = 0f,
            float areaRadius = 0f,
            int firstProducedGmu = 0,
            int secondProducedGmu = 0)
        {
            Reaction = reaction;
            FirstConsumedGmu = firstConsumedGmu;
            SecondConsumedGmu = secondConsumedGmu;
            AreaDamage = areaDamage;
            AreaRadius = areaRadius;
            FirstProducedGmu = firstProducedGmu;
            SecondProducedGmu = secondProducedGmu;
        }

        public ElementReactionId Reaction { get; }
        public int FirstConsumedGmu { get; }
        public int SecondConsumedGmu { get; }
        public float AreaDamage { get; }
        public float AreaRadius { get; }
        public int FirstProducedGmu { get; }
        public int SecondProducedGmu { get; }
    }
}
