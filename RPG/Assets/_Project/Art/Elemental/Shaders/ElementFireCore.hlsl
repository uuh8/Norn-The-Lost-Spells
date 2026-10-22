#ifndef ELEMENT_FIRE_CORE_INCLUDED
#define ELEMENT_FIRE_CORE_INCLUDED

// 这个文件只保存“火焰形状的数学”，不声明 URP Pass、Blend Mode 或 Material 属性。
// 这样后续 Alpha Blend 的 Fire Body 与 Additive 的 Fire Core 可以复用同一套轮廓公式，
// 避免两个 Shader 在继续加入 Noise、流动和扭曲后逐渐产生不一致。

// 把二维坐标稳定地映射到 [0,1) 的伪随机数。同一个坐标每次得到相同结果，
// 所以动画来自“采样坐标随 Time 移动”，不是每帧重新掷随机数；后者会产生闪烁而非连续流动。
float ElementFireHash21(float2 position)
{
    position = frac(position * float2(123.34, 456.21));
    position += dot(position, position + 45.32);
    return frac(position.x * position.y);
}

// 二维 Value Noise：先用 floor 找到整数格子，再对四个角点的 Hash 值做 Bilinear Interpolation。
// smooth=f*f*(3-2*f) 是 Hermite 曲线，它让格子边界处的一阶变化更平缓，避免方块状跳变。
// 这里得到的是连续标量场，而不是火焰纹理；它稍后只作为轮廓位移和宽度扰动的控制信号。
float ElementFireValueNoise(float2 position)
{
    float2 cell = floor(position);
    float2 local = frac(position);
    float2 smooth = local * local * (3.0 - 2.0 * local);

    float bottomLeft = ElementFireHash21(cell);
    float bottomRight = ElementFireHash21(cell + float2(1.0, 0.0));
    float topLeft = ElementFireHash21(cell + float2(0.0, 1.0));
    float topRight = ElementFireHash21(cell + float2(1.0, 1.0));

    float bottom = lerp(bottomLeft, bottomRight, smooth.x);
    float top = lerp(topLeft, topRight, smooth.x);
    return lerp(bottom, top, smooth.y);
}

// 轮廓公共核心。CenterOffset 移动某一高度处的轮廓中线，WidthOffset 改变该高度的半宽。
// Step 5A 把两个 Offset 都传 0；Step 5B 再用随时间变化的 Noise 驱动它们。
float ElementFireShapeFromOffsets(
    float2 uv,
    float centerOffset,
    float widthOffset,
    float baseWidth,
    float tipWidth,
    float shapePower,
    float edgeSoftness,
    float baseFade,
    float tipFadeStart)
{
    float height01 = saturate(uv.y);
    float distanceFromCenter = abs((uv.x * 2.0 - 1.0) - centerOffset);

    float shapedHeight = pow(height01, max(shapePower, 0.0001));
    float halfWidth = saturate(lerp(
        saturate(baseWidth),
        saturate(tipWidth),
        shapedHeight) + widthOffset);

    float safeSoftness = max(edgeSoftness, 0.0001);
    float innerEdge = max(halfWidth - safeSoftness, 0.0);
    float horizontalMask = 1.0 - smoothstep(
        innerEdge,
        max(halfWidth, 0.0001),
        distanceFromCenter);

    float bottomMask = smoothstep(0.0, max(baseFade, 0.0001), height01);
    float safeTipFadeStart = min(saturate(tipFadeStart), 0.9999);
    float topMask = 1.0 - smoothstep(safeTipFadeStart, 1.0, height01);
    return saturate(horizontalMask * bottomMask * topMask);
}

// 输入 UV 来自 Quad，范围通常是 [0,1]×[0,1]：
//   UV.x=0/1 分别是左右边界，UV.x=0.5 是中线；
//   UV.y=0 是火焰底部，UV.y=1 是火焰顶部。
//
// 当前函数只解决 Step 5A 的静态轮廓，故意不使用 _Time 或 Noise。输出值是 [0,1] Mask：
//   0 = 完全透明；1 = 完全属于火焰；中间值 = 柔和边缘。
float ElementFireStaticShape(
    float2 uv,
    float baseWidth,
    float tipWidth,
    float shapePower,
    float edgeSoftness,
    float baseFade,
    float tipFadeStart)
{
    return ElementFireShapeFromOffsets(
        uv,
        0.0,
        0.0,
        baseWidth,
        tipWidth,
        shapePower,
        edgeSoftness,
        baseFade,
        tipFadeStart);
}

// Step 5B 的动画轮廓。两个 Noise 层使用不同 Scale 和 Upward Speed：
//   Noise A（低频）主要推动轮廓中线，产生大尺度摇摆；
//   Noise B（高频）主要改变局部宽度，打破过于光滑的几何边缘。
//
// 采样公式：Noise(UV - float2(0, Speed * Time) * Scale)。
// 注意“采样坐标向下”时，观察到的图案反而向上：若某特征原本位于 y0，
// y-Speed*t=y0 可得 y=y0+Speed*t，因此屏幕上的特征随时间向 +Y 移动。
void ElementFireAnimatedShape(
    float2 uv,
    float timeValue,
    float baseWidth,
    float tipWidth,
    float shapePower,
    float edgeSoftness,
    float baseFade,
    float tipFadeStart,
    float noiseScaleA,
    float noiseScaleB,
    float upwardSpeedA,
    float upwardSpeedB,
    float distortionStrength,
    float detailStrength,
    out float shapeMask,
    out float combinedNoise)
{
    float2 movingUvA = uv - float2(0.0, max(upwardSpeedA, 0.0) * timeValue);

    // 第二层镜像 X 并加常量偏移，避免两层在相同格点上产生过强相关性。
    // 3.17/5.31 只是打散采样位置的 Seed，不是任何燃烧物理常数。
    float2 movingUvB = float2(1.0 - uv.x + 3.17, uv.y + 5.31)
        - float2(0.0, max(upwardSpeedB, 0.0) * timeValue);

    float noiseA = ElementFireValueNoise(movingUvA * max(noiseScaleA, 0.0001));
    float noiseB = ElementFireValueNoise(movingUvB * max(noiseScaleB, 0.0001));
    combinedNoise = saturate(noiseA * 0.65 + noiseB * 0.35);

    // heightInfluence=y²：底部 y≈0 时几乎不动，顶部 y≈1 时得到完整扰动。
    // 这不是流体力学，而是针对火焰“根部附着、顶部活跃”观察特征的风格化约束。
    float height01 = saturate(uv.y);
    float heightInfluence = height01 * height01;
    float centeredNoiseA = noiseA * 2.0 - 1.0;
    float centeredNoiseB = noiseB * 2.0 - 1.0;

    float centerOffset = centeredNoiseA * distortionStrength * heightInfluence;
    float widthOffset = centeredNoiseB * detailStrength * heightInfluence;

    shapeMask = ElementFireShapeFromOffsets(
        uv,
        centerOffset,
        widthOffset,
        baseWidth,
        tipWidth,
        shapePower,
        edgeSoftness,
        baseFade,
        tipFadeStart);
}

// 把“越靠近底部通常越热”和“Noise 中上升的局部高温团”合并为 Temperature Mask。
// baseHeat=1-y 只是风格化高度梯度；combinedNoise 是 Step 5B 已计算的动态标量场。
// NoiseWeight=0 时只看高度，=1 时只看 Noise。Contrast 使用 pow 重新分布灰度：
// Contrast>1 会压低中间值，让高温区域更集中；<1 会扩大高温区域。
float ElementFireTemperatureMask(
    float height01,
    float combinedNoise,
    float noiseWeight,
    float contrast)
{
    float baseHeat = 1.0 - saturate(height01);
    float mixedHeat = lerp(baseHeat, saturate(combinedNoise), saturate(noiseWeight));
    return pow(saturate(mixedHeat), max(contrast, 0.0001));
}

#endif
