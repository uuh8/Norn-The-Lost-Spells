namespace Game.Materials
{
    /// <summary>
    /// 整个 Material World 共用的稳定物质身份。显式指定 byte 固定 Scene、存档与 GPU Buffer
    /// 的数值协议；身份只回答“它是什么”，不负责决定 Solver、Renderer 或 Status。
    /// </summary>
    public enum MaterialId : byte
    {
        Empty = 0,
        Water = 1,
        Fire = 2,

        // 显式编号已进入 Scene、Asset 与 GPU Buffer 协议，后续只能追加，不能重排。
        Poison = 3,
        Sticky = 4,
        // 固体和气体保有独立身份；相变是数量转移，不能改写旧材料编号。
        Ice = 5,
        DriedSlime = 6,
        Steam = 7,
        ToxicVapor = 8,
    }
}
