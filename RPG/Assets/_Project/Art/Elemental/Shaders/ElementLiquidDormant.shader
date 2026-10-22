Shader "Game/Elemental/Liquid Dormant Cell"
{
    Properties
    {
        _CartoonSwellAmplitude("Cartoon Swell Height (m)",Range(0,.08))=.025
        [Header(Color)]
        _ShallowColor("Shallow Color", Color) = (0.35, 0.85, 0.88, 1)
        _DeepColor("Deep Color", Color) = (0.04, 0.18, 0.65, 1)
        _FresnelColor("Fresnel Color", Color) = (0.72, 1.0, 0.96, 1)

        [Header(Transparency)]
        _ShallowAlpha("Shallow Alpha", Range(0, 1)) = 0.2
        _DeepAlpha("Deep Alpha", Range(0, 1)) = 0.65
        _FresnelAlphaStrength("Fresnel Alpha Strength", Range(0, 0.5)) = 0.15
        _WaterAlpha("Global Water Alpha", Range(0, 1)) = 1.0

        [Header(Waves)]
        _WaveScaleA("Wave Scale A", Float) = 15.0
        _WaveScaleB("Wave Scale B", Float) = 12.0
        _WaveSpeedA("Wave Speed A", Vector) = (0.05, 0.02, 0, 0)
        _WaveSpeedB("Wave Speed B", Vector) = (-0.03, 0.04, 0, 0)
        _WaveNormalStrength("Wave Normal Strength", Range(0, 1)) = 0.25
        _TriplanarSharpness("Triplanar Sharpness", Range(1, 8)) = 4.0
        _VolumeWaveWorldScale("Volume Wave World Scale", Float) = 1.0

        [Header(Depth And Fresnel)]
        _DepthFadeDistance("Depth Fade Distance", Range(0.05, 5)) = 1.5
        _DepthNoiseStrength("Depth Noise Strength", Range(0, 0.5)) = 0.12
        _FresnelPower("Fresnel Power", Range(0.5, 8)) = 4.0
        _Smoothness("Smoothness", Range(0, 1)) = 0.85

        [Header(Foam)]
        _FoamColor("Foam Color", Color) = (0.85, 1.0, 1.0, 1)
        _FoamDistance("Foam Distance", Range(0.01, 1)) = 0.25
        _FoamStrength("Foam Strength", Range(0, 1)) = 0.8
        _FoamAlphaBoost("Foam Alpha Boost", Range(0, 0.5)) = 0.2

        [Header(Development Fragment Isolation)]
        [Toggle] _DebugForceOpaqueFragment("Force Opaque Fragment", Float) = 0
        [HideInInspector] _LiquidMotionHorizon("Motion Horizon", Float) = 0.1
        [HideInInspector] _StableLiquidShape("Stable Quantity Shape", Vector) = (0,0,0,0)
    }



    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Back
        Pass
        {
            Name "WaterForward"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DormantVertex
            #pragma fragment LiquidProceduralFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog


            #define ELEMENT_LIQUID_STABLE_SHAPE
            #include "ElementLiquidSurfaceLighting.hlsl"
            struct DormantAttributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 falling : TEXCOORD1;
                float2 shape : TEXCOORD0;
                float4 shapeGradient : TEXCOORD2;
            };
            ProceduralVaryings DormantVertex(DormantAttributes input)
            {
                ProceduralVaryings output = (ProceduralVaryings)0;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                // Mesh 存 Chunk-local 坐标，Draw 的平移矩阵恢复固定 World Space。
                // Fragment 与 GPU Marching Cubes 共用同一个基于世界法线的正交基。
                if(_StableLiquidShape.w>0.5)
                {
                    // 静水的轮廓/导数仅在拓扑变化时上传。水位改变只改 Uniform；
                    // 圆冠随剩余高度减小，填满粗格时峰值回到实际水位。
                    input.positionOS.y=(_StableLiquidShape.x*(input.shape.x-1)
                        +_StableLiquidShape.y*input.shape.y)*_StableLiquidShape.z;
                    if(input.normalOS.y>0.001)
                    {
                        float2 slope=_StableLiquidShape.x*input.shapeGradient.xy
                            +_StableLiquidShape.y*input.shapeGradient.zw;
                        input.normalOS=normalize(float3(-slope.x,1,-slope.y));
                    }
                }
                output.positionWS = TransformObjectToWorld(input.positionOS);
                // falling=(速度 m/s, 加速度 m/s², 已验证安全距离 m, Tick 时间 s)。
                // 碰撞由 Gameplay 决定；Vertex 只连续显示两 Tick 之间的积分位移。
                float dt = clamp(_Time.y-input.falling.w,0,max(0,_LiquidMotionHorizon));
                float fall = min(input.falling.z,
                    input.falling.x*dt+0.5*input.falling.y*dt*dt);
                output.positionWS.y -= max(0,fall);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                // 不在不同细度的 Mesh 顶点上做正弦位移：长 Quad 与细分边的插值不同，
                // 会露出 Chunk/T-junction 裂缝。起伏光照改为 Fragment 的连续世界法线。
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.shadowCoord = TransformWorldToShadowCoord(output.positionWS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                OUTPUT_SH(output.normalWS, output.vertexSH);
                return output;
            }
            ENDHLSL
        }
    }
}
