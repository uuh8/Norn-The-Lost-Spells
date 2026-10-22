using Game.Core;

namespace Game.Run
{
    /// <summary>
    /// 整局在 NotStarted、Running、StageCleared、Completed、Failed 之间发生有效转换后发布。
    /// 同时携带前后状态，便于 UI 区分地图清场与真正 Terminal Result，而不反查 Scene。
    /// </summary>
    public struct RunStateChangedEvent : IGameEvent
    {
        public RunState PreviousState;
        public RunState CurrentState;
        public int CurrentEncounterIndex;
    }
}
