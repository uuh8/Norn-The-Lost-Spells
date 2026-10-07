# GPU Fire Parcel 两阶段表现实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. 本项目当前未授权 SubAgent，除非用户针对本任务再次明确批准。

**Goal:** 保留 `MaterialId.Fire` Cell 作为 Gameplay Truth，把现有三层贴图 ParticleSystem 表现替换为 GPU Fire Parcel：生命前段形成轻薄、相连的液态火团，后段无缝扩散成气态软粒子并淡出。

**Architecture:** `Game.ElementField` 继续决定 Fire Cell、Amount、衰减与元素反应；`Game.Rendering` 以固定容量 CPU Snapshot 上传可见 Fire Cell Seed，再在 GPU 固定池中推进只读视觉 Parcel。相同 Parcel State 同时供早期 Density/Marching Cubes Surface 与后期 Gas Splat 两个 Renderer 消费，阶段权重在 40%–60% 生命周期交叉淡化；没有可交互 Gas Material，也不把 Fire 塞进 PBF Incompressibility Solver。

**Tech Stack:** Unity 6.3、URP 17、C#、Compute Shader、`GraphicsBuffer`、World-anchored 3D Density Grid、Marching Cubes、`Graphics.RenderPrimitivesIndirect`、NUnit EditMode/GPU tests、Profiler/Frame Debugger。

**Spec:** 用户于 2026-08-24 确认的“极轻液态火焰→气体→消失”视觉相变；本计划吸收完整设计，不另建 Spec。

## Global Constraints

- Gameplay Fire 仍由 `ElementWorldRuntime/IElementWorldReadOnly` 持有，Rendering 没有写权限。
- 第一阶段 Gas 仅为 Presentation，不产生 Material Cell、Status、碰撞、遮挡或 Reaction。
- 不复用 PBF Rest Density/Lambda/Collision Solver；Fire Parcel 只用 Buoyancy、Drag、Noise 与有限 Source Tether。
- 所有 Buffer 固定容量；`Update/LateUpdate` 禁止 `new`、LINQ、Boxing、Closure 和同步 GPU Readback。
- 旧 `ElementWorldFireRenderer` 保留为 Profile/Scene 可切换回滚路径，GPU Gate 完成前不删除。
- 不要求外部美术资产。颜色、Noise、Surface/Gas 混合均程序化；未来可选 RGBA Noise Texture 只能作为质量增强，不能成为首版依赖。

---

### Task 1：Fire Parcel 生命周期与 Layout Contract

**Files:**
- Create: `Assets/_Project/Scripts/Rendering/ElementField/FireParcelPhaseMath.cs`
- Create: `Assets/_Project/Scripts/Rendering/ElementField/FireParcelGpuLayout.cs`
- Create: `Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderProfile.cs`
- Create: `Assets/_Project/Tests/Rendering/FireParcelPhaseMathTests.cs`
- Create: `Assets/_Project/Tests/Rendering/FireParcelGpuLayoutTests.cs`

**Interfaces:**
- Consumes: `MaterialId.Fire` 与 normalized age `0..1`。
- Produces: `FireParcelPhaseSample Evaluate(float normalizedAge, in FireParcelPhaseSettings settings)`；固定 stride 的 `FireParcelSpawnGpu` 与 `FireParcelStateGpu`。

- [ ] 写 RED：0% 只有 Surface、50% Surface/Gas 同时大于 0、100% 两者为 0；所有权重单调、有限且交叉段连续。
- [ ] 运行 `Game.Rendering.Tests`，确认因类型缺失失败。
- [ ] 实现 `smoothstep` 交叉权重、Radius Growth、Alpha Fade、Buoyancy/Drag Snapshot；禁止把 Rest Density 暴露为 Fire 参数。
- [ ] 用 `Marshal.SizeOf` 锁定 C#/HLSL stride，并加入非法 NaN/负生命周期拒绝测试。
- [ ] 运行 focused tests 与 `dotnet build Game.Rendering.Tests.csproj --no-restore`。

### Task 2：Fire Cell Seed Snapshot 与固定 GPU Parcel Pool

**Files:**
- Create: `Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderer.cs`
- Create: `Assets/_Project/Art/Elemental/Compute/FireParcelLifecycle.compute`
- Create: `Assets/_Project/Tests/Rendering/GpuFireParcelLifecycleTests.cs`

**Interfaces:**
- Consumes: `IElementWorldReadOnly.CopyVisibleChunkKeys`、`TryGetCell`、`FireCellEmissionSampler`。
- Produces: 固定容量 `GraphicsBuffer<FireParcelStateGpu>`、active counter 与 indirect dispatch args。

- [ ] 写 RED：相同 Global Cell/Sequence 产生确定 Seed；满池使用 Ring Overwrite；失效 Cell 不立即删除已出生 Parcel，而让其完成视觉寿命。
- [ ] Renderer 初始化时一次性分配 Visible Keys、CPU Seed Array、Spawn Buffer、State Buffer、Counter/Indirect Args；视觉 Tick 只覆盖已有数组并 `SetData`。
- [ ] Compute 更新 `position += velocity*dt`；Velocity 由 World Up Buoyancy、指数 Drag、解析 Hash Noise 和早期 Source Tether 组成；Tether 在 Gas 权重上升时归零。
- [ ] Pause 时不积累追赶债务；单帧最多提交一次固定 `MaximumVisualDeltaTime`。
- [ ] GPU Test Readback 检查 finite、Age 单调、终点 inactive、Counter 不越容量；Runtime 禁止 Readback。

### Task 3：早期 Liquid-like Fire Density Surface

**Files:**
- Create: `Assets/_Project/Art/Elemental/Compute/FireParcelDensity.compute`
- Create: `Assets/_Project/Art/Elemental/Compute/FireParcelMarchingCubes.compute`
- Create: `Assets/_Project/Art/Elemental/Shaders/ElementFireParcelSurface.shader`
- Create: `Assets/_Project/Scripts/Rendering/ElementField/FireParcelSurfaceResources.cs`
- Create: `Assets/_Project/Scripts/Rendering/ElementField/FireParcelSurfaceGridPlanner.cs`
- Create: `Assets/_Project/Tests/Rendering/GpuFireParcelSurfaceTests.cs`

**Interfaces:**
- Consumes: Parcel Position、Radius、Heat、SurfaceWeight；复用 Task 28/29 的 World-anchor 与局部窗口原则，但使用独立 Fire Profile/Grid 类型，避免把 Fire 参数伪装成 `LiquidRenderSettings`。
- Produces: emissive Surface Triangle Buffer 与 Indirect Draw Args。

- [ ] 写 RED：三个相邻年轻 Parcel 形成一个主要 Density Component；过渡完成后 Surface Triangle Count 归零；孤立 Parcel 不产生无限尖刺或 NaN。
- [ ] Density 使用紧支撑 Smooth Kernel 累加 `SurfaceWeight*Heat`，不执行 Lambda/Rest Density；Iso Level 只属于 Presentation Profile。
- [ ] Marching Cubes 复用既有 Case Table 与 finite/overflow 防线，但使用独立 Buffer，禁止与 Liquid Triangle Counter 混写。
- [ ] Shader 使用 HDR Core/Edge Color、Fresnel 与程序化 World-space Noise；`ZWrite Off`、Additive/Alpha Blend 由 Profile 选择，不采样外部贴图。
- [ ] Frame Debugger 验证单 Surface Draw、World Position/Clip Space 正确、Triangle Overflow=0。

### Task 4：后期 Gas Splat 与无缝交叉淡化

**Files:**
- Create: `Assets/_Project/Art/Elemental/Shaders/ElementFireGasSplat.shader`
- Extend: `Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderer.cs`
- Create: `Assets/_Project/Tests/Rendering/FireParcelGasDrawContractTests.cs`

**Interfaces:**
- Consumes: 同一 Parcel State 的 Position、Radius、Heat、GasWeight。
- Produces: Camera-facing Soft Splat Indirect Draw；不产生 Gameplay Gas。

- [ ] 写 RED：GasWeight=0 不贡献 Alpha；交叉区 SurfaceAlpha+GasAlpha 在容差内连续；过期 Parcel 不进入 Draw Count。
- [ ] Vertex Shader 用 `UNITY_MATRIX_I_V` 的 Camera Right/Up 构造 World-space Quad，Fragment 计算径向 Soft Density、Heat Gradient、Depth Fade 与程序化扰动。
- [ ] Gas Radius 随 Age 增长，Alpha/Heat 衰减；40%–60% 与 Surface 交叉，禁止硬切 Renderer。
- [ ] 使用 GPU Counter/Indirect Args，不为每 Parcel 创建 GameObject、MeshRenderer 或 Material Instance。
- [ ] 低角度、俯视、遮挡物前后验证 Depth Fade；Profiler 检查 Overdraw 与 GPU 时间。

### Task 5：数据资产、Scene 回滚 Gate 与完整解析

**Files:**
- Create through Unity: `Assets/_Project/ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset`
- Create through Unity: `Assets/_Project/Art/Elemental/Materials/M_ElementFireParcelSurface.mat`
- Create through Unity: `Assets/_Project/Art/Elemental/Materials/M_ElementFireGasSplat.mat`
- Modify through Unity: `Assets/_Project/Scenes/P7_DemoRun.unity`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: Task 1–4 完整 GPU Fire Presentation。
- Produces: P7 可切换 Legacy ParticleSystem/GPU Parcel 的人工 Gate。

- [ ] Profile 暴露 Capacity、Spawn Interval、Lifetime、Surface/Gas Transition、Buoyancy、Drag、Tether、Noise、Radius、HDR Colors、Local Bounds 与 Triangle Capacity。
- [ ] Scene 新增一个 GPU Renderer，先禁用旧 `ElementWorldFireRenderer` 但保留引用；GPU Renderer 失败时人工一键回滚旧 Renderer。
- [ ] 运行 `Game.Rendering.Tests`、相关 ElementField Reaction tests 与 `.csproj` builds；记录 Passed/Failed/Skipped，既有失败单列。
- [ ] Play Mode 验证 Fire Cell 位置一致、Water 灭火、Poison ToxicCombustion、暂停、跨 Chunk、满池、相机移动与 10 分钟运行。
- [ ] Profiler 分开记录 Seed Upload、Parcel Lifecycle、Density、MC、Surface Draw、Gas Draw、Overdraw、VRAM 和 `GC Alloc=0 B`。
- [ ] Living Explainer 按“Cell Truth→Parcel Projection→两阶段权重→两个 GPU Draw→回滚/Debug”更新，整个 Phase 在 Editor Gate 前保持“进行中”。

**建议提交序列：**

1. `feat(rendering): define gpu fire parcel lifecycle`
2. `feat(rendering): simulate fixed gpu fire parcel pool`
3. `feat(rendering): reconstruct liquid-like fire surface`
4. `feat(rendering): blend fire parcels into gas splats`
5. `feat(rendering): integrate gpu fire parcel presentation`

### Task 6：Shader Import 修复与卡通火苗密度校准

- 修复 `FireParcelDensity.compute` 使用 HLSL 冲突标识符导致 Kernel 0 无效，以及 Surface Shader 对完整 Struct 使用 Ternary 导致的类型错误。
- Renderer 在分配 Buffer 前检查完整 Kernel Contract；Import 失败时只输出一次架构诊断并停用，禁止逐帧 Dispatch Error。
- Profile 新增 Spawn Probability、统一 Radius Scale、Surface Kernel Scale、Gas Radius/Alpha、Edge Irregularity、Vertical Stretch 与 Emission Scale。
- Surface/Gas 改用普通 Alpha Blend，保留有限 HDR Bloom；Gas Billboard 使用非圆形解析边界扰动与纵向拉伸，减少可辨认的大圆片。
- 本 Task 只修改脚本/Shader；既有 Profile 会通过新增缩放字段得到安全默认值，最终美术值仍由开发者在 Inspector 人工验收。

### Task 7：2026-10-03 火团连通与两阶段外观修复（本轮实现与集中回归完成，整体Phase更新中）

用户本轮已明确要求直接修复“近处碎片火球、远处橙色火苗交替；邻近火团各自离散”。沿用以上只读 Rendering、固定 Pool、Surface→Gas 方案，不修改 Fire Gameplay、反应或伤害，也不增加外部素材依赖。

- 根因证据：96³ Density 每帧遍历最新512 Parcel，约4.53亿次检查；旧活跃 Parcel 被排除。Tetra 的4个交点按发现顺序组成两个三角形，可能交叉/缺面，且双面透明壳未排序。远景 Splat 独立圆边、单 Parcel Fresnel 和近景 Surface 外观不统一。
- Ruling：以固定容量 uint Accumulation Buffer 的 Scatter 取代全空间 Gather；每个活跃 Surface Parcel 只累加附近 Voxel，用 65536 fixed-point 保证 Atomic Add 确定性，另一次 Resolve 写入原 R32 Float Density。成本是每个 Voxel 额外4字节，而非减少粒子或降低画面质量。
- Ruling：Surface Kernel 加入有限 Cohesion 半径、上下连续且上方收尖的形状；相近来源合成一个主体，远离来源保持独立。保留 Parcel 上升、阶段 Blend 与 Gas 尾段；Surface 统一 Outward Winding 和 Cull Back，Gas 仍负责飘散细节。视觉连通不创建或改变 Gameplay 火格。
- 修改：GpuFireParcelRenderer/Profile、FireParcelSurfaceResources、FireParcelDensity/FireParcelMarchingCubes、Surface/Splat Shader、默认 Profile；新增真实 GPU Surface 回归和显式临时视觉验证工具（如需要），Unity 生成 .meta。
- 验证：先实际 GPU RED（旧 Parcel 丢失、外壳封闭、相邻/远离连通）；修改后同一断言 GREEN，再运行 Fire 生命周期/Layout/Seed/Reaction 相关回归。真实 P7 混合施法与近/远景截图，记录帧时间和 GC Alloc。截图/Readback 帧不混入性能窗口。最终恢复 P7 与原设置，不自动提交。
- Editor/回滚：保持原场景中唯一 GPU Fire Renderer 和 Legacy 禁用状态；不保存场景。回滚只撤销本 Task 精确差异，保留已有液体/性能修改。当前 Task 与整体 Phase 仍在验证中，不用历史通过计数代替本轮证据。

2026-10-03 追加执行证据/裁决：真实 GPU RED 为0/3；相邻0.7m两个主体、接边计数错误、Slot700无表面全部复现。Scatter/Topology 后25/25通过。P7实测2048池会在Hold阶段覆盖旧Parcel，扩大默认固定池到8192（256/0.08×2.3≈7360），保留完整Gas；四次实际近景均Overflow=0、Renderer Peak GC=0。透明凹凸前壳需要专用Depth Prepass，不只Cull Back；Depth Material仅初始化创建且归Renderer所有，LOD Blend区域仍Alpha混合。截图中曾有两张全黑ScreenCapture，保留为失败诊断，不作为验收图片。

Ruling：Profiler分拆得到CollectSeeds平均20.2697ms，SeedUpload总20.3198ms，说明主要是逐格坐标/Dictionary读取。已有只读World边界增加可选`IElementWorldChunkReadOnly.CopyChunkCells`，只把Cell值复制进调用者预分配数组；Runtime每Chunk一次Dictionary查找+Array.Copy，Renderer仍按同一Cell顺序和Hash采样。新增接口不泄露Store、内部数组或写能力，现有`IElementWorldReadOnly`消费者/替身仍兼容。新增/修改文件：Data/IElementWorldReadOnly.cs、Runtime/ElementWorldRuntime.cs、Focused Chunk Snapshot tests。该修复在已批准的只读Snapshot与性能优化范围内，不改变Assembly依赖或EventBus架构，不重新请求实施许可。

最终裁决与验收（2026-10-03）：

- 低角度分层对照关闭Surface/Gas Splat后条带仍在，近景主体Alpha从0.92改成1后消失。当前完整Near主体使用完整背景覆盖，LOD区保持交叉淡化、Gas保留0.35软Alpha；不通过删除Gas或压缩生命周期掩盖问题。
- 最终生产代码后，20:47:15—20:47:52（Asia/Shanghai）26个fixture **238/238**，Failed/Skipped/Aborted均0、SceneRestored=true。真实GPU闭壳/连通/旧Slot及生产池预算通过，Snapshot三例与Persistent Fire Source十例通过，原液体/反应集中回归也通过。报告是Direct NUnit/Coroutine JSON，非官方Test Runner XML。中间43/46的三个失败来自工具缺LogScope及重复无参数案例，工具已修复且未忽略Error。
- 最后近景3张、低角度1张、强制Far LOD1张均实际检查；Active4511—5348，Surface/Gas同时存在，Triangle12030—12916（Far窗口移开后3520），Overflow=0、ReadbackError=false、Peak GC=0。Forced Far为分支诊断，不作为自然远景性能。
- 最后真实P7四法术5/10/15组，TimeScale1、133/132/136样本；15组56694 GMU/48页，P50/P95 **40.01/63.11ms**。Fire/液体Renderer GC0B、无液体Overflow/Drop/CPU回退；整场其他路径仍有GC。最终5组CollectSeeds平均2.3508ms（优化前相似5组20.2697ms），15组2.3163ms；不声称固定输入的严格倍数或稳定60FPS。
- 累加Buffer增加3.375MiB，Parcel池增加0.375MiB；Shader/GPU逐Kernel耗时仍未可靠验收，Profiler报告NotSupportedWithNativeGfxJobs及0ns不能当作GPU零耗时。远景竖直Billboard、8m边缘透明LOD、有限Voxel轮廓、Standalone和长期无限累积仍有边界。
- P7已回到Edit Mode、Clean、TimeScale1、后台运行false；22:04只读接线Audit=0失败。没有保存/丢弃/重启关卡，没有提交或使用SubAgent。所有临时相机/LOD/写入均随Stop撤销。
- 原始RED/GREEN、历史工具失败、背景覆盖对照、最终五视角、完整60帧TSV、帧窗口与源码SHA256见[本轮证据](../validation/2026-10-03-fire-cohesion/README.md)。学习文档新增Task F7第41节，包括数据所有权、坐标、Atomic/同步、Tetra、透明合成、生命周期容量、只读Snapshot、文件责任和Editor制作验证步骤。整体阶段继续更新中，不把本轮火焰修复写成全游戏性能验收完成。

### Task 8：统一柔软火舌与宽范围淡出（用户已要求直接实施，当前验证中）

用户偏好远景 Splat 的柔软、上方收尖效果，要求近处也统一并扩大玩家能察觉到的边界范围。当前8m方形 Near Surface 与外侧 Splat 的1.5m互补 Mask 是形态边界根因；它不是 Fire Amount 或 Gameplay 激活域。仅扩大 Density 网格会保留形态差异，并提高 Voxel 成本。本 Task 继续直接执行已授权的 Rendering 调整，不新建计划或重复询问。

- 默认 Profile 启用统一 Surface/Gas Splat；保留两阶段寿命、相位、颜色、运动、池预算和 Fire Gameplay。旧局部 Mesh 模式保留为数据选项，在启动时决定资源布局，方便比较和回滚。
- 统一模式同时绕过 Vertex 的 Near Quad 折叠和 Fragment 的互补 Mask。不要只关闭 Mesh，否则近处 Splat 也被旧 Mask 隐藏。统一模式不分配 Density/Accumulation/Triangle/Depth Material，也不执行相关六次 Compute Dispatch 和两次 Mesh Draw。
- Presentation 使用32m至56m的24m连续 Alpha 淡出，取代可察觉的8m形态切换；Shader 按火源到 Rendering Interest 的距离计算同一 Parcel 权重，避免 Quad 内出现径向切口。World 当前相机可见 AABB 约64m，不扩大 Gameplay 激活域/物理/反应/Streaming。Draw Bounds 包含整个淡出域、Parcel 运动与竖直 Quad，防止旧约14m bounds 提前裁掉远处火。
- 修改 Renderer/Profile/默认 Profile、Splat Shader；Editor 火焰诊断兼容无 Surface Resources，并增加范围/模式证据。无需用户提供模型、贴图或其他素材，复用现有程序化 Shader/Material。
- 先用真实 Shader 像素回读复现8m内外的不一致与缺少宽淡出；修复后检查近/低角度/移开旧 LOD 中心、真实 P7 混合施法与 GC。同一批集中回归覆盖已有238例和本次用例，截图/Readback 不纳入性能窗口。
- 不保存关卡/Profile Play 实例，不重启/丢弃用户场景；测试事务恢复原根对象。最终记录实际数据、限制和可切换步骤，更新学习文档与独立证据目录。

Task8本轮闭环：实际Shader像素RED 0/3（Near被隐藏、44m无淡出、Bounds13.79m）→GREEN 3/3；最终27个fixture **241/241**，Failed/Skipped/Aborted0、SceneRestored=true（21:55:46—21:56:20，Direct NUnit/Coroutine JSON，非官方TestRunner XML）。P7五张非黑实拍均使用两个Splat Draw、无Surface Resources、Surface/Gas同时存在、Peak GC0；实际Bounds65.2411m，距离曲线32/44/56m的像素权重1/.5/0通过。没有额外素材依赖或Gameplay修改。

统一模式省去约18.750MiB局部场/网格资源载荷，剩余火焰Buffer约0.508MiB；无对应六次重建Dispatch/两次Mesh Draw。真实混合5/10/15组分别26590/40722/57354 GMU，32/36/46页，P50/P95分别30.93/55.46、34.43/56.78、42.25/67.57ms；Fire/液体Renderer GC0，无液体Overflow/Drop/CPU回退。15组采样均值2.2420ms，整场其他路径仍有GC。此次是外观统一和无用资源删除，不声称固定输入A/B提升或稳定60FPS；透明Overdraw、极限俯视/Roll、Standalone、长期累积和GPU逐Kernel计时仍有边界。

最终P7 Edit Mode、Clean、TimeScale1、后台false，21:59只读Audit0失败；未保存/丢弃/重启/提交/使用SubAgent。学习文档新增Task F8第42节；新证据[统一柔软火舌索引](../validation/2026-10-03-fire-unified-soft/README.md)保留Task7原归档。该Task实施与本轮检查完成，美术预览已生成，整体阶段继续更新中。

### Task 9：统一软火舌的俯视薄片修复（本轮实施与检查完成，整体Phase更新中）

用户已认可正面外观，明确指出俯视时火焰变成横向薄片；继续按已授权的只读Rendering/统一风格边界修复，不重新请求执行许可，不使用SubAgent。

- 根因：Splat把Camera Up强行替换为World Up，纵向投影随俯视角接近90°收缩到0；即使Fragment软边正确，Rasterization也只有薄条Pixel。仅把Quad转向相机会保留屏幕上的二维火舌，但没有正确的World Up体积投影；增加交叉Quad会加重透明Overdraw。
- 选择单Quad的解析3D Impostor：相机Right/Up构造保守的屏幕Coverage，Fragment把视线转到按半径/纵向比例归一化的火舌空间，计算视线到体积中心的最近点，以同一上方收尖Kernel生成轮廓和颜色。正面对应原二维截面，俯视对应圆润横截面；同一公式连续响应视角，不增加距离LOD切换。
- 使用URP实际View函数支持Perspective/Orthographic，保留场景Depth Fade和32—56m曲线、原Surface/Gas两阶段及GPU固定池。无Density Grid、Raymarch循环、新贴图/模型或额外Draw/Buffer；GPU Pixel算术增加，需要按实际画面和Profiler记录代价，不能先宣称无成本。
- 修改Splat Shader；扩展真实GPU像素fixture检查Surface/Gas在0°/60°/80°/90°时的Coverage和90° Roll稳定性，保留前轮分界/淡出断言。Editor临时相机增加高角度/正俯视/Roll截图，工具不保存关卡。
- 先验证旧Shader的薄片RED及基线正面/俯视实拍，再实现并做GREEN；最终一次集中回归、P7实际角度截图与有限压力窗口。所有临时状态恢复后更新Task F9学习文档和独立证据，历史241只属于Task8。

Task9执行闭环：旧Shader三个既有检查通过、两个新增视角检查失败，80°覆盖约17%、90°为0；修复后5/5通过。固定正面白色Orthographic轮廓保持54×103/能量1297.3323；年轻阶段90°为54×54/742.0777，Gas为32×32，正俯视Roll0/45/90°稳定。最后代码后27个fixture **243/243**（23:07:06—23:07:42），Failed/Skipped/Aborted0、SceneRestored=true，非官方TestRunner XML。P7前后各8张角度图保留对照，修复后两阶段/两个Draw、无Surface资源、ReadbackError=false、Renderer Peak GC0；黑图重试另存为失败证据。

最新15组四法术45203 GMU/50页、134有效样本，P50/P95 **44.92/77.32ms**，无液体Overflow/Drop/CPU回退，Renderer GC0。Shader增加每Pixel向量运算与俯视有效Coverage，没有额外Buffer/Draw/交叉贴片；不能宣称GPU零成本或稳定60FPS。Perspective代理深度/最近点Kernel仍为近似，完整体积积分、Standalone/长期累积/可靠GPU时间仍有限制。P7恢复Edit Mode/Clean/TimeScale1/后台false，23:11只读Audit0失败，场景磁盘SHA256与Task8一致；无保存/丢弃/重启/提交/SubAgent。学习文档Task F9第43节与[本轮索引](../validation/2026-10-03-fire-view-projection/README.md)已同步。

