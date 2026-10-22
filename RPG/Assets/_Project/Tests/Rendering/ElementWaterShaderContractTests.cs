using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>
    /// 锁定体积水 Shader 与 Material/Editor 的序列化契约。
    /// 这里只证明属性存在，不把 Shader Import 成功冒充为最终 Game View 视觉验收。
    /// </summary>
    public sealed class ElementWaterShaderContractTests
    {
        private static readonly string[] SharedWaterProperties =
        {
            "_ShallowColor", "_DeepColor", "_FresnelColor",
            "_ShallowAlpha", "_DeepAlpha", "_FresnelAlphaStrength", "_WaterAlpha",
            "_WaveScaleA", "_WaveScaleB", "_WaveSpeedA", "_WaveSpeedB",
            "_WaveNormalStrength", "_TriplanarSharpness", "_VolumeWaveWorldScale",
            "_DepthFadeDistance", "_DepthNoiseStrength", "_FresnelPower", "_Smoothness",
            "_FoamColor", "_FoamDistance", "_FoamStrength", "_FoamAlphaBoost"
        };

        [Test]
        public void ElementWaterShaderExposesVolumeProjectionProperties()
        {
            AssertSharedProperties("Game/Elemental/Water");
        }

        [Test]
        public void ProceduralLiquidPreservesLegacyWaterProperties()
        {
            AssertSharedProperties("Game/Elemental/Liquid Procedural");
        }

        [Test]
        public void ProceduralLiquidImportsWithoutShaderCompilerErrors()
        {
            Shader shader = Shader.Find("Game/Elemental/Liquid Procedural");
            Assert.That(shader, Is.Not.Null);
            Assert.That(
                ShaderUtil.ShaderHasError(shader),
                Is.False,
                "ElementLiquidProcedural.shader has a Unity Shader Import/compile error.");
        }

        [Test]
        public void ProceduralLiquidUsesUnityIndirectContractAndSharedWaterCore()
        {
            const string shaderPath =
                "Assets/_Project/Art/Elemental/Shaders/ElementLiquidProcedural.shader";
            string source = File.ReadAllText(shaderPath);
            int defineIndex = source.IndexOf("#define UNITY_INDIRECT_DRAW_ARGS IndirectDrawArgs");
            int includeIndex = source.IndexOf("#include \"UnityIndirect.cginc\"");
            Assert.That(defineIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(includeIndex, Is.GreaterThan(defineIndex));
            StringAssert.Contains("InitIndirectDrawArgs(0)", source);
            StringAssert.Contains("GetIndirectVertexID(input.vertexID)", source);
            StringAssert.Contains("StructuredBuffer<FluidSurfaceTriangle> _FluidSurfaceTriangles", source);
            // Unity 6 的 DXC/HLSL 语法会把 triangle 识别为几何着色器输入修饰符，
            // 因此它不能再作为局部变量名，否则 Shader Import 失败并让所有液体不可见。
            StringAssert.Contains("FluidSurfaceTriangle surfaceTriangle", source);
            StringAssert.DoesNotContain("FluidSurfaceTriangle triangle =", source);
            // Unity 6 DXC 也不能用 ?: 在两个自定义 Struct 之间选返回值；
            // 三个 Corner 必须各自通过显式 return 返回。
            StringAssert.Contains("if (cornerIndex == 1u)", source);
            StringAssert.DoesNotContain(
                "return cornerIndex == 1u ? surfaceTriangle.v1 : surfaceTriangle.v2;",
                source);
            StringAssert.Contains("ElementWaterCore.hlsl", source);
            StringAssert.Contains("ElementWaterBuildOrthonormalBasis", source);
            StringAssert.Contains("\"Queue\" = \"Transparent\"", source);
            StringAssert.Contains("Cull Back", source);
        }

        [Test]
        public void ProceduralLiquidOpaqueFragmentDiagnosticIsBoundPerDraw()
        {
            const string shaderPath =
                "Assets/_Project/Art/Elemental/Shaders/ElementLiquidProcedural.shader";
            const string rendererPath =
                "Assets/_Project/Scripts/Rendering/ElementField/GpuLiquidSurfaceRenderer.cs";
            string shaderSource = File.ReadAllText(shaderPath);
            string rendererSource = File.ReadAllText(rendererPath);

            // 诊断开关必须从 Component 经 MaterialPropertyBlock 到达 Fragment；
            // 只在 Material 上声明但 Draw 时未绑定，会制造“参数怎么调都没变化”的假象。
            StringAssert.Contains("_DebugForceOpaqueFragment", shaderSource);
            StringAssert.Contains("if (_DebugForceOpaqueFragment > 0.5f)", shaderSource);
            StringAssert.Contains("return half4(_ShallowColor.rgb, 1.0h);", shaderSource);
            StringAssert.Contains("Shader.PropertyToID(\"_DebugForceOpaqueFragment\")", rendererSource);
            // 生产默认必须走真实透明 Fragment；诊断值仍通过 MPB 逐 Draw 绑定，按需在 Inspector 开启。
            StringAssert.Contains("private bool _debugForceOpaqueFragment = false", rendererSource);
            // 换行属于格式，不应让源码 Contract 测试失败；分别锁定 MPB、Property Id 与值表达式。
            StringAssert.Contains("_materialPropertyBlock.SetFloat(", rendererSource);
            StringAssert.Contains("DebugForceOpaqueFragmentId,", rendererSource);
            StringAssert.Contains("_debugForceOpaqueFragment ? 1f : 0f);", rendererSource);
        }

        private static void AssertSharedProperties(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, $"Shader import failed: {shaderName}");

            var material = new Material(shader);
            try
            {
                for (int i = 0; i < SharedWaterProperties.Length; i++)
                {
                    Assert.That(material.HasProperty(SharedWaterProperties[i]), Is.True,
                        $"{shaderName} is missing {SharedWaterProperties[i]}.");
                }
            }
            finally
            {
                // Material 是 Unity Native Engine Object，不由普通 C# using/IDisposable 管理。
                Object.DestroyImmediate(material);
            }
        }
    }
}
