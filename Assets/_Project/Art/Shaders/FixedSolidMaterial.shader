Shader "Game/Materials/FixedSolid"
{
    Properties
    {
        _IceColor("Ice Color", Color) = (0.35,0.8,0.95,1)
        _ResidueColor("Dried Slime Color", Color) = (0.62,0.52,0.18,1)
        _Smoothness("Ice Smoothness", Range(0,1)) = 0.8
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _IceColor, _ResidueColor;
                float _Smoothness;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float4 id:COLOR; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; float id:TEXCOORD2; };
            Varyings Vert(Attributes a)
            {
                Varyings o; o.positionWS=TransformObjectToWorld(a.positionOS.xyz);
                o.positionCS=TransformWorldToHClip(o.positionWS); o.normalWS=TransformObjectToWorldNormal(a.normalOS);
                o.id=a.id.r*255.0; return o;
            }
            half4 Frag(Varyings i):SV_Target
            {
                // 材料 ID 决定读哪组视觉参数；Shader 不修改几何高度或碰撞。
                float residue=step(5.5,i.id); half3 color=lerp(_IceColor.rgb,_ResidueColor.rgb,residue);
                float3 normal=normalize(i.normalWS), view=GetWorldSpaceNormalizeViewDir(i.positionWS);
                Light light=GetMainLight(); float diffuse=saturate(dot(normal,light.direction));
                float gloss=lerp(_Smoothness,0.22,residue);
                float highlight=pow(saturate(dot(normal,normalize(view+light.direction))),lerp(12,100,gloss));
                half3 lit=color*(SampleSH(normal)+light.color*(0.25+0.75*diffuse));
                lit+=light.color*highlight*lerp(0.75,0.12,residue);
                lit+=_IceColor.rgb*pow(1-saturate(dot(normal,view)),3)*0.12*(1-residue);
                return half4(lit,1);
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }
}
