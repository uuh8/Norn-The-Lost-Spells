Shader "Game/Materials/RealGasVolume"
{
    Properties
    {
        _SteamColor("Steam Color",Color)=(0.78,0.88,0.94,1)
        _ToxicColor("Toxic Vapor Color",Color)=(0.34,0.75,0.12,1)
        _Extinction("Density Extinction",Range(0.1,20))=1.6
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
            float NoiseHash(float3 p)
            {
                p=frac(p*float3(0.1031,0.1030,0.0973));
                p+=dot(p,p.yxz+33.33);
                return frac((p.x+p.y)*p.z);
            }
            float CloudNoise(float3 positionWS)
            {
                // Value Noise 在 World Space 连续，Chunk 共用同一坐标；Hermite 权重
                // f*f*(3-2*f) 使格边界的一阶变化平滑。只有 ALU，无额外 Texture Fetch。
                float3 p=positionWS*2.1+float3(-0.07,0.11,-0.025)*_Time.y;
                float3 cell=floor(p),f=frac(p); f=f*f*(3-2*f);
                float a=lerp(NoiseHash(cell),NoiseHash(cell+float3(1,0,0)),f.x);
                float b=lerp(NoiseHash(cell+float3(0,1,0)),NoiseHash(cell+float3(1,1,0)),f.x);
                float c=lerp(NoiseHash(cell+float3(0,0,1)),NoiseHash(cell+float3(1,0,1)),f.x);
                float d=lerp(NoiseHash(cell+float3(0,1,1)),NoiseHash(cell+float3(1,1,1)),f.x);
                return lerp(lerp(a,b,f.y),lerp(c,d,f.y),f.z);
            }
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
                    // Noise 柔化云团内部密度；真正的移动来自 CPU Advection，不能只移动贴图
                    // 却把中毒位置留在出生点。保留非零下限，避免可接触气体被 Noise 完全抹去。
                    float variation=lerp(0.3,1.25,smoothstep(0.18,0.85,CloudNoise(world)));
                    float alpha=(1-exp(-_Extinction*amount*length*_GasLayout.z*variation))*_Visibility;
                    half3 color=lerp(_SteamColor.rgb,_ToxicColor.rgb,density.y/max(amount,1e-5));
                    sum.rgb+=(1-sum.a)*color*alpha; sum.a+=(1-sum.a)*alpha;
                    if(sum.a>0.97) break;
                }
                return sum;
            }
            ENDHLSL
        }
    }
}
