#ifndef FIRE_PARCEL_APPEARANCE_INCLUDED
#define FIRE_PARCEL_APPEARANCE_INCLUDED

// 近处 Surface 与远处/Gas Billboard 共用 World-space 的颜色流动。
// 颜色不再按三角形或粒子身份生成轮廓，从而不把内部相邻 Parcel 画出各自的硬边。
half3 FireParcelColor(float3 positionWS, float core, float visualTime, half3 edgeColor, half3 coreColor)
{
    float wave = sin(positionWS.y * 5.5 - visualTime * 5.0
        + sin(positionWS.x * 3.1 + positionWS.z * 2.7) * 0.8);
    float heat = saturate(0.18 + core * 0.74 + wave * 0.08);
    return lerp(edgeColor, coreColor, heat);
}

float FireParcelNearWeight(float3 positionWS, float3 center, float3 halfSize, float blendWidth)
{
    float2 distanceToEdge = halfSize.xz - abs(positionWS.xz - center.xz);
    return smoothstep(0.0, max(blendWidth, 0.0001), min(distanceToEdge.x, distanceToEdge.y));
}

float3 FireParcelTipWarp(float3 positionWS, float3 normalWS, float visualTime)
{
    // 只让上端火舌起伏；共享 World-space 函数让焊接顶点得到相同位移，不沿三角形/粒子边界裂开。
    // Depth/Color 两次 Vertex Stage 都调用它，保证 Depth Prepass 与实际可见几何完全吻合。
    float tip = saturate(normalWS.y * 2.0);
    float phase = positionWS.x * 4.7 + positionWS.z * 3.3;
    positionWS.x += sin(phase + visualTime * 5.8) * 0.06 * tip;
    positionWS.y += sin(phase * 0.73 - visualTime * 5.1) * 0.09 * tip;
    return positionWS;
}
#endif
