using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>地面输运积分的独立数学入口；速度单位m/s，Normal在World Space。</summary>
    public static class LiquidTransportMath
    {
        public static Vector3 IntegrateGroundVelocity(Vector3 velocity, Vector3 normal,
            Vector3 pressureAcceleration, float gravity, float drag, float dt, float maximumSpeed)
        {
            if (dt <= 0f || maximumSpeed <= 0f || normal.sqrMagnitude < .5f) return Vector3.zero;
            normal.Normalize();
            Vector3 g = Vector3.down * gravity;
            Vector3 a = g + pressureAcceleration;
            // 去掉法向加速度与速度，只沿实际支撑面输运；平地重力不会造成横向移动。
            a -= Vector3.Dot(a, normal) * normal;
            velocity -= Vector3.Dot(velocity, normal) * normal;
            return Vector3.ClampMagnitude((velocity + a * dt) * Mathf.Exp(-Mathf.Max(0f, drag) * dt), maximumSpeed);
        }
    }

    internal readonly struct LiquidTransportKey : IEquatable<LiquidTransportKey>
    {
        internal readonly Vector3Int Cell;
        internal readonly MaterialId Material;
        internal LiquidTransportKey(Vector3Int cell, MaterialId material) { Cell = cell; Material = material; }
        public bool Equals(LiquidTransportKey other) => Cell == other.Cell && Material == other.Material;
        public override bool Equals(object other) => other is LiquidTransportKey key && Equals(key);
        public override int GetHashCode() => unchecked(Cell.GetHashCode() * 397 ^ (int)Material);
    }

    /// <summary>固定活动槽池，休眠后槽位可回收；回收运动状态不会回收GMU。</summary>
    internal sealed class LiquidTransportState
    {
        internal struct State { internal Vector3 Velocity; internal Vector4 Remainder; internal int Tick; }
        private readonly Dictionary<LiquidTransportKey, int> _map;
        private readonly LiquidTransportKey[] _keys;
        private readonly State[] _states;
        private int _used, _cursor;
        internal uint CapacityFallbacks { get; private set; }
        internal LiquidTransportState(int capacity)
        { _map = new Dictionary<LiquidTransportKey, int>(capacity); _keys = new LiquidTransportKey[capacity]; _states = new State[capacity]; }
        internal bool TryRead(Vector3Int cell, MaterialId material, int tick, out State state)
        {
            if (_map.TryGetValue(new LiquidTransportKey(cell, material), out int i) && tick - _states[i].Tick <= 2)
            { state = _states[i]; return true; }
            state = default; return false;
        }
        internal void Put(Vector3Int cell, MaterialId material, State state)
        {
            var key = new LiquidTransportKey(cell, material);
            if (!_map.TryGetValue(key, out int i))
            {
                if (_used < _states.Length) i = _used++;
                else
                {
                    i = -1;
                    // 有界回收扫描，绝不因运动槽满在一帧遍历全湖或扩容。
                    for (int n = 0; n < 64; n++)
                    { int c = _cursor; _cursor = (_cursor + 1) % _states.Length; if (state.Tick - _states[c].Tick > 2) { i = c; break; } }
                    if (i < 0) { CapacityFallbacks++; return; }
                    _map.Remove(_keys[i]);
                }
                _keys[i] = key; _map.Add(key, i);
            }
            _states[i] = state;
        }
    }
}
