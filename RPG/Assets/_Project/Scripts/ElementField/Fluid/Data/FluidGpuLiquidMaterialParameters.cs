using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>与 HLSL 两个 float4 完全一致，固定 32 bytes；数组下标就是 byte MaterialId。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidGpuLiquidMaterialParameters
    {
        public readonly Vector4 MassDensityViscosityPressure;
        public readonly Vector4 CohesionAndSleep;

        public FluidGpuLiquidMaterialParameters(in LiquidMaterialSettings settings)
        {
            MassDensityViscosityPressure = new Vector4(
                settings.ParticleMass,
                settings.RestDensity,
                settings.Viscosity,
                settings.ArtificialPressure);
            CohesionAndSleep = new Vector4(
                settings.CohesionStrength,
                settings.CohesionRestDistanceRatio,
                settings.MaximumCohesionDeltaSpeed,
                settings.SleepDensityErrorThreshold);
        }
    }
}
