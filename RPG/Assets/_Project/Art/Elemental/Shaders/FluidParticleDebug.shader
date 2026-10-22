Shader "Game/ElementField/Fluid Particle Debug"
{
    Properties
    {
        _FluidParticleSize("Particle Size", Float) = 0.12
        _FluidParticleColor("Particle Color", Color) = (0.2, 0.7, 1.0, 0.8)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
        }

        Pass
        {
            Name "FluidParticleDebug"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<float4> _FluidPositions;
            float _FluidParticleSize;
            float4 _FluidParticleColor;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 radialUv : TEXCOORD0;
            };

            float2 GetQuadCorner(uint corner)
            {
                // 一个 Slot 固定发出两个三角形（6 vertices）；不需要 Mesh/GameObject per particle。
                if (corner == 0u) return float2(-1.0, -1.0);
                if (corner == 1u) return float2(1.0, -1.0);
                if (corner == 2u) return float2(1.0, 1.0);
                if (corner == 3u) return float2(-1.0, -1.0);
                if (corner == 4u) return float2(1.0, 1.0);
                return float2(-1.0, 1.0);
            }

            Varyings Vert(uint vertexId : SV_VertexID)
            {
                Varyings output;
                uint particleIndex = vertexId / 6u;
                float4 particlePosition = _FluidPositions[particleIndex];
                float2 corner = GetQuadCorner(vertexId % 6u);
                output.radialUv = corner;

                // inactive slot 的 w=0；放到 Clip Space 外而不是让 Fragment 参与透明 Overdraw。
                if (particlePosition.w <= 0.5)
                {
                    output.positionCS = float4(2.0, 2.0, 2.0, 1.0);
                    return output;
                }

                // inverse View Matrix 的前两列是 camera 的 world-space right/up。
                // 使用它们展开 billboard，摄像机旋转时 quad 始终正对 Screen。
                float3 cameraRight = UNITY_MATRIX_I_V._m00_m10_m20;
                float3 cameraUp = UNITY_MATRIX_I_V._m01_m11_m21;
                float3 worldPosition = particlePosition.xyz
                    + (cameraRight * corner.x + cameraUp * corner.y) * _FluidParticleSize;
                output.positionCS = TransformWorldToHClip(worldPosition);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // 圆形 mask；矩形 quad 外的像素直接 discard，视觉上是单粒子圆盘。
                clip(1.0 - dot(input.radialUv, input.radialUv));
                return _FluidParticleColor;
            }
            ENDHLSL
        }
    }
}
