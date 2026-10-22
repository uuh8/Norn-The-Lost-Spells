using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public readonly struct ElementPerformanceWrite
    {
        public readonly int Sequence;
        public readonly double DueSeconds;
        public readonly ElementWriteRequest Request;
        public ElementPerformanceWrite(int sequence, double dueSeconds, in ElementWriteRequest request)
        { Sequence = sequence; DueSeconds = dueSeconds; Request = request; }
    }

    /// <summary>初始化时接受事件数组所有权；卡顿后按 FIFO 补发，每个请求仅尝试一次。</summary>
    public sealed class ElementWorldPerformanceSchedule
    {
        private readonly ElementPerformanceWrite[] _events;
        private int _next;
        public int RemainingCount => _events.Length - _next;
        public double LastDueSeconds => _events.Length == 0 ? 0d : _events[_events.Length - 1].DueSeconds;

        public ElementWorldPerformanceSchedule(ElementPerformanceWrite[] ownedEvents)
        {
            _events = ownedEvents ?? throw new ArgumentNullException(nameof(ownedEvents));
            for (int i = 0; i < _events.Length; i++)
            {
                if (!Finite(_events[i].DueSeconds) || _events[i].DueSeconds < 0
                    || _events[i].Sequence != i
                    || (i > 0 && _events[i].DueSeconds < _events[i - 1].DueSeconds))
                    throw new ArgumentException("事件需要连续序号与有限、单调递增的非负时间。");
            }
        }

        public bool TryDequeueDue(double elapsedSeconds, out ElementPerformanceWrite write)
        {
            write = default;
            if (!Finite(elapsedSeconds) || _next >= _events.Length
                || _events[_next].DueSeconds > elapsedSeconds) return false;
            write = _events[_next++];
            return true;
        }

        public static ElementWorldPerformanceSchedule CreateParticleFill(int particles, int batch,
            uint scale, MaterialId material, Vector3[] positions, float radius, double interval)
        {
            if (particles < 0 || batch <= 0 || scale == 0 || (ulong)(uint)batch * scale > ushort.MaxValue
                || !Finite(interval) || interval <= 0 || !Finite(radius) || radius <= 0)
                throw new ArgumentOutOfRangeException(nameof(particles));
            if (positions == null || positions.Length == 0) throw new ArgumentException("需要落点。");
            int count = checked((int)(((long)particles + batch - 1) / batch));
            var events = new ElementPerformanceWrite[count];
            int remaining = particles;
            for (int i = 0; i < count; i++)
            {
                int amount = Math.Min(remaining, batch);
                remaining -= amount;
                var request = new ElementWriteRequest(positions[i % positions.Length], material,
                    (ushort)((uint)amount * scale), radius, false, Vector3.zero, Vector3.up);
                events[i] = new ElementPerformanceWrite(i, i * interval, in request);
            }
            return new ElementWorldPerformanceSchedule(events);
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
