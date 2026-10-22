# P1：从一个像素开始理解 URP Lit 与 Shader Graph

> 创建时间：2026-07-11 19:03  
> 本文不是 Shader Graph 节点手册，而是对我们刚才实际完成的 Base Map、Normal Map、Metallic/Smoothness、AO 和 Emission 操作进行图形学溯源。目标是让第一次使用 Shader Graph 的学习者明白：每一根线为什么存在，它最终改变了 GPU 的哪一步计算。

---

## 一、我们不是在“给球贴图”，而是在定义每个像素如何被计算

刚才我们创建了 `SG_CharacterStatusLit`，又创建了 `M_StatusLab_Neutral`，最后把 Material 交给一个球体。表面上看，这像是在 Unity 里给球换皮肤；从图形学角度看，我们实际上是在回答一个更底层的问题：

> 当 GPU 准备绘制球体覆盖到屏幕上的某个像素时，这个像素应该是什么颜色、朝向、金属度、光滑度和自发光强度？

一个球体模型并不是由“像素”组成的。Mesh 只保存有限数量的顶点，以及顶点之间如何组成三角形。每个顶点通常携带位置、法线、切线和 UV 等数据。**CPU 把 Mesh、Material、Transform、灯光等信息组织成绘制命令，交给 URP；GPU 随后把三角形变成屏幕上的大量 Fragment**。为了便于入门，可以先把 Fragment 理解为“候选像素”，尽管它与最终屏幕像素并不总是一一对应。

```text
Mesh 顶点
  ↓ Vertex Shader：把顶点变换到屏幕相关空间
三角形
  ↓ Rasterization：找出三角形覆盖了哪些屏幕位置，并插值顶点数据
Fragments
  ↓ Fragment Shader：计算表面材质和光照
Depth Test 等测试
  ↓
屏幕颜色
```

Shader Graph 中间的 `Vertex` 和 `Fragment` 两个区域，正对应这条管线中的两个可编程阶段。我们目前没有修改 Vertex 的 Position，所以没有改变球体形状；我们一直在连接 Fragment 的输入，**因为当前任务是改变表面如何着色，而不是改变几何体**。

这里还需要区分四种经常被混在一起的对象。

1. Shader 是 GPU 要执行的计算规则；
2. Texture 是提供给计算的二维数据；
3. Material 是“选用哪个 Shader，并给它传哪些 Texture、颜色和数值”的参数集合；
4. Renderer 则把 Mesh 与 Material 一起交给渲染管线。

用 C# 类比，Shader 更像一个函数或类的定义，Material 更像一组具体实参或一个实例。

```text
SG_CharacterStatusLit：规定怎样采样和计算
M_StatusLab_Neutral：指定 Costumes_Albedo、Costumes_Normal 等具体输入
Sphere/Mesh：提供顶点、三角形、法线和 UV
MeshRenderer：把 Mesh 与 Material 提交给 URP
```

因此，“我们已经创建了 Shader，为什么还要找贴图”这个疑问的答案是：Shader 只规定“我会怎样使用一张 Normal Map”，并不包含某个角色的具体 Normal Map。**同一个 Shader 必须能够服务 Wizard、Enemy、木头或石头，而各自的 Material 会向它提供不同纹理**。

Shader Graph 的 Blackboard 就是这份函数的“参数表”。例如 Display Name 为 `Base Color` 的属性，其 Reference 是 `_BaseColor`。Display Name 主要给人阅读，Reference 才是生成 Shader 后真正的属性名，也是 C# 通过 `Shader.PropertyToID("_BaseColor")` 寻址的名字。`_Base_Color` 和 `_BaseColor` 对 GPU 来说是两个完全不同的变量，这就是为什么我们之前必须修正那个多余的下划线。

当你把 Blackboard 属性拖到画布，出现的只是一个“读取参数”的节点；当你把它连接到另一个节点，才形成数据依赖。Shader Graph 并不是按节点在画布上的左右位置执行，而是根据连线形成的有向图生成 Shader 代码。节点放在上面还是下面不会改变结果，连线才会。

---

## 二、Base Map 链路背后的核心：UV、插值和逐通道乘法

我们完成的第一条链路是：

```text
UV0 → Sample Texture 2D(_BaseMap)
采样颜色 × _BaseColor
→ Fragment/Base Color
```

要理解它，必须先回答 GPU 怎样知道“球面上的这个位置应该读取图片中的哪个位置”。Texture 本质是一张二维数组，但 Mesh 位于三维空间。两者之间的映射由 UV 完成。U、V 是二维纹理坐标，常见范围为 `[0,1]`：

```text
UV = (0,0)：纹理左下角
UV = (1,0)：纹理右下角
UV = (0,1)：纹理左上角
UV = (1,1)：纹理右上角
```

模型制作阶段会把三维表面“剪开并摊平”，为每个顶点保存 UV。球体顶点有一套适合球面的 UV，角色服装有一套适合身体部件和衣服图集的 UV。它们使用同一张纹理时会读取完全不同的位置，所以角色服装贴图放到球上显得杂乱，并不说明 Shader 错了，而是说明这张 Texture 原本就不是按 Sphere 的 UV 绘制的。

顶点数量远少于屏幕上的 Fragment 数量，GPU 不可能只在顶点处读取纹理。**Rasterization 会对顶点 UV 做透视正确插值**。假设三角形三个顶点的 UV 是 `uv0`、`uv1`、`uv2`，三角形内部某个 Fragment 会依据它相对三个顶点的位置得到一组权重，再计算插值 UV。忽略透视修正细节时，可以先用重心坐标理解：

```text
uv = λ0 × uv0 + λ1 × uv1 + λ2 × uv2
λ0 + λ1 + λ2 = 1
```

这就是为什么只有三个顶点，三角形内部仍能出现连续纹理，而不是三个突兀色块。`Sample Texture 2D` 接收到这个被插值的 UV 后，才从 `_BaseMap` 读取当前位置的 RGBA 数据。纹理过滤还会在邻近 Texel 之间**采样并插值**，但当前先把它理解为 `color = Texture(UV)` 即可。

我们没有把采样结果直接送进 Base Color，而是先乘 `_BaseColor`：

```text
FinalBaseColor = Sample(_BaseMap, UV).rgb × _BaseColor.rgb
```

这是**逐通道乘法**。假设纹理某处是灰色 `(0.5, 0.5, 0.5)`，Material 的 Base Color 是红色 `(1, 0, 0)`，结果为：

```text
(0.5×1, 0.5×0, 0.5×0) = (0.5, 0, 0)
```

它不是用红色覆盖纹理，而是在保留纹理明暗的前提下进行染色。Base Color 默认设置为白色 `(1,1,1)`，原因不是审美，而是乘法单位元：

```text
C × 1 = C
```

任何贴图颜色乘白色都保持不变；乘黑色则全部归零。这是一个很基础但会反复出现的 Shader 思维：先寻找“关闭某个效果时的单位元”。乘法效果的关闭值通常是 1，加法效果的关闭值通常是 0，Lerp 的关闭端则取决于 A、B 的定义。

我们最终把这个结果连接到 Lit Master Stack 的 `Base Color`。这并不等于最终屏幕颜色。Base Color 只是材质交给光照模型的输入之一。为了形成直觉，可以先看最简单的  **Lambert 漫反射**：

```text
Diffuse = BaseColor × LightColor × max(dot(N, L), 0)
```

`N` 是单位表面法线，`L` 是指向灯光的单位方向。两个单位向量的点积满足：

```text
dot(N,L) = |N||L|cosθ = cosθ
```

表面正对灯光时 `θ≈0°`，结果接近 1；灯光沿表面掠过时 `θ≈90°`，结果接近 0；灯光从表面背后照来时结果为负，用 `max(...,0)` 截断。你旋转 Directional Light 时看到球体明暗区域移动，背后就是类似的几何关系。

URP Lit 实际使用的 PBR 光照远比 Lambert 复杂，还会处理镜面反射、阴影、间接光和环境反射；但 Base Color 仍然只是“表面属性”，而不是“最终像素颜色”。选择 Lit Shader Graph 的价值就在于：我们提供材质参数，URP 帮我们完成成熟的光照部分。

---

## 三、Normal Map 为什么能让没有新增三角形的表面看起来凹凸

第二条链路是：

```text
UV0 → Sample Texture 2D(_BumpMap, Type=Normal)
→ Normal Strength(_BumpScale)
→ Fragment/Normal (Tangent Space)
```

在上一节的简化光照公式里，明暗取决于 `dot(N,L)`。这意味着：只要改变参与光照的法线 `N`，即使不改变顶点位置，也能改变局部明暗，让视觉系统误以为表面方向发生了细小起伏。Normal Map 正是为每个 Fragment 提供一个经过扰动的法线。

这是一种“改变光照，不改变轮廓”的技巧。它能表现衣服纹理、划痕和小凹槽，但从侧面看，物体外轮廓仍然是原来的平滑球面。如果真的需要改变轮廓，就要增加几何体、使用位移或其他更昂贵的方案。

Normal 是方向向量，分量通常位于 `[-1,1]`；普通纹理通道适合保存 `[0,1]`。因此 Normal Map 会把方向编码到颜色范围。简化编码与解码可以写成：

```text
encoded = normal × 0.5 + 0.5
normal  = encoded × 2 - 1
```

在 Tangent Space 中，未经扰动、垂直表面的法线约为：

```text
Ntangent = (0, 0, 1)
```

编码后约为：

```text
encoded = (0.5, 0.5, 1)
```

RGB 中蓝色通道最高，因此 Normal Map 看起来普遍是蓝紫色。这个蓝色不是要显示到角色身上的颜色，而是方向数据的可视化。Shader Graph 节点 Preview 显示蓝色，只说明它当前在预览一个接近 `(0,0,1)` 的方向。

为什么节点必须设置 `Type = Normal`？因为 Normal Map 不是普通颜色。Unity 可能采用特定纹理压缩和法线编码，Shader 采样后需要正确解码；若按普通颜色采样，得到的 RGB 不能直接当作合法单位法线。`Type = Normal` 告诉 Shader Graph：这里读取的是方向数据，请生成对应的解码逻辑。

接下来要理解 Tangent Space。一个角色在世界中会旋转，身体不同三角形也朝向不同方向。如果 Normal Map 直接存 World Space 方向，同一张贴图几乎无法复用于旋转后的模型。于是每个顶点表面建立一个局部坐标系：

```text
Tangent   T：沿纹理 U 增大的方向
Bitangent B：沿纹理 V 增大的方向
Normal    N：垂直表面的方向
```

这三个方向形成 TBN 基。Normal Map 保存的是相对于当前表面的局部方向，URP 再把它变换到光照计算需要的空间：

```text
TBN = [T B N]
Nworld = normalize(TBN × Ntangent)
```

因此同一个“向右凸起”的切线空间法线，贴在角色胸口、手臂或背部时都会相对各自表面正确凸起，而不是固定指向世界右侧。这也是“坐标空间必须一致”的第一个实例：向量只有在同一 Coordinate Space 中做点积或比较才有几何意义。

`Normal Strength` 并不是简单给显示颜色乘一个数，而是调节法线相对平坦方向的偏移，再重新得到可用于光照的方向。直观上：

```text
Strength = 0：接近平坦法线 (0,0,1)
Strength = 1：使用贴图原始凹凸
Strength > 1：夸大 XY 偏移，凹凸更强，但可能失真
```

把 Strength 从 0 改为 1 后，Mesh 没有增加顶点，Base Map 也没变化；只有灯光在细节处的响应变了。这正好证明 Normal Map 修改的是光照使用的方向，不是表面颜色。

---

## 四、Metallic、Smoothness、AO 和 Emission 如何共同描述一种“材质”

完成 Base Color 和 Normal 后，我们仍然只能回答“表面是什么颜色”和“微观朝向如何变化”。木头、布、皮肤和金属即使颜色相近，面对灯光时也完全不同。PBR 的目标，是用具有一定物理含义的参数描述这种差异，让材质在不同光照环境下仍然保持相对可信。

我们使用的是 Metallic Workflow。它把表面粗略分成金属与非金属，并用 Smoothness 描述微表面分布。

Metallic 通常在 `[0,1]`：0 表示非金属，1 表示金属。非金属表面会产生明显漫反射，例如布料把进入表面的光散射后再离开；它的镜面反射通常接近无色。金属则几乎不产生普通漫反射，Base Color 更多参与有色镜面反射。现实中大多数像素更接近 0 或 1，中间值通常出现在纹理过滤、边界、污损或混合材质中。

Smoothness 也在 `[0,1]`，它描述微观表面朝向分布有多集中。不要把它理解成“亮度”：

```text
低 Smoothness：微表面方向分散，高光宽而模糊
高 Smoothness：微表面方向集中，高光窄而清晰
```

同样一束光照射到粗糙表面，会被许多略有差异的微表面方向反射到较宽范围；照射到光滑表面时，反射方向更集中，所以高光更锐利。Smoothness 改变的是 BRDF 中高光分布，不是直接给颜色加一个数。

本项目的 `Costumes_MetallicSmoothness.png` 把两种标量装进同一张 RGBA Texture：

```text
R 通道：Metallic
A 通道：Smoothness
```

这叫 Channel Packing。一次 Texture Sample 原本就能返回四个通道，如果每种单通道数据都单独用一张 RGBA 纹理，会浪费内存和采样。我们让 Sample 的 R 连接 Metallic，同时让 A 乘 `_Smoothness` 后连接 Smoothness：

```text
Metallic = Sample(_MetallicGlossMap).r
Smoothness = Sample(_MetallicGlossMap).a × _Smoothness
```

`_Smoothness` 是整体缩放系数。贴图保留“哪些区域更光滑”的相对差异，Material 参数控制整件材质总体需要多光滑。假设某像素 Alpha 为 0.8：

```text
_Smoothness = 1.0 → 0.8
_Smoothness = 0.5 → 0.4
_Smoothness = 0.0 → 0
```

这里再次出现乘法单位元：默认值 1 表示保留原贴图，0 表示关闭光滑度。

AO 的职责不同。Ambient Occlusion 表示环境光进入局部缝隙有多困难。它不是某盏灯实时投下的阴影，而是对褶皱、接缝和接触区域的低频遮蔽近似：

```text
AO = 1：不遮蔽
AO = 0：遮蔽最强
```

我们使用 `Costumes_AO.png` 的 G 通道，并设计一个 `_OcclusionStrength`。需求是：Strength 为 0 时完全关闭 AO，输出必须回到“无遮挡”的 1；Strength 为 1 时完整使用贴图。于是公式自然是：

```text
FinalAO = Lerp(1, AOMap.g, Strength)
```

Lerp 的完整公式为：

```text
Lerp(A,B,T) = A×(1-T) + B×T
```

代入两个边界值：

```text
T=0：Result=A=1
T=1：Result=B=AOMap.g
```

这解释了为什么不能直接使用 `AO × Strength`。如果 Strength 为 0，乘法结果也是 0，而 AO=0 恰恰代表最强遮蔽；“关闭效果”反而把表面压到最暗。选择 Multiply 还是 Lerp，不是凭节点习惯，而要先写清楚两个端点分别应该是什么。

Emission 则不属于反射参数。Base Color、Metallic、Smoothness 和 Normal 主要告诉 URP“外界光照到这里后如何反射”；Emission 直接向最终表面结果贡献高亮颜色：

```text
Emission = Sample(_EmissionMap).rgb × _EmissionColor.rgb
```

Emission Map 决定哪里能发光，Emission Color 决定颜色和强度。黑色区域乘任何颜色仍为 0，因此不发光；白色区域保留完整 Emission Color。默认 Emission Color 是黑色，所以即使默认纹理是白色，无状态材质也不会发光。

我们把 Emission Color 设置为 HDR，是因为普通 `[0,1]` 颜色无法表达“比屏幕普通白色更亮”的线性颜色值。HDR 可以提供大于 1 的分量，例如 `(4, 0.5, 0)`。这些高亮值随后可能被 Tone Mapping 映射到屏幕范围，也能成为 Bloom 的输入。

Emission 和 Bloom 必须分开理解。Emission 在材质 Shader 中生成高亮像素；Bloom 是屏幕后处理，根据亮度阈值取出高亮区域，再做降采样、模糊和叠加，使亮色向相邻屏幕像素扩散。没有足够亮的 Emission，Bloom 无内容可扩散；有 Emission 但没 Bloom，物体本身会亮，却不一定出现外围光晕。Emission 通常也不会自动让附近墙壁获得实时照明，因为那是另一个光照问题。

把这几类输入放在一起，Material 才不只是“一张颜色图片”，而是一组对光的响应规则：

```text
Base Color：表面基础颜色
Normal：每个 Fragment 的微观朝向
Metallic：金属/非金属反射模型倾向
Smoothness：镜面反射分布宽窄
AO：局部环境光遮蔽
Emission：不依赖入射光的主动颜色贡献
```

---

## 五、回看整张 Graph：每一根线都在完成一段明确的数学关系

现在可以不再把 Graph 看作一堆节点，而把它读成一组并行的数据方程：

```text
uv = 当前 Fragment 从 Mesh 插值得到的 UV0

baseSample = Sample(_BaseMap, uv)
baseColor = baseSample.rgb × _BaseColor.rgb

normalEncoded = SampleNormal(_BumpMap, uv)
normalTS = ApplyNormalStrength(normalEncoded, _BumpScale)

mask = Sample(_MetallicGlossMap, uv)
metallic = mask.r
smoothness = mask.a × _Smoothness

aoSample = Sample(_OcclusionMap, uv).g
occlusion = Lerp(1, aoSample, _OcclusionStrength)

emissionSample = Sample(_EmissionMap, uv).rgb
emission = emissionSample × _EmissionColor.rgb
```

这些方程的结果被送入 URP Lit。URP 再结合世界空间法线、视线方向、主灯与附加灯、阴影、环境光和反射探针，计算屏幕输出。我们没有从零实现完整 BRDF，而是在为已有 Lit 模型准备正确的 Surface Inputs。

也可以从 Debug 角度反向阅读：如果球体变成纯黑，先检查 Base Map 与 Base Color 的乘法；如果物体变蓝，检查 Normal 是否误接到 Base Color，或 Normal Sample 是否没设为 Normal；如果金属区域完全没有金属感，检查是否用了正确的 `RegularPBR/Costumes_MetallicSmoothness`，以及 R 通道是否接入 Metallic；如果 AO Strength 设为 0 后反而变黑，检查 Lerp 的 A 是否为 1；如果 Emission 很亮却没有光晕，检查 Bloom，而不是继续把 Emission 数值无限调高。

到这里，我们完成的并不是最终状态 Shader，而是一条可验证的 PBR 基线。后续 Burning 和 Wet 必须建立在这些结果上，而不是替换它们。例如 Burning 会生成一个 `burnMask`，用它在原 Base Color 和焦黑颜色之间 Lerp，并向原 Emission 加入高温颜色；Wet 会提高 Smoothness，并使用法线与视线关系生成 Fresnel 边缘。原有 Base Map、Normal 和 PBR 数据仍然参与计算。

这也解释了为什么 P1 先做这一步：状态视觉不是给角色套一层不透明滤镜，而是在保持材质身份的前提下，按 Gameplay 强度连续修改若干物理和非物理表面参数。只有真正理解当前这些输入，后面才有能力判断“潮湿应该改变颜色还是 Smoothness”“燃烧裂纹应该影响 Base Color 还是 Emission”“某个向量是否处在正确 Coordinate Space”。

下一步建立 `_BurningIntensity`、`_WetIntensity` 等属性时，它们只是从 CPU 进入 Shader 的标量。真正的图形工作，是把这些标量转化为随空间变化的 Mask，再让 Mask 参与上述方程。届时我们会继续从公式出发理解 Object Space、Time、Noise、Threshold 和 Lerp，而不是只记住节点连接顺序。
