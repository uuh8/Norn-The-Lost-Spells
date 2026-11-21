Shader "Game/Elemental/Cartoon Liquid"
{
    Properties
    {
        _CartoonSwellAmplitude("Cartoon Swell Height (m)",Range(0,.08))=.025
        [HideInInspector] _CellularMotionClock("Cellular Motion Clock", Float) = 0
        [HideInInspector] _CellularMotionRate("Cellular Motion Rate", Float) = 0
        _ShallowColor("Body Color", Color) = (0.12,0.72,0.75,1)
        _DeepColor("Shade Color", Color) = (0.035,0.32,0.40,1)
        _FresnelColor("Rim Color", Color) = (0.38,0.92,0.87,1)
        _CartoonWaveScale("Broad Wave Scale", Range(0.1,8)) = 1.4
        _CartoonWaveStrength("Broad Wave Strength", Range(0,0.3)) = 0.06
        _CartoonGloss("Broad Highlight", Range(8,96)) = 28
        _CartoonRippleLight("Broad Ripple Contrast", Range(0,0.35)) = 0.16
        _SurfaceReflection("Environment Reflection", Range(0,1)) = 0.18
        [HideInInspector] _UseCellularDepth("Cellular Surface Depth", Float) = 0
    }
    SubShader
    {
        // 卡通实体外壳使用 Opaque + Depth Write，消除透明分片顺序造成的区域闪烁。
        // 不采样 Scene Depth、不做折射、不为深水逐层画透明面，大片湖面开销接近平面。
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        ZWrite On
        Cull Back
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        CBUFFER_START(UnityPerMaterial)
            half4 _ShallowColor, _DeepColor, _FresnelColor;
            float _CartoonWaveScale, _CartoonWaveStrength, _CartoonGloss, _CartoonRippleLight;
            float _CartoonSwellAmplitude, _CellularMotionClock, _CellularMotionRate, _SurfaceReflection;
            float _UseCellularDepth;
        CBUFFER_END
        struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; half4 material : COLOR; float4 arrival : TEXCOORD3; };

        // 各 Pass 使用相同的世界位置。移动中的水滴在颜色、相机深度和灯光深度上必须位于同一处；
        // 正式 Cellular 的 swell 为零，平静水位和侧壁始终落在提交几何上。
        void SurfaceVertex(Attributes input, out float3 positionWS, out float3 normalWS)
        {
            float remaining = saturate((input.arrival.w - _CellularMotionClock) * _CellularMotionRate);
            positionWS = TransformObjectToWorld(input.positionOS + input.arrival.xyz * remaining);
            normalWS = TransformObjectToWorldNormal(input.normalOS);
            if (_CartoonSwellAmplitude > 0)
            {
                float mask = saturate(normalWS.y);
                float strength = _CartoonSwellAmplitude * mask * mask;
                float a = dot(positionWS.xz, float2(1.1, .75)) + _Time.y * .8;
                float b = dot(positionWS.xz, float2(-.65, 1.25)) - _Time.y * .62;
                positionWS.y += strength * (.65 * sin(a) + .35 * sin(b));
                float2 slope = strength * (.65 * cos(a) * float2(1.1,.75) + .35 * cos(b) * float2(-.65,1.25));
                normalWS = normalize(normalWS - float3(slope.x, 0, slope.y));
            }
        }
        half DepthFrag(float4 positionCS : SV_POSITION) : SV_Target { return positionCS.z; }
        ENDHLSL
        Pass
        {
            Name "CartoonLiquidForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half fog : TEXCOORD2;
                half depth : TEXCOORD3;
            };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                float3 normalWS;
                SurfaceVertex(input, o.positionWS, normalWS);
                o.normalWS = normalWS;
                o.depth = input.material.g;
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                half3 n = normalize(i.normalWS);
                float3 phase = i.positionWS * _CartoonWaveScale;
                // 仅扰动光照法线，不挪动 Vertex；两条低频波避免细碎噪声和地板穿插。
                half3 wave = half3(cos(phase.z + _Time.y * .65),
                    sin(phase.x - _Time.y * .45) * .3, sin(phase.x + phase.y - _Time.y * .55));
                n = normalize(n + wave * _CartoonWaveStrength);
                half3 v = SafeNormalize(GetWorldSpaceViewDir(i.positionWS));
                Light light = GetMainLight(TransformWorldToShadowCoord(i.positionWS), i.positionWS, half4(1,1,1,1));
                half diffuse = saturate(dot(n, light.direction));
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(i.positionCS));
                half direct = light.distanceAttenuation * light.shadowAttenuation * ao.directAmbientOcclusion;
                half rim = pow(1 - saturate(dot(n, v)), 3);
                half highlight = pow(saturate(dot(n, SafeNormalize(light.direction + v))), _CartoonGloss);
                // 简化的环境亮带让平静大面也有低频波纹可读性，不依赖某个主光角度。
                // 这是卡通光照表现，不是泡沫/数量数据；禁止以颜色亮带驱动 Gameplay。
                half broadWave = sin(phase.x * .8 + phase.z * .55 + _Time.y * .35)
                    + sin(phase.x * .45 - phase.z * .9 - _Time.y * .28);
                half crest = smoothstep(.25, 1.6, broadWave);
                half3 body = lerp(_DeepColor.rgb, _ShallowColor.rgb, diffuse);
                half thin = _UseCellularDepth * (1 - smoothstep(.015, .55, i.depth));
                body = lerp(body, lerp(_ShallowColor.rgb, _FresnelColor.rgb, .5), thin * .62);
                half3 ambient = max(SampleSH(n), half3(.12,.12,.12)) * ao.indirectAmbientOcclusion;
                half3 color = body * (ambient + light.color * (direct * diffuse * .75));
                // 水面与落水的侧壁反射不同方向的天空。每个可见片元读取一次当前环境探针，
                // 不抓屏、不逐层绘制水体；水面下方的洞和地面上的阴影来自同一份真实表面网格。
                half roughness = rsqrt(max(_CartoonGloss, 1.0)) * 1.5;
                half3 reflected = GlossyEnvironmentReflection(reflect(-v, n), roughness, ao.indirectAmbientOcclusion);
                color = lerp(color, reflected, _SurfaceReflection * (.18 + .82 * rim));
                color += _FresnelColor.rgb * (rim * .12 + direct * (highlight * .55 + crest * _CartoonRippleLight));
                color += _FresnelColor.rgb * thin * (.045 + .10 * rim) * ao.indirectAmbientOcclusion;
                return half4(MixFog(color, i.fog), 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment DepthFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 _LightDirection, _LightPosition;
            float4 ShadowVert(Attributes input) : SV_POSITION
            {
                float3 positionWS, normalWS;
                SurfaceVertex(input, positionWS, normalWS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirection = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirection = _LightDirection;
                #endif
                return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirection)));
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            float4 DepthVert(Attributes input) : SV_POSITION
            {
                float3 positionWS, normalWS;
                SurfaceVertex(input, positionWS, normalWS);
                return TransformWorldToHClip(positionWS);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex NormalVert
            #pragma fragment NormalFrag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            struct NormalVaryings { float4 positionCS : SV_POSITION; half3 normalWS : TEXCOORD0; };
            NormalVaryings NormalVert(Attributes input)
            {
                NormalVaryings o;
                float3 positionWS, normalWS;
                SurfaceVertex(input, positionWS, normalWS);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.normalWS = normalWS;
                return o;
            }
            half4 NormalFrag(NormalVaryings i) : SV_Target
            {
                // PC Renderer 的 SSAO 读取 DepthNormals。只写几何的真实法线，
                // Forward Pass 的微波光斑不应被识别成地面和岸线上的微小凸起。
                float3 normalWS = normalize(i.normalWS);
                #if defined(_GBUFFER_NORMALS_OCT)
                    return half4(PackFloat2To888(saturate(PackNormalOctQuadEncode(normalWS) * .5 + .5)), 0);
                #else
                    return half4(normalWS, 0);
                #endif
            }
            ENDHLSL
        }
    }
}
