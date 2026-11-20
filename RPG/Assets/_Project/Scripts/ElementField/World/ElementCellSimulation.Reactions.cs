using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        // 所有配方只读中心和六个面邻格。源、两种产物与显式消散共享一次提交，
        // 中途发现空间不足时，候选仍拥有全部原料和已累计的反应时间。
        internal bool TryReact(MaterialConversionAddress source, in MaterialConversionRule rule, uint batches,
            MaterialConversionPlacement[] placements, MaterialWorldQuantityLedger ledger, out int count)
        {
            count = 0;
            if (_stepping || batches == 0 || !rule.IsValid || rule.Source != source.Material
                || !ElementCellMaterialRules.Supports(rule.FirstProduct)
                || rule.SecondUnits != 0 && !ElementCellMaterialRules.Supports(rule.SecondProduct)) return false;
            uint available = Read(source.Cell).Quantity(source.Material);
            if (batches > available / rule.InputUnits) return false;
            uint consumed = batches * rule.InputUnits;
            _editCount = 0;
            _edits[Stage(source.Cell)].After.Remove(source.Material, consumed);
            if (!PlaceRecipeProduct(source.Cell, rule.FirstProduct, batches * rule.FirstUnits, placements, ref count)
                || !PlaceRecipeProduct(source.Cell, rule.SecondProduct, batches * rule.SecondUnits, placements, ref count)) return false;
            uint dissipated = batches * rule.DissipatedUnits;
            if (!ledger.Allows(source.Material, consumed, placements, count, dissipated) || !Commit()) return false;
            ledger.Record(source.Material, consumed, placements, count, dissipated);
            return true;
        }

        private bool PlaceRecipeProduct(Vector3Int center, MaterialId material, uint remaining,
            MaterialConversionPlacement[] placements, ref int count)
        {
            bool gas = ElementCellMaterialRules.Motion(material) == ElementCellMotion.Gas;
            for (int i = -1; remaining != 0 && i < Faces.Length; i++)
            {
                Vector3Int cell = center;
                if (i >= 0 && (!Offset(center, Faces[i], out cell) || Blocked(cell))) continue;
                ref Contents value = ref _edits[Stage(cell)].After;
                if (!gas && value.Amount != 0 && value.Bulk != material) continue;
                uint room = value.Free(CellCapacity);
                if (ElementCellMaterialRules.OccupiesWholeCell(material))
                {
                    if (value.Steam != 0 || value.Vapor != 0) continue;
                    room = CellCapacity - value.Amount;
                }
                uint part = Math.Min(remaining, room);
                if (part == 0) continue;
                if (count >= placements.Length) return false;
                value.Add(material, part);
                placements[count++] = new MaterialConversionPlacement(cell, material, part);
                remaining -= part;
            }
            return remaining == 0;
        }
    }
}
