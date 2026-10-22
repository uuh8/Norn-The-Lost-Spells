Shader "Game/Elemental/Fire Additive"
{
    // 这个 Shader 与 Fire Body 复用 ElementFireCore.hlsl 的轮廓、Noise 和温度公式，
    // 但使用独立的 Additive Render State。它的职责是增加白黄高温亮芯，不负责保持完整外轮廓。
    Properties
    {
        [Header(Color)]
        [HDR] _BottomColor("Core Bottom Color", Color) = (2.4, 1.15, 0.12, 1.0)
        [HDR] _TopColor("Core Top Color", Color) = (1.2, 0.20, 0.015, 1.0)
        _ColorIntensity("Core Intensity", Range(0, 8)) = 1.4
        _GlobalAlpha("Core Alpha", Range(0, 1)) = 0.7
        [Toggle] _UseVertexColor("Use Particle Vertex Color", Float) = 0

        [Header(Temperature)]
        _HeatNoiseWeight("Heat Noise Weight", Range(0, 1)) = 0.5
        _HeatContrast("Heat Contrast", Range(0.1, 5)) = 1.8
        _AlphaNoiseStrength("Alpha Noise Strength", Range(0, 1)) = 0.45

        [Header(Static Shape)]
        // Core 默认比 Body 窄且更早淡出，确保 Additive 只留在主体内部。
        _BaseWidth("Base Half Width", Range(0.05, 1)) = 0.44
        _TipWidth("Tip Half Width", Range(0.001, 0.5)) = 0.025
        _ShapePower("Shape Power", Range(0.1, 5)) = 1.5
        _EdgeSoftness("Edge Softness", Range(0.001, 0.3)) = 0.07
        _BaseFade("Base Fade", Range(0.001, 0.4)) = 0.05
        _TipFadeStart("Tip Fade Start", Range(0.4, 0.999)) = 0.7

        [Header(Flow And Distortion)]
        _NoiseScaleA("Noise Scale A", Range(0.5, 12)) = 3.0
        _NoiseScaleB("Noise Scale B", Range(0.5, 20)) = 7.0
        _UpwardSpeedA("Upward Speed A", Range(0, 2)) = 0.32
        _UpwardSpeedB("Upward Speed B", Range(0, 2)) = 0.55
        _DistortionStrength("Distortion Strength", Range(0, 0.5)) = 0.12
        _DetailStrength("Detail Strength", Range(0, 0.3)) = 0.045
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            // Core 在 Body 之后绘制，方便两个共面实验 Quad 得到稳定的分层顺序。
            "Queue" = "Transparent+10"
        }

        // Additive Blend：Cout = Csrc * SrcAlpha + Cdst。
        // 与 Alpha Blend 不同，目标颜色不会乘 (1-Alpha) 被压暗；重叠越多越亮，因此必须控制数量和强度。
        Blend SrcAlpha One
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            Name "FireAdditiveForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex FireAdditiveVertex
            #pragma fragment FireAdditiveFragment
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ElementFireCore.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BottomColor;
                half4 _TopColor;
                float _ColorIntensity;
                float _GlobalAlpha;
                float _UseVertexColor;
                float _HeatNoiseWeight;
                float _HeatContrast;
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
                // Attributes 来自 Mesh/Particle vertex stream，仍处于 Object Space。
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                // Varyings 由 Vertex Shader 输出，并由 Rasterizer 对三角形内部像素做透视正确插值。
                float2 uv : TEXCOORD0;
                half fogFactor : TEXCOORD1;
                half4 color : COLOR;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings FireAdditiveVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                // Object → World → View → Clip；SV_POSITION 是 Rasterization 必需的裁剪空间坐标。
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 FireAdditiveFragment(Varyings input) : SV_Target
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

                // shapeMask 决定“这个像素属于火焰多少”，combinedNoise 描述内部随时间上升的标量场。
                // 两者来自共享 Core，因此 Body 与 Core 的运动相位一致，不会像两张无关贴图一样滑脱。
                float temperatureMask = ElementFireTemperatureMask(
                    input.uv.y,
                    combinedNoise,
                    _HeatNoiseWeight,
                    _HeatContrast);

                half3 heightColor = lerp(_BottomColor.rgb, _TopColor.rgb, saturate(input.uv.y));
                // Additive 的黑色是“什么都不增加”，因此用 TemperatureMask 调制亮度最自然。
                half heatBrightness = (half)lerp(0.35, 1.0, temperatureMask);
                half4 vertexTint = lerp(
                    half4(1.0h, 1.0h, 1.0h, 1.0h),
                    input.color,
                    (half)saturate(_UseVertexColor));
                half3 finalColor = heightColor * vertexTint.rgb * heatBrightness * (half)_ColorIntensity;

                // 普通 MixFog 会向 Fog Color 插值；对 Additive 而言，这会把雾色额外加到背景上。
                // 所以这里向黑色插值：距离越远，贡献越接近 Additive 的中性元素黑色。
                finalColor = MixFogColor(finalColor, half3(0.0h, 0.0h, 0.0h), input.fogFactor);

                float alphaVariation = lerp(
                    1.0 - saturate(_AlphaNoiseStrength),
                    1.0,
                    combinedNoise);
                half finalAlpha = (half)saturate(
                    shapeMask
                    * _GlobalAlpha
                    * alphaVariation
                    * lerp(0.4, 1.0, temperatureMask)
                    * vertexTint.a);
                return half4(finalColor, finalAlpha);
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
