using System;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// 只在原Portal探针显式短窗口内附加。没有Update，也不改原Player碰撞推力分支；
    /// Unity把现有CC回调同时送给同物体的观察器，只复制数值，不保存引擎回调对象。
    /// </summary>
    public sealed class HubMovementContactProbe : MonoBehaviour
    {
        private HubMovementContactWindow _window;
        internal void Begin(HubMovementContactWindow window) => _window = window;
        internal void End() => _window = null;

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (_window == null || hit == null) return;
            _window.Observe(gameObject.GetInstanceID(), hit.collider != null ? hit.collider.GetInstanceID() : 0,
                Time.frameCount, Time.realtimeSinceStartupAsDouble, hit.point, hit.normal, hit.moveDirection, hit.moveLength);
        }

        private void OnDisable() => End();
    }

    /// <summary>窗口级固定32条；Actor切换时共用同一预算。截止/停止后不再记录或分配。</summary>
    internal sealed class HubMovementContactWindow
    {
        [Serializable] internal struct Contact
        {
            public int ActorInstanceId, ColliderInstanceId, Frame;
            public double Realtime;
            public Vector3 Point, Normal, MoveDirection;
            public float MoveLength;
        }

        private readonly Contact[] _contacts = new Contact[32];
        private readonly double _deadline;
        public int Count { get; private set; }
        public int Dropped { get; private set; }
        public bool Accepting { get; private set; } = true;
        public HubMovementContactWindow(double deadline) => _deadline = deadline;

        public void Observe(int actor, int collider, int frame, double now, Vector3 point, Vector3 normal, Vector3 direction, float length)
        {
            if (!Accepting) return;
            if (double.IsNaN(now) || double.IsInfinity(now) || now >= _deadline) { Stop(); return; }
            if (Count == _contacts.Length) { if (Dropped < int.MaxValue) Dropped++; return; }
            _contacts[Count++] = new Contact
            {
                ActorInstanceId = actor, ColliderInstanceId = collider, Frame = frame, Realtime = now,
                Point = point, Normal = normal, MoveDirection = direction, MoveLength = length
            };
        }

        public void Stop() => Accepting = false;
        public void CopyTo(Contact[] target) => Array.Copy(_contacts, target, Count);
    }
}
