using System;

namespace Game.Run
{
    /// <summary>某一张卡的不可变供料设置；按实例ID持续关联，配置本身没有材料数量所有权。</summary>
    public sealed class SpellAttachmentState
    {
        public string ContainerId { get; }
        public ulong Dose { get; }
        public bool Enabled { get; }
        public SpellAttachmentState(string containerId, ulong dose, bool enabled = true)
        {
            if (string.IsNullOrWhiteSpace(containerId) || dose == 0) throw new ArgumentException("附着需要来源身份和正剂量。");
            ContainerId = containerId; Dose = dose; Enabled = enabled;
        }
        internal bool SameAs(SpellAttachmentState other) => other != null && ContainerId == other.ContainerId && Dose == other.Dose && Enabled == other.Enabled;
    }
}
