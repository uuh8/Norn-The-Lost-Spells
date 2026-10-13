namespace Game.Run
{
    /// <summary>界面主分类与底层数量/状态类型分开；能力决定可使用道具，资源仍保留 Food/Herb 细类。</summary>
    public static class ItemInventoryClassification
    {
        public static bool IsTool(ItemKind kind, ItemUseKind useKind) =>
            useKind != ItemUseKind.None && kind != ItemKind.Spell && kind != ItemKind.Wand && kind != ItemKind.Backpack;

        public static bool IsMaterial(ItemKind kind) =>
            kind == ItemKind.Material || kind == ItemKind.Consumable || kind == ItemKind.Alchemy;
    }
}
