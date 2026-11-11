using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>
    /// 每连接的发送范围，固定四槽、无运行期扩容。这里只判断数据是否应发给某个连接，
    /// 不驱动世界模拟、清除权威材料或决定认证。装配层先核实connectionId/ConnectionEpoch，
    /// 再更新范围；复制Owner在范围离开时释放客户端缓存，重新进入时请求当前Baseline。
    /// </summary>
    public sealed class WorldInterestCoordinator
    {
        public const int Capacity = 4;
        private readonly bool[] _active = new bool[Capacity];
        private readonly int[] _connections = new int[Capacity];
        private readonly ulong[] _epochs = new ulong[Capacity];
        private readonly Bounds[] _bounds = new Bounds[Capacity];

        /// <summary>
        /// 把权威玩家位置对齐到独立网络参考块，窗口包含玩家所在块及两侧指定数量的邻块。
        /// 例如2m、XZ半径10/Y半径3，得到42×14×42m；这里的参考块不是材料原生Chunk，
        /// 也不是模拟活动范围。原点由装配层提供成功提交的世界几何，不能从客户端位置推断。
        /// </summary>
        public static bool TryBuildReferenceBounds(Vector3 position, Vector3 origin, float chunkWorldSize,
            int radiusXZ, int radiusY, out Bounds bounds)
        {
            bounds = default;
            if (!Finite(position) || !Finite(origin) || !float.IsFinite(chunkWorldSize) || chunkWorldSize <= 0
                || radiusXZ < 0 || radiusY < 0
                || radiusXZ > (int.MaxValue - 1) / 2 || radiusY > (int.MaxValue - 1) / 2)
                return false;

            // 先在double中算floor和端点，避免float减法/乘法先溢出，以及负坐标向零截断。
            // 最终仍必须经过真实Bounds的可表示性检查；double中合法不代表float还有正体积。
            if (!TryBuildReferenceAxis(position.x, origin.x, chunkWorldSize, radiusXZ, out float centerX, out float sizeX)
                || !TryBuildReferenceAxis(position.y, origin.y, chunkWorldSize, radiusY, out float centerY, out float sizeY)
                || !TryBuildReferenceAxis(position.z, origin.z, chunkWorldSize, radiusXZ, out float centerZ, out float sizeZ))
                return false;

            var candidate = new Bounds(new Vector3(centerX, centerY, centerZ), new Vector3(sizeX, sizeY, sizeZ));
            if (!Valid(candidate)) return false;
            bounds = candidate;
            return true;
        }

        /// <summary>
        /// 完整验证后一次替换范围。同一连接可用相同epoch移动；旧epoch不能覆盖新位置。
        /// 不同活跃连接必须先按原身份移除，防止误把旧槽当作新连接直接覆盖。
        /// </summary>
        public bool TrySetConnection(int slot, int connectionId, ulong connectionEpoch, Bounds bounds)
        {
            if ((uint)slot >= Capacity || connectionId < 0 || connectionEpoch == 0 || !Valid(bounds)) return false;
            if (_active[slot] && _connections[slot] != connectionId) return false;
            if (_connections[slot] == connectionId && connectionEpoch < _epochs[slot]) return false;
            for (int i = 0; i < Capacity; i++)
                if (i != slot && _active[i] && _connections[i] == connectionId) return false;
            _connections[slot] = connectionId; _epochs[slot] = connectionEpoch;
            _bounds[slot] = bounds; _active[slot] = true;
            return true;
        }

        public bool RemoveConnection(int connectionId, ulong connectionEpoch)
        {
            int slot = Find(connectionId, connectionEpoch);
            if (slot < 0) return false;
            _active[slot] = false; _bounds[slot] = default;
            return true;
        }

        public bool TryGetInterest(int connectionId, ulong connectionEpoch, out Bounds bounds)
        {
            int slot = Find(connectionId, connectionEpoch);
            bounds = slot < 0 ? default : _bounds[slot];
            return slot >= 0;
        }

        /// <summary>使用半开AABB，边界交接不会把同一格同时划到相邻两块中；负坐标遵守相同规则。</summary>
        public bool Contains(int connectionId, ulong connectionEpoch, Vector3 position)
        {
            if (!TryGetInterest(connectionId, connectionEpoch, out Bounds bounds)) return false;
            Vector3 min = bounds.min, max = bounds.max;
            return position.x >= min.x && position.y >= min.y && position.z >= min.z
                && position.x < max.x && position.y < max.y && position.z < max.z;
        }

        /// <summary>
        /// 与发送窗口有正体积交集的原生Chunk整块发送，不能只判断Chunk中心是否落入窗口。
        /// Unity Bounds.Intersects包含仅接触边界的情况；这里显式采用严格不等式，与Contains
        /// 的半开边界一致，面/边/角接触不会误订阅相邻块。身份失效或非法几何一律拒绝。
        /// </summary>
        public bool Intersects(int connectionId, ulong connectionEpoch, Bounds chunkBounds)
        {
            if (!Valid(chunkBounds) || !TryGetInterest(connectionId, connectionEpoch, out Bounds interest)) return false;
            Vector3 minimum = interest.min, maximum = interest.max;
            Vector3 chunkMinimum = chunkBounds.min, chunkMaximum = chunkBounds.max;
            return minimum.x < chunkMaximum.x && maximum.x > chunkMinimum.x
                && minimum.y < chunkMaximum.y && maximum.y > chunkMinimum.y
                && minimum.z < chunkMaximum.z && maximum.z > chunkMinimum.z;
        }

        private static bool TryBuildReferenceAxis(float position, float origin, float chunkWorldSize, int radius,
            out float center, out float size)
        {
            center = size = 0;
            double block = Math.Floor(((double)position - origin) / chunkWorldSize);
            double minimum = origin + (block - radius) * chunkWorldSize;
            double maximum = origin + (block + radius + 1d) * chunkWorldSize;
            double length = maximum - minimum;
            if (minimum < -float.MaxValue || maximum > float.MaxValue || length <= 0 || length > float.MaxValue)
                return false;
            center = (float)((minimum + maximum) * .5d);
            size = (float)length;
            return float.IsFinite(center) && float.IsFinite(size) && size > 0;
        }

        private int Find(int id, ulong epoch)
        {
            if (epoch == 0) return -1;
            for (int i = 0; i < Capacity; i++)
                if (_active[i] && _connections[i] == id && _epochs[i] == epoch) return i;
            return -1;
        }
        private static bool Valid(Bounds bounds)
        {
            Vector3 minimum = bounds.min, maximum = bounds.max, size = bounds.size;
            // 大坐标+小size可能在float中得到min==max；仅检查size>0不能识别这种塌缩。
            return Finite(minimum) && Finite(maximum) && Finite(size)
                && size.x > 0 && size.y > 0 && size.z > 0
                && minimum.x < maximum.x && minimum.y < maximum.y && minimum.z < maximum.z;
        }
        private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    }
}
