using System.IO;
using NUnit.Framework;

namespace Game.Rendering.Tests
{
    public sealed class GpuFireParcelShaderContractTests
    {
        [Test]
        public void DensityComputeAvoidsReservedPointIdentifier()
        {
            string source = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Compute/FireParcelDensity.compute");

            StringAssert.DoesNotContain("float3 point =", source);
        }

        [Test]
        public void SurfaceShaderAvoidsStructValuedTernary()
        {
            string source = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Shaders/ElementFireParcelSurface.shader");

            StringAssert.DoesNotContain("ci==0?t.a:(ci==1?t.b:t.c)", source);
        }

        [Test]
        public void LifecycleUsesIndependentAbsolutePhaseDurations()
        {
            string source = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Compute/FireParcelLifecycle.compute");

            StringAssert.Contains("_LiquidLikeHoldSeconds", source);
            StringAssert.Contains("_SurfaceToGasBlendSeconds", source);
            StringAssert.Contains("_GasLifetimeSeconds", source);
            StringAssert.DoesNotContain("_SurfaceFadeStart", source);
            StringAssert.DoesNotContain("_FinalFadeStart", source);
        }

        [Test]
        public void LiquidLikeAndGasHaveIndependentRadiusControls()
        {
            string density = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Compute/FireParcelDensity.compute");
            string splat = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Shaders/ElementFireParcelSplat.shader");

            StringAssert.Contains("_LiquidLikeRadiusMultiplier", density);
            StringAssert.Contains("_GasRadiusMultiplier", splat);
        }

        [Test]
        public void LiquidLikeFireHasIndependentFarSurfaceSplatDraw()
        {
            string renderer = File.ReadAllText(
                "Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderer.cs");
            string splat = File.ReadAllText(
                "Assets/_Project/Art/Elemental/Shaders/ElementFireParcelSplat.shader");

            StringAssert.Contains("_surfaceSplatProperties", renderer);
            StringAssert.Contains("_surfaceSplatRenderParams", renderer);
            StringAssert.Contains("_SurfaceLodCenter", splat);
            StringAssert.Contains("_SurfaceLodBlendWidth", splat);
            StringAssert.Contains("SetFloat(\"_PhaseMode\", 0f)", renderer);
        }
    }
}
