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
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
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
                Light light=GetMainLight(TransformWorldToShadowCoord(i.positionWS), i.positionWS, half4(1,1,1,1));
                AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(i.positionCS));
                float direct = light.shadowAttenuation * light.distanceAttenuation * ao.directAmbientOcclusion;
                float diffuse=saturate(dot(normal,light.direction));
                float gloss=lerp(_Smoothness,0.22,residue);
                // Sand 的稳定材料 ID 为 11。近看时提供细砂的漫反射差异，远处按像素覆盖
                // 衰减细节，避免大沙坡移动时闪烁。采样世界坐标使压缩/唤醒和跨页都保持连续。
                if (abs(i.id - 11.0) < .5)
                {
                    float3 grainPosition = i.positionWS * 38;
                    float3 footprint = fwidth(grainPosition);
                    float visibility = saturate(1 - max(footprint.x, max(footprint.y, footprint.z)));
                    float3 grain = frac(floor(grainPosition) * float3(.1031,.1030,.0973));
                    grain += dot(grain, grain.yzx + 33.33);
                    color *= 1.0 + (frac((grain.x + grain.y) * grain.z) - .5) * .22 * visibility;
                    gloss = _Smoothness;
                }
                float highlight=pow(saturate(dot(normal,normalize(view+light.direction))),lerp(12,100,gloss));
                half3 ambient = max(SampleSH(normal), half3(.12,.12,.12)) * ao.indirectAmbientOcclusion;
                half3 lit=color*(ambient+light.color*(direct*diffuse));
                lit+=light.color*direct*highlight*lerp(0.75,0.12,residue);
                lit+=_IceColor.rgb*pow(1-saturate(dot(normal,view)),3)*0.12*(1-residue);
                return half4(lit,1);
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        UsePass "Universal Render Pipeline/Lit/DepthNormals"
    }
}
