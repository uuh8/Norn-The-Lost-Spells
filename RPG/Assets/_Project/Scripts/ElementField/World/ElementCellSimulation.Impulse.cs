using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        // 喷口的有限冲量只附在实际喷出的 GMU 上。普通流水、静水和固体不携带逐格速度场。
        private struct CellImpulse
        {
            public MaterialId Material;
            public uint Quantity;
            public Vector3 Velocity, Carry;
            public float Gravity;
            public int Steps;
            public ulong EligibleStep;
        }
        private readonly int _impulseLimit;
        private readonly Dictionary<Vector3Int, CellImpulse> _impulses;
        internal bool HasPendingImpulse(Vector3Int cell) => _impulses.ContainsKey(cell);
        internal int AvailableImpulseSlots => _impulseLimit - _impulses.Count;

        internal void GiveImpulse(Vector3Int cell, MaterialId material, uint amount, Vector3 velocity, float gravity)
        {
            var motion = ElementCellMaterialRules.Motion(material);
            if ((motion != ElementCellMotion.Liquid && motion != ElementCellMotion.Grain) || amount == 0 ||
                !TryGetQuantity(cell, material, out uint present)) return;
            Vector3 speed = Vector3.ClampMagnitude(velocity, 1f);
            if (speed.sqrMagnitude < .01f) return;
            if (_impulses.TryGetValue(cell, out var old) && old.Material == material)
            {
                // 多个喷口落在同格时按份额合并，只更新本次真正进入格内的物质。
                uint prior = Math.Min(old.Quantity, present > amount ? present - amount : 0);
                speed = (old.Velocity * prior + speed * amount) / (prior + (float)amount);
                amount += prior;
            }
            StoreImpulse(cell, new CellImpulse { Material = material, Quantity = Math.Min(amount, present),
                Velocity = speed, Gravity = Mathf.Clamp(gravity, 0f, 1f), Steps = 8, EligibleStep = _step + 1 });
        }

        private void StoreImpulse(Vector3Int cell, CellImpulse impulse)
        {
            // 高负载时超过短时运动预算的物质仍由通常重力推进；不会产生新的 Owner 或丢失 GMU。
            if (_impulses.Count >= _impulseLimit && !_impulses.ContainsKey(cell)) return;
            _impulses[cell] = impulse;
            Wake(cell);
        }

        private bool AdvanceImpulse(Vector3Int cell, MaterialId material)
        {
            if (_impulses.Count == 0 || !_impulses.TryGetValue(cell, out var pulse)) return false;
            if (pulse.Material != material) { _impulses.Remove(cell); return false; }
            if (pulse.EligibleStep > _step) { _retry = true; return true; }
            _impulses.Remove(cell);
            uint available = Math.Min(pulse.Quantity, Movable(cell, material));
            if (available == 0 || pulse.Steps-- <= 0) return false;
            pulse.Carry += pulse.Velocity;
            pulse.Velocity.y = Mathf.Max(-1f, pulse.Velocity.y - pulse.Gravity);
            // 离散步只经过一个面。对角方向的未用位移留到以后，墙角不会因跨棱跳步而漏料。
            int axis = Mathf.Abs(pulse.Carry.x) >= Mathf.Abs(pulse.Carry.z) ? 0 : 2;
            if (Mathf.Abs(pulse.Carry.y) > Mathf.Abs(pulse.Carry[axis])) axis = 1;
            pulse.EligibleStep = _step + 1;
            if (Mathf.Abs(pulse.Carry[axis]) < .5f)
            { StoreImpulse(cell, pulse); return true; }
            int sign = pulse.Carry[axis] > 0 ? 1 : -1;
            Vector3Int direction = Vector3Int.zero;
            direction[axis] = sign;
            pulse.Carry[axis] -= sign;
            if (!Offset(cell, direction, out var next)) return false;
            uint before = Read(next).Quantity(material);
            if (!MoveBulk(cell, next, material, available, false)) return false;
            pulse.Quantity = Read(next).Quantity(material) - before;
            if (pulse.Steps > 0 && pulse.Quantity != 0) StoreImpulse(next, pulse);
            return true;
        }
    }
}
