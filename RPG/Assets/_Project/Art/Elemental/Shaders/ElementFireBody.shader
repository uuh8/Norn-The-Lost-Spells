Shader "Game/Elemental/Fire Body"
{
    // Fire Body 使用 Alpha Blend 保留橙红色外围轮廓。
    // CPU 侧 Material 提供参数；Vertex Shader 把 Quad 顶点从 Object Space 变换到 Clip Space；
    // Rasterizer 在三角形内部插值 UV；Fragment Shader 再用 UV 为每个像素计算颜色与 Alpha。
    // Step 5C 已加入动态 Noise 与温度 Mask，但仍不包含 Particle System 或 Gameplay 逻辑。
    Properties
    {
        [Header(Color)]
        [HDR] _BottomColor("Bottom Color", Color) = (1.0, 0.38, 0.02, 1.0)
        [HDR] _TopColor("Top Color", Color) = (0.85, 0.04, 0.005, 1.0)
        [HDR] _HotColor("Hot Color", Color) = (1.8, 0.72, 0.08, 1.0)
        _ColorIntensity("Color Intensity", Range(0, 5)) = 1.0
        _GlobalAlpha("Global Alpha", Range(0, 1)) = 1.0
        // 普通实验 Quad 保持 0；Particle Material 设为 1，才消费 Particle System 的 COLOR Stream。
        [Toggle] _UseVertexColor("Use Particle Vertex Color", Float) = 0

        [Header(Temperature)]
        _HeatNoiseWeight("Heat Noise Weight", Range(0, 1)) = 0.45
        _HeatContrast("Heat Contrast", Range(0.1, 5)) = 1.35
        _HotColorStrength("Hot Color Strength", Range(0, 1)) = 0.55
        _AlphaNoiseStrength("Alpha Noise Strength", Range(0, 0.75)) = 0.12

        [Header(Static Shape)]
        // Width 使用“到中线的归一化距离”：1 代表从中线一直延伸到 Quad 左/右边界。
        _BaseWidth("Base Half Width", Range(0.05, 1)) = 0.72
        _TipWidth("Tip Half Width", Range(0.001, 0.5)) = 0.06
        _ShapePower("Shape Power", Range(0.1, 5)) = 1.6
        _EdgeSoftness("Edge Softness", Range(0.001, 0.3)) = 0.08
        _BaseFade("Base Fade", Range(0.001, 0.4)) = 0.08
        _TipFadeStart("Tip Fade Start", Range(0.5, 0.999)) = 0.78

        [Header(Flow And Distortion)]
        // Scale 是 UV 空间频率：数值越大，同一个 Quad 中出现的 Noise 格子越多、细节越碎。
        _NoiseScaleA("Noise Scale A", Range(0.5, 12)) = 3.0
        _NoiseScaleB("Noise Scale B", Range(0.5, 20)) = 7.0
        // Speed 单位近似为 UV/秒；Shader 内部让采样坐标向下移动，因此图案看起来向上流动。
        _UpwardSpeedA("Upward Speed A", Range(0, 2)) = 0.32
        _UpwardSpeedB("Upward Speed B", Range(0, 2)) = 0.55
        // Distortion 移动轮廓中线；Detail 改变局部半宽。两者都会乘以 y²，使底部保持稳定。
        _DistortionStrength("Distortion Strength", Range(0, 0.5)) = 0.16
        _DetailStrength("Detail Strength", Range(0, 0.3)) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        // 标准 Alpha Blend：Cout = Csrc * Alpha + Cdst * (1-Alpha)。
        // Fire Body 需要保留橙红轮廓，所以不能像高温核心那样直接使用纯 Additive。
        Blend SrcAlpha OneMinusSrcAlpha

        // 透明物体通常不写入 Depth Buffer，否则较早绘制的火焰片会错误挡住后续透明粒子。
        // 它仍执行 ZTest，因此位于墙后的火焰不会穿墙显示。代价是大量重叠火焰片会产生 Overdraw。
        ZWrite Off
        ZTest LEqual

        // Particle Billboard 应当朝向 Camera，但独立 Quad 的正反面取决于创建方向。
        // Step 5A 使用 Cull Off 让两面都可观察；正式性能 Gate 仍需检查额外 Fragment 成本。
        Cull Off

        Pass
        {
            Name "FireBodyForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FireBodyVertex
            #pragma fragment FireBodyFragment
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ElementFireCore.hlsl"

            // 放入 UnityPerMaterial CBUFFER 可让材质参数符合 SRP Batcher 的布局要求。
            // 同一 Material 的参数在 Draw Call 期间复用，而不是为每个 Fragment 单独传输。
            CBUFFER_START(UnityPerMaterial)
                half4 _BottomColor;
                half4 _TopColor;
                half4 _HotColor;
                float _ColorIntensity;
                float _GlobalAlpha;
                float _UseVertexColor;
                float _HeatNoiseWeight;
                float _HeatContrast;
                float _HotColorStrength;
                float _AlphaNoiseStrength;
                float _BaseWidth;
                float _TipWidth;
                float _ShapePower;
                float _EdgeSoftness;
                float _BaseFade;
                float _TipFadeStart;
                float _NoiseScaleA;
                float _NoiseScaleB;
                float _UpwardSpeedA;
                float _UpwardSpeedB;
                float _DistortionStrength;
                float _DetailStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                half fogFactor : TEXCOORD1;
                half4 color : COLOR;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings FireBodyVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // TransformObjectToHClip 内部完成 Object -> World -> View -> Clip 的矩阵变换。
                // Clip Space Position 交给 Rasterizer 决定该三角形覆盖哪些屏幕像素。
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 FireBodyFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float shapeMask;
                float combinedNoise;
                ElementFireAnimatedShape(
                    input.uv,
                    _Time.y,
                    _BaseWidth,
                    _TipWidth,
                    _ShapePower,
                    _EdgeSoftness,
                    _BaseFade,
                    _TipFadeStart,
                    _NoiseScaleA,
                    _NoiseScaleB,
                    _UpwardSpeedA,
                    _UpwardSpeedB,
                    _DistortionStrength,
                    _DetailStrength,
                    shapeMask,
                    combinedNoise);

                // heightColor 提供稳定的底黄顶红基色；TemperatureMask 再把随时间上升的 Noise
                // 混入“底部更热”的高度梯度。两者都是美术近似，并非真实黑体辐射温度换算。
                half3 heightColor = lerp(_BottomColor.rgb, _TopColor.rgb, saturate(input.uv.y));
                float temperatureMask = ElementFireTemperatureMask(
                    input.uv.y,
                    combinedNoise,
                    _HeatNoiseWeight,
                    _HeatContrast);
                half hotBlend = (half)saturate(temperatureMask * _HotColorStrength);
                half3 temperatureColor = lerp(heightColor, _HotColor.rgb, hotBlend);
                // COLOR Stream 是逐粒子数据；Material Color 是整批 Draw Call 共享数据。
                // 开关为 0 时强制使用白色，保证没有 Vertex Color 的普通 Quad 仍保持现有结果。
                half4 vertexTint = lerp(
                    half4(1.0h, 1.0h, 1.0h, 1.0h),
                    input.color,
                    (half)saturate(_UseVertexColor));
                half3 finalColor = temperatureColor * vertexTint.rgb * (half)_ColorIntensity;
                finalColor = MixFog(finalColor, input.fogFactor);

                // Alpha Noise 只在 [1-Strength,1] 范围内调制，默认不会把 Body 打成许多透明孔洞。
                // Body 的职责是保持整体轮廓；更强的能量分区交给 Additive Core。
                float alphaVariation = lerp(
                    1.0 - saturate(_AlphaNoiseStrength),
                    1.0,
                    combinedNoise);
                half finalAlpha = (half)saturate(
                    shapeMask * _GlobalAlpha * alphaVariation * vertexTint.a);
                return half4(finalColor, finalAlpha);
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
