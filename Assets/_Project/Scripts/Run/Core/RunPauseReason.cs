using System;

namespace Game.Run
{
    /// <summary>
    /// 标识当前由哪些局内界面持有暂停所有权。
    /// 使用 Flags 而不是单一 bool，是为了避免一个界面关闭时误恢复仍被另一个界面暂停的 Gameplay。
    /// </summary>
    [Flags]
    public enum RunPauseReason : ushort
    {
        None = 0,
        // bit 0 曾用于旧法杖编辑器；保留空位，现有理由的数值不整体移动。
        PauseMenu = 1 << 1,
        TerminalResult = 1 << 2,
        SceneTransition = 1 << 3,
        GameplayGuide = 1 << 4,
        Inventory = 1 << 5,
        Shop = 1 << 6,
        HubConfirmation = 1 << 7,
        SpellResearch = 1 << 8,
        DungeonEntryChoice = 1 << 9 // 玩法选择独立持有暂停，取消不能清掉背包/转场的其他Reason。
    }
}
