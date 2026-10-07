Shader "Game/Elemental/Cartoon Liquid"
{
    Properties
    {
        _CartoonSwellAmplitude("Cartoon Swell Height (m)",Range(0,.08))=.025
        _ShallowColor("Body Color", Color) = (0.12,0.72,0.75,1)
        _DeepColor("Shade Color", Color) = (0.035,0.32,0.40,1)
        _FresnelColor("Rim Color", Color) = (0.38,0.92,0.87,1)
        _CartoonWaveScale("Broad Wave Scale", Range(0.1,8)) = 1.4
        _CartoonWaveStrength("Broad Wave Strength", Range(0,0.3)) = 0.06
        _CartoonGloss("Broad Highlight", Range(8,96)) = 28
        _CartoonRippleLight("Broad Ripple Contrast", Range(0,0.35)) = 0.16
    }
    SubShader
    {
        // 卡通实体外壳使用 Opaque + Depth Write，消除透明分片顺序造成的区域闪烁。
        // 不采样 Scene Depth、不做折射、不为深水逐层画透明面，大片湖面开销接近平面。
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        ZWrite On
        Cull Back
        Pass
        {
            Name "CartoonLiquidForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor, _DeepColor, _FresnelColor;
                float _CartoonWaveScale, _CartoonWaveStrength, _CartoonGloss, _CartoonRippleLight;
                float _CartoonSwellAmplitude;
            CBUFFER_END
            struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half fog : TEXCOORD2;
            };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                // Chunk-local -> World -> Clip：波纹固定在世界坐标，换 Mesh 不重置纹理相位。
                o.positionWS = TransformObjectToWorld(input.positionOS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                float mask=saturate(o.normalWS.y);mask*=mask;
                float a=dot(o.positionWS.xz,float2(1.1,.75))+_Time.y*.8;
                float b=dot(o.positionWS.xz,float2(-.65,1.25))-_Time.y*.62;
                float strength=_CartoonSwellAmplitude*mask;
                o.positionWS.y+=strength*(.65*sin(a)+.35*sin(b));
                float2 slope=strength*(.65*cos(a)*float2(1.1,.75)+.35*cos(b)*float2(-.65,1.25));
                o.normalWS=normalize(o.normalWS-half3(slope.x,0,slope.y));
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
                Light light = GetMainLight();
                half diffuse = saturate(dot(n, light.direction)) * .65 + .35;
                half rim = pow(1 - saturate(dot(n, v)), 3);
                half highlight = pow(saturate(dot(n, SafeNormalize(light.direction + v))), _CartoonGloss);
                // 简化的环境亮带让平静大面也有低频波纹可读性，不依赖某个主光角度。
                // 这是卡通光照表现，不是泡沫/数量数据；禁止以颜色亮带驱动 Gameplay。
                half broadWave = sin(phase.x * .8 + phase.z * .55 + _Time.y * .35)
                    + sin(phase.x * .45 - phase.z * .9 - _Time.y * .28);
                half crest = smoothstep(.25, 1.6, broadWave);
                half3 body = lerp(_DeepColor.rgb, _ShallowColor.rgb, diffuse);
                half3 color = body * (half3(.35,.35,.35) + light.color * .65)
                    + _FresnelColor.rgb * (rim * .22 + highlight * .42 + crest * _CartoonRippleLight);
                return half4(MixFog(color, i.fog), 1);
            }
            ENDHLSL
        }
    }
}
