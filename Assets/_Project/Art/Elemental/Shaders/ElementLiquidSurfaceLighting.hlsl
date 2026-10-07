// 活动 GPU 液面与休眠 Surface Nets 共用完全相同的 World-space 波纹/法线/深度/PBR。
// 几何输入不同不会再选择不同的光照模型；只在各自的 Vertex Shader 中处理坐标变换。
#ifndef ELEMENT_LIQUID_SURFACE_LIGHTING_INCLUDED
#define ELEMENT_LIQUID_SURFACE_LIGHTING_INCLUDED
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // URP透明表面直接采样Shadow Map。屏幕空间阴影只适用于已经写入深度的Opaque表面；
            // 缺少该标记会把World shadowCoord当成Screen UV，出现规则虚线/错误阴影。
            #define _SURFACE_TYPE_TRANSPARENT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "ElementWaterCore.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                half4 _FresnelColor;
                half4 _FoamColor;

                float _ShallowAlpha;
                float _DeepAlpha;
                float _FresnelAlphaStrength;
                float _WaterAlpha;

                float _WaveScaleA;
                float _WaveScaleB;
                float4 _WaveSpeedA;
                float4 _WaveSpeedB;
                float _WaveNormalStrength;
                float _TriplanarSharpness;
                float _VolumeWaveWorldScale;

                float _DepthFadeDistance;
                float _DepthNoiseStrength;
                float _FresnelPower;
                float _Smoothness;

                float _FoamDistance;
                float _FoamStrength;
                float _FoamAlphaBoost;
                float _DebugForceOpaqueFragment;
                // Mesh 落水插值也是材质参数，放在 UnityPerMaterial 中保留 SRP Batcher 兼容性。
                float _LiquidMotionHorizon;
                float _CartoonSwellAmplitude;
#ifdef ELEMENT_LIQUID_CONTINUOUS_PALETTE
                // 所有液体绘制变体使用同一UnityPerMaterial布局，颜色不会随其他Draw的全局状态串色。
                float4 _PaletteShallow[8],_PaletteDeep[8],_PaletteFresnel[8],_PaletteFoam[8],_PaletteAlpha[8],_PaletteSurface[8];
                int _PaletteCount,_PaletteGroupCount;
                float4 _PaletteGroups0,_PaletteGroups1;
#endif
#ifdef ELEMENT_LIQUID_STABLE_SHAPE
                // 主体高度、受剩余层高限制的圆冠、格边长和启用标记。
                // 必须是有零默认值的材质参数，动态 Mesh 才不会误用静水的 UV 形状数据。
                float4 _StableLiquidShape;
#endif
            CBUFFER_END

            void ApplyLiquidCartoonSwell(inout float3 positionWS,inout half3 normalWS,float active)
            {
                // Vertex 阶段抬动真实剪影；世界坐标锁定相位，重建 Mesh 不会重新开始起伏。
                // 两条低频正弦及解析偏导数同时补偿法线，避免只动位置仍像硬质平板。
                float mask=saturate(normalWS.y);mask*=mask*active;
                float a=dot(positionWS.xz,float2(1.1,.75))+_Time.y*.8;
                float b=dot(positionWS.xz,float2(-.65,1.25))-_Time.y*.62;
                float strength=_CartoonSwellAmplitude*mask;
                positionWS.y+=strength*(.65*sin(a)+.35*sin(b));
                float2 slope=strength*(.65*cos(a)*float2(1.1,.75)+.35*cos(b)*float2(-.65,1.25));
                normalWS=normalize(normalWS-half3(slope.x,0,slope.y));
            }

            struct ProceduralVaryings
            {
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float4 shadowCoord : TEXCOORD2;
                half fogFactor : TEXCOORD3;
                half3 vertexSH : TEXCOORD4;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // 同一个外壳逐顶点携带数量权重；Palette参数化保留旧Shader入口，
            // 避免为每种液体重复画透明外壳，也不能在Fragment内改写CBUFFER。
            struct LiquidSurfacePalette
            {
                half4 shallow, deep, fresnel, foam;
                float4 alpha;
                float smoothness;
            };
            LiquidSurfacePalette LiquidDefaultPalette()
            {
                LiquidSurfacePalette p;
                p.shallow=_ShallowColor;p.deep=_DeepColor;p.fresnel=_FresnelColor;p.foam=_FoamColor;
                p.alpha=float4(_ShallowAlpha,_DeepAlpha,_FresnelAlphaStrength,_WaterAlpha);
                p.smoothness=_Smoothness;return p;
            }
            // Stable湖面仅在活动外壳负责的Chunk内裁掉。其余湖面仍使用稀疏Patch，
            // 不因附近排水而逐帧重建整湖。未绑定时Enabled=0，不影响旧材质。
            StructuredBuffer<int4> _LiquidOwnerLookup;
            int _LiquidOwnerLookupMask;
            float4 _LiquidDomainOriginSize;
            int _LiquidDomainChunkSize, _LiquidDomainEnabled;
            // 显示半径只作用于Fragment Alpha，Gameplay量/接触仍由CPU账本负责。
            float4 _LiquidVisibilityCenter,_LiquidVisibility;
            float LiquidVisibilityFactor(float3 p)
            {
                return _LiquidVisibility.z<.5?1:1-smoothstep(_LiquidVisibility.x,_LiquidVisibility.y,distance(p,_LiquidVisibilityCenter.xyz));
            }
            StructuredBuffer<int4> _LiquidSecondaryOwnerLookup;
            int _LiquidSecondaryOwnerLookupMask,_LiquidSecondaryDomainEnabled;
            bool LiquidSecondaryOwnedDomain(float3 p)
            {
                if(_LiquidSecondaryDomainEnabled==0)return false;
                int3 c=(int3)floor((p-_LiquidDomainOriginSize.xyz)/(_LiquidDomainOriginSize.w*_LiquidDomainChunkSize));
                uint h=((uint)c.x*73856093u^(uint)c.y*19349663u^(uint)c.z*83492791u)&(uint)_LiquidSecondaryOwnerLookupMask;
                [loop]for(int i=0;i<32;i++)
                {int4 e=_LiquidSecondaryOwnerLookup[h];if(e.w==0)return false;if(all(e.xyz==c))return true;h=(h+1)&(uint)_LiquidSecondaryOwnerLookupMask;}
                return false;
            }
            bool LiquidOwnedDomain(float3 p)
            {
                if(_LiquidDomainEnabled==0)return false;
                int3 c=(int3)floor((p-_LiquidDomainOriginSize.xyz)/(_LiquidDomainOriginSize.w*_LiquidDomainChunkSize));
                uint h=((uint)c.x*73856093u^(uint)c.y*19349663u^(uint)c.z*83492791u)&(uint)_LiquidOwnerLookupMask;
                [loop]for(int i=0;i<32;i++)
                {
                    int4 e=_LiquidOwnerLookup[h];if(e.w==0)return false;
                    if(all(e.xyz==c))return true;h=(h+1)&(uint)_LiquidOwnerLookupMask;
                }
                return false;
            }
            half4 LiquidProceduralFragmentWithPalette(ProceduralVaryings input,LiquidSurfacePalette palette)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float visibility=LiquidVisibilityFactor(input.positionWS);clip(visibility-.0001);

                // 受控诊断：仅旁路复杂 Fragment 光照/深度/波纹计算，Vertex、Cull、ZTest 与 Draw 保持不变。
                // 若该颜色可见，说明 Rasterization 已成功，故障范围可收敛到下方 Fragment 计算。
                if (_DebugForceOpaqueFragment > 0.5f && _DebugForceOpaqueFragment < 1.5f)
                    return half4(palette.shallow.rgb, visibility);

                float3 baseNormalWS = NormalizeNormalPerPixel(input.normalWS);
                // 对照诊断保留未经Fragment半球修正的几何Normal，判断暗齿来自几何还是翻面。
                if(_DebugForceOpaqueFragment>3.5f)return half4(baseNormalWS*.5+.5,1);
#ifdef ELEMENT_LIQUID_CONTINUOUS_PALETTE
                // 极薄/退化Triangle保留Fragment半球保险，但不整条翻转平滑Normal。
                // 负点积逐渐投影到真实面半球，避免反向Normal跨插值零点时发生180°光照跳变。
                float3 faceNormalWS=SafeNormalize(cross(ddy(input.positionWS),ddx(input.positionWS)));
                if(dot(faceNormalWS,GetWorldSpaceNormalizeViewDir(input.positionWS))<0)faceNormalWS=-faceNormalWS;
                baseNormalWS=SafeNormalize(baseNormalWS+faceNormalWS*max(0,.15-dot(baseNormalWS,faceNormalWS)));
#endif
                // 只在显式Editor诊断使用：分别隔离几何Normal和最终Alpha，不改正常材质外观。
                if(_DebugForceOpaqueFragment>2.5f)return half4(baseNormalWS*.5+.5,1);
#ifdef ELEMENT_LIQUID_STABLE_SHAPE
                // 在 Fragment 解析连续斜率，粗 Quad 与细分面采样同一 World Space 波。
                // 不改顶点位置，所以不同细度的交界不会被正弦位移撕开。
                float3 ignoredPosition=input.positionWS;
                ApplyLiquidCartoonSwell(ignoredPosition,baseNormalWS,1);
#endif
                float3 tangentWS;
                float3 bitangentWS;
                ElementWaterBuildOrthonormalBasis(baseNormalWS, tangentWS, bitangentWS);
                float3x3 tangentToWorld = float3x3(tangentWS, bitangentWS, baseNormalWS);

                // 复用 Legacy Water Core 的 World-space Triplanar Noise；封闭水滴的侧壁不会拉花。
                float waveNoiseA;
                float waveNoiseB;
                float combinedNoise;
                float3 gradientA,gradientB;
                waveNoiseA=ElementWaterTriplanarNoiseGradient(input.positionWS,baseNormalWS,_VolumeWaveWorldScale,
                    _WaveScaleA,_WaveSpeedA.xy*_Time.y,_TriplanarSharpness,gradientA);
                waveNoiseB=ElementWaterTriplanarNoiseGradient(input.positionWS,baseNormalWS,_VolumeWaveWorldScale,
                    _WaveScaleB,_WaveSpeedB.xy*_Time.y,_TriplanarSharpness,gradientB);
                combinedNoise=saturate((waveNoiseA+waveNoiseB)*.5);

                // 圆肩侧面优先保留几何法线，避免高频波纹把柔和轮廓照成锋利碎片。
                // 顶面继续使用既有波纹；这里只按世界法线朝上程度衰减扰动，无额外采样。
                float waveStrength = _WaveNormalStrength * smoothstep(0.15,0.85,abs(baseNormalWS.y));
                // 屏幕导数法在稀疏Surface Nets的三角边附近会放大投影行列式，出现规则虚线。
                // 直接求Noise的World Space梯度，再去掉Normal方向分量，保留连续切向波纹。
                float3 gradient=gradientA+gradientB;gradient-=baseNormalWS*dot(gradient,baseNormalWS);
                float3 dynamicNormalWS=NormalizeNormalPerPixel(baseNormalWS-waveStrength*gradient);
                float3 normalTS=TransformWorldToTangent(dynamicNormalWS,tangentToWorld);

                // Camera Depth 给出水面后方第一个不透明几何的深度；两者之差近似视线方向水层厚度。
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float rawSceneDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = unity_OrthoParams.w == 0.0
                    ? LinearEyeDepth(rawSceneDepth, _ZBufferParams)
                    : LinearDepthToEyeDepth(rawSceneDepth);
                float surfaceEyeDepth = -TransformWorldToView(input.positionWS).z;
                float waterThickness = max(sceneEyeDepth - surfaceEyeDepth, 0.0);

                float3 viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                float3 viewDirectionTS = TransformWorldToTangent(viewDirectionWS, tangentToWorld);
                float depthMask;
                float fresnelMask;
                ElementWaterSurface_float(
                    waterThickness,
                    _DepthFadeDistance,
                    _DepthNoiseStrength,
                    combinedNoise,
                    normalTS,
                    viewDirectionTS,
                    _FresnelPower,
                    depthMask,
                    fresnelMask);

                float contactMask = 1.0 - saturate(
                    waterThickness / max(_FoamDistance, 0.0001));
                float foamNoise = smoothstep(0.35, 0.70, combinedNoise);
                float foamMask = saturate(contactMask * foamNoise * _FoamStrength);
                half3 depthColor = lerp(palette.shallow.rgb, palette.deep.rgb, depthMask);
                half3 fresnelColor = lerp(depthColor, palette.fresnel.rgb, fresnelMask);
                half3 finalAlbedo = lerp(fresnelColor, palette.foam.rgb, foamMask);
                float depthAlpha = lerp(palette.alpha.x, palette.alpha.y, depthMask);
                half finalAlpha = saturate((depthAlpha
                    + fresnelMask * palette.alpha.z
                    + foamMask * _FoamAlphaBoost) * palette.alpha.w);
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = finalAlbedo;
                surfaceData.metallic = 0.0h;
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = (half)palette.smoothness;
                surfaceData.normalTS = (half3)normalTS;
                surfaceData.occlusion = 1.0h;
                surfaceData.emission = half3(0.0h, 0.0h, 0.0h);
                surfaceData.alpha = finalAlpha;
                surfaceData.clearCoatMask = 0.0h;
                surfaceData.clearCoatSmoothness = 0.0h;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = dynamicNormalWS;
                inputData.viewDirectionWS = (half3)viewDirectionWS;
                inputData.shadowCoord = input.shadowCoord;
                inputData.fogCoord = input.fogFactor;
                inputData.vertexLighting = VertexLighting(input.positionWS, dynamicNormalWS);
#ifdef ELEMENT_LIQUID_CONTINUOUS_PALETTE
                // 半球修正后不能复用原反向Vertex Normal的SH项，否则Environment Lighting仍留暗条。
                inputData.bakedGI=SampleSH(dynamicNormalWS);
#else
                inputData.bakedGI = SampleSHPixel(input.vertexSH, dynamicNormalWS);
#endif
                inputData.normalizedScreenSpaceUV = screenUV;
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);
                inputData.tangentToWorld = (half3x3)tangentToWorld;

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = _DebugForceOpaqueFragment>1.5f?1:finalAlpha;
                color.a*=visibility;
                return color;
            }
            half4 LiquidProceduralFragment(ProceduralVaryings input) : SV_Target
            {
                if(LiquidOwnedDomain(input.positionWS) || LiquidSecondaryOwnedDomain(input.positionWS))discard;
                return LiquidProceduralFragmentWithPalette(input,LiquidDefaultPalette());
            }

#endif
