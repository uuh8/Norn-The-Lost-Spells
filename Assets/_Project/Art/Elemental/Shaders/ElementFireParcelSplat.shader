Shader "Game/Elemental/Fire Parcel Splat"
{
    Properties
    {
        [HDR]_CoreColor("Core Color", Color) = (4,1.2,0.05,1)
        [HDR]_EdgeColor("Edge Color", Color) = (1.2,0.04,0.01,1)
        _Softness("Softness", Range(0.01,1)) = 0.35
        _PhaseMode("Phase Mode (0 Surface, 1 Gas)", Float) = 0
        _GasRadiusMultiplier("Gas Radius Multiplier", Range(0.25,2)) = 0.78
        _GasAlphaScale("Gas Alpha Scale", Range(0,1)) = 0.42
        _SurfaceRadiusMultiplier("Surface Splat Radius Multiplier", Range(0.25,2)) = 0.9
        _SurfaceAlphaScale("Surface Splat Alpha Scale", Range(0,1)) = 0.48
        _EdgeIrregularity("Edge Irregularity", Range(0,0.4)) = 0.16
        _VerticalStretch("Vertical Stretch", Range(1,2)) = 1.28
        _DepthFadeDistance("Depth Fade (m)", Float) = 0.2
        _UseLocalSurface("Use Complementary Local Mesh", Float) = 1
        _VisibilityFadeStart("Visibility Fade Start (m)", Float) = 32
        _VisibilityFadeEnd("Visibility Fade End (m)", Float) = 56
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "FireParcelSplat"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "FireParcelAppearance.hlsl"

            struct FireParcelStateGpu
            {
                float3 position; float age;
                float3 velocity; float lifetime;
                float3 sourcePosition; float radius;
                float surfaceWeight; float gasWeight; float heat; float active;
            };
            StructuredBuffer<FireParcelStateGpu> _FireParcelStates;
            half4 _CoreColor;
            half4 _EdgeColor;
            float _Softness;
            float _PhaseMode;
            float _GasRadiusMultiplier;
            float _GasAlphaScale;
            float _SurfaceRadiusMultiplier;
            float _SurfaceAlphaScale;
            float _EdgeIrregularity;
            float _VerticalStretch;
            float3 _SurfaceLodCenter;
            float3 _SurfaceLodHalfSize;
            float _SurfaceLodBlendWidth;
            float _UseLocalSurface, _VisibilityFadeStart, _VisibilityFadeEnd;
            float3 _VisibilityCenter;
            float _CohesionRadius, _VisualTime, _DepthFadeDistance;

            struct Attributes { uint vertexID : SV_VertexID; uint instanceID : SV_InstanceID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : TEXCOORD1;
                float seed : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float eyeDepth : TEXCOORD4;
                float4 parcel : TEXCOORD5; // xyz为体积中心(World Space)，w为统一水平半径。
            };

            Varyings Vert(Attributes input)
            {
                static const float2 corners[6] = {
                    float2(-1,-1), float2(-1,1), float2(1,1),
                    float2(-1,-1), float2(1,1), float2(1,-1)
                };
                FireParcelStateGpu state = _FireParcelStates[input.instanceID];
                float phaseWeight = _PhaseMode < 0.5 ? state.surfaceWeight : state.gasWeight;
                float2 corner = corners[input.vertexID % 6];
                float radius = state.radius * (_PhaseMode < 0.5
                    ? _SurfaceRadiusMultiplier
                    : _GasRadiusMultiplier);
                if (_PhaseMode < 0.5) radius = max(radius, _CohesionRadius * 0.75);
                float3 cameraRight = float3(UNITY_MATRIX_I_V._m00, UNITY_MATRIX_I_V._m10, UNITY_MATRIX_I_V._m20);
                float3 cameraUp = float3(UNITY_MATRIX_I_V._m01, UNITY_MATRIX_I_V._m11, UNITY_MATRIX_I_V._m21);
                float verticalStretch = _VerticalStretch;
                float3 centerWS = _PhaseMode < 0.5 ? lerp(state.sourcePosition, state.position, 0.35) : state.position;
                if (_PhaseMode < 0.5 && _UseLocalSurface > 0.5)
                {
                    float2 margin = _SurfaceLodHalfSize.xz - abs(centerWS.xz - _SurfaceLodCenter.xz);
                    // 整个 Quad 落在完整 Near 内部时互补权重必定为0。提前折叠顶点，
                    // 避免数千个不可见备用 Quad 仍经过 Rasterization/Depth Texture 采样。
                    if (min(margin.x, margin.y) > _SurfaceLodBlendWidth + radius * (1.0 + verticalStretch)) phaseWeight = 0;
                }
                // Quad只负责Pixel Coverage，真正轮廓在Fragment里按3D视线计算。
                // 使用完整Camera Right/Up，正俯视和Roll也不会把World Up投影压成零面积薄条。
                centerWS += float3(0,radius*0.25,0);
                float3 shapeScale = float3(1,verticalStretch,1);
                float2 extent = radius * float2(length(cameraRight * shapeScale), length(cameraUp * shapeScale));
                // 上式是World Up椭球在屏幕两轴的支撑半径。Perspective近景留少量余量，
                // 防止从中心平面射出的边缘视线仍穿过体积、却落在Quad外被裁掉。
                if (IsPerspectiveProjection())
                {
                    float distanceToCenter = distance(GetCameraPositionWS(),centerWS);
                    float ratio = min(radius * verticalStretch / max(distanceToCenter,.001), .95);
                    extent *= rsqrt(max(1.0-ratio*ratio,.05));
                }
                float3 positionWS = centerWS + cameraRight * corner.x * extent.x + cameraUp * corner.y * extent.y;
                Varyings output;
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = corner;
                float alphaScale = _PhaseMode < 0.5 ? _SurfaceAlphaScale : _GasAlphaScale;
                // 同一Parcel的整个Quad共享距离权重；按SourcePosition测距，
                // 避免火苗上升或Quad横跨距离圈时被切出一条可辨认的边界。
                float visibility = 1.0 - smoothstep(_VisibilityFadeStart,
                    max(_VisibilityFadeEnd, _VisibilityFadeStart + 0.001),
                    distance(state.sourcePosition, _VisibilityCenter));
                phaseWeight *= visibility;
                output.color = half4(
                    lerp(_EdgeColor.rgb, _CoreColor.rgb, state.heat),
                    phaseWeight * state.active * alphaScale);
                output.seed = frac((input.instanceID + 1u) * 0.61803398875);
                output.positionWS = positionWS;
                output.eyeDepth = -TransformWorldToView(positionWS).z;
                output.parcel = float4(centerWS,radius);
                // 固定池含未出生、过期或另一阶段的 Slot。退化三角形没有 Pixel Coverage，
                // 既保留 Indirect 固定容量 Contract，也无需 CPU Readback/逐帧重新分配 Draw List。
                if (phaseWeight * state.active <= 0.001) output.positionCS = float4(0,0,0,1);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // 将视线转入单位火舌空间：x/z除半径，y再除纵向拉伸。
                // 最近点t=-dot(p,d)/dot(d,d)给出这条视线离体积中心的最小距离，
                // 因而正面得到原收尖截面，俯视得到饱满横截面，无需Raymarch或交叉多层Quad。
                float3 rayWS = -GetWorldSpaceNormalizeViewDir(input.positionWS);
                float3 inverseScale = rcp(max(input.parcel.w,.001)) * float3(1,rcp(_VerticalStretch),1);
                float3 p = (input.positionWS-input.parcel.xyz) * inverseScale;
                float3 d = rayWS * inverseScale;
                float closestT = -dot(p,d) / max(dot(d,d),.000001);
                float3 closest = p+d*closestT;
                float angle = atan2(input.uv.y, input.uv.x);
                float edgeNoise = sin(angle * 3.0 + input.seed * 6.28318) * 0.62
                    + sin(angle * 5.0 - input.seed * 11.0) * 0.38;
                float irregularRadius = 1.0 + edgeNoise * _EdgeIrregularity;
                // 上半部收尖、根部圆润；无需贴图，保持卡通火舌而不是同样大小的圆斑。
                float3 flamePoint = closest;
                flamePoint.xz /= lerp(1.0,0.32,smoothstep(0.0,1.0,closest.y));
                float radial = length(flamePoint) / max(irregularRadius, 0.55);
                float density = 1.0 - smoothstep(1.0 - _Softness, 1.0, radial);
                if (_PhaseMode < 0.5 && _UseLocalSurface > 0.5)
                {
                    // Liquid-like Splat 只补局部 Marching Surface 外侧；越靠近 Near 内部权重越低。
                    float nearWeight = FireParcelNearWeight(input.positionWS, _SurfaceLodCenter, _SurfaceLodHalfSize, _SurfaceLodBlendWidth);
                    // 现在所有年轻 Parcel 都进入 Density；使用真正互补权重，避免近处再叠一套独立火苗。
                    density *= 1.0 - nearWeight;
                }
                // Quad包围盒角落没有火焰，先丢弃这些Pixel再采样Depth Texture，降低额外Coverage的带宽成本。
                clip(input.color.a * density - 0.001);
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(input.positionCS.xy / _ScaledScreenParams.xy), _ZBufferParams);
                density *= saturate((sceneDepth - input.eyeDepth) / max(_DepthFadeDistance, 0.001));
                clip(input.color.a * density - 0.001);
                float core=saturate(1.0-radial*radial);
                half3 color=FireParcelColor(input.positionWS+rayWS*closestT,core,_VisualTime,_EdgeColor.rgb,_CoreColor.rgb);
                // 普通 Alpha Blend 只在 Alpha 乘 Density；RGB 再乘一次会把软边画成灰黑圆边。
                return half4(color, input.color.a * density);
            }
            ENDHLSL
        }
    }
}
