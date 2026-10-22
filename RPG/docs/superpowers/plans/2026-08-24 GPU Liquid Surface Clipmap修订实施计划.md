# GPU Liquid Surface Clipmap 修订实施计划

**Goal:** 在保留近景高分辨率液面轮廓的同时，让所有仍然 Alive 的 Water/Poison 在已访问世界区域内保持可见，消除跟随角色移动的硬裁切圈。

**Architecture:** 复核发现旧版 Far 层仍读取只含 `InterestActive` 粒子的 Solver Spatial Hash，并继续使用随角色移动的 `ActiveBounds`，所以扩大半径只能移动消失边界。本修订仍保持一份 GPU 粒子 Truth，但把 Hash 的成员语义提升为 `Alive`：Solver 在消费候选时继续过滤 `RequiresSimulation/InterestActive`，Rendering 则允许读取冻结的 Alive 粒子。Runtime 额外维护只增不减的 `ResidentRenderBounds`（Active Bounds 与已接受 Spawn 覆盖范围的并集），Far Surface 使用它而不是当前 Active Bounds。Near/Far 继续通过世界坐标互补权重交叉淡化。

**Assets:** 不依赖外部美术资源。沿用现有 Water/Poison Material、Density Compute、Marching Cubes Compute 和 Procedural Liquid Shader；只增加数据参数与 Shader LOD Mask。

## Task 30：纯 Clipmap 规划契约

- 扩展 `LiquidRenderProfile/LiquidRenderSettings`：`EnableSurfaceClipmap`、Far Target Voxel、Far Maximum Resolution、Blend Width。
- 新增纯 `FluidSurfaceClipmapPlanner`，输出 Near/Far Bounds、Grid 与安全的过渡区。
- RED/GREEN 覆盖：Far 完整覆盖 Simulation Bounds、Near 不扩大 Simulation、Blend 不超过 Near 半径、非法配置拒绝。

## Task 31：双层 GPU 资源与 Runtime 编排

- `GpuLiquidSurfaceRenderer` 分配 Near/Far 两套 Density/Triangle/Indirect 资源。
- 每帧只做一次粒子邻域重建；Near/Far 各做 Density、Marching Cubes 和 Draw。
- 两层资源只允许 World Origin 平移；形状变化仍显式失败，禁止热路径重分配与 Managed Allocation。

## Task 32：World-space LOD Cross-fade

- `ElementLiquidProcedural.shader` 根据世界坐标到 Near Bounds 的距离计算连续权重。
- Near 权重与 Far 权重互补；过渡带隐藏硬边界并避免两层都以完整 Alpha 重叠。
- 近景优先保留完整轮廓，远景只承担可读性；世界锚定保持相机运动时稳定。

## Task 36：Resident 粒子可见性修订

- `PbfSpatialHash.compute` 对所有 `Alive` 粒子建立 Cell/Range；失活 Slot 才写入 Sentinel。
- PBF Solver、Wake 与 Gameplay 继续在消费端检查 `InterestActive/RequiresSimulation`，因此远处粒子不会恢复物理解算。
- `FluidDensity.compute` 接受 Alive 粒子；远处缺少最新 Anisotropy 时依靠既有 Identity fallback，保证有限、稳定的低精度外观。

## Task 37：持久化 Resident Render Bounds

- 新增纯 `FluidResidentBoundsTracker`，只在初始化、Active Bounds 移动与 Spawn 成功时扩张 Bounds，绝不随角色离开而收缩。
- `FluidGpuSnapshot` 同时发布 `ActiveBounds` 与 `ResidentRenderBounds`；Near 使用前者，Far 使用后者。
- Resident Bounds 只影响 Presentation Coverage，不改变粒子的 Owner、Simulation、Gameplay Occupancy 或 Wake 规则。

## 验证与回滚

- 静态检查与 `Game.Rendering.Tests.csproj --no-restore` 必须通过。
- Unity Editor 中运行 `Game.Rendering.Tests`，随后在 P7 观察远处 Water/Poison、穿越 Near 边界、移动/旋转相机、低角度地板交界以及 Profiler `GC Alloc`。
- 如 GPU 预算不满足，可在 Profile 关闭 Clipmap 回到 Near-only，或临时扩大 Near Bounds；不修改 PBF Solver。
