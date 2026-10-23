using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>物理根立即接受纠正；此有限偏移只用于模型和相机表现，超过2米的传送直接重置。</summary>
    public struct MovementCorrectionBlend
    {
        public Vector3 Offset { get; private set; }
        private Vector3 _start;
        private float _remaining;
        public void Begin(Vector3 worldError)
        {
            // 大于2米视为传送/严重校正，不能把模型留在墙另一侧慢慢飘回来。
            bool valid = !float.IsNaN(worldError.sqrMagnitude) && worldError.sqrMagnitude <= 4;
            _start = Offset = valid ? worldError : Vector3.zero; _remaining = valid ? .1f : 0;
        }
        public void Advance(float elapsed)
        {
            _remaining = Mathf.Max(0, _remaining - Mathf.Max(0, elapsed));
            Offset = _remaining > .00001f ? _start * (_remaining / .1f) : Vector3.zero;
        }
    }
}
