using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        private void InitializeCellularGas()
        {
            var profile = _gasRuntime != null ? _gasRuntime.ConfigurationProfile : null;
            if (profile == null) return;
            _gasRuntime.ResetCellularVentObservations();
            CellularSimulation.ConfigureGasEnvironment(profile, _origin, _profile.CellSize, _profile.ChunkSize,
                _gasRuntime.RegisteredVentRate, RecordCellularGasDissipation);
        }

        private void BeginCellularGasTick()
        {
            if (!CellularSimulation.HasGasEnvironment) return;
            CellularSimulation.BeginGasEnvironmentTick(_tickInterval, LiquidActivityDomain(),
                _gasRuntime != null && _gasRuntime.HasRegisteredVents, true);
            if (_gasRuntime != null) _gasRuntime.WakeChangedCellularVents(CellularSimulation);
        }

        private void RecordCellularGasDissipation(MaterialId material, ulong amount)
            => ConversionLedger?.RecordExplicitConsumption(material, amount);
    }

    // 保留场景中既有通风口与世界的显式绑定；本类只提供配置和已注册入口，无须分配旧 GasVolumeStore。
    public sealed partial class GasWorldRuntime
    {
        private struct CellularVentObservation
        {
            public GasVentAuthoring Vent;
            public Bounds Bounds;
            public MaterialId Material;
            public bool Open;
        }
        private CellularVentObservation[] _cellularVentObservations;
        private int _cellularObservedVentCount;
        internal void ResetCellularVentObservations()
        {
            if (_cellularObservedVentCount > 0) Array.Clear(_cellularVentObservations, 0, _cellularObservedVentCount);
            _cellularObservedVentCount = 0;
        }

        internal void WakeChangedCellularVents(ElementCellSimulation simulation)
        {
            if (_ventCount == 0 && _cellularObservedVentCount == 0) return;
            if (_cellularVentObservations == null) _cellularVentObservations = new CellularVentObservation[_vents.Length];
            for (int i = 0; i < _ventCount; i++)
            {
                var vent = _vents[i];
                if (vent == null) { _cellularVentObservations[i] = default; continue; }
                Bounds bounds = vent.Bounds;
                bool open = vent.isActiveAndEnabled && vent.Rate > 0;
                ref var prior = ref _cellularVentObservations[i];
                if (open && (prior.Vent != vent || !prior.Open || !prior.Bounds.Equals(bounds) || prior.Material != vent.Material))
                    simulation.WakeGasInBounds(bounds);
                prior = new CellularVentObservation { Vent = vent, Bounds = bounds, Material = vent.Material, Open = open };
            }
            if (_cellularObservedVentCount > _ventCount)
                Array.Clear(_cellularVentObservations, _ventCount, _cellularObservedVentCount - _ventCount);
            _cellularObservedVentCount = _ventCount;
        }

        internal bool HasRegisteredVents => _ventCount != 0;
        internal double RegisteredVentRate(Vector3 center, MaterialId material)
        {
            double rate = 0;
            for (int i = 0; i < _ventCount; i++)
            {
                var vent = _vents[i];
                if (vent != null && vent.isActiveAndEnabled &&
                    (vent.Material == MaterialId.Empty || vent.Material == material) && vent.Bounds.Contains(center))
                    rate += vent.Rate;
            }
            return rate;
        }
    }
}
