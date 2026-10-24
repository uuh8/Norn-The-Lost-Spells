namespace Game.Run
{
    /// <summary>
    /// Character 只读取施法所需的工作区能力；实现可以是单机会话或 DS 的隔离内存服务。
    /// 接口不暴露 Profile/Store 写引用，资源提交仍经过带 InventoryId 的 Core 命令。
    /// </summary>
    public interface IOwnedCastWorkspace
    {
        bool IsActiveAndEnabled { get; }
        bool UseSpellProgrammingV2 { get; }
        bool WorldActionsAllowed { get; }
        string InventoryId { get; }
        uint InventoryVersion { get; }
        IWandLoadoutReadOnly WandsReadOnly { get; }
        ICastResourceReadOnly CastResourcesReadOnly { get; }
        ICastResourceReadOnly V2CastResourcesReadOnly { get; }
        bool TryGetWandExecution(string id, out WandExecutionSnapshot snapshot);
    }
}
