using UnityEngine;

namespace Game.Character
{
    public enum EnemyNavigationFailureSignal : byte
    {
        None = 0,
        Repath = 1
    }

    /// <summary>
    /// 累计“想移动但路径不可达/没有实际进展”的时间，并周期性请求 Repath。
    /// 它永远不会要求 FSM 放弃玩家；脱战只由 EnemyPerception 的 LoseRadius 决定。
    /// </summary>
    public sealed class EnemyNavigationFailureTracker
    {
        private float _repathElapsed;

        public EnemyNavigationFailureSignal Tick(
            float deltaTime,
            bool pathUnreachable,
            bool wantsToMove,
            bool madeProgress,
            float repathInterval)
        {
            if (!wantsToMove || (!pathUnreachable && madeProgress))
            {
                Reset();
                return EnemyNavigationFailureSignal.None;
            }

            float delta = Mathf.Max(0f, deltaTime);
            _repathElapsed += delta;

            float interval = Mathf.Max(0.01f, repathInterval);
            if (_repathElapsed >= interval)
            {
                _repathElapsed = 0f;
                return EnemyNavigationFailureSignal.Repath;
            }

            return EnemyNavigationFailureSignal.None;
        }

        public void Reset()
        {
            _repathElapsed = 0f;
        }
    }
}
