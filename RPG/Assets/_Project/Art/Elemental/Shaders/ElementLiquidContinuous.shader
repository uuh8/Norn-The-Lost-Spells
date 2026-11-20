Shader "Game/Elemental/Liquid Continuous Field"
{
    Properties
    {
        _ShallowColor("Shallow Color",Color)=(.3,.9,1,1)
        _DeepColor("Deep Color",Color)=(.04,.45,.65,1)
        _FresnelColor("Fresnel Color",Color)=(.8,1,1,1)
        _FoamColor("Foam Color",Color)=(.85,1,1,1)
        _ShallowAlpha("Shallow Alpha",Float)=.7
        _DeepAlpha("Deep Alpha",Float)=.9
        _FresnelAlphaStrength("Fresnel Alpha Strength",Float)=.15
        _WaterAlpha("Water Alpha",Float)=1
        _WaveScaleA("Wave Scale A",Float)=15
        _WaveScaleB("Wave Scale B",Float)=12
        _WaveSpeedA("Wave Speed A",Vector)=(.05,.02,0,0)
        _WaveSpeedB("Wave Speed B",Vector)=(-.03,.04,0,0)
        _WaveNormalStrength("Wave Normal Strength",Float)=.25
        _TriplanarSharpness("Triplanar Sharpness",Float)=4
        _VolumeWaveWorldScale("Volume Wave World Scale",Float)=1
        _DepthFadeDistance("Depth Fade Distance",Float)=1.5
        _DepthNoiseStrength("Depth Noise Strength",Float)=.12
        _FresnelPower("Fresnel Power",Float)=4
        _Smoothness("Smoothness",Float)=.85
        _FoamDistance("Foam Distance",Float)=.25
        _FoamStrength("Foam Strength",Float)=.8
        _FoamAlphaBoost("Foam Alpha Boost",Float)=.2
        _DebugForceOpaqueFragment("Force Opaque Fragment",Float)=0
        _CartoonSwellAmplitude("Cartoon Swell",Float)=.025
        _LiquidRefractionStrength("Refraction UV Offset",Float)=0
        [HideInInspector] _PaletteCount("Palette Count",Int)=0
        [HideInInspector] _PaletteGroupCount("Density Groups",Int)=0
        [HideInInspector] _PaletteGroups0("Groups 0",Vector)=(0,0,0,0)
        [HideInInspector] _PaletteGroups1("Groups 1",Vector)=(0,0,0,0)
    }
    SubShader
    {
        Tags {"RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent"}
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Back
        Pass
        {
            Name "WaterForward"
            Tags {"LightMode"="UniversalForward"}
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex ContinuousVertex
            #pragma fragment ContinuousFragment
            #pragma multi_compile_local _ LIQUID_CPU_REFERENCE LIQUID_SMALL_DROP
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #define ELEMENT_LIQUID_CONTINUOUS_PALETTE
            #include "ElementLiquidSurfaceLighting.hlsl"
            #include "ElementLiquidGroundContact.hlsl"
            #define UNITY_INDIRECT_DRAW_ARGS IndirectDrawIndexedArgs
            #include "UnityIndirect.cginc"
            struct SurfaceVertex {float3 position;float valid;float3 normal;uint weights0;uint3 weights;uint pad;};
            struct SmallDrop {float3 position;float radius;uint4 weights;};
            StructuredBuffer<SurfaceVertex> _FieldVertices;
            StructuredBuffer<SmallDrop> _SmallDrops;
            struct Attributes {float3 position:POSITION;float3 normal:NORMAL;float4 first:TEXCOORD0;float4 second:TEXCOORD1;float4 swell:TEXCOORD2;uint vertex:SV_VertexID;uint instance:SV_InstanceID;};
            struct Varyings {ProceduralVaryings surface;float4 first:TEXCOORD5;float4 second:TEXCOORD6;};
            float2 Unpack(uint p){return float2(f16tof32(p&65535u),f16tof32(p>>16));}
            float4 DropWeights(uint p){return float4(p&255u,p>>8&255u,p>>16&255u,p>>24)/255.0;}
            float4 UnpackFlow(uint p)
            {
                int x=(int)(p>>8&255u),z=(int)(p>>16&255u);if(x>127)x-=256;if(z>127)z-=256;
                return float4((p&255u)/255.0,x*(8.0/127.0),z*(8.0/127.0),(p>>24)/255.0);
            }
            void SelectDropLayer(float height,inout float4 first,inout float4 second)
            {
                float all=dot(first,1)+dot(second,1),bottom=0;
                [loop]for(int g=0;g<_PaletteGroupCount;g++)
                {
                    float amount=0;[unroll]for(int m=0;m<8;m++)if(m<_PaletteCount && (int)(m<4?_PaletteGroups0[m&3]:_PaletteGroups1[m&3])==g)amount+=m<4?first[m&3]:second[m&3];
                    if(amount<=0)continue;float top=bottom+amount/max(.00001,all);
                    if(height<=top || g==_PaletteGroupCount-1)
                    {
                        [unroll]for(int m=0;m<8;m++)
                        {float value=m<4?first[m&3]:second[m&3];float weight=(int)(m<4?_PaletteGroups0[m&3]:_PaletteGroups1[m&3])==g?(value<=0?0:value>=amount?1:value/amount):0;if(m<4)first[m&3]=weight;else second[m&3]=weight;}
                        return;
                    }
                    bottom=top;
                }
            }
            Varyings ContinuousVertex(Attributes a)
            {
                Varyings output=(Varyings)0;UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output.surface);
                float3 p,n;float swellWeight=0;
                #if defined(LIQUID_CPU_REFERENCE)
                p=a.position;n=a.normal;output.first=a.first;output.second=a.second;swellWeight=a.swell.x;output.surface.flowImpact=a.swell.yzw;
                #elif defined(LIQUID_SMALL_DROP)
                SmallDrop drop=_SmallDrops[a.instance];
                float2 velocityXY=Unpack(drop.weights.z),velocityZS=Unpack(drop.weights.w);
                float3 velocity=float3(velocityXY,velocityZS.x);float localHeight;
                if(velocityZS.y<0)
                {
                    // 相同32B实例与同一Draw：负code选择体积一致的接地球冠，不把微量摊成宽膜。
                    float3 offset;GroundContactVertex(a.position,drop.radius,-velocityZS.y,offset,n);
                    p=drop.position+offset;localHeight=offset.y/max(.000001,drop.radius*GroundContactHeightRatio(-velocityZS.y));
                }
                else
                {
                float speed=length(velocity),stretch=clamp(velocityZS.y,1,1.6);
                float3 axis=speed>.0001?velocity/speed:float3(0,1,0);float transverse=rsqrt(stretch);
                // 沿真实飞行方向拉长；两个横轴同时收缩，determinant恒为1。
                // 直接用轴投影表达旋转缩放，避免每滴上传矩阵或垂直/反向LookRotation奇点。
                float3 offset=a.position*transverse+axis*dot(a.position,axis)*(stretch-transverse);
                p=drop.position+offset*drop.radius;
                n=normalize(a.normal/transverse+axis*dot(a.normal,axis)*(1/stretch-1/transverse));
                // 模板共享几何，颜色仍按重底轻顶的密度层选组；不会把两种密度整体混成一滴。
                float verticalRadius=sqrt(transverse*transverse+(stretch*stretch-transverse*transverse)*axis.y*axis.y);
                localHeight=offset.y/verticalRadius*.5+.5;
                }
                output.first=DropWeights(drop.weights.x);output.second=DropWeights(drop.weights.y);output.surface.flowImpact=float3(velocity.xz,0);
                SelectDropLayer(localHeight,output.first,output.second);
                #else
                InitIndirectDrawArgs(0);SurfaceVertex v=_FieldVertices[GetIndirectVertexID(a.vertex)];p=v.position;n=v.normal;
                output.first=float4(Unpack(v.weights0),Unpack(v.weights.x));output.second=float4(Unpack(v.weights.y),Unpack(v.weights.z));
                float4 flowDetail=UnpackFlow(v.pad);swellWeight=flowDetail.x;output.surface.flowImpact=flowDetail.yzw;
                #endif
                // 卡通档只向上抬动朝上的表面，最大3cm；底面保持不动，不让Q弹起伏穿进地板。
                float swellMask=saturate(n.y);swellMask*=swellMask*swellMask*swellWeight;
                ApplyLiquidCartoonSwell(p,n,swellWeight);p.y+=_CartoonSwellAmplitude*swellMask;
                output.surface.positionWS=p;output.surface.normalWS=n;
                output.surface.positionCS=TransformWorldToHClip(p);output.surface.shadowCoord=TransformWorldToShadowCoord(p);
                output.surface.fogFactor=ComputeFogFactor(output.surface.positionCS.z);output.surface.vertexSH=SampleSH(n);return output;
            }
            half4 ContinuousFragment(Varyings input):SV_Target
            {
                #if defined(LIQUID_CPU_REFERENCE)
                // 远页CPU可能保留上一快照；新GPU域优先，逐Fragment裁掉重复部分，避免双壳。
                if(LiquidOwnedDomain(input.surface.positionWS))discard;
                #endif
                LiquidSurfacePalette p=(LiquidSurfacePalette)0;float sum=0;
                [unroll]for(int i=0;i<8;i++)
                {
                    if(i>=_PaletteCount)continue;
                    float w=max(0,i<4?input.first[i&3]:input.second[i&3]);sum+=w;
                    p.shallow+=_PaletteShallow[i]*w;p.deep+=_PaletteDeep[i]*w;p.fresnel+=_PaletteFresnel[i]*w;p.foam+=_PaletteFoam[i]*w;
                    p.alpha+=_PaletteAlpha[i]*w;p.smoothness+=_PaletteSurface[i].x*w;
                }
                if(sum<=.00001)p=LiquidDefaultPalette();else{p.shallow/=sum;p.deep/=sum;p.fresnel/=sum;p.foam/=sum;p.alpha/=sum;p.smoothness/=sum;}
                return LiquidProceduralFragmentWithPalette(input.surface,p);
            }
            ENDHLSL
        }
    }
}
