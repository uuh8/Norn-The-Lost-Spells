using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// 同一套背包与快捷栏读取的 Presentation 能力。这里只暴露快照和预览，
    /// 不提供 Profile、Store、Save 或事务写入口；数据来自单机还是服务器不改变 View 的美术和布局。
    /// </summary>
    public interface IInventoryPresentationSource
    {
        IInventoryReadOnly ReadOnly { get; }
        IToolbeltReadOnly ToolbeltReadOnly { get; }
        IWandLoadoutReadOnly WandsReadOnly { get; }
        ISpellUseReadOnly SpellUsesReadOnly { get; }
        IWalletReadOnly WalletReadOnly { get; }
        bool UseSpellProgrammingV2 { get; }
        bool CanAccessWarehouse { get; }
        string WorldSceneKey { get; }
        string EquippedBackpackId { get; }
        int SpellStorageCapacity { get; }
        string InitializationMessage { get; }
        bool HasUnsavedChanges { get; }
        bool TryCaptureWandResearch(string id, out WandResearchSnapshot snapshot);
        bool TryCopyContainer(string itemId, MaterialAmount[] buffer, out int count, out ulong total, out ulong capacity);
    }
}
