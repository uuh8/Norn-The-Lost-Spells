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
            CellularSimulation.ConfigureGasEnvironment(profile, _origin, _profile.CellSize, _profile.ChunkSize,
                _gasRuntime.RegisteredVentRate, RecordCellularGasDissipation);
        }

        private void BeginCellularGasTick()
        {
            if (CellularSimulation.HasGasEnvironment)
                CellularSimulation.BeginGasEnvironmentTick(_tickInterval, LiquidActivityDomain(),
                    _gasRuntime != null && _gasRuntime.HasRegisteredVents);
        }

        private void RecordCellularGasDissipation(MaterialId material, ulong amount)
            => ConversionLedger?.RecordExplicitConsumption(material, amount);
    }

    // 保留场景中既有通风口与世界的显式绑定；本类只提供配置和已注册入口，无须分配旧 GasVolumeStore。
    public sealed partial class GasWorldRuntime
    {
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
