Shader "Norn/ItemPickupMask"
{
    Properties
    {
        _BaseMap("Alpha silhouette",2D)="white"{} _AlphaClip("Alpha clip",Float)=0 _Cutoff("Cutoff",Float)=.5
        _PickupGrassWind("Match source grass wind",Float)=0
        _WindGrassSpeed("Wind speed",Float)=1
        _WindGrassAmplitude("Wind amplitude",Float)=1
        _WindGrassFlexibility("Wind flexibility",Float)=1
        _WindGrassScale("Wind scale",Float)=1
        _WindGrassTurbulence("Wind turbulence",Float)=1
    }
    SubShader
    {
        Tags{"RenderPipeline"="UniversalPipeline" "Queue"="Transparent-20" "RenderType"="Transparent"}
        Pass
        {
            Tags{"LightMode"="SRPDefaultUnlit"}
            Cull Off ColorMask 0 ZWrite Off ZTest LEqual
            // 当前Forward/Forward+只占用bit7；不清除其他系统的Stencil。
            Stencil{Ref 128 ReadMask 128 WriteMask 128 Comp Always Pass Replace}
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap);SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;float _AlphaClip;float _Cutoff;
            float _PickupGrassWind;half _WindGrassSpeed,_WindGrassAmplitude,_WindGrassFlexibility,_WindGrassScale,_WindGrassTurbulence;
            CBUFFER_END
            #include "ItemPickupGrassWind.hlsl"
            struct Attributes{float4 positionOS:POSITION;float2 uv:TEXCOORD0;half4 color:COLOR;};
            struct Varyings{float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;};
            Varyings Vert(Attributes a){Varyings o;o.positionCS=TransformObjectToHClip(PickupGrassPosition(a.positionOS.xyz,a.color));o.uv=TRANSFORM_TEX(a.uv,_BaseMap);return o;}
            half4 Frag(Varyings i):SV_Target{if(_AlphaClip>.5)clip(SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).a-_Cutoff);return 0;}
            ENDHLSL
        }
        // 独立RenderGraph捕获轮廓RGB/覆盖Alpha；沿用原UV和Vertex wind。
        Pass
        {
            Tags{"LightMode"="ItemGlowCoverage"}
            Cull Off ColorMask A ZWrite Off ZTest LEqual
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCoverage
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap);SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;float _AlphaClip;float _Cutoff;
            float _PickupGrassWind;half _WindGrassSpeed,_WindGrassAmplitude,_WindGrassFlexibility,_WindGrassScale,_WindGrassTurbulence;
            CBUFFER_END
            #include "ItemPickupGrassWind.hlsl"
            struct Attributes{float4 positionOS:POSITION;float2 uv:TEXCOORD0;half4 color:COLOR;};
            struct Varyings{float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;};
            Varyings Vert(Attributes a){Varyings o;o.positionCS=TransformObjectToHClip(PickupGrassPosition(a.positionOS.xyz,a.color));o.uv=TRANSFORM_TEX(a.uv,_BaseMap);return o;}
            half4 FragCoverage(Varyings i):SV_Target{if(_AlphaClip>.5)clip(SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).a-_Cutoff);return half4(0,0,0,1);}
            ENDHLSL
        }
    }
}
