Shader "Game/Elemental/Fire Parcel Surface"
{
    Properties
    {
        [HDR]_CoreColor("Core",Color)=(4,1.2,0.05,1)
        [HDR]_EdgeColor("Edge",Color)=(1.2,0.04,0.01,1)
        _Alpha("Alpha",Range(0,1))=1
        [HideInInspector]_DepthOnly("Depth Only",Float)=0
        [HideInInspector]_DepthWrite("Depth Write",Float)=0
        [HideInInspector]_ColorWriteMask("Color Write Mask",Float)=15
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }
        Pass
        {
            // 普通 Alpha 合成避免多个 Surface 层把 HDR 能量无上限相加；Bloom 仍由 HDR RGB 驱动。
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_DepthWrite]
            ColorMask [_ColorWriteMask]
            Cull Back
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "FireParcelAppearance.hlsl"
            struct V{float3 p;float p0;float3 n;float p1;}; struct T{V a;V b;V c;};
            StructuredBuffer<T> _FireSurfaceTriangles;
            half4 _CoreColor,_EdgeColor; float _Alpha;
            float3 _SurfaceLodCenter;
            float3 _SurfaceLodHalfSize;
            float _SurfaceLodBlendWidth;
            float _VisualTime;
            float _DepthOnly;
            struct A{uint id:SV_VertexID;}; struct O{float4 cs:SV_POSITION;float3 ws:TEXCOORD0;float3 n:TEXCOORD1;};
            O Vert(A i){uint ti=i.id/3,ci=i.id%3;T t=_FireSurfaceTriangles[ti];V v;if(ci==0)v=t.a;else if(ci==1)v=t.b;else v=t.c;O o;o.ws=FireParcelTipWarp(v.p,v.n,_VisualTime);o.n=v.n;o.cs=TransformWorldToHClip(o.ws);return o;}
            half4 Frag(O i):SV_Target
            {
                float nearWeight = FireParcelNearWeight(i.ws, _SurfaceLodCenter, _SurfaceLodHalfSize, _SurfaceLodBlendWidth);
                if (_DepthOnly > 0.5)
                {
                    // 只在完整近景写入最近外壳深度。颜色 Pass 后续只画最前表面，
                    // 避免凹凸透明壳的三角形绘制顺序留下裂纹；LOD Blend 区仍能正常交叉。
                    clip(nearWeight - 0.999);
                    return 0;
                }
                float3 view=GetWorldSpaceNormalizeViewDir(i.ws);
                float facing = saturate(dot(normalize(i.n), view));
                // 热核心只占正面内侧，顶部与轮廓保持橙红，避免整个主体被 HDR 染成一团纯黄。
                float core = facing * facing * facing * (1.0 - saturate(i.n.y) * 0.45);
                half3 color = FireParcelColor(i.ws, core, _VisualTime, _EdgeColor.rgb, _CoreColor.rgb);
                return half4(color,_Alpha * nearWeight);
            }
            ENDHLSL
        }
    }
}
