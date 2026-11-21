using System.IO;
using Game.ElementField;
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
        public void ProductionCellularSurfaceProvidesVisibleMeshShadowAndCameraDepth()
        {
            // 从正式场景共用的 Profile 取材质，避免只检查了一个碰巧能在 Editor 中找到的 Shader。
            var profile = AssetDatabase.LoadAssetAtPath<ElementCellWorldProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/CellularWorld.asset");
            Assert.That(profile, Is.Not.Null);
            var liquid = profile.LiquidSurface;
            Assert.That(liquid, Is.Not.Null);
            Assert.That(liquid.shader.name, Is.EqualTo("Game/Elemental/Cartoon Liquid"));
            Assert.That(ShaderUtil.ShaderHasError(liquid.shader), Is.False);
            Assert.That(liquid.FindPass("CartoonLiquidForward"), Is.GreaterThanOrEqualTo(0));
            Assert.That(liquid.FindPass("ShadowCaster"), Is.GreaterThanOrEqualTo(0));
            Assert.That(liquid.FindPass("DepthOnly"), Is.GreaterThanOrEqualTo(0));
            Assert.That(liquid.FindPass("DepthNormals"), Is.GreaterThanOrEqualTo(0));
            Assert.That(liquid.HasProperty("_SurfaceReflection"), Is.True);
            Assert.That(profile.SolidSurface, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(profile.SolidSurface.shader), Is.False);
            Assert.That(profile.SolidSurface.FindPass("DepthNormals"), Is.GreaterThanOrEqualTo(0));
        }

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
            string lighting = ReadSharedLighting(source);
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
            StringAssert.Contains("#pragma fragment LiquidProceduralFragment", source);
            StringAssert.Contains("#include \"ElementWaterCore.hlsl\"", lighting);
            StringAssert.Contains("ElementWaterBuildOrthonormalBasis", lighting);
            StringAssert.Contains("return LiquidProceduralFragmentWithPalette(input,LiquidDefaultPalette());", lighting);
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
            string lighting = ReadSharedLighting(shaderSource);
            string rendererSource = File.ReadAllText(rendererPath);

            // 诊断开关必须从 Component 经 MaterialPropertyBlock 到达 Fragment；
            // 只在 Material 上声明但 Draw 时未绑定，会制造“参数怎么调都没变化”的假象。
            StringAssert.Contains("_DebugForceOpaqueFragment", shaderSource);
            // Fragment已提取为共享Lighting。诊断1旁路光照但保留远景visibility，默认palette仍取材质浅水色。
            StringAssert.Contains("if (_DebugForceOpaqueFragment > 0.5f && _DebugForceOpaqueFragment < 1.5f)", lighting);
            StringAssert.Contains("return half4(palette.shallow.rgb, visibility);", lighting);
            StringAssert.Contains("p.shallow=_ShallowColor;", lighting);
            StringAssert.Contains("float visibility=LiquidVisibilityFactor(input.positionWS);", lighting);
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

        private static string ReadSharedLighting(string shaderSource)
        {
            // 同时锁定include接线与被包含文件的语义，不能只在无关文件找到同名代码就算Shader合同成立。
            StringAssert.Contains("#include \"ElementLiquidSurfaceLighting.hlsl\"", shaderSource);
            return File.ReadAllText("Assets/_Project/Art/Elemental/Shaders/ElementLiquidSurfaceLighting.hlsl");
        }
    }
}
