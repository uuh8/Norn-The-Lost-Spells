#ifndef ELEMENT_WATER_CORE_INCLUDED
#define ELEMENT_WATER_CORE_INCLUDED

// 生成稳定的伪随机标量 Hash21：输入 float2，输出一个 [0,1) 标量。
// Shader 中没有适合这里的“保存状态并不断取下一个数”的 CPU Random；每个 Fragment
// 还必须让同一格点在每帧得到同一个值，否则 Noise 会闪烁。因此使用纯函数 Hash：
// 相同 position -> 相同结果，不同格点 -> 看似无规律的结果。
// frac(x)=x-floor(x) 只保留小数部分，把任意数折回 [0,1)。这些常数用于打散相关性，
// 不是水的物理常数；更高质量的 Hash 可以降低规律，但也会增加 ALU 成本。
float ElementWaterHash21(float2 position)
{
    position = frac(position * float2(123.34, 456.21));
    position += dot(position, position + 45.32);
    return frac(position.x * position.y);
}

// 二维 Value Noise：
// 1. floor(position) 找到当前整数格子坐标 cell；
// 2. frac(position) 得到格子内部位置 local=(u,v)；
// 3. Hash 四个角点；
// 4. 先沿 X、再沿 Y 做 Bilinear Interpolation。
//
// 若直接用 local 插值，数值虽然连续，但格子边界斜率会突变。Hermite 曲线
// smooth=f*f*(3-2*f) 满足 smooth(0)=0、smooth(1)=1，并在两端导数为 0，
// 因而相邻格子连接更柔和。它生成的是连续“高度”，不是随机粒子或真实波方程。
float ElementWaterValueNoise(float2 position)
{
    float2 cell = floor(position);
    float2 local = frac(position);
    float2 smooth = local * local * (3.0 - 2.0 * local);

    float bottomLeft = ElementWaterHash21(cell);
    float bottomRight = ElementWaterHash21(cell + float2(1.0, 0.0));
    float topLeft = ElementWaterHash21(cell + float2(0.0, 1.0));
    float topRight = ElementWaterHash21(cell + float2(1.0, 1.0));

    float bottom = lerp(bottomLeft, bottomRight, smooth.x);
    float top = lerp(topLeft, topRight, smooth.x);
    return lerp(bottom, top, smooth.y);
}

// 与Value Noise完全相同的高度，同时直接求World波纹所需偏导数。
// Hermite插值导数为6*f*(1-f)，不依赖屏幕2×2像素的ddx/ddy或三角形的投影面积。
float ElementWaterValueNoiseGradient(float2 position,out float2 gradient)
{
    float2 cell=floor(position),f=frac(position),s=f*f*(3-2*f),ds=6*f*(1-f);
    float a=ElementWaterHash21(cell),b=ElementWaterHash21(cell+float2(1,0));
    float c=ElementWaterHash21(cell+float2(0,1)),d=ElementWaterHash21(cell+1);
    gradient=float2(lerp(b-a,d-c,s.y)*ds.x,lerp(c-a,d-b,s.x)*ds.y);
    return lerp(lerp(a,b,s.x),lerp(c,d,s.x),s.y);
}

float ElementWaterTriplanarNoiseGradient(float3 p,float3 n,float worldScale,float noiseScale,
    float2 offset,float sharpness,out float3 gradient)
{
    float3 weights=pow(abs(SafeNormalize(n)),max(sharpness,1));weights/=max(1e-5,weights.x+weights.y+weights.z);
    float scale=max(.0001,worldScale)*max(.0001,noiseScale);p*=scale;offset*=scale;
    float2 gx,gy,gz;
    float x=ElementWaterValueNoiseGradient(p.zy+offset,gx),y=ElementWaterValueNoiseGradient(p.xz+offset,gy),z=ElementWaterValueNoiseGradient(p.xy+offset,gz);
    // 投影权重只负责采样方向混合。它不是水波高度，不能把几何Normal变化当成额外波纹斜率。
    gradient=(float3(0,gx.y,gx.x)*weights.x+float3(gy.x,0,gy.y)*weights.y+float3(gz.x,gz.y,0)*weights.z)*scale;
    return x*weights.x+y*weights.y+z*weights.z;
}

// World-space Triplanar Mapping：
// - 法线朝 X 的表面看 ZY 平面；
// - 法线朝 Y 的表面看 XZ 平面；
// - 法线朝 Z 的表面看 XY 平面。
//
// abs(N) 让正负朝向使用相同权重；pow(..., sharpness) 控制三种投影的过渡宽度，
// 再除以权重和，保证最终 Noise 仍是三者的凸组合而不会无故变亮。
// positionWS 使相邻 Chunk 在同一世界位置得到同一 Noise 相位，不依赖各自 Local UV 原点。
float ElementWaterTriplanarNoise(
    float3 positionWS,
    float3 normalWS,
    float worldScale,
    float noiseScale,
    float2 panningOffset,
    float triplanarSharpness)
{
    float3 safeNormal = SafeNormalize(normalWS);
    float3 weights = pow(
        abs(safeNormal),
        max(triplanarSharpness, 1.0));
    weights /= max(weights.x + weights.y + weights.z, 1.0e-5);

    float safeWorldScale = max(worldScale, 0.0001);
    float safeNoiseScale = max(noiseScale, 0.0001);
    float3 scaledPosition =
        positionWS * safeWorldScale * safeNoiseScale;
    // Speed 的单位仍按 World Meter/Second 理解，因此 Position 与时间偏移必须经过
    // 相同 Scale；否则只调纹理密度时，波纹的世界移动速度会出现不一致。
    float2 scaledOffset =
        panningOffset * safeWorldScale * safeNoiseScale;

    float noiseX = ElementWaterValueNoise(
        scaledPosition.zy + scaledOffset);
    float noiseY = ElementWaterValueNoise(
        scaledPosition.xz + scaledOffset);
    float noiseZ = ElementWaterValueNoise(
        scaledPosition.xy + scaledOffset);

    return noiseX * weights.x
        + noiseY * weights.y
        + noiseZ * weights.z;
}

// 把标量高度场 H 转换成 Tangent Space Normal。这里沿用 Shader Graph
// Normal From Height 的核心微分思路：ddx/ddy 取得相邻像素的世界位置和高度变化，
// 近似偏导数 dH/dx、dH/dy，再构造表面梯度 grad(H)。直觉上，右侧高度升得越快，
// 法线就越向左倾斜；强度为 0 时回到原始几何法线。
// 它不会真的移动顶点，只改变用于光照的法线，因此成本远低于细分或真实几何波浪。
float3 ElementWaterNormalFromHeight(
    float height,
    float strength,
    float3 positionWS,
    float3x3 tangentToWorld)
{
    float3 worldDerivativeX = ddx(positionWS);
    float3 worldDerivativeY = ddy(positionWS);
    float3 crossX = cross(tangentToWorld[2], worldDerivativeX);
    float3 crossY = cross(worldDerivativeY, tangentToWorld[2]);
    float determinant = dot(worldDerivativeX, crossY);
    float signValue = determinant < 0.0 ? -1.0 : 1.0;
    float surface = signValue / max(1.0e-15, abs(determinant));

    float heightDerivativeX = ddx(height);
    float heightDerivativeY = ddy(height);
    float3 surfaceGradient = surface
        * (heightDerivativeX * crossY + heightDerivativeY * crossX);
    float3 perturbedNormalWS = SafeNormalize(
        tangentToWorld[2] - strength * surfaceGradient);

    return TransformWorldToTangent(perturbedNormalWS, tangentToWorld);
}

// 合并两张 Tangent Space Normal。平坦 Tangent Normal 是 (0,0,1)，所以不能直接 A+B：
// 两个 Z 都含有同一“朝外基准”，相加会过度增加 Z，让法线越来越平。当前采用与
// Shader Graph Default Normal Blend 一致的近似：xy 斜率相加、z 相乘，最后 Normalize。
float3 ElementWaterBlendNormals(float3 normalA, float3 normalB)
{
    return SafeNormalize(float3(
        normalA.xy + normalB.xy,
        normalA.z * normalB.z));
}

// Procedural Marching Cubes Vertex 只有 World Position/Normal，没有美术 Mesh 的 Tangent。
// 这里从单位法线构造一个稳定 Orthonormal Basis：选择与 N 不平行的参考轴，再用 Cross Product
// 得到 T/B。这样 Triplanar Wave 的 Normal From Height 仍可在任意朝向封闭液面上工作。
void ElementWaterBuildOrthonormalBasis(
    float3 normalWS,
    out float3 tangentWS,
    out float3 bitangentWS)
{
    float3 safeNormal = SafeNormalize(normalWS);
    float3 referenceAxis = abs(safeNormal.y) < 0.999f
        ? float3(0.0f, 1.0f, 0.0f)
        : float3(1.0f, 0.0f, 0.0f);
    tangentWS = SafeNormalize(cross(referenceAxis, safeNormal));
    bitangentWS = SafeNormalize(cross(safeNormal, tangentWS));
}

// 生成两层程序化波纹。Panning 公式：UV'(t)=UV+Speed*t。
// Scale 通过 NoisePosition=UV'*Scale 改变频率；Scale 越大，单位 UV 内格子越多。
// 两层采用不同方向和频率，用较少的算术抵消单层 Noise 明显的方向性和重复感。
void ElementWaterWaves_float(
    float2 UV,
    float TimeValue,
    float WaveScaleA,
    float WaveScaleB,
    float2 WaveSpeedA,
    float2 WaveSpeedB,
    out float WaveNoiseA,
    out float WaveNoiseB,
    out float CombinedNoise)
{
    float2 movingUvA = UV + WaveSpeedA * TimeValue;
    float2 movingUvB = UV + WaveSpeedB * TimeValue;
    WaveNoiseA = ElementWaterValueNoise(movingUvA * max(WaveScaleA, 0.0001));
    WaveNoiseB = ElementWaterValueNoise(movingUvB * max(WaveScaleB, 0.0001));
    CombinedNoise = saturate((WaveNoiseA + WaveNoiseB) * 0.5);
}

// 体积水专用波纹入口。与 Legacy UV 入口相比，它在 World Space 的三个正交平面上采样，
// 因此水平顶面、竖直侧面和圆滑水滴都能得到尺度一致的 Noise。
void ElementWaterVolumeWaves_float(
    float3 PositionWS,
    float3 BaseNormalWS,
    float TimeValue,
    float WaveScaleA,
    float WaveScaleB,
    float2 WaveSpeedA,
    float2 WaveSpeedB,
    float VolumeWaveWorldScale,
    float TriplanarSharpness,
    out float WaveNoiseA,
    out float WaveNoiseB,
    out float CombinedNoise)
{
    float2 offsetA = WaveSpeedA * TimeValue;
    float2 offsetB = WaveSpeedB * TimeValue;

    WaveNoiseA = ElementWaterTriplanarNoise(
        PositionWS,
        BaseNormalWS,
        VolumeWaveWorldScale,
        WaveScaleA,
        offsetA,
        TriplanarSharpness);
    WaveNoiseB = ElementWaterTriplanarNoise(
        PositionWS,
        BaseNormalWS,
        VolumeWaveWorldScale,
        WaveScaleB,
        offsetB,
        TriplanarSharpness);
    CombinedNoise = saturate((WaveNoiseA + WaveNoiseB) * 0.5);
}

// 消费已经得到的 CombinedNoise 和 Tangent Space Normal，计算深度与 Fresnel。
// 拆成独立函数让主 Fragment 保持 Wave -> Normal -> Surface 的单向数据流。
void ElementWaterSurface_float(
    float RawDepth,
    float DepthFadeDistance,
    float DepthNoiseStrength,
    float CombinedNoise,
    float3 NormalTS,
    float3 ViewDirectionTS,
    float FresnelPower,
    out float DepthMask,
    out float FresnelMask)
{
    // RawDepth 是 SceneDepth(Eye)-SurfaceDepth(Eye)。归一化公式：
    // depth01=saturate(max(RawDepth,0)/DepthFadeDistance)。
    // 再把 Noise 从 [0,1] 移到约 [-0.5,0.5]，使它既能把局部推深也能推浅；若不减
    // 0.5，Noise 只能单向增加深度。max 防止错误参数导致除零和负深度扩散。
    float safeFadeDistance = max(DepthFadeDistance, 0.0001);
    float normalizedDepth = saturate(max(RawDepth, 0.0) / safeFadeDistance);
    float centeredNoise = CombinedNoise - 0.5;
    DepthMask = saturate(normalizedDepth + centeredNoise * DepthNoiseStrength);

    // 风格化 Fresnel：F=pow(saturate(1-dot(N,V)),Power)。单位向量点积 N·V=cos(theta)：
    // 正视时接近 1，掠射角接近 0。Power 越大，中间值衰减越快，边缘越窄。
    // 两个向量必须位于同一 Tangent Space，否则点积不再代表真实夹角。
    float3 normal = normalize(NormalTS);
    float3 viewDirection = normalize(ViewDirectionTS);
    float fresnelBase = saturate(1.0 - dot(normal, viewDirection));
    FresnelMask = pow(fresnelBase, max(FresnelPower, 0.0001));
}

// 若以后把 Graph Precision 改成 Half，Shader Graph 会寻找 _half 入口。
// 包装函数复用 float 核心，避免两份公式逐渐产生行为差异。
void ElementWaterWaves_half(
    half2 UV,
    half TimeValue,
    half WaveScaleA,
    half WaveScaleB,
    half2 WaveSpeedA,
    half2 WaveSpeedB,
    out half WaveNoiseA,
    out half WaveNoiseB,
    out half CombinedNoise)
{
    float waveNoiseAFloat;
    float waveNoiseBFloat;
    float combinedNoiseFloat;

    ElementWaterWaves_float(
        UV,
        TimeValue,
        WaveScaleA,
        WaveScaleB,
        WaveSpeedA,
        WaveSpeedB,
        waveNoiseAFloat,
        waveNoiseBFloat,
        combinedNoiseFloat);

    WaveNoiseA = (half)waveNoiseAFloat;
    WaveNoiseB = (half)waveNoiseBFloat;
    CombinedNoise = (half)combinedNoiseFloat;
}

void ElementWaterSurface_half(
    half RawDepth,
    half DepthFadeDistance,
    half DepthNoiseStrength,
    half CombinedNoise,
    half3 NormalTS,
    half3 ViewDirectionTS,
    half FresnelPower,
    out half DepthMask,
    out half FresnelMask)
{
    float depthMaskFloat;
    float fresnelMaskFloat;

    ElementWaterSurface_float(
        RawDepth,
        DepthFadeDistance,
        DepthNoiseStrength,
        CombinedNoise,
        NormalTS,
        ViewDirectionTS,
        FresnelPower,
        depthMaskFloat,
        fresnelMaskFloat);

    DepthMask = (half)depthMaskFloat;
    FresnelMask = (half)fresnelMaskFloat;
}

#endif
