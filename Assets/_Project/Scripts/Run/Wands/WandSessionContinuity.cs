using System.Collections.Generic;
using Game.Skills;

namespace Game.Run
{
    /// <summary>仅进程内转场的纯资源快照；不保存飞行票据，不放进Profile DTO。</summary>
    internal sealed class WandSessionContinuity
    {
        internal readonly List<Entry> Entries = new List<Entry>();
        internal string Selected;
        internal sealed class Entry
        {
            internal string Id;
            internal WandStats Stats;
            internal float Mana, Ready, Cast, Reload;
            internal uint Revision;
            internal SpellProgram Program;
            internal WandDrawSnapshot Draw;
            internal string[] InstalledIds;
        }
    }
    internal sealed class WandSessionTransfer
    {
        internal bool UseV2;
        internal WandSessionContinuity Resources;
        internal WandExecutionReplayArchive Archive;
    }
}
