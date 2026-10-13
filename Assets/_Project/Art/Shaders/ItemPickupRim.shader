Shader "Norn/ItemPickupRim"
{
    Properties
    {
        _Color("Outline color",Color)=(.14,.10,.05,1)
        _OutlinePixels("Outline width in pixels",Float)=1.5
        _CenterOS("Mesh center",Vector)=(0,0,0,0)
        _BaseMap("Alpha silhouette",2D)="white"{}
        _AlphaClip("Alpha clip",Float)=0
        _Cutoff("Cutoff",Float)=.5
        _PickupGrassWind("Match source grass wind",Float)=0
        _WindGrassSpeed("Wind speed",Float)=1
        _WindGrassAmplitude("Wind amplitude",Float)=1
        _WindGrassFlexibility("Wind flexibility",Float)=1
        _WindGrassScale("Wind scale",Float)=1
        _WindGrassTurbulence("Wind turbulence",Float)=1
    }
    SubShader
    {
        // 遮罩先写完，外壳只显示模型覆盖区外的像素，透明瓶内部不染色。
        Tags{"RenderPipeline"="UniversalPipeline" "Queue"="Transparent-10" "RenderType"="Transparent"}
        Pass
        {
            // 清晰边线现在由可见Coverage在屏幕空间生成，Alpha叶片也连续，避免双重叠亮。
            Tags{"LightMode"="ItemGlowLegacyRim"}
            Cull Off Blend One One ZWrite Off ZTest LEqual
            Stencil{Ref 128 ReadMask 128 WriteMask 0 Comp NotEqual Pass Keep}
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap);SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
            half4 _Color;float4 _BaseMap_ST;float4 _CenterOS;
            float _OutlinePixels;float _AlphaClip;float _Cutoff;
            float _PickupGrassWind;half _WindGrassSpeed,_WindGrassAmplitude,_WindGrassFlexibility,_WindGrassScale,_WindGrassTurbulence;
            CBUFFER_END
            #include "ItemPickupGrassWind.hlsl"
            struct Attributes{float4 positionOS:POSITION;float3 normalOS:NORMAL;float2 uv:TEXCOORD0;half4 color:COLOR;};
            struct Varyings{float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;};
            Varyings Vert(Attributes a)
            {
                Varyings o;float4 clipPos=TransformObjectToHClip(PickupGrassPosition(a.positionOS.xyz,a.color));
                float3 normalVS=TransformWorldToViewDir(TransformObjectToWorldNormal(a.normalOS),true);
                float2 direction=mul((float3x3)UNITY_MATRIX_P,normalVS).xy;
                // 正对镜头的平面法线投影为零，用顶点相对模型中心方向补偿，不产生NaN。
                if(dot(direction,direction)<1e-6){float4 center=TransformObjectToHClip(_CenterOS.xyz);direction=clipPos.xy/max(abs(clipPos.w),1e-5)-center.xy/max(abs(center.w),1e-5);}
                float2 pixelDirection=direction*_ScaledScreenParams.xy;
                pixelDirection*=rsqrt(max(dot(pixelDirection,pixelDirection),1e-6));
                // NDC范围[-1,1]，乘clip.w才能在Perspective divide后保持像素宽度。
                clipPos.xy+=pixelDirection*(_OutlinePixels*2/_ScaledScreenParams.xy)*clipPos.w;
                o.positionCS=clipPos;o.uv=TRANSFORM_TEX(a.uv,_BaseMap);return o;
            }
            half4 Frag(Varyings i):SV_Target
            {if(_AlphaClip>.5)clip(SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).a-_Cutoff);return half4(_Color.rgb,0);}
            ENDHLSL
        }
        // 独立RenderGraph捕获轮廓RGB/覆盖Alpha；沿用原UV和Vertex wind。
        Pass
        {
            Tags{"LightMode"="ItemGlowSource"}
            Cull Off Blend One One ZWrite Off ZTest LEqual
            Stencil{Ref 128 ReadMask 128 WriteMask 0 Comp NotEqual Pass Keep}
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragGlow
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap);SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
            half4 _Color;float4 _BaseMap_ST;float4 _CenterOS;
            float _OutlinePixels;float _AlphaClip;float _Cutoff;
            float _PickupGrassWind;half _WindGrassSpeed,_WindGrassAmplitude,_WindGrassFlexibility,_WindGrassScale,_WindGrassTurbulence;
            CBUFFER_END
            #include "ItemPickupGrassWind.hlsl"
            struct Attributes{float4 positionOS:POSITION;float3 normalOS:NORMAL;float2 uv:TEXCOORD0;half4 color:COLOR;};
            struct Varyings{float4 positionCS:SV_POSITION;float2 uv:TEXCOORD0;};
            Varyings Vert(Attributes a)
            {
                Varyings o;float4 clipPos=TransformObjectToHClip(PickupGrassPosition(a.positionOS.xyz,a.color));
                float3 normalVS=TransformWorldToViewDir(TransformObjectToWorldNormal(a.normalOS),true);
                float2 direction=mul((float3x3)UNITY_MATRIX_P,normalVS).xy;
                // 正对镜头的平面法线投影为零，用顶点相对模型中心方向补偿，不产生NaN。
                if(dot(direction,direction)<1e-6){float4 center=TransformObjectToHClip(_CenterOS.xyz);direction=clipPos.xy/max(abs(clipPos.w),1e-5)-center.xy/max(abs(center.w),1e-5);}
                float2 pixelDirection=direction*_ScaledScreenParams.xy;
                pixelDirection*=rsqrt(max(dot(pixelDirection,pixelDirection),1e-6));
                // NDC范围[-1,1]，乘clip.w才能在Perspective divide后保持像素宽度。
                clipPos.xy+=pixelDirection*(_OutlinePixels*2/_ScaledScreenParams.xy)*clipPos.w;
                o.positionCS=clipPos;o.uv=TRANSFORM_TEX(a.uv,_BaseMap);return o;
            }
            half4 FragGlow(Varyings i):SV_Target
            {if(_AlphaClip>.5)clip(SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,i.uv).a-_Cutoff);return half4(_Color.rgb,0);}
            ENDHLSL
        }
    }
}
