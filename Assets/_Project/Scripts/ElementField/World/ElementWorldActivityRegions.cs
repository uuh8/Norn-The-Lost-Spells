using System;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 固定容量的空间并集。Envelope 只用于粗略包围，资格判断始终逐区域 OR，
    /// 所以两个远处来源之间的空白不会被误激活，重叠区域也没有重复 Tick 的含义。
    /// </summary>
    public sealed class ElementWorldActivityRegions
    {
        public const int MaximumGpuRegions = 32;
        private readonly Bounds[] _bounds;
        public int Count { get; private set; }
        public int RejectedCount { get; private set; }
        public Bounds Envelope { get; private set; }

        public ElementWorldActivityRegions(int capacity = MaximumGpuRegions)
        {
            if (capacity < 1 || capacity > MaximumGpuRegions)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            _bounds = new Bounds[capacity];
        }

        public void Clear() { Count = 0; RejectedCount = 0; Envelope = default; }
        public Bounds GetBounds(int index)
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _bounds[index];
        }

        public bool TryAdd(Bounds bounds)
        {
            if (!Finite(bounds.min) || !Finite(bounds.max)
                || bounds.size.x <= 0 || bounds.size.y <= 0 || bounds.size.z <= 0)
            { RejectedCount++; return false; }
            for (int i = 0; i < Count; i++)
                if (_bounds[i] == bounds) return true;
            if (Count == _bounds.Length) { RejectedCount++; return false; }
            _bounds[Count++] = bounds;
            Bounds envelope = Count == 1 ? bounds : Envelope;
            envelope.Encapsulate(bounds);
            Envelope = envelope;
            return true;
        }

        public bool Contains(Vector3 point)
        {
            for (int i = 0; i < Count; i++)
            {
                Vector3 min = _bounds[i].min, max = _bounds[i].max;
                if (point.x >= min.x && point.y >= min.y && point.z >= min.z
                    && point.x < max.x && point.y < max.y && point.z < max.z) return true;
            }
            return false;
        }

        public bool Intersects(Bounds bounds)
        {
            for (int i = 0; i < Count; i++)
            {
                Vector3 min = _bounds[i].min, max = _bounds[i].max;
                // 边界相接没有体积交集，避免只因共享边界扩出一层 Chunk。
                if (min.x < bounds.max.x && min.y < bounds.max.y && min.z < bounds.max.z
                    && max.x > bounds.min.x && max.y > bounds.min.y && max.z > bounds.min.z) return true;
            }
            return false;
        }

        public void CopyFrom(ElementWorldActivityRegions source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (ReferenceEquals(source, this)) return;
            if (source.Count > _bounds.Length) throw new ArgumentException("Frozen region capacity is too small.");
            Clear();
            for (int i = 0; i < source.Count; i++) TryAdd(source._bounds[i]);
        }

        private static bool Finite(Vector3 v) => !float.IsNaN(v.x) && !float.IsInfinity(v.x)
            && !float.IsNaN(v.y) && !float.IsInfinity(v.y) && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
    }
}
