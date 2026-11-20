using System;
using System.Collections.Generic;
using Game.Combat;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private Queue<Vector3Int> _absorbQueue;
        private HashSet<Vector3Int> _absorbSet;
        private int _absorbLimit, _absorbCursor, _absorbRecoveryRemaining;
        private MaterialConversionPlacement[] _absorbPlacements;
        private ElementReactionTuningSnapshot _absorbTuning;
        private static readonly MaterialConversionRule AbsorbRecipe = new MaterialConversionRule
        {
            Source = MaterialId.Water, FirstProduct = MaterialId.Sticky,
            InputUnits = 1, FirstUnits = 1, InputGmuPerSecond = 1
        };

        private void InitializeCellularAbsorption()
        {
            if (!_materialReactions.TryResolve(MaterialId.Water, MaterialId.Sticky, out var id)
                || id != ElementReactionId.AbsorbWater || _profile.AbsorbWater.WaterConvertPerSecond <= 0f) return;
            _absorbLimit = Math.Max(32, _cellDefinition.WorkBudget);
            _absorbQueue = new Queue<Vector3Int>(_absorbLimit);
            _absorbSet = new HashSet<Vector3Int>(_absorbLimit);
            _absorbPlacements = new MaterialConversionPlacement[7];
            _absorbTuning = new ElementReactionTuningSnapshot(default, default, default, default, _profile.AbsorbWater, 0f, 0f);
        }

        // 只在水或黏液发生变化时登记接触面。用水格作为唯一候选，多个相邻黏液格不能重复结算同一次接触。
        private void DiscoverCellularAbsorption(Vector3Int cell)
        {
            if (TryGetCellularQuantity(cell, MaterialId.Water, out _))
            {
                if (FindTouchingSticky(cell, out _)) EnqueueAbsorption(cell);
            }
            else if (TryGetCellularQuantity(cell, MaterialId.Sticky, out _))
                foreach (var face in ElementCellSimulation.Faces)
                    if (ElementCellSimulation.Offset(cell, face, out var water) && TryGetCellularQuantity(water, MaterialId.Water, out _))
                        EnqueueAbsorption(water);
        }

        private bool FindTouchingSticky(Vector3Int cell, out uint sticky)
        {
            foreach (var face in ElementCellSimulation.Faces)
                if (ElementCellSimulation.Offset(cell, face, out var neighbor) && TryGetCellularQuantity(neighbor, MaterialId.Sticky, out sticky))
                    return true;
            sticky = 0;
            return false;
        }

        private void EnqueueAbsorption(Vector3Int cell)
        {
            if (_absorbSet.Contains(cell)) return;
            if (_absorbQueue.Count >= _absorbLimit)
            { _absorbRecoveryRemaining = CellularSimulation.OccupiedCellCount; return; }
            _absorbSet.Add(cell);
            _absorbQueue.Enqueue(cell);
        }

        private void ProcessCellularAbsorption()
        {
            if (_absorbQueue == null) return;
            int work = Math.Min(32, _absorbQueue.Count);
            for (int i = 0; i < work; i++)
            {
                var cell = _absorbQueue.Dequeue();
                _absorbSet.Remove(cell);
                if (!TryGetCellularQuantity(cell, MaterialId.Water, out var water) || !FindTouchingSticky(cell, out var sticky)) continue;
                var contact = new MaterialContactSnapshot(MaterialId.Water, ToCellIntensity(water), MaterialId.Sticky, ToCellIntensity(sticky));
                if (!ElementReactionEvaluator.TryEvaluateMaterialContact(in contact, _tickInterval, ElementReactionId.AbsorbWater,
                    in _absorbTuning, out var result) || result.FirstConsumedGmu <= 0) continue;
                uint used = (uint)Math.Min(water, ((ulong)result.FirstConsumedGmu * CellularCellCapacity + 254) / 255);
                if (!RestingLiquids.TryGetCell(cell, out _, out _) || RestingLiquids.TryMaterialize(cell, CellularSimulation))
                    CellularSimulation.TryReact(new MaterialConversionAddress(cell, MaterialId.Water), in AbsorbRecipe,
                        used, _absorbPlacements, CellularLedger, out _);
                // 完全封闭的接触也可能暂时放不下产物。本格保持一个候选，继续受相同的预算约束。
                if (TryGetCellularQuantity(cell, MaterialId.Water, out _)) EnqueueAbsorption(cell);
            }
            for (int i = 0; i < 16 && _absorbRecoveryRemaining > 0 && CellularSimulation.OccupiedCellCount > 0; i++)
            {
                _absorbRecoveryRemaining--;
                _absorbCursor %= CellularSimulation.OccupiedCellCount;
                if (CellularSimulation.TryGetOccupiedCoordinate(_absorbCursor++, out var cell)) DiscoverCellularAbsorption(cell);
            }
        }

        private byte ToCellIntensity(uint amount) => (byte)Math.Max(1UL, (ulong)amount * 255 / CellularCellCapacity);
    }
}
