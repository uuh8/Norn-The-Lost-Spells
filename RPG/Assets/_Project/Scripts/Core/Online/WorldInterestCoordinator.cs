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

        private int Find(int id, ulong epoch)
        {
            if (epoch == 0) return -1;
            for (int i = 0; i < Capacity; i++)
                if (_active[i] && _connections[i] == id && _epochs[i] == epoch) return i;
            return -1;
        }
        private static bool Valid(Bounds bounds) => Finite(bounds.min) && Finite(bounds.max)
            && bounds.size.x > 0 && bounds.size.y > 0 && bounds.size.z > 0;
        private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    }
}
