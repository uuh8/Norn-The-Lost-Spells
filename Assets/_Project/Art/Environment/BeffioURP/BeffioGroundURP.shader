Shader "Game/Environment/BeffioGroundURP"
{
Properties
	{
		_ColorshiftBlackChannel("Color shift(Black Channel)", Color) = (1,1,1,0)
		_ColorshiftRedChannel("Color shift(Red Channel)", Color) = (1,1,1,0)
		_ColorshiftGreenChannel0("Color shift(Green Channel) 0", Color) = (1,1,1,0)
		_ColorshiftAlphaChannel("Color shift (Alpha Channel)", Color) = (1,1,1,0)
		_SmoothnessshiftBlackchannel("Smoothness shift (Black channel)", Range( 0 , 4)) = 1
		_SmoothnessshiftRedChannel("Smoothness shift (Red Channel)", Range( 0 , 4)) = 1
		_SmoothnessshiftGreenChannel("Smoothness shift (Green Channel)", Range( 0 , 4)) = 1
		_SmoothnessshiftAlphaChannel("Smoothness shift (Alpha Channel)", Range( 0 , 4)) = 1
		_Wetness_color("Wetness_color", Color) = (1,1,1,0)
		_TilingBlack("Tiling Black", Range( 1 , 100)) = 10
		_TilingRed("Tiling Red", Range( 1 , 100)) = 10
		_TilingGreen("Tiling Green", Range( 1 , 100)) = 10
		_TilingAlpha("Tiling Alpha", Range( 1 , 100)) = 10
		_ParallaxStrength("Parallax Strength", Range( 0 , 0.2)) = 0
		_BlendingstrengthRed("Blending strength (Red)", Range( 0 , 10)) = 5
		_BlendingStrengthGreen("Blending Strength (Green)", Range( 0 , 10)) = 5
		_BlendingStrengthAlpha("Blending Strength (Alpha)", Range( 0 , 10)) = 5
		_NormalmapstrengthBlack("Normal map strength (Black)", Range( 0 , 10)) = 1
		_NormalmapstrengthRed("Normal map strength (Red)", Range( 0 , 10)) = 1
		_NormalmapstrengthGreen("Normal map strength (Green)", Range( 0 , 10)) = 1
		_NormalmapstrengthAlpha("Normal map strength (Alpha)", Range( 0 , 10)) = 1
		_DiffuseBlackChannel("Diffuse (Black Channel)", 2D) = "white" {}
		_NormalBlackChannel("Normal (Black Channel)", 2D) = "bump" {}
		_HeightBlackChannel("Height (Black Channel)", 2D) = "white" {}
		_DiffuseRedChannel("Diffuse (Red Channel)", 2D) = "white" {}
		_NormalRedChannel("Normal (Red Channel)", 2D) = "bump" {}
		_HeightRedChannel("Height (Red Channel)", 2D) = "white" {}
		_DiffuseGreenChannel("Diffuse (Green Channel)", 2D) = "white" {}
		_NormalGreenChannel("Normal (Green Channel)", 2D) = "bump" {}
		_HeightGreenchannel("Height (Green channel)", 2D) = "white" {}
		_DiffuseAlphaChannel("Diffuse (Alpha Channel)", 2D) = "white" {}
		_NormalAlphaChannel("Normal (Alpha Channel)", 2D) = "bump" {}
		_HeightAlphachannel("Height (Alpha channel)", 2D) = "white" {}
		[HideInInspector] _texcoord( "", 2D ) = "white" {}
		[HideInInspector] __dirty( "", Int ) = 1
	}
SubShader
{
    Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
    Cull Back
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceData.hlsl"

    // 所有Pass保持相同UnityPerMaterial布局，避免换Pass时SRP Batcher失配。
    CBUFFER_START(UnityPerMaterial)
        float4 _ColorshiftBlackChannel, _ColorshiftRedChannel, _ColorshiftGreenChannel0, _ColorshiftAlphaChannel, _Wetness_color;
        float _TilingBlack, _TilingRed, _TilingGreen, _TilingAlpha, _ParallaxStrength;
        float _NormalmapstrengthBlack, _NormalmapstrengthRed, _NormalmapstrengthGreen, _NormalmapstrengthAlpha;
        float _BlendingstrengthRed, _BlendingStrengthGreen, _BlendingStrengthAlpha;
        float _SmoothnessshiftBlackchannel, _SmoothnessshiftRedChannel, _SmoothnessshiftGreenChannel, _SmoothnessshiftAlphaChannel;
    CBUFFER_END
    // 十二张地面纹理共享Repeat采样器，给URP阴影/反射贴图留Sampler槽；不把RGBA贴图重打包。
    TEXTURE2D(_DiffuseBlackChannel);
TEXTURE2D(_NormalBlackChannel);
TEXTURE2D(_HeightBlackChannel);
TEXTURE2D(_DiffuseRedChannel);
TEXTURE2D(_NormalRedChannel);
TEXTURE2D(_HeightRedChannel);
TEXTURE2D(_DiffuseGreenChannel);
TEXTURE2D(_NormalGreenChannel);
TEXTURE2D(_HeightGreenchannel);
TEXTURE2D(_DiffuseAlphaChannel);
TEXTURE2D(_NormalAlphaChannel);
TEXTURE2D(_HeightAlphachannel);

    // Core.hlsl已提供sampler_LinearRepeat；重复声明会在D3D Shader编译阶段报redefinition。

    struct Attributes
    {
        float4 positionOS:POSITION; float3 normalOS:NORMAL; float4 tangentOS:TANGENT;
        float2 uv:TEXCOORD0; float2 lightmapUV:TEXCOORD1; float4 color:COLOR;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };
    struct Varyings
    {
        float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; half3 normalWS:TEXCOORD1;
        half4 tangentWS:TEXCOORD2; float2 uv:TEXCOORD3; half4 color:TEXCOORD4;
        half4 fogAndVertexLight:TEXCOORD5; float4 shadowCoord:TEXCOORD6;
        DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 7);
        UNITY_VERTEX_INPUT_INSTANCE_ID
        UNITY_VERTEX_OUTPUT_STEREO
    };
    Varyings Vert(Attributes a)
    {
        Varyings o=(Varyings)0; UNITY_SETUP_INSTANCE_ID(a); UNITY_TRANSFER_INSTANCE_ID(a,o); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
        // Object坐标属于模型；World用于光照/视线；Clip用于Rasterization，三者不能混用。
        VertexPositionInputs p=GetVertexPositionInputs(a.positionOS.xyz);
        VertexNormalInputs n=GetVertexNormalInputs(a.normalOS,a.tangentOS);
        o.positionCS=p.positionCS; o.positionWS=p.positionWS; o.normalWS=n.normalWS;
        o.tangentWS=half4(n.tangentWS,a.tangentOS.w*GetOddNegativeScale());
        o.uv=a.uv; o.color=a.color;
        o.fogAndVertexLight=half4(ComputeFogFactor(p.positionCS.z),VertexLighting(p.positionWS,n.normalWS));
        o.shadowCoord=GetShadowCoord(p);
        OUTPUT_LIGHTMAP_UV(a.lightmapUV,unity_LightmapST,o.lightmapUV);
        OUTPUT_SH(n.normalWS,o.vertexSH);
        return o;
    }
    void SampleBlack(float2 uv0, float3 viewWS, out half4 color, out half3 normalTS, out half height)
{
    float2 tiled = uv0 * _TilingBlack;
    height = SAMPLE_TEXTURE2D(_HeightBlackChannel, sampler_LinearRepeat, tiled).r;
    // 兼容原资源的世界方向XY偏移；原材质Parallax=0。它不是正确的Tangent-space POM，不扩展为新视差算法。
    float2 uv = tiled + (height - 1) * viewWS.xy * _ParallaxStrength;
    color = SAMPLE_TEXTURE2D(_DiffuseBlackChannel, sampler_LinearRepeat, uv); color.rgb *= _ColorshiftBlackChannel.rgb;
    normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalBlackChannel, sampler_LinearRepeat, uv), _NormalmapstrengthBlack);
}
void SampleRed(float2 uv0, float3 viewWS, out half4 color, out half3 normalTS, out half height)
{
    float2 tiled = uv0 * _TilingRed;
    height = SAMPLE_TEXTURE2D(_HeightRedChannel, sampler_LinearRepeat, tiled).r;
    // 兼容原资源的世界方向XY偏移；原材质Parallax=0。它不是正确的Tangent-space POM，不扩展为新视差算法。
    float2 uv = tiled + (height - 1) * viewWS.xy * _ParallaxStrength;
    color = SAMPLE_TEXTURE2D(_DiffuseRedChannel, sampler_LinearRepeat, uv); color.rgb *= _ColorshiftRedChannel.rgb;
    normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalRedChannel, sampler_LinearRepeat, uv), _NormalmapstrengthRed);
}
void SampleGreen(float2 uv0, float3 viewWS, out half4 color, out half3 normalTS, out half height)
{
    float2 tiled = uv0 * _TilingGreen;
    height = SAMPLE_TEXTURE2D(_HeightGreenchannel, sampler_LinearRepeat, tiled).r;
    // 兼容原资源的世界方向XY偏移；原材质Parallax=0。它不是正确的Tangent-space POM，不扩展为新视差算法。
    float2 uv = tiled + (height - 1) * viewWS.xy * _ParallaxStrength;
    color = SAMPLE_TEXTURE2D(_DiffuseGreenChannel, sampler_LinearRepeat, uv); color.rgb *= _ColorshiftGreenChannel0.rgb;
    normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalGreenChannel, sampler_LinearRepeat, uv), _NormalmapstrengthGreen);
}
void SampleAlpha(float2 uv0, float3 viewWS, out half4 color, out half3 normalTS, out half height)
{
    float2 tiled = uv0 * _TilingAlpha;
    height = SAMPLE_TEXTURE2D(_HeightAlphachannel, sampler_LinearRepeat, tiled).r;
    // 兼容原资源的世界方向XY偏移；原材质Parallax=0。它不是正确的Tangent-space POM，不扩展为新视差算法。
    float2 uv = tiled + (height - 1) * viewWS.xy * _ParallaxStrength;
    color = SAMPLE_TEXTURE2D(_DiffuseAlphaChannel, sampler_LinearRepeat, uv); color.rgb *= _ColorshiftAlphaChannel.rgb;
    normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalAlphaChannel, sampler_LinearRepeat, uv), _NormalmapstrengthAlpha);
}

    half BlendMask(half height, half painted, float strength)
    {
        // 顶点Red/Green/Alpha控制三层依次覆盖Black基底；保留原Height边界和覆盖顺序。
        // 不能归一化成四权重之和：原资产是逐层lerp，Blue另用于湿亮程度。
        return saturate(pow(max(0,height*painted*4+painted*2),max(0,strength)));
    }
    void Surface(Varyings i, out SurfaceData s, out half3 normalWS)
    {
        float3 viewWS=GetWorldSpaceNormalizeViewDir(i.positionWS);
        half4 c0,c1,c2,c3; half3 n0,n1,n2,n3; half h0,h1,h2,h3;
        SampleBlack(i.uv,viewWS,c0,n0,h0); SampleRed(i.uv,viewWS,c1,n1,h1);
        SampleGreen(i.uv,viewWS,c2,n2,h2); SampleAlpha(i.uv,viewWS,c3,n3,h3);
        half r=BlendMask(h1,i.color.r,_BlendingstrengthRed);
        half g=BlendMask(h2,i.color.g,_BlendingStrengthGreen);
        half a=BlendMask(h3,i.color.a,_BlendingStrengthAlpha);
        half4 color=lerp(lerp(lerp(c0,c1,r),c2,g),c3,a);
        color.rgb=lerp(color.rgb,color.rgb*_Wetness_color.rgb,i.color.g);
        half3 normalTS=normalize(lerp(lerp(lerp(n0,n1,r),n2,g),n3,a));
        half3 normal=normalize(i.normalWS),tangent=normalize(i.tangentWS.xyz);
        half3 bitangent=cross(normal,tangent)*i.tangentWS.w;
        normalWS=normalize(TransformTangentToWorld(normalTS,half3x3(tangent,bitangent,normal)));
        s=(SurfaceData)0;s.albedo=color.rgb;s.normalTS=normalTS;s.alpha=1;s.occlusion=1;s.specular=half3(.04,.04,.04);
        // Diffuse的Alpha是光滑度而非透明度；地面仍是Opaque。
        half smooth=lerp(lerp(lerp(c0.a*_SmoothnessshiftBlackchannel,c1.a*_SmoothnessshiftRedChannel,r),c2.a*_SmoothnessshiftGreenChannel,g),c3.a*_SmoothnessshiftAlphaChannel,a);
        s.smoothness=saturate(smooth*lerp(1,4,i.color.b));
    }
    half4 Frag(Varyings i):SV_Target
    {
        UNITY_SETUP_INSTANCE_ID(i);UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
        SurfaceData s;half3 normalWS;Surface(i,s,normalWS);
        InputData input=(InputData)0;input.positionWS=i.positionWS;input.normalWS=normalWS;
        input.viewDirectionWS=GetWorldSpaceNormalizeViewDir(i.positionWS);input.shadowCoord=i.shadowCoord;
        input.fogCoord=i.fogAndVertexLight.x;input.vertexLighting=i.fogAndVertexLight.yzw;
        input.bakedGI=SAMPLE_GI(i.lightmapUV,i.vertexSH,normalWS);
        input.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(i.positionCS);input.shadowMask=SAMPLE_SHADOWMASK(i.lightmapUV);
        half4 lit=UniversalFragmentPBR(input,s);lit.rgb=MixFog(lit.rgb,input.fogCoord);return lit;
    }
    float3 _LightDirection, _LightPosition;
    Varyings ShadowVert(Attributes a)
    {
        Varyings o=(Varyings)0;UNITY_SETUP_INSTANCE_ID(a);UNITY_TRANSFER_INSTANCE_ID(a,o);UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
        float3 p=TransformObjectToWorld(a.positionOS.xyz),n=TransformObjectToWorldNormal(a.normalOS);
        #if _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 direction=normalize(_LightPosition-p);
        #else
            float3 direction=_LightDirection;
        #endif
        // 阴影Bias仅影响光源的深度图，不移动可见地形或Collider。
        o.positionCS=ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(p,n,direction)));return o;
    }
    half4 DepthFrag(Varyings i):SV_Target{return 0;}
    half4 NormalFrag(Varyings i):SV_Target
    {
        SurfaceData s;half3 n;Surface(i,s,n);
        #if defined(_GBUFFER_NORMALS_OCT)
            float2 oct=PackNormalOctQuadEncode(n);return half4(PackFloat2To888(saturate(oct*.5+.5)),0);
        #else
            return half4(n,0);
        #endif
    }
    ENDHLSL
    Pass
    {
        Name "ForwardLit"
        Tags { "LightMode"="UniversalForwardOnly" }
        HLSLPROGRAM
        #pragma target 4.5
        #pragma vertex Vert
        #pragma fragment Frag
        #pragma multi_compile_instancing
        #pragma multi_compile_fog
        #pragma multi_compile _ LIGHTMAP_ON
        #pragma multi_compile _ DIRLIGHTMAP_COMBINED
        #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
        #pragma multi_compile _ SHADOWS_SHADOWMASK
        #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
        #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
        #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
        #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
        #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
        #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
        #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
        #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
        ENDHLSL
    }
    Pass
    {
        Name "ShadowCaster" Tags { "LightMode"="ShadowCaster" } ZWrite On ColorMask 0
        HLSLPROGRAM
        #pragma target 4.5
        #pragma vertex ShadowVert
        #pragma fragment DepthFrag
        #pragma multi_compile_instancing
        #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
        ENDHLSL
    }
    Pass
    {
        Name "DepthOnly" Tags { "LightMode"="DepthOnly" } ZWrite On ColorMask R
        HLSLPROGRAM
        #pragma target 4.5
        #pragma vertex Vert
        #pragma fragment DepthFrag
        #pragma multi_compile_instancing
        ENDHLSL
    }
    Pass
    {
        Name "DepthNormals" Tags { "LightMode"="DepthNormalsOnly" } ZWrite On
        HLSLPROGRAM
        #pragma target 4.5
        #pragma vertex Vert
        #pragma fragment NormalFrag
        #pragma multi_compile_instancing
        #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
        ENDHLSL
    }
}
Fallback Off
}
