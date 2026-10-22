# GPU PBF 移除 LOD 并恢复原流体 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 本计划不得使用 SubAgent，除非用户针对本次任务重新明确授权。

**Goal:** 不重写 GPU PBF 或流体表面算法，精准移除 Task 29–45 引入的液体 Near/Mid/Far LOD、Particle Splat 与交叉权重，恢复已经实现并被用户认可过的单套 `Density Grid → Marching Cubes → Surface Draw` 流体表现。

**Architecture:** `Game.ElementField` 的 GPU PBF Simulation Truth、Water/Poison Material、Gameplay Occupancy、Status/Reaction、Sleeping 与 Remote Spawn Simulation Lease 全部保持不变。`Game.Rendering.GpuLiquidSurfaceRenderer` 恢复为一个 Renderer 拥有一套 `FluidSurfaceGpuResources`，每帧只读取一次 GPU Snapshot、构建一套 Surface Grid、执行一次 Density、一次 Marching Cubes 和一次 Draw；保留与 LOD 无关的 `Anisotropy`、Stylized Crown、Tensile 所形成的体积和凸起效果。

**Tech Stack:** Unity 6.3、URP 17.3、C#、HLSL、Compute Shader、GPU PBF、Marching Cubes、NUnit EditMode Tests。

**Spec:** 用户于 2026-08-25 明确纠正：原流体效果已经实现，问题来自后来加入的 LOD；本轮只移除/禁用 LOD 并恢复原路径，不从头实现新的 Screen-Space Fluid Rendering。

## Global Constraints

- 不创建 `ScriptableRendererFeature`、Render Graph Pass、Screen-space Depth/Thickness Texture 或新的流体 Shader 系统。
- 不修改 `PbfSolver.compute`、PBF Position/Velocity/Density Constraint、Cohesion、Tensile、Collision、Sleep/Wake 或液体 Gameplay 数据。
- 不删除 Task 40 的 Remote Spawn Simulation Lease；远处生成液体继续获得临时 Simulation 资格并正常下落。
- 不删除 `Anisotropy`、Stylized Crown、Water/Poison Material Id 过滤及其既有 Compute/Shader Contract。
- 删除液体专属的 Near/Mid/Far Clipmap、世界空间 LOD Mask、Far Particle Splat、Near Underlay 与多层 Alpha 交接。
- Fire 的 Liquid-like/Gas 生命周期不是本轮目标；只移除它对液体 Surface LOD Box 的不必要绑定时才允许修改 Fire Shader/Renderer，并必须保持火焰当前视觉与生命周期不变。
- 不修改 Scene、Prefab、Material 或 ScriptableObject Asset；Editor 配置和 Play Mode 由用户手动完成。
- 当前工作树包含大量用户改动；只编辑本计划列出的文件，不还原、不覆盖无关内容。
- C# 与 Shader 热路径继续保持无 Managed Allocation；不得在 `LateUpdate` 中新增 Array、LINQ、Closure、Delegate 或 GPU Readback。
- 每个 Task 完成后更新 `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`，按“症状 → 证据 → 根因 → 被否决方案 → 最终修复 → 验证边界”记录。

## 已核实事实与范围纠正

当前 Git `HEAD` 中的 `GpuLiquidSurfaceRenderer` 已经存在完整单路径：

```text
TryGetGpuSnapshot
  -> FluidSurfaceGridPlanner.Plan
  -> DispatchAnisotropy
  -> DispatchDensity
  -> DispatchMarchingCubes
  -> DrawSurface
```

当前工作树相对 `HEAD` 的后续改动才加入 `_nearResources`、`_midResources`、`FluidSurfaceClipmapPlanner`、`DrawFarParticleSplats`、第二次 Density/MC Dispatch 和 `_SurfaceLodMode`。因此原来的流体算法没有丢失，也不需要用 Screen-Space Fluid Rendering 替换。

但“关闭 `Enable Surface Clipmap`”不是有效修复：现在的关闭分支仍使用 Task 29 的 12m Near Bounds，范围外没有 Density Sample 和 Triangle，液体仍会消失。本轮必须把局部 Near Bounds 与 LOD Composite 一起撤掉，而不是只改一个 Inspector Boolean。

本轮先恢复原高质量单 Surface 基线。40m 外的性能 LOD 暂不实现；只有该基线经用户 Game View 验证后，才能另开阶段讨论不会侵入近景主路径的远景优化。

---

### Task 46：建立“单 Surface、无 LOD”回归 Contract

**Files:**
- Modify: `Assets/_Project/Tests/Rendering/FluidSurfaceGridPlannerTests.cs`
- Modify: `Assets/_Project/Tests/Rendering/GpuCohesiveFluidSurfaceTests.cs`
- Modify: `Assets/_Project/Tests/Rendering/GpuLiquidSurfaceRendererInitializationTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: 现有 `FluidSurfaceGridPlanner.Plan(Bounds, in LiquidRenderSettings)`、`GpuLiquidSurfaceRenderer` Source Contract、`ElementLiquidProcedural.shader` 文本 Contract。
- Produces: 锁定 Task 47 删除边界的失败测试；不新增 Runtime 类型。

- [x] **Step 1: 把 Planner Test 改为单一完整 Bounds Contract**

保留原有 Grid 数学、Voxel 对齐、非法输入与 Resolution 上限测试；删除 `FluidSurfaceInterestBoundsPlanner`、`FluidSurfaceClipmapPlanner`、Near/Mid/Far Blend 的测试。新增明确 Contract：输入 Bounds 不得被角色附近的局部窗口二次裁剪。

```csharp
[Test]
public void SingleSurfaceGridCoversTheRequestedRenderBounds()
{
    var requested = new Bounds(Vector3.zero, new Vector3(18f, 6f, 22f));
    var settings = new LiquidRenderSettings(
        targetVoxelSize: 0.1f,
        maximumResolutionPerAxis: 128,
        boundsPadding: 0.3f,
        isoLevel: 500f,
        maximumTriangleCount: 131072);

    FluidSurfaceGridSettings grid = FluidSurfaceGridPlanner.Plan(requested, in settings);

    Assert.That(grid.WorldBounds.min.x, Is.LessThanOrEqualTo(requested.min.x));
    Assert.That(grid.WorldBounds.max.x, Is.GreaterThanOrEqualTo(requested.max.x));
    Assert.That(grid.WorldBounds.min.z, Is.LessThanOrEqualTo(requested.min.z));
    Assert.That(grid.WorldBounds.max.z, Is.GreaterThanOrEqualTo(requested.max.z));
}
```

- [x] **Step 2: 增加 Renderer/Shader 的禁止回归 Contract**

测试必须明确禁止 LOD 类型与热路径重新出现，同时确认体积相关功能仍保留：

```csharp
StringAssert.DoesNotContain("_midResources", rendererSource);
StringAssert.DoesNotContain("DrawFarParticleSplats", rendererSource);
StringAssert.DoesNotContain("FluidSurfaceClipmapPlanner", rendererSource);
StringAssert.DoesNotContain("_SurfaceLodMode", shaderSource);
StringAssert.DoesNotContain("_FarSplatRadius", shaderSource);

StringAssert.Contains("DispatchSurfaceNeighborhood", rendererSource);
StringAssert.Contains("DispatchDensity", rendererSource);
StringAssert.Contains("DispatchMarchingCubes", rendererSource);
StringAssert.Contains("UseStylizedCrown", profileSource);
```

- [x] **Step 3: 运行 RED Gate**

Run:

```powershell
dotnet build Game.Rendering.Tests.csproj --no-restore
```

Expected: 新增禁止 Contract 因当前仍存在 `_midResources`、`DrawFarParticleSplats`、`_SurfaceLodMode` 等而失败。记录失败点，不能把现有其他测试或 Unity Editor 结果混进 RED 结论。

- [x] **Step 4: 更新 Task 46 学习文档**

新增数百字概述，解释为什么“原流体算法仍存在”和“只取消 Boolean 不等于移除 LOD”，列出本 Task 修改的三个 Test 文件及其架构责任。阶段状态保持“进行中”。

---

### Task 47：外科式移除液体 LOD，恢复单资源单 Draw

**Files:**
- Modify: `Assets/_Project/Scripts/Rendering/ElementField/LiquidRenderProfile.cs`
- Modify: `Assets/_Project/Scripts/Rendering/ElementField/FluidSurfaceGridPlanner.cs`
- Modify: `Assets/_Project/Scripts/Rendering/ElementField/GpuLiquidSurfaceRenderer.cs`
- Modify: `Assets/_Project/Art/Elemental/Shaders/ElementLiquidProcedural.shader`
- Modify: `Assets/_Project/Tests/Rendering/FluidSurfaceGridPlannerTests.cs`
- Modify: `Assets/_Project/Tests/Rendering/GpuCohesiveFluidSurfaceTests.cs`
- Modify: `Assets/_Project/Tests/Rendering/GpuLiquidSurfaceRendererInitializationTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: `IFluidGpuSource.TryGetGpuSnapshot(out FluidGpuSnapshot)`、`FluidGpuSnapshot.ActiveBounds`、现有 Particle/Hash/Anisotropy GPU Buffer。
- Produces: `GpuLiquidSurfaceRenderer` 的唯一流体表现路径；`LiquidRenderSettings` 只包含 Density/MC/Anisotropy/Crown 参数。

- [x] **Step 1: 从 Profile 删除 LOD Presentation 数据**

删除以下序列化字段及其 Settings Snapshot：

```text
_maximumSurfaceBoundsSize
_enableSurfaceClipmap
_midTargetVoxelSize
_midMaximumResolutionPerAxis
_midSurfaceRadius
_midSurfaceHeight
_surfaceClipmapBlendWidth
_nearVolumeUnderlayWeight
_nearSurfaceAlphaScale
_farSplatRadiusMultiplier
_farSplatAlphaScale
_farSplatEdgeStartRatio
_farSplatDepthLift
LiquidPresentationSettings
```

保留 `_targetVoxelSize`、`_maximumResolutionPerAxis`、`_boundsPadding`、`_isoLevel`、`_maximumTriangleCount`、全部 Anisotropy 字段和全部 Stylized Crown 字段。`CreateSettings()` 只生成单 Surface 所需的不可变 Snapshot。

- [x] **Step 2: 从 Planner 删除 Interest Bounds 与 Clipmap**

完整删除：

```text
FluidSurfaceClipmapSettings
FluidSurfaceClipmapPlanner
FluidSurfaceInterestBoundsPlanner
```

保留 `FluidSurfaceGridSettings` 与 `FluidSurfaceGridPlanner`。Renderer 直接把 `snapshot.ActiveBounds` 交给 `FluidSurfaceGridPlanner.Plan`，恢复 LOD 前的 Bounds 语义，不再围绕角色额外裁剪为 12m Near Box。

- [x] **Step 3: Renderer 恢复一个 Resource 与一次 Surface Pipeline**

把 `_nearResources` 恢复为语义明确的 `_resources`；删除 `_midResources`、Far Splat `GraphicsBuffer`、Far Splat `RenderParams/MaterialPropertyBlock`、所有 LOD `Shader.PropertyToID` 以及对应 Dispose。`LateUpdate` 的目标结构固定为：

```csharp
FluidSurfaceGridSettings grid = FluidSurfaceGridPlanner.Plan(
    snapshot.ActiveBounds,
    in _settings);

if (!_resources.TryUpdateGrid(in grid))
{
    DisableWithFailure(
        "Fluid Surface Resolution 在运行中发生不兼容变化；为避免热路径重分配已禁用 Renderer。");
    return;
}

if ((_settings.UseAnisotropy || _settings.UseStylizedCrown)
    && _anisotropySchedule.ShouldUpdateAndAdvance(snapshot.TopologyVersion))
{
    DispatchSurfaceNeighborhood(in snapshot);
}

DispatchDensity(in snapshot, in grid, _resources);
DispatchMarchingCubes(in grid, _resources);
DrawSurface(in snapshot, in grid, _resources);
```

不得留下第二次 Density/MC Dispatch、Far Splat Draw 或 Near/Mid/Far 排序。`DrawSurface` 继续绑定 Material Id、Anisotropy/Crown 数据与同一套 Water/Poison Material，不再写 LOD Uniform。

- [x] **Step 4: Shader 恢复纯 Marching Surface**

删除 `_SurfaceRenderMode`、`_SurfaceLodMode`、Near/Mid Box、Blend、Underlay、Far Splat Radius/Alpha/Depth、六顶点 Billboard Corner、Splat UV 和 Fragment LOD Weight。Vertex Stage 只从 `_SurfaceTriangles` 读取 Marching Cubes 顶点；Fragment Stage 保留 Water/Poison 颜色、Transparency、Fresnel、Foam、Wave/Normal 与 PBR 光照，不再把最终 Alpha 乘世界距离权重。

- [x] **Step 5: 保留并审计非 LOD 功能**

逐项检查 Diff，确认以下内容没有随 LOD 删除：

```text
MaterialId 过滤
FluidAnisotropy.compute 与邻域 Buffer
Stylized Crown 邻居计数与竖直尺度
Tensile/Cohesion 产生的粒子位置
Sleeping 粒子的稳定位置
Remote Spawn Simulation Lease
Water/Poison 各自 Material/Profile 引用
```

- [x] **Step 6: 运行 GREEN Gate**

Run:

```powershell
dotnet build Game.Rendering.Tests.csproj --no-restore
dotnet build Game.ElementField.Tests.csproj --no-restore
git diff --check -- \
  Assets/_Project/Scripts/Rendering/ElementField/LiquidRenderProfile.cs \
  Assets/_Project/Scripts/Rendering/ElementField/FluidSurfaceGridPlanner.cs \
  Assets/_Project/Scripts/Rendering/ElementField/GpuLiquidSurfaceRenderer.cs \
  Assets/_Project/Art/Elemental/Shaders/ElementLiquidProcedural.shader
```

Expected: 两个静态 Build 均 `0 error`；LOD 禁止 Contract 与体积保留 Contract 同时通过；`git diff --check` 没有本 Task 新增空白错误。普通 MSBuild 不代表 Unity Shader Import 或 Play Mode 成功。

- [x] **Step 7: 更新 Task 47 学习文档**

解释删除清单、保留清单、Runtime 单路径、为什么恢复原路径比新增 SSFR 风险更低，以及代价：当前版本暂时没有 40m 外专用性能 LOD。逐文件说明 Assembly、责任和调用链。

---

### Task 48：Editor Gate、视觉基线与未来 LOD 隔离边界

**Files:**
- Modify: `Assets/_Project/Tests/Rendering/GpuCohesiveFluidSurfaceTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`
- Modify: `docs/superpowers/plans/2026-08-09 GPU PBF流体模拟实施计划.md`

**Interfaces:**
- Consumes: Task 47 恢复的唯一 Surface Pipeline。
- Produces: 用户可复现的 Editor 验证结果和未来远景优化的架构边界；不新增 Runtime API。

- [ ] **Step 1: Unity Editor 重新导入 Gate**

用户退出 Play Mode，等待 Script/Shader Import 完成并清空 Console。要求 C# Error 与 Shader Error 均为 0；既有第三方 Warning 单独记录，不能混写成通过或失败。

- [ ] **Step 2: 原流体视觉基线 Gate**

在 P7 同一区域分别生成 Water 与 Poison，验证：

1. 静止水滩中间高于边缘，不是水平薄片。
2. 边缘是连续流体轮廓，不出现 Far Splat 圆片、浅蓝 LOD 线或 Near/Mid 毛刺交接。
3. 角色静止、移动、相机旋转时，轮廓不会因为世界距离权重改变而闪烁。
4. Poison 继续使用绿色材质与更强 Cohesion；Water 继续使用水材质。
5. 强冲击仍可分裂成少量液滴，靠近后重新融合。
6. 空中生成液体时持续下落；Task 40 的 Remote Spawn Simulation Lease 未回归。

- [ ] **Step 3: Profiler Gate**

CPU Timeline 检查 `GpuFluid.Surface.Density`、`GpuFluid.Surface.MarchingCubes`、`GpuFluid.Surface.Draw`，确认每种液体每帧只有一套 Surface Pipeline，`GC Alloc=0 B`。GPU Profiler 记录 1080p 当前 Game View Scale 下的耗时与 Triangle 数量。

- [x] **Step 4: 明确未来远景优化边界**

文档写明：本轮不承诺 40m 外 LOD；未来若重新加入，只允许在原高质量 Surface 的可见范围之外添加完全隔离的远景表示，且必须先有独立 Debug View、无可见交界的验收截图和 GPU 预算。不得再次降低 0–40m 主 Surface Alpha、把水平 Splat 混入近景、或让 Inspector 的 LOD 参数修改原流体体积。

- [x] **Step 5: 更新 Task 48 与长期主计划**

把 Task 29–45 标记为“实验性 LOD 路径已撤回”，保留其中 Task 40 Remote Spawn Simulation Lease 等非 LOD 成果。完整解析文档记录最终 Game View 与 Profiler 证据；在用户人工 Gate 前仍标记“脚本完成，Editor Gate 待人工执行”。

## 你需要在 Unity Editor 中做的事

1. 等待 Unity 自动重新导入修改后的 C# 与 `ElementLiquidProcedural.shader`。
2. 不需要添加 Renderer Feature，不需要创建新 Material，也不需要替换 Water/Poison Prefab。
3. Water/Poison 的 `GpuLiquidSurfaceRenderer` 保留现有 Source、Compute、Material、Target Material 配置。
4. 删除 LOD 字段后 Inspector 会自动不再显示 `Enable Surface Clipmap`、Mid、Far Splat、Underlay 参数；无需手工迁移这些值。
5. 重新进入 P7 Play Mode，按 Task 48 的六项视觉基线验证。
6. 如果水完全不可见，先查看 Console 的 Shader Error 和 Renderer 的资源不兼容错误；不要调 Iso/Alpha 掩盖初始化失败。
7. 如果重新出现原路径早期的边缘精度问题，先截图并记录 Profile 的 `Target Voxel Size`、`Maximum Resolution`、`Iso Level` 和粒子数量；该问题单独诊断，不重新启用 LOD。

## 计划自检

- **需求覆盖：** 复用原 GPU PBF 和 Marching Surface，只删除/禁用 LOD；不实现新的 SSFR。
- **保留边界：** Anisotropy、Crown、Tensile、Sleeping、Remote Spawn、Water/Poison Material 与 Gameplay 全部明确保留。
- **删除边界：** Near/Mid/Far Grid、Interest Bounds、Far Splat、LOD Shader Uniform、Underlay 与交叉 Alpha 全部有对应修改步骤和禁止回归测试。
- **验证边界：** 静态 Build、Shader Import、Game View、Profiler 分开报告；用户负责 Editor 操作。
- **资产依赖：** 不需要下载、创建或替换任何 Texture、Normal Map、VFX、Model、Material 或 Prefab。
