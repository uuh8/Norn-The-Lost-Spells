#ifndef GAME_STATUS_FRESNEL_INCLUDED
#define GAME_STATUS_FRESNEL_INCLUDED

// 这个文件只负责一段很小的图形计算：根据表面法线 N 与视线方向 V 的夹角，
// 生成“正面接近 0、轮廓接近 1”的 Fresnel Mask。
//
// Shader Graph 的 Custom Function 节点在 File 模式下会根据节点精度，
// 自动寻找“函数名_float”或“函数名_half”。因此这里同时提供两种版本：
// - float：32-bit 浮点，适合第一次学习、桌面平台和公式对照；
// - half：通常是 16-bit 浮点，部分移动 GPU 上吞吐更高，但精度范围更小。
// 当前 Graph 的 Custom Function 节点应先显式选择 Float，便于与节点版比较。

/// <summary>
/// 计算风格化 Wet Fresnel Mask（Float 精度）。
/// </summary>
/// <param name="NormalWS">World Space 的表面法线方向。</param>
/// <param name="ViewDirectionWS">World Space 中从表面指向 Camera 的方向。</param>
/// <param name="Power">控制边缘宽度；越大，亮边越集中于轮廓。</param>
/// <param name="Fresnel">输出 0~1 Mask：正面接近 0，掠射角/轮廓接近 1。</param>
void StatusFresnel_float(
    float3 NormalWS,
    float3 ViewDirectionWS,
    float Power,
    out float Fresnel)
{
    // 点积只有在两个向量处于同一个 Coordinate Space 时才表示夹角关系。
    // 调用端必须同时传入 World Space 的 Normal 与 View Direction。
    // normalize 消除向量长度，让 dot(N,V) 可以解释为 cos(theta)。
    float3 normal = normalize(NormalWS);
    float3 viewDirection = normalize(ViewDirectionWS);

    // 正对 Camera 时 N·V 接近 1，轮廓处接近 0。
    // saturate 等价于 clamp(x, 0, 1)，用于防御插值和浮点误差造成的越界值。
    float ndotv = saturate(dot(normal, viewDirection));

    // One Minus 把“正面亮”反转成“轮廓亮”。
    // Power 越大，中间灰度衰减越快，最终亮边越窄。
    // max 防止 0 或负指数造成 0^0、无穷大等不稳定结果。
    Fresnel = pow(1.0 - ndotv, max(Power, 0.0001));
}

/// <summary>
/// 与 Float 版相同公式的 Half 精度版本。
/// 目前不是为了提前优化，而是让节点以后切换 Precision 时仍有明确实现。
/// 真正是否使用 Half，应通过目标平台的 GPU Profiler 和画面误差比较决定。
/// </summary>
void StatusFresnel_half(
    half3 NormalWS,
    half3 ViewDirectionWS,
    half Power,
    out half Fresnel)
{
    half3 normal = normalize(NormalWS);
    half3 viewDirection = normalize(ViewDirectionWS);
    half ndotv = saturate(dot(normal, viewDirection));
    Fresnel = pow((half)1.0 - ndotv, max(Power, (half)0.0001));
}

#endif
