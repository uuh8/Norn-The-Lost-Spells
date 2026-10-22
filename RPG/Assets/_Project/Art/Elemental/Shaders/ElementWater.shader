Shader "Game/Elemental/Water"
{
    // =====================================================================
    // 阅读指南：这个文件描述“怎样把一个水面 Mesh 画到屏幕上”。
    //
    // CPU 侧的 Material 会为 Properties 提供具体参数；GPU 先对 Mesh 的每个顶点执行
    // WaterVertex，再由固定功能 Rasterizer 把三角形覆盖成大量 Fragment，最后对每个
    // Fragment 执行 WaterFragment。Fragment Shader 输出的不是“水的物理模拟结果”，
    // 而是该像素的颜色与 Alpha。水面流动、深浅和泡沫都是视觉近似，不会移动 Gameplay
    // Collider，也不会改变 StatusVolume 中的 Wet 强度。
    //
    // 推荐按以下数据流阅读：
    // Properties -> Render State -> Attributes/Varyings -> WaterVertex
    // -> WaterFragment(Noise -> Normal -> Depth -> Fresnel -> Foam -> Lighting)。
    // 具体数学函数位于同目录的 ElementWaterCore.hlsl。
    // =====================================================================

    Properties
    {
        [Header(Color)]
        // 三个 Color 都只使用 rgb；a 不参与最终透明度。最终 Alpha 由下方 Transparency
        // 参数独立计算，这样颜色调节和透明度调节不会互相污染。
        // DepthMask=0 使用 Shallow，DepthMask=1 使用 Deep；FresnelColor 在掠射角出现。
        _ShallowColor("Shallow Color", Color) = (0.35, 0.85, 0.88, 1)
        _DeepColor("Deep Color", Color) = (0.04, 0.18, 0.65, 1)
        _FresnelColor("Fresnel Color", Color) = (0.72, 1.0, 0.96, 1)

        [Header(Transparency)]
        // Alpha=0 表示完全显示背景，Alpha=1 表示完全显示水面颜色。
        // 浅/深 Alpha 仍通过同一个 DepthMask 插值；FresnelAlphaStrength 让斜视水面更实，
        // WaterAlpha 是最终全局倍率，便于一次控制整种水材质的淡入/淡出。
        _ShallowAlpha("Shallow Alpha", Range(0, 1)) = 0.2
        _DeepAlpha("Deep Alpha", Range(0, 1)) = 0.65
        _FresnelAlphaStrength("Fresnel Alpha Strength", Range(0, 0.5)) = 0.15
        _WaterAlpha("Global Water Alpha", Range(0, 1)) = 1.0

        [Header(Waves)]
        // Scale 在这里表示 UV 空间频率：数值越大，同一块 Mesh 上出现的 Noise 格子越多，
        // 视觉波纹越细；它不是世界空间中的真实波长。Speed 的 xy 是 UV/秒，两个方向
        // 不同的 Noise 叠加后能削弱单层纹理“整齐平移”的人工感。
        _WaveScaleA("Wave Scale A", Float) = 15.0
        _WaveScaleB("Wave Scale B", Float) = 12.0
        _WaveSpeedA("Wave Speed A", Vector) = (0.05, 0.02, 0, 0)
        _WaveSpeedB("Wave Speed B", Vector) = (-0.03, 0.04, 0, 0)
        // Strength 只放大用于光照的表面梯度，不会修改 Vertex Position 和物体轮廓。
        _WaveNormalStrength("Wave Normal Strength", Range(0, 1)) = 0.25
        // 体积水不能只把 Noise 投到 XZ 顶面。Triplanar 按 World Normal 在
        // ZY/XZ/XY 三个投影间混合；Sharpness 越大，主投影越明确、过渡越窄。
        _TriplanarSharpness("Triplanar Sharpness", Range(1, 8)) = 4.0
        // World Position 先乘该值再进入 WaveScale；统一控制所有 Chunk 的世界空间纹理密度。
        _VolumeWaveWorldScale("Volume Wave World Scale", Float) = 1.0

        [Header(Depth And Fresnel)]
        // DepthFadeDistance 的单位近似为沿 Camera 视线方向的世界距离：水层厚度达到该值
        // 时 DepthMask 进入 1。DepthNoiseStrength 只扰动深浅交界，避免规则色带。
        _DepthFadeDistance("Depth Fade Distance", Range(0.05, 5)) = 1.5
        _DepthNoiseStrength("Depth Noise Strength", Range(0, 0.5)) = 0.12
        // FresnelPower 越大，Mask 越集中在掠射角；Smoothness 越高，URP PBR 高光越窄。
        _FresnelPower("Fresnel Power", Range(0.5, 8)) = 4.0
        _Smoothness("Smoothness", Range(0, 1)) = 0.85

        [Header(Foam)]
        // FoamDistance 控制接触线宽度；Strength 控制颜色覆盖比例；AlphaBoost 只提高泡沫
        // 区域的不透明度。泡沫形状来自已有 Noise，不依赖外部 Foam Texture。
        _FoamColor("Foam Color", Color) = (0.85, 1.0, 1.0, 1)
        _FoamDistance("Foam Distance", Range(0.01, 1)) = 0.25
        _FoamStrength("Foam Strength", Range(0, 1)) = 0.8
        _FoamAlphaBoost("Foam Alpha Boost", Range(0, 0.5)) = 0.2
    }

    SubShader
    {
        // SubShader 是一组针对某个 Render Pipeline/硬件能力的实现。RenderPipeline Tag
        // 防止 Built-in Pipeline 错误选中本实现；RenderType/Queue 告诉 URP 这是透明物体，
        // 通常在不透明物体和 Camera Depth Texture 准备好之后再绘制。
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        // Blend 的完整逐通道近似公式：
        // C_out = C_water * Alpha + C_background * (1 - Alpha)。
        // SrcAlpha 对应水面输出的 Alpha，OneMinusSrcAlpha 对应背景权重。
        //
        // ZWrite Off：水仍会执行 ZTest，但不把自己的深度写回 Depth Buffer。否则先画的
        // 半透明水会挡住后画的透明粒子/水面。代价是透明物体不能只靠 Depth Buffer 自动
        // 获得完全正确的交叠顺序，通常需要按 Camera 距离排序，并承担重复着色 Overdraw。
        // Cull Back 只画法线朝外的一面；若要从水下观察，需要额外设计双面法线而不是盲目
        // 改为 Cull Off，否则会让 Fragment 数量和透明 Overdraw 近似翻倍。
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Back

        Pass
        {
            // 一个 Pass 对应一组 Render State 和一套 GPU 程序。LightMode=UniversalForward
            // 让 URP 在 Forward Rendering 阶段选择该 Pass；Frame Debugger 中会看到此名称。
            Name "WaterForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            // Shader Model 3.5 满足当前 PC URP、屏幕深度采样和 Fragment Derivative 需求。
            // pragma vertex/fragment 指定本 Pass 的两个可编程入口，而不是普通 C# 方法。
            #pragma target 3.5
            #pragma vertex WaterVertex
            #pragma fragment WaterFragment

            // multi_compile 会为“是否有阴影/附加光/Forward+”生成不同 Shader Variant。
            // 运行时 URP 根据当前 Renderer 和灯光配置选择匹配版本。Variant 并非免费：
            // 关键字越多，构建时间、包体与暖机成本越高，因此这里只声明水需要的组合。
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            // Core：矩阵、坐标空间、深度线性化等基础函数。
            // Lighting：InputData/SurfaceData、阴影、GI 和 UniversalFragmentPBR。
            // DeclareDepthTexture：声明 _CameraDepthTexture 和 SampleSceneDepth。
            // ElementWaterCore：本项目自研的 Noise、Normal、Depth、Fresnel 数学。
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "ElementWaterCore.hlsl"

            // UnityPerMaterial CBUFFER 把同一 Material 的参数放入一个常量缓冲区。
            // 保持所有材质属性都在该块内，是兼容 SRP Batcher 的关键条件之一；GPU 不需要
            // 为每个 Fragment 重新寻找 Material，而是从当前 Draw Call 的常量缓冲读取。
            // half 适合颜色等容错较高的数据，float 用于深度和导数相关计算以保留精度。
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
            CBUFFER_END

            struct Attributes
            {
                // Attributes 来自 Mesh Vertex Buffer，每调用一次 WaterVertex 读取一个顶点。
                // 语义 POSITION/NORMAL/TANGENT/TEXCOORD0 告诉图形 API 每个字段从哪里取。
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                // tangentOS.xyz 是表面 U 方向；w 是 +1/-1 的 Handedness，用于恢复 B 方向。
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                // Varyings 是 Vertex Shader 写出、Rasterizer 插值、Fragment Shader 读入的数据。
                // 例如三角形只有三个顶点 UV，但 Rasterizer 会为内部每个 Fragment 产生连续 UV。
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half4 tangentWS : TEXCOORD3;
                // shadowCoord 用于采样主光阴影；fogFactor 用于最终雾混合；vertexSH 是在顶点处
                // 计算的低频环境光球谐系数。SV_POSITION 是 Rasterizer 必需的 Clip/Screen 位置。
                float4 shadowCoord : TEXCOORD4;
                half fogFactor : TEXCOORD5;
                half3 vertexSH : TEXCOORD6;
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings WaterVertex(Attributes input)
            {
                // Vertex Shader 的目标不是算最终颜色，而是把 Mesh 顶点放到正确屏幕位置，
                // 并准备 Fragment 阶段需要的低频数据。复杂 Noise 留到 Fragment 执行，
                // 因为当前效果需要逐像素细节，而低密度 Plane 的逐顶点 Noise 会过于粗糙。
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // 常见坐标变换可以写成：
                // p_world = M_model * p_object
                // p_view  = M_view  * p_world
                // p_clip  = M_proj  * p_view
                // Object Space 随物体移动；World Space 使用场景统一坐标；View Space 以 Camera
                // 为原点；Clip Space 用于裁剪和透视除法。Unity API 封装了平台差异。
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.uv = input.uv;
                output.normalWS = normalInputs.normalWS;

                // T、B、N 构成表面局部坐标系。B 通常由 cross(N,T) 恢复，但 Cross Product
                // 有固定朝向；镜像 UV 或负缩放会翻转基底，所以必须再乘 tangent.w 和
                // GetOddNegativeScale 得到正确 Handedness。忽略它会让一部分 Mesh 法线反向。
                half tangentSign = input.tangentOS.w * GetOddNegativeScale();
                output.tangentWS = half4(normalInputs.tangentWS, tangentSign);
                output.shadowCoord = GetShadowCoord(positionInputs);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                OUTPUT_SH(output.normalWS, output.vertexSH);
                return output;
            }

            half4 WaterFragment(Varyings input) : SV_Target
            {
                // Fragment Shader 对 Rasterization 产生的候选像素执行。它可能因为 Depth Test
                // 被丢弃，也可能经 Alpha Blend 与已有颜色混合，所以 Fragment 不完全等于
                // 最终屏幕 Pixel。当前主要 GPU 成本也集中在这里：透明 Overdraw 会重复运行它。
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // ---------- 1. 建立 Tangent Space 基底 ----------
                // TBN = [T B N]。Tangent Space 把任意三角形表面都视为局部 XY 平面，
                // 其中平坦法线是 (0,0,1)。程序化高度场先生成 Tangent Space Normal，
                // 再计算 N_world = normalize(TBN * N_tangent)，才能与 World Space 灯光 L
                // 计算 dot(N,L)。不同 Coordinate Space 的 XYZ 数字不能直接点积。
                float3 baseNormalWS = NormalizeNormalPerPixel(input.normalWS);
                float3 tangentWS = normalize(input.tangentWS.xyz);
                float3 bitangentWS = input.tangentWS.w * normalize(cross(baseNormalWS, tangentWS));
                float3x3 tangentToWorld = float3x3(tangentWS, bitangentWS, baseNormalWS);

                // ---------- 2. World-space Triplanar Value Noise ----------
                // 旧 XZ/UV 投影适合近似水平面，但在封闭水滴和竖直侧壁上会沿一个方向拉伸。
                // 现在按 abs(BaseNormalWS) 在 ZY/XZ/XY 三个平面间加权；PositionWS 保证跨
                // Chunk 相位连续。Panning 仍为 offset=speed*time，只是被应用到每个投影。
                float waveNoiseA;
                float waveNoiseB;
                float combinedNoise;
                ElementWaterVolumeWaves_float(
                    input.positionWS,
                    baseNormalWS,
                    _Time.y,
                    _WaveScaleA,
                    _WaveScaleB,
                    _WaveSpeedA.xy,
                    _WaveSpeedB.xy,
                    _VolumeWaveWorldScale,
                    _TriplanarSharpness,
                    waveNoiseA,
                    waveNoiseB,
                    combinedNoise);

                // Height 是标量“高低”，Normal 是三维单位方向。ddx/ddy 近似当前 Fragment
                // 与屏幕相邻 Fragment 的变化率，由此得到表面斜率。Normal From Height 只改变
                // 光照法线，不移动几何顶点，所以轮廓仍是平面；两层 Normal 再按斜率合成。
                float3 normalA = ElementWaterNormalFromHeight(
                    waveNoiseA, _WaveNormalStrength, input.positionWS, tangentToWorld);
                float3 normalB = ElementWaterNormalFromHeight(
                    waveNoiseB, _WaveNormalStrength, input.positionWS, tangentToWorld);
                float3 normalTS = ElementWaterBlendNormals(normalA, normalB);
                float3 dynamicNormalWS = NormalizeNormalPerPixel(
                    TransformTangentToWorld(normalTS, tangentToWorld));

                // ---------- 3. Scene Depth -> 水体厚度 ----------
                // Depth Buffer 中通常不是线性世界距离，而是经过 Projection 的 Raw Depth，
                // 近处精度高、远处精度低。LinearEyeDepth/LinearDepthToEyeDepth 将其恢复为
                // 沿 Camera 前向的 Eye Depth。水层厚度公式：
                // thickness = max(sceneEyeDepth - waterSurfaceEyeDepth, 0)。
                // 它是沿视线方向的近似厚度，不等同于竖直方向的真实水深。
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                float rawSceneDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = unity_OrthoParams.w == 0.0
                    ? LinearEyeDepth(rawSceneDepth, _ZBufferParams)
                    : LinearDepthToEyeDepth(rawSceneDepth);
                float surfaceEyeDepth = -TransformWorldToView(input.positionWS).z;
                float waterThickness = max(sceneEyeDepth - surfaceEyeDepth, 0.0);

                // View Direction 定义为 Fragment 指向 Camera 的单位方向。将它与 normalTS
                // 放在同一 Tangent Space 后才能计算 N·V=cos(theta)。正视 theta≈0，N·V≈1；
                // 掠射角 theta≈90°，N·V≈0，随后 1-N·V 得到边缘更强的 Fresnel Mask。
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

                // ---------- 4. 程序化岸边泡沫 ----------
                // 接触 Mask 公式：contact = 1-saturate(thickness/FoamDistance)。
                // 接触点 thickness≈0，所以 contact≈1；达到 FoamDistance 后 contact=0。
                // smoothstep(0.35,0.70,noise) 把连续 Noise 变成仍有柔边的块状 Mask，最终：
                // foam = saturate(contact * foamNoise * FoamStrength)。
                float contactMask = 1.0 - saturate(
                    waterThickness / max(_FoamDistance, 0.0001));
                float foamNoise = smoothstep(0.35, 0.70, combinedNoise);
                float foamMask = saturate(contactMask * foamNoise * _FoamStrength);

                // ---------- 5. 颜色和 Alpha ----------
                // lerp(A,B,t)=A*(1-t)+B*t。深度决定浅/深水色，观察角决定 Fresnel 色，
                // 最后让泡沫覆盖在最上层。每一级都输出 0~1 Mask，因此可以连续调节。
                half3 depthColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, depthMask);
                half3 fresnelColor = lerp(depthColor, _FresnelColor.rgb, fresnelMask);
                half3 finalAlbedo = lerp(fresnelColor, _FoamColor.rgb, foamMask);

                // Alpha 同样从深度开始，再加角度和泡沫贡献。最后 saturate 防止总和越过 1；
                // WaterAlpha 放在末尾作为总开关。注意 Alpha 只影响混合，不代表真实吸收系数。
                float depthAlpha = lerp(_ShallowAlpha, _DeepAlpha, depthMask);
                float fresnelAlpha = fresnelMask * _FresnelAlphaStrength;
                float foamAlpha = foamMask * _FoamAlphaBoost;
                half finalAlpha = saturate(
                    (depthAlpha + fresnelAlpha + foamAlpha) * _WaterAlpha);

                // ---------- 6. 交给 URP PBR Lighting ----------
                // SurfaceData 描述“材质是什么”：Albedo、Smoothness、Metallic、Alpha 等。
                // InputData 描述“像素在哪里、朝哪、看到哪些灯光/阴影/GI”。二者分离后，
                // UniversalFragmentPBR 统一执行主光/附加光、N·L、阴影、GI 和镜面 BRDF。
                // 水设 Metallic=0，因为它是 Dielectric；高 Smoothness 表示微表面方向集中，
                // 因而高光更窄、更清晰。这里复用 URP PBR，而不是重复手写整套 BRDF。
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = finalAlbedo;
                surfaceData.metallic = 0.0h;
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = (half)_Smoothness;
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
                inputData.bakedGI = SampleSHPixel(input.vertexSH, dynamicNormalWS);
                inputData.normalizedScreenSpaceUV = screenUV;
                inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);
                inputData.tangentToWorld = (half3x3)tangentToWorld;

                // UniversalFragmentPBR 返回“水面自身被光照后的颜色”。随后 MixFog 处理远景雾，
                // Render State 中的 Blend 再把它与 Framebuffer 里已有的背景颜色做 Alpha 混合。
                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = finalAlpha;
                return color;
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
