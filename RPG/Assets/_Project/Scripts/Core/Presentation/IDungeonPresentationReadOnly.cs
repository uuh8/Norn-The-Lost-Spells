namespace Game.Core
{
    /// <summary>
    /// 原关卡HUD的中性读口。UI能读目标文本与已安装进度，却取不到Character里的Definition、
    /// Registry、门或Authority方法；TryCaptureProgress为false时必须清空显示，不能回退旧单机Controller。
    /// </summary>
    public interface IDungeonPresentationReadOnly
    {
        int EncounterCount { get; }
        string GetDisplayName(int encounterIndex);
        string GetObjectiveText(int encounterIndex);
        bool TryCaptureProgress(out OnlineDungeonProgress progress);
    }

    /// <summary>
    /// 可选的区域 HUD 显示策略，与进度数据分开：同一份战斗／进度事实不决定必须显示哪种界面。
    /// 例如原 Boss 区域已经有专属 Boss 栏，源场景会隐藏“区域／目标／剩余敌人”三项；
    /// 共用 HUD 应保留这份 authoring 选择，而不是根据副本名称或 Boss 血量在 UI 内猜测。
    /// 未实现此接口的旧来源保持原有显示行为；false 只隐藏遭遇叶树，不关闭通知、生命／法力或输入组件。
    /// </summary>
    public interface IDungeonHudPresentationPolicy
    {
        bool ShowEncounterHud { get; }
    }

    /// <summary>只公布当前World的只读来源；WorldSceneHandle不能使用DDOL HUD自身的Scene handle代替。</summary>
    public struct DungeonPresentationSourceChangedEvent : IGameEvent
    {
        public int WorldSceneHandle;
        public IDungeonPresentationReadOnly Source;
    }
}
