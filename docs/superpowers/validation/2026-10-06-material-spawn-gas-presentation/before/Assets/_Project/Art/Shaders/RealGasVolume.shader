Shader "Game/Materials/RealGasVolume"
{
    Properties
    {
        _SteamColor("Steam Color",Color)=(0.78,0.88,0.94,1)
        _ToxicColor("Toxic Vapor Color",Color)=(0.34,0.75,0.12,1)
        _Extinction("Density Extinction",Range(0.1,20))=6
        _GasDensity("Density Field",3D)=""{}
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Cull Front ZWrite Off ZTest Always Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            TEXTURE3D(_GasDensity); SAMPLER(sampler_GasDensity);
            CBUFFER_START(UnityPerMaterial)
                float4 _SteamColor, _ToxicColor, _GasLayout;
                float _Extinction, _Visibility;
            CBUFFER_END
            struct Attributes { float4 positionOS:POSITION; };
            struct Varyings { float4 positionCS:SV_POSITION; float3 positionOS:TEXCOORD0; };
            Varyings Vert(Attributes a) { Varyings o; o.positionCS=TransformObjectToHClip(a.positionOS.xyz); o.positionOS=a.positionOS.xyz; return o; }
            half4 Frag(Varyings i):SV_Target
            {
                // Ray 位于 Chunk 的 [0,1]^3 Local Space；每步采样真实密度，非固定竖直贴片。
                float3 origin=TransformWorldToObject(GetCameraPositionWS());
                float3 ray=normalize(i.positionOS-origin);
                float3 safeRay=sign(ray+1e-8)*max(abs(ray),1e-5);
                float3 t0=(0-origin)/safeRay, t1=(1-origin)/safeRay;
                float3 nearT=min(t0,t1), farT=max(t0,t1);
                float start=max(0,max(nearT.x,max(nearT.y,nearT.z)));
                float end=min(farT.x,min(farT.y,farT.z));
                if(end<=start) return 0;
                float2 uv=GetNormalizedScreenSpaceUV(i.positionCS);
                float sceneDepth=LinearEyeDepth(SampleSceneDepth(uv),_ZBufferParams);
                float length=(end-start)/16.0; half4 sum=0;
                [unroll] for(int s=0;s<16;s++)
                {
                    float3 p=origin+ray*(start+(s+0.5)*length);
                    float3 world=TransformObjectToWorld(p);
                    // Opaque Depth 截断光线路径，墙后的气体不能透墙显示。
                    if(-TransformWorldToView(world).z>sceneDepth) break;
                    float3 textureUV=(p*_GasLayout.x+1)/_GasLayout.y;
                    float2 density=SAMPLE_TEXTURE3D_LOD(_GasDensity,sampler_GasDensity,textureUV,0).rg;
                    float amount=density.x+density.y;
                    float variation=0.85+0.15*sin(dot(world,float3(2.1,2.8,1.7))+_Time.y*0.45);
                    float alpha=(1-exp(-_Extinction*amount*length*_GasLayout.z*variation))*_Visibility;
                    half3 color=lerp(_SteamColor.rgb,_ToxicColor.rgb,density.y/max(amount,1e-5));
                    sum.rgb+=(1-sum.a)*color*alpha; sum.a+=(1-sum.a)*alpha;
                }
                return sum;
            }
            ENDHLSL
        }
    }
}
