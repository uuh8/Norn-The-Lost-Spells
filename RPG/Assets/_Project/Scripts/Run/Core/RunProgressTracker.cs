using System;

namespace Game.Run
{
    /// <summary>
    /// 整局流程的纯状态机，只负责 Encounter 顺序和胜负裁定。
    /// 它不知道门如何开、Enemy 如何激活、UI 如何显示，这些由后续 Adapter 消费结果完成。
    /// </summary>
    public sealed class RunProgressTracker
    {
        private int _encounterCount;
        private RunCompletionMode _completionMode;

        public RunState State { get; private set; } = RunState.NotStarted;

        public int CurrentEncounterIndex { get; private set; } = -1;

        public bool IsCurrentEncounterActive { get; private set; }

        public bool StartRun(
            int encounterCount,
            RunCompletionMode completionMode = RunCompletionMode.TerminalVictory)
        {
            if (encounterCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(encounterCount),
                    encounterCount,
                    "A run must contain at least one encounter.");
            }

            if (State != RunState.NotStarted)
            {
                return false;
            }

            if (completionMode != RunCompletionMode.TerminalVictory &&
                completionMode != RunCompletionMode.AwaitStageExit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completionMode),
                    completionMode,
                    "Unknown run completion mode.");
            }

            _encounterCount = encounterCount;
            _completionMode = completionMode;
            CurrentEncounterIndex = 0;
            IsCurrentEncounterActive = false;
            State = RunState.Running;
            return true;
        }

        public bool TryStartEncounter(int encounterIndex)
        {
            if (State != RunState.Running ||
                IsCurrentEncounterActive ||
                encounterIndex != CurrentEncounterIndex)
            {
                return false;
            }

            IsCurrentEncounterActive = true;
            return true;
        }

        public bool CompleteCurrentEncounter()
        {
            if (State != RunState.Running || !IsCurrentEncounterActive)
            {
                return false;
            }

            IsCurrentEncounterActive = false;

            if (CurrentEncounterIndex + 1 >= _encounterCount)
            {
                State = _completionMode == RunCompletionMode.AwaitStageExit
                    ? RunState.StageCleared
                    : RunState.Completed;
                return true;
            }

            CurrentEncounterIndex++;
            return true;
        }

        public bool FailRun()
        {
            if (State != RunState.Running && State != RunState.StageCleared)
            {
                return false;
            }

            IsCurrentEncounterActive = false;
            State = RunState.Failed;
            return true;
        }
    }
}
