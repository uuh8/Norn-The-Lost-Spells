using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        private int FirstSide(Vector3Int cell)
        {
            unchecked
            {
                uint hash = (uint)cell.x * 73856093u ^ (uint)cell.y * 19349663u ^ (uint)cell.z * 83492791u;
                return (int)((hash ^ (hash >> 16)) + (uint)_step) & 3;
            }
        }

        private void AdvanceCell(Vector3Int cell)
        {
            Contents initial = Read(cell);
            if (!AdvanceImpulse(cell, initial.Bulk))
            {
                switch (ElementCellMaterialRules.Motion(initial.Bulk))
                {
                    case ElementCellMotion.Grain: AdvanceGrain(cell, initial.Bulk); break;
                    case ElementCellMotion.Liquid: AdvanceLiquid(cell, initial.Bulk); break;
                }
            }
            if (initial.Steam == 0 && initial.Vapor == 0) return;
            if (_gasEnvironment != null && (Movable(cell, MaterialId.Steam) == 0 && Movable(cell, MaterialId.ToxicVapor) == 0 ||
                !_gasEnvironment.TryBeginCell(cell))) return;
            if (initial.Steam > 0)
            {
                // 第一种接触相变：冰面附近的蒸汽可等量冷凝。墙体查询只能回答是否阻挡，
                // 不会把任意 Collider 当成冰。新生成的一份水要到下个 Step 才能移动。
                uint available = Movable(cell, MaterialId.Steam);
                if (available != 0 && TouchesIce(cell))
                    Convert(cell, MaterialId.Steam, MaterialId.Water, Math.Min(available, 1 + (CellCapacity - 1) / 16));
            }
            // 两种气体可能争一份空余容积。轮换先处理的物种，避免总是由 Steam 获得最后一个单位。
            bool steamFirst = (FirstSide(cell) & 1) == 0;
            if (steamFirst && initial.Steam != 0) AdvanceGas(cell, MaterialId.Steam);
            if (initial.Vapor != 0) AdvanceGas(cell, MaterialId.ToxicVapor);
            if (!steamFirst && initial.Steam != 0) AdvanceGas(cell, MaterialId.Steam);
        }

        private bool TouchesIce(Vector3Int cell)
        {
            bool dynamicIce = GetTotalQuantity(MaterialId.Ice) != 0;
            bool coldIce = _restingMaterials != null && _restingMaterials.GetTotalQuantity(MaterialId.Ice) != 0;
            if (!dynamicIce && !coldIce) return false;
            for (int i = 0; i < Faces.Length; i++)
                if (Offset(cell, Faces[i], out Vector3Int neighbor) && (dynamicIce && Read(neighbor).Bulk == MaterialId.Ice ||
                    coldIce && _restingMaterials.TryGetQuantity(neighbor, MaterialId.Ice, out _))) return true;
            return false;
        }

        private void AdvanceGrain(Vector3Int cell, MaterialId material)
        {
            uint quantity = Read(cell).Amount;
            if (quantity == 0 || Movable(cell, material) < quantity) return;
            if (Offset(cell, Vector3Int.down, out Vector3Int below) &&
                (MoveBulk(cell, below, material, quantity, true) || CanDescendIntoCold(below, material))) return;

            int first = FirstSide(cell);
            for (int i = 0; i < Sides.Length; i++)
            {
                Vector3Int side = Sides[(first + i) & 3];
                // 从上格到斜下格必须经相邻的侧面；两块相贴的岩石不能凭共享棱角漏砂。
                if (!Offset(cell, side, out Vector3Int passage) || Blocked(passage) || Read(passage).Amount != 0) continue;
                if (Offset(passage, Vector3Int.down, out Vector3Int target) && MoveBulk(cell, target, material, quantity, true)) return;
            }
        }

        private void AdvanceLiquid(Vector3Int cell, MaterialId material)
        {
            uint available = Movable(cell, material);
            if (available == 0) return;
            uint down = Math.Min(available, _transport.FlowLimit(material, CellCapacity, true));
            if (Offset(cell, Vector3Int.down, out Vector3Int below) &&
                (MoveBulk(cell, below, material, down, true) || CanDescendIntoCold(below, material))) return;

            int first = FirstSide(cell);
            uint sideLimit = _transport.FlowLimit(material, CellCapacity, false);
            uint budget = Math.Min(available, sideLimit);
            for (int i = 0; i < Sides.Length && budget != 0; i++)
            {
                if (!Offset(cell, Sides[(first + i) & 3], out Vector3Int target)) continue;
                Contents destination = Read(target);
                if (destination.Amount != 0 && destination.Bulk != material) continue;
                uint demand = LateralDemand(material, Read(cell).Amount, destination.Amount, target);
                if (demand == 0) continue;
                // 新润湿的格至少要收到这次可用的完整首批流量。避免先找平耗掉预算后，
                // 仅因遍历顺序就在另一块干地丢下一两 GMU，留下与实际液量不相称的脚印。
                if (destination.Amount == 0 && budget < Math.Min(Math.Min(available, sideLimit), demand)) continue;
                uint move = Math.Min(budget, demand);
                if (MoveBulk(cell, target, material, move, false)) budget -= move;
            }
        }

        internal uint LateralDemand(MaterialId material, uint sourceLevel, uint targetLevel, Vector3Int target)
        {
            if (sourceLevel <= targetLevel) return 0;
            if (targetLevel != 0) return (sourceLevel - targetLevel) / 2;
            // 湿格之间始终按实际液位找平。在平坦干地上则要有足够液量才能推进接触线，
            // 否则一个水格在不停的整数二分后会污染大片平地，最终每格只剩一两个 GMU。
            if (sourceLevel >= _transport.DrySpreadHead(material, CellCapacity) && sourceLevel > 1) return sourceLevel / 2;
            // 低于铺展门槛的小水滴也能沿台阶边缘排走，包括最后一个 GMU。只检查正下邻格，
            // 不搜索坡道或容器，因而每个活跃格的计算量仍有上界。
            if (!Offset(target, Vector3Int.down, out var below)) return 0;
            if (Blocked(below)) return CanDescendIntoCold(below, material) ? sourceLevel : 0;
            Contents lower = Read(below);
            return lower.Amount == 0 || lower.Bulk == material && lower.Amount < CellCapacity ||
                CanDisplaceRestingLiquid(material, lower.Bulk) ? sourceLevel : 0;
        }

        // 下方的部分量床层可能正归冷区所有。真实空余量在交接前后相同，不能因为冷区本 Tick
        // 的唤醒预算排在后面，就让本应向下填实的砂或水提前从侧面溢走。交接的提交会唤醒本格。
        private bool CanDescendIntoCold(Vector3Int below, MaterialId material)
        {
            if (_restingMaterials == null || !Blocked(below) ||
                !_restingMaterials.TryGetCell(below, out var lower, out uint amount)) return false;
            return lower == material && amount < CellCapacity || CanDisplaceRestingLiquid(material, lower);
        }

        private bool MoveBulk(Vector3Int from, Vector3Int to, MaterialId material, uint requested, bool mayDisplace)
        {
            if (requested == 0 || Blocked(to)) return false;
            _editCount = 0;
            int a = Stage(from), b = Stage(to);
            ref Contents source = ref _edits[a].After;
            ref Contents destination = ref _edits[b].After;
            if (source.Bulk != material) return false;
            if (destination.Amount != 0 && destination.Bulk != material)
            {
                MaterialId displaced = destination.Bulk;
                if (!mayDisplace || ElementCellMaterialRules.Motion(displaced) != ElementCellMotion.Liquid ||
                    _transport.Density(material) <= _transport.Density(displaced) ||
                    Movable(from, material) < source.Amount || Movable(to, displaced) < destination.Amount) return false;
                // 两种不混溶主材交换完整占据。分别保留自己的 GMU，包括未装满的一层；不按密度换算质量。
                uint oldSource = source.Amount;
                source.Bulk = displaced; source.Amount = destination.Amount;
                destination.Bulk = material; destination.Amount = oldSource;
            }
            else
            {
                uint move = Math.Min(requested, CellCapacity - destination.Amount);
                if (move == 0 || Movable(from, material) < move) return false;
                // 同类砂先填实下层的余量。分批倒出的砂和一次性倒出的砂遵守同一容量，
                // 下层装满后余量才留在上层或向斜下滚落。此处不影响异种主材的整格密度置换。
                source.Remove(material, move);
                destination.Add(material, move);
            }

            // 进入含气空间时，气体回到这一步腾出的源空间。两边水/油互换时腾出空间的也可能是另一边。
            if ((source.Steam | source.Vapor | destination.Steam | destination.Vapor)==0) return Commit();
            MoveExcess(b, a);
            MoveExcess(a, b);
            return Excess(in source) == 0 && Excess(in destination) == 0 && Commit();
        }

        private void AdvanceGas(Vector3Int cell, MaterialId material)
        {
            if (_gasEnvironment != null) { _gasEnvironment.Advance(cell, material); return; }
            int first = FirstSide(cell);
            for (int i = 0; i < Faces.Length; i++)
            {
                uint available = Movable(cell, material);
                if (available == 0) return;
                Vector3Int direction = i == 0 ? Vector3Int.up : i == Faces.Length - 1 ? Vector3Int.down : Sides[(first + i - 1) & 3];
                if (!Offset(cell, direction, out Vector3Int target) || Blocked(target)) continue;
                Contents source = Read(cell), destination = Read(target);
                uint room = destination.Free(CellCapacity);
                if (room == 0) continue;

                // 浓度由“该气体数量 / 未被主材料占据的空间”决定。比较 a/Va 与 b/Vb 用整数交叉乘；
                // 默认 Steam 的上侧平衡权重为 2，ToxicVapor 在六个面上按普通浓差传输。
                long sourceVolume = source.GasSpace(CellCapacity), targetVolume = destination.GasSpace(CellCapacity);
                int upperWeight = _transport.GasUpperWeight(material);
                if (direction.y > 0) targetVolume *= upperWeight;
                if (direction.y < 0) sourceVolume *= upperWeight;
                long gradient = source.Quantity(material) * targetVolume - destination.Quantity(material) * sourceVolume;
                if (gradient <= 0) continue;
                uint flux = (uint)(gradient / (sourceVolume + targetVolume));
                uint move = Math.Min(available, Math.Min(flux, room));
                if (move == 0) continue;
                _editCount = 0;
                _edits[Stage(cell)].After.Remove(material, move);
                _edits[Stage(target)].After.Add(material, move);
                Commit();
            }
        }
    }
}
