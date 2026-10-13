Shader "Hidden/Norn/ItemPickupGlow"
{
    SubShader
    {
        Tags{"RenderPipeline"="UniversalPipeline"}
        Cull Off ZWrite Off ZTest Always
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        TEXTURE2D_X(_ItemCoverage);TEXTURE2D_X(_ItemHalo);
        float _RadiusPixels,_HaloStrength,_OutlinePixels;half4 _OutlineColor,_HaloColor;
        half4 SilhouetteEdge(Varyings i):SV_Target
        {
            half covered=SAMPLE_TEXTURE2D_X(_BlitTexture,sampler_PointClamp,i.texcoord).a;
            half dilated=covered;
            // Morphological dilation：把可见覆盖向外扩固定像素，再减原覆盖；叶片Alpha和Wind都已包含。
            [unroll]for(int direction=0;direction<8;direction++)
            {
                float angle=direction*.785398163;
                float2 delta=float2(cos(angle),sin(angle))*_OutlinePixels*_BlitTexture_TexelSize.xy;
                dilated=max(dilated,SAMPLE_TEXTURE2D_X(_BlitTexture,sampler_LinearClamp,i.texcoord+delta).a);
                dilated=max(dilated,SAMPLE_TEXTURE2D_X(_BlitTexture,sampler_LinearClamp,i.texcoord+delta*.5).a);
            }
            return half4(_OutlineColor.rgb*saturate(dilated-covered),covered);
        }
        half4 Blur(Varyings i,float2 direction)
        {
            // UV属于[0,1]，像素半径除以纹理尺寸；9点Gaussian核和为1，不随分辨率改变亮度。
            float2 stepUV=direction*_BlitTexture_TexelSize.xy*(_RadiusPixels/4);
            half3 result=0;
            [unroll]for(int tap=-4;tap<=4;tap++)
            {
                float weight=exp(-tap*tap/8.0)/4.898031;
                result+=SAMPLE_TEXTURE2D_X(_BlitTexture,sampler_LinearClamp,i.texcoord+stepUV*tap).rgb*weight;
            }
            return half4(result,0);
        }
        half4 BlurX(Varyings i):SV_Target{return Blur(i,float2(1,0));}
        half4 BlurY(Varyings i):SV_Target{return Blur(i,float2(0,1));}
        half4 Compose(Varyings i):SV_Target
        {
            half4 scene=SAMPLE_TEXTURE2D_X(_BlitTexture,sampler_LinearClamp,i.texcoord);
            // Coverage排除物体内部，草药空洞/透明瓶表面由原AlphaClip决定；无需读取PostProcessing后的Stencil。
            half4 edge=SAMPLE_TEXTURE2D_X(_ItemCoverage,sampler_PointClamp,i.texcoord);
            half coverage=edge.a;
            half3 glow=SAMPLE_TEXTURE2D_X(_ItemHalo,sampler_LinearClamp,i.texcoord).rgb;
            scene.rgb+=(edge.rgb+glow*_HaloColor.rgb*_HaloStrength)*(1-saturate(coverage));return scene;
        }
        ENDHLSL
        Pass{ Name "Gaussian X" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment BlurX
            ENDHLSL }
        Pass{ Name "Gaussian Y" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment BlurY
            ENDHLSL }
        Pass{ Name "Composite" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Compose
            ENDHLSL }
        Pass{ Name "Visible silhouette edge" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment SilhouetteEdge
            ENDHLSL }
    }
}
