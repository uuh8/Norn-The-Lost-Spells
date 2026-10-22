using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// Boss Runtime 成功激活后发布的一次性只读 HUD 快照。
    /// Game.Character 负责从 BossDefinition 取值，Game.UI 只消费基础数据，
    /// 从而避免 UI Assembly 反向引用具体 Boss Controller 或 Authoring 类型。
    /// </summary>
    public struct BossHudInitializedEvent : IGameEvent
    {
        public int BossId;
        public string DisplayName;
        public float CurrentHp;
        public float MaxHp;
        public byte CurrentPhase;
        public float Phase2Threshold;
        public float Phase3Threshold;
    }
}
