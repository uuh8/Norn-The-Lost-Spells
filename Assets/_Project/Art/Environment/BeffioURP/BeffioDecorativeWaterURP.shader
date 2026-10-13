Shader "Game/Environment/BeffioDecorativeWaterURP"
{
    Properties
    {
        _Depth_Top_Color("Surface Color",Color)=(.5,.72,.70,1)
        _Depth_bottom_color("Deep Color",Color)=(.14,.24,.32,1)
        _Smoothness_level("Smoothness",Range(0,1))=.92
        _Normalmapinput("Water Normal",2D)="bump"{}
        _Normal1_intensity("Normal Strength 1",Range(0,2))=1
        _Normal2_intensity("Normal Strength 2",Range(0,2))=1
        _Normal_blend("Normal Blend",Range(0,1))=.5
        _UV1_speed("UV Speed 1",Vector)=(.03,0,0,0)
        _UV2_speed("UV Speed 2",Vector)=(.015,0,0,0)
        _UV1_tiling("UV Tiling 1",Range(.01,2))=1
        _UV2_tiling("UV Tiling 2",Range(.01,2))=1
        _Opacity("Opacity",Range(0,1))=.78
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            Name "DecorativeWater" Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceData.hlsl"
            CBUFFER_START(UnityPerMaterial)
                float4 _Depth_Top_Color,_Depth_bottom_color,_UV1_speed,_UV2_speed;
                float _Smoothness_level,_Normal1_intensity,_Normal2_intensity,_Normal_blend,_UV1_tiling,_UV2_tiling,_Opacity;
            CBUFFER_END
            TEXTURE2D(_Normalmapinput);SAMPLER(sampler_Normalmapinput);
            struct Attributes
            {
                float4 positionOS:POSITION;float3 normalOS:NORMAL;float4 tangentOS:TANGENT;float2 uv:TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS:SV_POSITION;float3 positionWS:TEXCOORD0;float2 uv:TEXCOORD1;
                half3 normalWS:TEXCOORD2;half4 tangentWS:TEXCOORD3;half fog:TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes a)
            {
                Varyings o=(Varyings)0;UNITY_SETUP_INSTANCE_ID(a);UNITY_TRANSFER_INSTANCE_ID(a,o);UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs p=GetVertexPositionInputs(a.positionOS.xyz);VertexNormalInputs n=GetVertexNormalInputs(a.normalOS,a.tangentOS);
                o.positionCS=p.positionCS;o.positionWS=p.positionWS;o.uv=a.uv;o.normalWS=n.normalWS;
                o.tangentWS=half4(n.tangentWS,a.tangentOS.w*GetOddNegativeScale());o.fog=ComputeFogFactor(p.positionCS.z);return o;
            }
            half4 Frag(Varyings i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                // 两个不同速度的Normal层仅改变光照朝向，不创建真实水材料，也不改变Collider或库存。
                half3 n1=UnpackNormalScale(SAMPLE_TEXTURE2D(_Normalmapinput,sampler_Normalmapinput,i.uv*_UV1_tiling+_Time.y*_UV1_speed.xy),_Normal1_intensity);
                half3 n2=UnpackNormalScale(SAMPLE_TEXTURE2D(_Normalmapinput,sampler_Normalmapinput,i.uv*_UV2_tiling+_Time.y*_UV2_speed.xy),_Normal2_intensity);
                half3 tangent=normalize(i.tangentWS.xyz),normal=normalize(i.normalWS),bitangent=cross(normal,tangent)*i.tangentWS.w;
                half3 normalTS=normalize(lerp(n1,n2,_Normal_blend));normal=normalize(TransformTangentToWorld(normalTS,half3x3(tangent,bitangent,normal)));
                float3 view=GetWorldSpaceNormalizeViewDir(i.positionWS);half fresnel=pow(1-saturate(dot(normal,view)),4);
                SurfaceData s=(SurfaceData)0;s.albedo=lerp(_Depth_bottom_color.rgb,_Depth_Top_Color.rgb,fresnel);s.normalTS=normalTS;
                s.smoothness=_Smoothness_level;s.occlusion=1;s.specular=half3(.04,.04,.04);s.alpha=_Opacity;
                InputData input=(InputData)0;input.positionWS=i.positionWS;input.normalWS=normal;input.viewDirectionWS=view;
                input.shadowCoord=TransformWorldToShadowCoord(i.positionWS);input.bakedGI=SampleSH(normal);input.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(i.positionCS);
                half4 result=UniversalFragmentPBR(input,s);result.rgb=MixFog(result.rgb,i.fog);result.a=_Opacity;return result;
            }
            ENDHLSL
        }
    }
    // 不保留Built-in GrabPass/Reflection_pass，也不冒充折射、岸边泡沫/波形位移或可采集ElementWorld。
    Fallback Off
}
