# GPU PBF 流体 Chunk Cell 快照归档与恢复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. 本仓库禁止在未获得用户对当前任务的明确授权时使用 SubAgent；若未授权，只能选择 Inline Execution。

**Goal:** 将远离玩家的 Water、Poison、Sticky GPU 粒子按 Chunk/Cell/Material 聚合为 RAM 中的低成本快照，确认快照成功后释放固定 GPU 粒子槽位，并在玩家返回前确定性重建粒子，从而避免历史流体永久占满 8192 粒子池。

**Architecture:** `Game.ElementField` 新增独立的 Fluid Chunk Streaming 控制层。GPU 粒子继续是已加载区域的 Simulation Truth；远处粒子通过低频 `AsyncGPUReadback` 聚合为 `(ChunkKey, LocalCellIndex, MaterialId) -> Amount + AverageVelocity`，归档成功后才释放 GPU slot。恢复采用 GPU all-or-nothing 容量预留，CPU 快照一直保留到恢复粒子已经进入新的 Gameplay Snapshot，任何时刻每个 Chunk 都只有一个 Gameplay 权威来源。

**Tech Stack:** Unity 6.3（6000.3.16f1）、URP 17.3、C#、HLSL、`ComputeShader`、`GraphicsBuffer`、`AsyncGPUReadback`、`NativeArray<T>`、NUnit EditMode GPU tests、Unity Test Runner、Unity Profiler、Frame Debugger、Windows Development Build。

**Spec:** 本文件“设计冻结”章节；按照项目约定，本功能使用单一详细实施计划，不另建 spec 文件。

## Global Constraints

- 保留一个全局 GPU PBF Solver 和一个固定粒子池；Chunk 只负责 Streaming、持久化和权威迁移，不创建 per-Chunk Solver。
- 第一版只保存 RAM 快照，不写磁盘，不建立存档格式，不模拟 Cold Chunk 的离屏流动与反应。
- 归档数据以 `(ElementChunkKey, LocalCellIndex, MaterialId)` 为键；同一 Cell 中不同液体必须分别保存。
- 归档 `Amount` 使用 `uint`，不得复用 `LiquidGameplaySnapshot` 的饱和 `byte Amount` 作为持久化真值。
- GPU 粒子只有在 CPU 快照完整构建并成功提交后才能释放；Readback、容量预留或恢复失败时必须保留可重试的权威数据。
- `Archiving` 期间 GPU 是权威；`Cold` 和 `Restoring` 期间 CPU Archive 是权威；恢复完成且新 Gameplay Snapshot 已覆盖恢复后的 Topology Version 后才切回 GPU。
- GPU Transfer 同时最多一个事务；Archive 与 Restore 不并发，避免多份 Buffer Lease、Free Stack Reservation 和回滚状态互相覆盖。
- 当前 `FluidGameplayOccupancyBridge` 继续承担热区低频 Gameplay Projection；归档使用独立 Buffer、独立 Readback State 和独立 Lease，不能复用其 published/staging 对象。
- `Game.Rendering` 继续只读 `IFluidGpuSource`；Cold 数据不生成 Surface Mesh。`Game.Combat`、`Game.Character` 和 `Game.Rendering` 不得反向依赖 Streaming 实现。
- `Update()`、`FixedUpdate()`、`LateUpdate()` 与 Compute dispatch 热路径禁止 `new`、LINQ、Closure、Delegate 分配和 Boxing；所有 NativeArray、Upload Array、Ring Buffer 与 callback delegate 在初始化边界创建并复用。
- 正常 Play 路径禁止同步 `GraphicsBuffer.GetData()` 和 `AsyncGPUReadbackRequest.WaitForCompletion()`；只允许最终 `OnDestroy` teardown 等待一次未完成请求。
- 所有新增生产脚本使用 `Game.ElementField` namespace；新增架构边界、异步状态机、坐标换算、GPU 布局和性能约束必须写简体中文原因注释。
- 保留 `MaterialId` 既有数值：Empty=0、Water=1、Fire=2、Poison=3、Sticky=4；只能追加，不能重排。
- `FluidGpuLayout.LayoutVersion` 从 8 升到 9；所有 C#/HLSL flag、stride、Renderer contract 和测试必须在同一 Task 同步更新。
- 不修改 PBF Density/Lambda/Constraint、Cohesion、Viscosity、Tensile、Vorticity、Collision 的物理公式。
- 不改动当前工作区中与本功能无关的 Scene、Prefab、Enemy 和 Spell 用户修改；Scene wiring 由用户在 Unity Editor 中执行并保存。
- 每个 Phase 完成后先执行对应 EditMode/GPU tests，再由用户完成列出的 Editor Gate；未通过 Gate 不进入下一 Phase。
- 静态编译、EditMode、GPU Kernel test、PlayMode、Scene Playthrough、Profiler、Frame Debugger 和 Development Build 证据分别记录，不能互相代替。

---

## 0. 设计冻结

### 0.1 当前问题与已核实边界

- `LiquidSimulationProfile_Water.asset` 当前固定 `ParticleCapacity=8192`。
- Water 每颗粒子表示 8 GMU；Poison 和 Sticky 每颗粒子分别表示 4 GMU。
- `UpdateParticleActivity` 会让远处粒子只保留 `Alive`，跳过大部分 Solver，但 slot 仍未回到 `FreeIndices`。
- `PackGameplaySamples` 会打包所有 `Alive` 粒子；当前 `LiquidGameplaySnapshot` 面向低频查询，使用 `byte` Amount 并在 255 饱和，不能承担无损归档。
- 现有 CPU ElementWorld 已经区分 Resident、Interest Active 和 RequiresSimulation；本计划将相同的“数据寿命与 Solver 工作寿命分离”扩展到 GPU 流体容量。

### 0.2 三层空间范围

1. **Simulation Region**：沿用 `GpuPbfFluidRuntime.ActiveBounds`，决定粒子是否参与 PBF。
2. **Warm Region**：将 CPU ElementWorld 的 active Chunk box 在 X/Y/Z 各外扩 `WarmPaddingChunks=1`。此范围中的液体保持 GPU Resident，用于预加载和边界缓冲。
3. **Cold Region**：位于 Warm Region 外，并在最近一次 Interest Chunk 改变后超过 `ArchiveGraceSeconds=2` 的液体允许归档；当空闲槽位压力触发归档时可跳过 Grace，但仍不能归档 Warm Region 内的粒子。

当前 `CellSize=0.25m`、`ChunkSize=8` 时，一个 Chunk 边长是 2m；一圈 Warm Padding 大于当前 `SmoothingRadius=0.25m`，足以隔离 PBF 邻域边界。参数仍由 Profile 保存，不能在 Runtime 中散落魔法数字。

### 0.3 权威状态机

| State | 权威数据 | GPU slot | Gameplay Query | Solver |
|---|---|---:|---|---|
| `GpuResident` | GPU Particle Buffer | 占用 | GPU Gameplay Snapshot | 由现有 Activity/Sleep 决定 |
| `Archiving` | GPU Particle Buffer | 占用且 `ArchiveLocked` | GPU Gameplay Snapshot | 目标粒子暂停 |
| `Cold` | RAM Archive Store | 已释放 | 远处不进入活跃查询 | 暂停 |
| `Restoring` | RAM Archive Store | 已整批预留，粒子带 `RestoreLoading` 且不含 `Alive` | CPU Archive，仅限 Warm Region | 暂停 |

允许的迁移只有：

```text
GpuResident -> Archiving -> Cold
GpuResident <- Archiving（Readback/Store 失败回滚）
Cold -> Restoring -> GpuResident
Cold <- Restoring（Reserve/Readback 失败回滚）
```

### 0.4 长期快照与临时 GPU 样本

归档 Readback 临时样本仍需要粒子级 Position、Velocity、MaterialId 和 Flags；它只活在单次 Transfer staging 中。长期 RAM 数据只保存：

```csharp
public readonly struct FluidArchiveCellRecord
{
    public readonly ElementChunkKey Chunk;
    public readonly int LocalCellIndex;
    public readonly MaterialId Material;
    public readonly uint Amount;
    public readonly Vector3 AverageVelocity;
}
```

聚合公式：

```text
Amount = ParticleCount × AmountUnitsPerParticle(MaterialId)
AverageVelocity = Sum(ParticleVelocity) / ParticleCount
```

Archive Store 使用预分配 Open Addressing Table，键为 `(Chunk, LocalCellIndex, MaterialId)`，避免在 Runtime Transfer 完成路径创建 Dictionary、List 或可变数组。空槽、占用槽、墓碑槽分别使用 0/1/2 标记；删除 Chunk 后保留墓碑，查找不能在墓碑处提前停止。

### 0.5 Archive 事务

```text
Interest Chunk 改变或 Capacity Pressure
-> 构建 Chunk-aligned Warm Region
-> Gate 与 Warm Region 相交之外的新增 Fluid Write
-> LockArchiveCandidates：Warm 外 Alive 粒子设为 Alive|ArchiveLocked，并清除 Solver 位
-> PackArchiveSamples：每个 GPU slot 写 32-byte 样本，非 Locked slot 写 Flags=0
-> AsyncGPUReadback.RequestIntoNativeArray
-> callback 只记录完成状态
-> 下一次 Streaming Runtime Update 使用预分配 Builder 聚合
-> Archive Store 容量验证与原子提交
-> ReleaseArchiveLockedParticles 释放 slot 并更新 Free/Active Counter
-> 发布 Topology Version，重放暂存写入
```

任一步失败都执行 `CancelArchiveLock`，保持 GPU 粒子和旧 Archive Store 不变，再重放暂存写入。

### 0.6 Restore 事务

```text
Cold Chunk 进入 Warm Region或收到 Fluid Write
-> Archive Store 把该 Chunk 的 Cell Record 复制到预分配 staging
-> Amount / AmountUnitsPerParticle 得到精确 ParticleCount
-> ChunkKey + LocalCell + MaterialId + SnapshotVersion 生成确定性 Seed
-> CPU 在 Cell 内生成位置，AverageVelocity 作为初速度
-> ReserveRestoreSlots 一次检查 FreeCount >= Required + GameplaySpawnReserve(512)
-> 成功时一次扣减 FreeCount；失败时不改变任何 slot
-> StageRestoreParticles 写入 RestoreLoading slot，暂不设置 Alive
-> Readback 16-byte TransferStatus
-> 成功后 ActivateRestoredParticles，并发布 Topology Version
-> 等待 FluidGameplayOccupancyBridge 发布包含该 Topology 的 Snapshot
-> Query Authority 切回 GPU，删除 CPU Archive Cell
```

Restore Status Readback 失败时运行 `RollbackRestoredParticles`，把本事务记录的 reserved indices 全部推回 Free Stack，CPU Archive 保持不变。

### 0.7 写入、反应与查询

- `FluidChunkStreamingRuntime` 同时实现 `IFluidDepositSink` 与 `ILiquidOccupancyReadOnly`，包装现有 GPU Deposit Sink 和 Gameplay Bridge。
- 写入只要覆盖 Cold/Restoring Chunk，或与正在归档的 Warm 外区域相交，就进入固定容量 `FluidPendingWriteQueue`；目标 Chunk 恢复后再把原始 `ElementWriteRequest` 原样交给现有 `FluidDepositQueueAdapter`，保留 SurfaceNormal、Radius、Falloff 与 density-packed 语义。
- Water-Fire、Poison-Fire、Sticky-Fire 的反应规划继续读取原始 GPU `FluidGameplayOccupancyBridge`，不读取 Cold Archive；Cold Chunk 的离屏反应暂停，恢复并发布 Gameplay Snapshot 后才继续。
- Character Exposure/Material Query 通过 Streaming Runtime 的组合查询读取数据。为避免形成覆盖整张历史地图的巨大 `OverlapBox`，`SnapshotBounds` 只返回当前 Warm Region；Cold Region 外的 Archive 不参与活跃角色查询。
- `CopyOccupiedCells` 只输出 Warm Region 内 Restoring Archive 和 GPU published data，并按当前 Chunk Authority 去重；不能把同一份物质量从两端相加。

### 0.8 容量与玩家体验边界

- `GameplaySpawnReserveParticles=512`，覆盖当前最大标准液体法术的 400 粒子并留出 112 slot 余量。
- Restore Reservation 必须在 `FreeCount - RequiredCount >= 512` 时才成功；恢复不会吃掉最后一次标准 Gameplay Spawn 的余量。
- Archive 在 Interest Chunk 改变后运行；若 Runtime 得到 `FreeCount <= 512` 的容量快照，则立即尝试归档 Warm 外粒子，不等待 Grace。
- 该机制解决历史世界占用；玩家在同一 Warm Region 无限连续制造液体仍有局部容量上限。第一版到达此边界时必须明确记录 `DroppedParticleCount` 和可见诊断，不能悄悄回写 Legacy Cell，也不能复制 Simulation Truth。

### 0.9 不纳入本计划

- 磁盘存档、压缩文件格式和跨版本迁移。
- Cold Chunk 离屏流动、反应 Tick 或时间补算。
- 流体 Surface LOD、远景替代 Mesh、粒子减少或视觉降采样。
- 修改 PBF 物理参数和 Shader 风格。
- 无限局部生成、粒子合并/分裂或按材质动态配额。

---

## 1. 文件结构与责任锁定

### 新建 Production 文件

| File | Responsibility |
|---|---|
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingSettings.cs` | 不可变配置快照与参数验证 |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingPlanner.cs` | Warm Region、Grace、压力触发和状态迁移 Pure Logic |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidArchiveCellRecord.cs` | Archive Cell key/value 与 32-byte GPU sample/restore particle contract |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkArchiveBuilder.cs` | 将一次粒子 Readback 无分配聚合为 Cell Record staging |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkArchiveStore.cs` | 预分配 RAM Cold Store、事务提交、查询、复制与删除 |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkRestorePlanner.cs` | 将 Cell Record 确定性扩展为精确数量的 GPU Restore Particle |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidPendingWriteQueue.cs` | Transfer 期间固定容量保存原始 ElementWriteRequest |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/IFluidChunkTransferBackend.cs` | CPU Streaming 与 GPU Runtime 的 Archive/Restore Lease contract |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkTransferState.cs` | Archive/Restore callback、commit、rollback、teardown 的 Pure 状态机 |
| `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs` | Unity Runtime Adapter、AsyncGPUReadback、权威路由和调度 |

### 修改 Production 文件

| File | Change |
|---|---|
| `Assets/_Project/Scripts/ElementField/Data/ElementWorldProfile.cs` | 添加 Streaming authoring fields 并创建 Settings Snapshot |
| `Assets/_Project/Scripts/ElementField/Fluid/Activity/FluidActivityPlanner.cs` | 增加 ArchiveLocked/RestoreLoading flag 语义 |
| `Assets/_Project/Scripts/ElementField/Fluid/Data/IFluidGpuSource.cs` | 增加 stride/flag/layout v9 与 topology publish helper |
| `Assets/_Project/Scripts/ElementField/Fluid/Runtime/FluidGpuResourceSet.cs` | 一次性创建 Archive/Restore/Status/Index Buffer |
| `Assets/_Project/Scripts/ElementField/Fluid/Runtime/GpuPbfFluidRuntime.cs` | 实现 `IFluidChunkTransferBackend`，绑定和调度 Transfer Kernels |
| `Assets/_Project/Scripts/ElementField/Fluid/Gameplay/FluidGameplayOccupancyBridge.cs` | 公开已发布 Snapshot 的冻结 Topology Version |
| `Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs` | 将液体 Deposit/Material Query 接到 Streaming Runtime，反应继续读 GPU-only Snapshot |
| `Assets/_Project/Art/Elemental/Compute/FluidGpuCommon.hlsl` | 同步新增 flags/helper |
| `Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute` | Lock/Pack/Cancel/Release/Reserve/Stage/Activate/Rollback Kernel |
| `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md` | 每个 Task 后追加基础原理、Runtime、Trade-off、测试和 Debug 证据 |

### 新建 Test 文件

| File | Coverage |
|---|---|
| `Assets/_Project/Tests/ElementField/FluidChunkStreamingPlannerTests.cs` | Region、Hysteresis、Pressure 与状态合法性 |
| `Assets/_Project/Tests/ElementField/FluidChunkArchiveBuilderTests.cs` | Cell/Material 聚合、Amount、AverageVelocity、负坐标、非法样本 |
| `Assets/_Project/Tests/ElementField/FluidChunkArchiveStoreTests.cs` | 原子提交、容量失败、墓碑查询、Chunk 删除和零 GC |
| `Assets/_Project/Tests/ElementField/FluidChunkRestorePlannerTests.cs` | Cell Amount 到确定性 Restore Particle 的整数守恒 |
| `Assets/_Project/Tests/ElementField/FluidPendingWriteQueueTests.cs` | Ring Buffer、FIFO、Overflow 与原值保留 |
| `Assets/_Project/Tests/ElementField/FluidChunkTransferStateTests.cs` | callback/commit/rollback/exactly-once lease |
| `Assets/_Project/Tests/ElementField/GpuFluidChunkArchiveTests.cs` | Archive GPU Kernel 与 Free Stack 守恒 |
| `Assets/_Project/Tests/ElementField/GpuFluidChunkRestoreTests.cs` | all-or-nothing reserve、staging、activate、rollback |
| `Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs` | Fake Backend 下的权威切换、pending write 与 topology gate |

---

### Task 1：冻结 Streaming 配置、空间范围与生命周期 Pure Contract

**Files:**
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingSettings.cs`
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingPlanner.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Data/ElementWorldProfile.cs`
- Test: `Assets/_Project/Tests/ElementField/FluidChunkStreamingPlannerTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: `ElementChunkKey`, `ElementWorldCoordinates`, `ElementWorldProfile.ActiveRadiusXZChunks`, `ActiveRadiusYChunks`, `CellSize`, `ChunkSize`。
- Produces: `FluidChunkStreamingSettings`, `FluidChunkRegion`, `FluidChunkLifecycleState`, `FluidChunkStreamingPlanner.BuildWarmRegion(...)`, `ShouldStartArchive(...)`。

- [ ] **Step 1: 写失败的 Region、Grace 和 Pressure tests**

```csharp
[Test]
public void BuildWarmRegion_ExpandsActiveChunkBoxByOneChunk()
{
    FluidChunkRegion region = FluidChunkStreamingPlanner.BuildWarmRegion(
        new ElementChunkKey(5, -2, 7), activeRadiusXZ: 2, activeRadiusY: 1, warmPadding: 1);

    Assert.That(region.Min, Is.EqualTo(new ElementChunkKey(2, -4, 4)));
    Assert.That(region.Max, Is.EqualTo(new ElementChunkKey(8, 0, 10)));
    Assert.That(region.Contains(new ElementChunkKey(8, 0, 10)), Is.True);
    Assert.That(region.Contains(new ElementChunkKey(9, 0, 10)), Is.False);
}

[Test]
public void ShouldStartArchive_PressureBypassesGraceButNeverRunsDuringTransfer()
{
    Assert.That(FluidChunkStreamingPlanner.ShouldStartArchive(
        interestChanged: true, secondsSinceInterestChange: 0.1f,
        archiveGraceSeconds: 2f, freeParticles: 400,
        gameplayReserve: 512, transferInFlight: false), Is.True);
    Assert.That(FluidChunkStreamingPlanner.ShouldStartArchive(
        true, 3f, 2f, 400, 512, transferInFlight: true), Is.False);
}
```

- [ ] **Step 2: 运行 RED Gate**

在 Unity Test Runner 只运行 `FluidChunkStreamingPlannerTests`。预期因 `FluidChunkStreamingPlanner`、`FluidChunkRegion`、`FluidChunkStreamingSettings` 尚不存在而编译失败；其他既有测试失败不算本 Task 的有效 RED。

- [x] **Step 3: 实现不可变 Settings 与 Pure Planner**

```csharp
public enum FluidChunkLifecycleState : byte
{
    GpuResident = 0,
    Archiving = 1,
    Cold = 2,
    Restoring = 3,
}

public readonly struct FluidChunkStreamingSettings
{
    public readonly bool Enabled;
    public readonly int WarmPaddingChunks;
    public readonly float ArchiveGraceSeconds;
    public readonly int MaximumArchivedChunks;
    public readonly int MaximumArchivedCellRecords;
    public readonly int MaximumPendingWrites;
    public readonly int GameplaySpawnReserveParticles;
}
```

`ElementWorldProfile` 添加 `EnableFluidChunkStreaming=false` Feature Toggle，并在 `OnValidate` 清洗以下默认值：`WarmPaddingChunks=1`、`ArchiveGraceSeconds=2f`、`MaximumArchivedChunks=512`、`MaximumArchivedCellRecords=65536`、`MaximumPendingWrites=256`、`GameplaySpawnReserveParticles=512`。`CreateFluidChunkStreamingSettings()` 必须再次进行构造期验证，损坏 YAML 不能绕过约束。Task 7 全部 Gate 完成前保持 Toggle 关闭。

`FluidChunkRegion.ToWorldBounds(origin, cellSize, chunkSize)` 必须使用 Chunk 的闭区间 Key 转成世界 AABB；Max Key 要加一整个 Chunk 才能得到 exclusive max。所有轴差值先转 `long`，避免极端坐标溢出。

- [x] **Step 4: 运行 GREEN Gate 与既有坐标回归**

Unity Test Runner 运行：

```text
FluidChunkStreamingPlannerTests
ElementWorldCoordinatesTests
ElementChunkActivityPlannerTests
FluidActivityPlannerTests
```

记录 Passed/Failed/Skipped 数。确认负 Chunk、边界 exclusive max、压力跳过 Grace 和 transfer 串行规则通过。

- [ ] **Step 5: 更新 Living Explainer 的 Task 1 小节**

写清 `Simulation Region / Warm Region / Cold Region`、Chunk 世界尺寸公式、Hysteresis 目的、Chunk Resident 与粒子 Solver Activity 的区别，并明确这一 Task 还没有释放任何 GPU 粒子。

- [ ] **Step 6: Commit**

```bash
git add Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingSettings.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingPlanner.cs Assets/_Project/Scripts/ElementField/Data/ElementWorldProfile.cs Assets/_Project/Tests/ElementField/FluidChunkStreamingPlannerTests.cs "Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md"
git commit -m "feat(element-field): define fluid chunk streaming lifecycle"
```

**Editor Gate:** 打开当前 `ElementWorldProfile` Asset，确认 Feature Toggle 默认为关闭，新字段显示默认值 1、2、512、65536、256、512；保存并重新进入 Play Mode 后值不回零。

---

### Task 2：实现无分配 Cell 聚合 Builder 与 RAM Archive Store

**Files:**
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidArchiveCellRecord.cs`
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkArchiveBuilder.cs`
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkArchiveStore.cs`
- Test: `Assets/_Project/Tests/ElementField/FluidChunkArchiveBuilderTests.cs`
- Test: `Assets/_Project/Tests/ElementField/FluidChunkArchiveStoreTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: `ElementWorldCoordinates`, `LiquidMaterialAmountScaleSnapshot`, Task 1 Settings。
- Produces: `FluidGpuArchiveSample`, `FluidArchiveCellRecord`, `FluidArchiveBuildContext`, `FluidChunkArchiveBuilder`, `FluidChunkArchiveStore`。

- [ ] **Step 1: 写失败的多材质、Amount 和平均速度 tests**

```csharp
[Test]
public void Rebuild_SameCellKeepsMaterialsSeparateAndPreservesAmountUnits()
{
    using var samples = ArchiveSamples(
        Sample(new Vector3(.1f, .1f, .1f), MaterialId.Water, new Vector3(2f, 0f, 0f)),
        Sample(new Vector3(.2f, .1f, .1f), MaterialId.Water, new Vector3(4f, 0f, 0f)),
        Sample(new Vector3(.1f, .1f, .1f), MaterialId.Poison, Vector3.zero));
    var builder = new FluidChunkArchiveBuilder(particleCapacity: 8);

    Assert.That(builder.TryRebuild(samples, BuildContext()), Is.True);
    Assert.That(builder.TryGet(
        new ElementChunkKey(0, 0, 0), localCellIndex: 0,
        MaterialId.Water, out FluidArchiveCellRecord water), Is.True);
    Assert.That(water.Amount, Is.EqualTo(16u));
    Assert.That(water.AverageVelocity.x, Is.EqualTo(3f).Within(0.0001f));
    Assert.That(builder.TryGet(
        new ElementChunkKey(0, 0, 0), 0,
        MaterialId.Poison, out FluidArchiveCellRecord poison), Is.True);
    Assert.That(poison.Amount, Is.EqualTo(4u));
}
```

再覆盖负世界坐标、Chunk 边界、inactive sample、非有限 Position/Velocity、未知 Material、`uint` checked overflow、同输入固定 slot 顺序和零样本空事务。

- [ ] **Step 2: 运行 Builder RED Gate**

Unity Test Runner 运行 `FluidChunkArchiveBuilderTests`，预期只因新类型不存在而失败。

- [x] **Step 3: 实现 32-byte Sample 与预分配 Builder**

```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly struct FluidGpuArchiveSample
{
    public readonly Vector3 Position;
    public readonly uint MaterialId;
    public readonly Vector3 Velocity;
    public readonly uint Flags;
}

public readonly struct FluidArchiveCellRecord
{
    public readonly ElementChunkKey Chunk;
    public readonly int LocalCellIndex;
    public readonly MaterialId Material;
    public readonly uint Amount;
    public readonly Vector3 AverageVelocity;
}
```

Builder 使用两倍 ParticleCapacity 并向上取 2 的幂的 Open Addressing arrays。累加阶段保存 `particleCount` 和 `velocitySum`；只有完整扫描成功后才计算 AverageVelocity 并发布 `RecordCount`。失败时清空 staging occupied 标记，不暴露部分记录。

- [ ] **Step 4: 写失败的 Store 原子性、墓碑与容量 tests**

```csharp
[Test]
public void Commit_WhenCapacityIsInsufficient_PreservesPreviousArchive()
{
    var store = new FluidChunkArchiveStore(maximumChunks: 2, maximumCellRecords: 1);
    Assert.That(store.TryCommit(OneRecordBuilder(MaterialId.Water), version: 1u), Is.True);

    Assert.That(store.TryCommit(OneRecordBuilder(MaterialId.Poison), version: 2u), Is.False);
    Assert.That(store.TryGetAmount(Vector3Int.zero, MaterialId.Water, out uint amount), Is.True);
    Assert.That(amount, Is.EqualTo(8u));
}

[Test]
public void RemoveChunk_TombstoneDoesNotBreakLaterProbeChain()
{
    // 构造两个碰撞 key，删除首项后仍必须找到第二项。
}
```

- [x] **Step 5: 实现固定容量 Archive Store**

Store 构造期一次性创建 Chunk header table 与 Cell record table。`TryCommit` 先计算新增 Chunk/Record 数并检查容量，再写 staging generation，全部完成后统一发布 version；不能边遍历边让 Query 看见新数据。提供以下精确入口：

```csharp
public bool ContainsChunk(ElementChunkKey chunk);
public bool TryGetAmount(Vector3Int globalCell, MaterialId material, out uint amount);
public int CopyChunkRecords(ElementChunkKey chunk, FluidArchiveCellRecord[] destination);
public bool TryGetChunkVersion(ElementChunkKey chunk, out uint version);
public void RemoveChunk(ElementChunkKey chunk);
public bool TryCommit(FluidChunkArchiveBuilder builder, uint version);
```

`CopyChunkRecords` 按 table slot 顺序输出，保证同一 Store 状态可复现。`TryGetAmount` 只返回 Archive 内部的 `uint`；转成 Gameplay `byte` 的饱和操作留给 Runtime Adapter。

- [ ] **Step 6: 运行 GREEN、零 GC 与既有 Gameplay Snapshot 回归**

运行：

```text
FluidChunkArchiveBuilderTests
FluidChunkArchiveStoreTests
LiquidGameplaySnapshotTests
```

零 GC test 先 warm up，再用 `GC.GetAllocatedBytesForCurrentThread()` 包围 `TryRebuild + TryCommit + TryGetAmount + CopyChunkRecords`；预期 0 bytes。

- [ ] **Step 7: 更新 Living Explainer 并 Commit**

文档解释临时粒子级 Readback 与长期 Cell 快照的区别、同 Cell 多材质键、Amount 单位、AverageVelocity 的精度损失、Open Addressing/tombstone 原理。

```bash
git add Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidArchiveCellRecord.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkArchiveBuilder.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkArchiveStore.cs Assets/_Project/Tests/ElementField/FluidChunkArchiveBuilderTests.cs Assets/_Project/Tests/ElementField/FluidChunkArchiveStoreTests.cs "Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md"
git commit -m "feat(element-field): add coarse fluid chunk archive"
```

**Editor Gate:** 本 Task 是 Pure Data Gate，无 Scene 行为变化；用户只需确认 Unity Import/Compile 无错误和测试通过。

---

### Task 3：建立 GPU Archive Lock、Pack、Cancel 与 Release 事务

**Files:**
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/IFluidChunkTransferBackend.cs`
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkTransferState.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Activity/FluidActivityPlanner.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Data/IFluidGpuSource.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Runtime/FluidGpuResourceSet.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Runtime/GpuPbfFluidRuntime.cs`
- Modify: `Assets/_Project/Art/Elemental/Compute/FluidGpuCommon.hlsl`
- Modify: `Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute`
- Test: `Assets/_Project/Tests/ElementField/FluidChunkTransferStateTests.cs`
- Test: `Assets/_Project/Tests/ElementField/GpuFluidChunkArchiveTests.cs`
- Modify: `Assets/_Project/Tests/ElementField/FluidGpuLayoutContractTests.cs`
- Modify: `Assets/_Project/Tests/ElementField/FluidActivityPlannerTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: Task 2 `FluidGpuArchiveSample`, existing Position/Velocity/Metadata/FreeIndices/Counters。
- Produces: `IFluidChunkTransferBackend.TryAcquireArchiveReadbackLease(...)`, `CommitArchiveAndRelease(...)`, `CancelArchive(...)`, exactly-once Transfer Lease。

- [ ] **Step 1: 写失败的 Layout v9 与 flag tests**

```csharp
[Test]
public void LayoutV9_DefinesArchiveAndRestoreFlagsWithoutChangingMetadataStride()
{
    Assert.That(FluidGpuLayout.ArchiveLockedFlag, Is.EqualTo(1u << 5));
    Assert.That(FluidGpuLayout.RestoreLoadingFlag, Is.EqualTo(1u << 6));
    Assert.That(FluidGpuLayout.ArchiveSampleStride, Is.EqualTo(32));
    Assert.That(FluidGpuLayout.LayoutVersion, Is.EqualTo(9u));
    Assert.That(FluidGpuLayout.UInt2Stride, Is.EqualTo(8));
}
```

`FluidActivityPlannerTests` 增加：ArchiveLocked 粒子保持 Alive、不能成为 Solver target；RestoreLoading 不含 Alive，不参与 Surface、Hash、Gameplay。

- [ ] **Step 2: 运行 Contract RED Gate**

运行 `FluidGpuLayoutContractTests + FluidActivityPlannerTests`，预期因 v9 常量和状态规则尚未实现而失败。

- [x] **Step 3: 同步 C#/HLSL flag 与布局**

```csharp
public const uint ArchiveLocked = 1u << 5;
public const uint RestoreLoading = 1u << 6;
```

HLSL 使用相同位值。`FluidRequiresSimulation` 显式排除 ArchiveLocked/RestoreLoading；`UpdateParticleActivity` 遇到 ArchiveLocked 时保留 Metadata，不得被普通 Activity pass 擦除；RestoreLoading 因不含 Alive 被既有 Solver、Spatial Hash、Gameplay Pack 和 Rendering 自然忽略。Layout Version 同步改为 9，更新 Renderer/contract tests 的期望值。

- [ ] **Step 4: 写失败的 GPU Archive Kernel tests**

至少覆盖：

```csharp
[Test]
public void Archive_OutsideRetainedBounds_LocksPacksAndReleasesExactlyThoseSlots()
{
    // slot0 在 retain bounds 内，slot1/2 在外；外部包含 Water 与 Poison。
    // Lock 后 slot0 flags 不变，slot1/2 为 Alive|ArchiveLocked。
    // Pack 后 slot1/2 的 Position/Velocity/Material 完整，slot0 Flags=0。
    // Release 后 ActiveCount 减 2、FreeCount 加 2，两个 index 各返回 Free Stack 一次。
}

[Test]
public void ArchiveCancel_RestoresRemoteSimulationLeaseAndDoesNotTouchFreeStack()
{
    // Cancel 后 locked 粒子恢复 Alive|RemoteSpawnActive|RequiresSimulation；计数不变。
}
```

- [x] **Step 5: 增加一次性 GPU Transfer Buffers**

`FluidGpuResourceSet` 初始化创建并在 `Dispose` 释放：

```text
ArchiveSamples          Structured, ParticleCapacity × 32 bytes
RestoreParticles        Structured, ParticleCapacity × 32 bytes
RestoreReservedIndices  Structured, ParticleCapacity × 4 bytes
TransferStatus          Structured, 1 record × 16 bytes
```

Buffer 创建失败必须沿用 ResourceSet 构造异常回滚，不能留下部分 GraphicsBuffer。

- [x] **Step 6: 实现 Archive Kernels 并控制 UAV 上限**

新增：

```hlsl
#pragma kernel LockArchiveCandidates
#pragma kernel PackArchiveSamples
#pragma kernel CancelArchiveLock
#pragma kernel ReleaseArchiveLockedParticles
#pragma kernel ClearReleasedArchiveAuxiliary
```

`LockArchiveCandidates` 只锁定 `Alive && outsideRetainedBounds`。`PackArchiveSamples` 按 slot 原位写样本，非 Locked 写零 Flags，因此 Readback 顺序确定。Release 使用 CAS 将 `Alive|ArchiveLocked` 改为 0，再把 index 推入 Free Stack；CAS 失败不能重复 free。Core/Auxiliary 分 Kernel，确保 D3D11 每 Kernel 不超过 8 个 RW UAV，并清理 Position、PredictedPosition、Velocity、DensityLambda、DeltaPosition、StableTick、WakeRequest。

- [ ] **Step 7: 实现 Archive Backend Lease 与 Pure Transfer State**

```csharp
internal interface IFluidChunkTransferBackend
{
    bool TryGetParticleCapacity(out int particleCapacity);
    bool TryAcquireArchiveReadbackLease(
        Bounds retainedBounds,
        Vector3 worldOrigin,
        float cellSize,
        int chunkSize,
        uint transactionId,
        out FluidArchiveReadbackLease lease);
    void CommitArchiveAndRelease(uint transactionId, int archivedParticleCount);
    void CancelArchive(uint transactionId);
    void ReleaseTransferReadbackLease();
}
```

Lease Metadata 冻结 Bounds、Origin、CellSize、ChunkSize、ParticleCapacity、AmountScale、LayoutVersion、TopologyVersion、TransactionId。`FluidChunkTransferState` 复用现有 Gameplay Readback 的原则，将 begin、callback completion、consume completion、commit/rollback 和 teardown lease release 合并为 exactly-once 状态机。

- [ ] **Step 8: 运行 GPU GREEN Gate 与原粒子生命周期回归**

运行：

```text
FluidChunkTransferStateTests
GpuFluidChunkArchiveTests
GpuFluidParticleLifecycleTests
FluidGpuLayoutContractTests
FluidActivityPlannerTests
GpuFluidSpatialHashTests
GpuPbfSolverTests
GpuPbfCollisionTests
```

无 ComputeShader 或 `GraphicsDeviceType.Null` 时 GPU tests 必须 `Assert.Ignore` 并记录 Skipped，不能记为 Passed。

- [ ] **Step 9: 更新 Living Explainer 并 Commit**

文档解释 Free Stack、CAS 防重复释放、ArchiveLocked、32-byte readback、Graphics Queue 顺序、D3D11 UAV 限制和失败回滚。

```bash
git add Assets/_Project/Scripts/ElementField/Fluid/Streaming/IFluidChunkTransferBackend.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkTransferState.cs Assets/_Project/Scripts/ElementField/Fluid/Activity/FluidActivityPlanner.cs Assets/_Project/Scripts/ElementField/Fluid/Data/IFluidGpuSource.cs Assets/_Project/Scripts/ElementField/Fluid/Runtime/FluidGpuResourceSet.cs Assets/_Project/Scripts/ElementField/Fluid/Runtime/GpuPbfFluidRuntime.cs Assets/_Project/Art/Elemental/Compute/FluidGpuCommon.hlsl Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute Assets/_Project/Tests/ElementField/FluidChunkTransferStateTests.cs Assets/_Project/Tests/ElementField/GpuFluidChunkArchiveTests.cs Assets/_Project/Tests/ElementField/FluidGpuLayoutContractTests.cs Assets/_Project/Tests/ElementField/FluidActivityPlannerTests.cs "Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md"
git commit -m "feat(element-field): archive remote gpu fluid particles"
```

**Editor Gate:** 在 `PBF_FluidSandbox` 通过 Debug Inspector 手动调用一次 Archive Outside Warm。确认锁定期间水面不消失、完成后远处粒子消失、FreeCount 精确增加、近处粒子不变、NumericalError 不增加；分别记录 D3D11 与 D3D12 结果。

---

### Task 4：接入自动 Archive 调度、AsyncGPUReadback 与失败回滚

**Files:**
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidPendingWriteQueue.cs`
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs`
- Test: `Assets/_Project/Tests/ElementField/FluidPendingWriteQueueTests.cs`
- Test: `Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: Task 1 Planner、Task 2 Builder/Store、Task 3 Archive Backend。
- Produces: 自动 `GpuResident -> Archiving -> Cold`、固定 pending write queue、Archive counters 与 Debug state。

- [x] **Step 1: 写失败的 FIFO、Overflow 与原值快照 tests**

```csharp
[Test]
public void Queue_PreservesFullWriteRequestAndRejectsWithoutOverwritingOldest()
{
    var queue = new FluidPendingWriteQueue(1);
    var first = new ElementWriteRequest(
        Vector3.one, MaterialId.Poison, 1600, 1.2f, true,
        new Vector3(1f, 2f, 3f), Vector3.up);
    Assert.That(queue.TryEnqueue(in first), Is.True);
    Assert.That(queue.TryEnqueue(in first), Is.False);
    Assert.That(queue.RejectedCount, Is.EqualTo(1u));
    Assert.That(queue.TryDequeue(out ElementWriteRequest restored), Is.True);
    Assert.That(restored.InitialVelocity, Is.EqualTo(first.InitialVelocity));
    Assert.That(restored.SurfaceNormal, Is.EqualTo(first.SurfaceNormal));
}
```

- [x] **Step 2: 实现固定 Ring Buffer**

构造期分配 `ElementWriteRequest[]`，运行时只移动 head/count；RejectedCount 饱和到 `uint.MaxValue`。提供 `TryEnqueue`、`TryPeek`、`TryDequeue`，不得使用 `Queue<T>` 扩容。

- [x] **Step 3: 用 Fake Backend 写 Archive Runtime RED tests**

覆盖：Grace 到期才 begin、Capacity Pressure 立即 begin、in-flight 时不重复 begin、callback 只记录状态、下一个 Update 聚合并 commit、Store 容量不足调用 cancel、Readback error 调用 cancel、lease exactly once、Archive 期间相交写入延后、Warm 内写入立即下发。

- [x] **Step 4: 实现 `FluidChunkStreamingRuntime` Archive 半链**

组件使用 `[DefaultExecutionOrder(50)]`，确保普通 GPU Simulation `Update` 已提交、Gameplay Bridge 的 `LateUpdate` 尚未打包。初始化时：

```text
读取 ElementWorldRuntime Origin/CellSize/ChunkSize/InterestChunk
-> 创建 Settings Snapshot
-> 从 Backend 取得 ParticleCapacity
-> 创建 Persistent NativeArray<FluidGpuArchiveSample>[ParticleCapacity] 双缓冲
-> 创建 Builder/Store/PendingWriteQueue
-> 缓存 AsyncGPUReadback callback delegate
```

正常 Update 只做状态推进。`AsyncGPUReadback.RequestIntoNativeArray` 读取 Task 3 lease 的 ArchiveSamples；callback 只调用 `RecordCompletion(request.hasError)`。`OnDestroy` 可对唯一 outstanding request `WaitForCompletion()` 一次，然后 release lease 和 Dispose NativeArray。

- [x] **Step 5: 接入 ElementWorldRuntime，但暂不启用 Restore Query**

`ElementWorldRuntime` 增加 serialized `_fluidChunkStreamingRuntime`。只有 `EnableFluidChunkStreaming=true` 时才验证它存在并成功初始化；Toggle 关闭时完整保留原 `GpuPbfFluidRuntime + FluidGameplayOccupancyBridge` 路径。启用后 Deposit Sink 由 Streaming Runtime 包装并转发到 GPU，反应继续显式使用 `_fluidGameplayOccupancy`。启用状态下组件缺失属于配置失败，不能静默回写 Legacy Cell。

- [x] **Step 6: 运行 GREEN Gate**

运行：

```text
FluidPendingWriteQueueTests
FluidChunkStreamingRuntimeTests（Archive fixture）
ElementWorldFluidRoutingTests
FluidGameplayOccupancyBridgeTests
ElementWorldSimulatorTests
```

- [ ] **Step 7: 更新文档并 Commit**

文档已更新；当前工作区还包含用户并行修改，本批未自动创建 Commit。

```bash
git add Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidPendingWriteQueue.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs Assets/_Project/Tests/ElementField/FluidPendingWriteQueueTests.cs Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs "Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md"
git commit -m "feat(element-field): stream remote fluid into ram archives"
```

**Editor Gate:** 在 Sandbox 创建近、远两滩水，移动 Interest Point。Grace 内远处水仍在 GPU；超过 2 秒后远处 ActiveCount 下降、FreeCount 上升、ArchiveChunkCount 增加；返回区域前本 Task 尚不要求恢复可见流体。

---

### Task 5：实现 GPU all-or-nothing Restore 与确定性 Cell 重建

**Files:**
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/IFluidChunkTransferBackend.cs`
- Create: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkRestorePlanner.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkTransferState.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Runtime/FluidGpuResourceSet.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Runtime/GpuPbfFluidRuntime.cs`
- Modify: `Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute`
- Test: `Assets/_Project/Tests/ElementField/FluidChunkRestorePlannerTests.cs`
- Test: `Assets/_Project/Tests/ElementField/GpuFluidChunkRestoreTests.cs`
- Modify: `Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: Archive Store Cell Records、Liquid Material Amount Scale、FreeIndices/Counters、Task 3 Transfer state。
- Produces: `FluidGpuRestoreParticle`, `FluidGpuTransferStatus`, `TryAcquireRestoreStatusLease(...)`, `CommitRestore(...)`, `RollbackRestore(...)`。

- [x] **Step 1: 写失败的确定性重建与整数守恒 tests**

```csharp
[Test]
public void ExpandArchiveRecord_PreservesParticleCountAndIsDeterministic()
{
    FluidArchiveCellRecord record = Record(
        MaterialId.Water, amount: 24u, averageVelocity: new Vector3(1f, 0f, 0f));
    var first = new FluidGpuRestoreParticle[3];
    var second = new FluidGpuRestoreParticle[3];

    Assert.That(FluidChunkRestorePlanner.Expand(
        in record, amountUnitsPerParticle: 8u, cellSize: .25f,
        snapshotVersion: 7u, first, 0), Is.EqualTo(3));
    Assert.That(FluidChunkRestorePlanner.Expand(
        in record, 8u, .25f, 7u, second, 0), Is.EqualTo(3));
    CollectionAssert.AreEqual(first, second);
}
```

若 `Amount % AmountUnitsPerParticle != 0`，Planner 必须拒绝损坏记录，不能 Ceiling 后静默制造物质量。

- [x] **Step 2: 实现 Restore Planner 与 32-byte upload contract**

```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly struct FluidGpuRestoreParticle
{
    public readonly Vector3 Position;
    public readonly uint MaterialId;
    public readonly Vector3 Velocity;
    public readonly uint TransactionId;
}

[StructLayout(LayoutKind.Sequential)]
public readonly struct FluidGpuTransferStatus
{
    public readonly uint Succeeded;
    public readonly uint ReservedBase;
    public readonly uint ParticleCount;
    public readonly uint TransactionId;
}
```

Cell 内位置使用固定 lattice/jitter 函数，Seed 只由 ChunkKey、LocalCellIndex、MaterialId、SnapshotVersion 和 local particle index 组成，不能读取 `UnityEngine.Random`。

- [x] **Step 3: 写失败的 GPU Reservation、Activate 与 Rollback tests**

覆盖：FreeCount 足够时一次扣减 required；`free-required < 512` 时整批失败且 buffer/counter 不变；Stage 后 slot 只有 RestoreLoading 不含 Alive；Activate 后 inside ActiveBounds 成为 Interest Active，外部成为 RemoteSpawnActive；Rollback 将所有 reserved index 各归还一次；错误 TransactionId 不改变任何 slot。

- [x] **Step 4: 实现四段 Restore Kernels**

```hlsl
#pragma kernel ReserveRestoreSlots
#pragma kernel StageRestoreParticlesCore
#pragma kernel StageRestoreParticlesAuxiliary
#pragma kernel ActivateRestoredParticles
#pragma kernel RollbackRestoredParticles
```

Reserve 由单线程读取 FreeCount，检查 `required + gameplayReserve` 后一次写回 ReservedBase 和 TransferStatus。Stage 以 `_FreeIndices[ReservedBase + localIndex]` 取得唯一 slot，并写 `RestoreReservedIndices[localIndex]`；Core/Auxiliary 拆分以满足 D3D11 8-UAV 上限。Activate 校验 TransactionId，设置 Alive/activity flags 并增加 ActiveCount。Rollback 只处理仍为 RestoreLoading 的 reserved slot，通过 `InterlockedAdd(FreeCount, 1)` 归还。

- [x] **Step 5: 扩展 Backend Restore Lease**

```csharp
bool TryAcquireRestoreStatusLease(
    NativeArray<FluidGpuRestoreParticle> particles,
    int particleCount,
    int gameplayReserveParticles,
    uint transactionId,
    out FluidRestoreStatusReadbackLease lease);
void CommitRestore(uint transactionId, int particleCount, out uint publishedTopologyVersion);
void RollbackRestore(uint transactionId, int particleCount);
```

Status 使用 `AsyncGPUReadback.RequestIntoNativeArray` 读取一个预分配长度 1 的 `NativeArray<FluidGpuTransferStatus>`。Backend 在 Stage dispatch 后返回 lease；CPU 收到 `Succeeded=1` 且 TransactionId/ParticleCount 全部匹配后才允许 Commit。

- [x] **Step 6: 实现 `Cold -> Restoring` 调度**

每次只选择距离 Interest Chunk 最近的一个 Cold Chunk。先 `CopyChunkRecords` 到预分配 Cell staging，再扩展到长度 ParticleCapacity 的预分配 RestoreParticle staging；required 超过 ParticleCapacity 时拒绝并记录 `OversizedChunkRestoreCount`。进入 Restoring 后 Archive Store 仍保留记录。

Commit 后保存 `publishedTopologyVersion`，等待 Gameplay Bridge 覆盖该版本；在等待期间 GPU particles 已可见，但组合 Gameplay Query 仍读取 Archive。Status error/mismatch/timeout 执行 Rollback，状态退回 Cold。

- [x] **Step 7: 运行 GPU 与 Runtime GREEN Gate**

运行：

```text
FluidChunkRestorePlannerTests
GpuFluidChunkRestoreTests
FluidChunkStreamingRuntimeTests（Restore fixture）
GpuFluidChunkArchiveTests
GpuFluidParticleLifecycleTests
GpuFluidSpatialHashTests
GpuPbfSolverTests
```

- [ ] **Step 8: 更新文档并 Commit**

文档已更新；当前工作区仍包含用户并行修改，本批未自动创建 Commit。

```bash
git add Assets/_Project/Scripts/ElementField/Fluid/Streaming/IFluidChunkTransferBackend.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkRestorePlanner.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkTransferState.cs Assets/_Project/Scripts/ElementField/Fluid/Runtime/FluidGpuResourceSet.cs Assets/_Project/Scripts/ElementField/Fluid/Runtime/GpuPbfFluidRuntime.cs Assets/_Project/Art/Elemental/Compute/PbfParticleLifecycle.compute Assets/_Project/Tests/ElementField/FluidChunkRestorePlannerTests.cs Assets/_Project/Tests/ElementField/GpuFluidChunkRestoreTests.cs Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs "Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md"
git commit -m "feat(element-field): restore archived fluid chunks"
```

**Editor Gate:** Sandbox 中依次验证 Water、Poison、Sticky 离开后归档、返回前预加载、进入活动区重新可见。记录归档前粒子数、快照 Amount、恢复后粒子数；误差必须为 0 个粒子。观察恢复瞬间允许液面重新稳定，但不能出现爆炸速度、NaN、穿透场景或跨材质变色。

---

### Task 6：完成 Gameplay Authority、Pending Write 与反应边界集成

**Files:**
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidPendingWriteQueue.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Gameplay/FluidGameplayOccupancyBridge.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs`
- Modify: `Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs`
- Modify: `Assets/_Project/Tests/ElementField/ElementWorldFluidRoutingTests.cs`
- Modify: `Assets/_Project/Tests/ElementField/FluidGameplayOccupancyBridgeTests.cs`
- Modify: `Assets/_Project/Tests/ElementField/LiquidCellReactionSystemTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`

**Interfaces:**
- Consumes: GPU published Snapshot、Archive Store、Restore Topology gate、ElementWorld Write Router。
- Produces: `FluidChunkStreamingRuntime : IFluidDepositSink, ILiquidOccupancyReadOnly` 完整权威路由。

- [x] **Step 1: 写失败的 Topology Gate 和单写者 tests**

```csharp
[Test]
public void RestoringChunk_KeepsArchiveAuthorityUntilGpuSnapshotContainsRestoreTopology()
{
    // restore commit 发布 topology 12；bridge 当前仍是 11。
    // TryGetAmount 必须返回 archive amount，store 记录不能删除。
    // bridge 发布 topology 12 后，下一个 Update 才删除 archive 并切换 GpuResident。
}

[Test]
public void WriteIntersectingColdChunk_IsQueuedAndReplayedExactlyOnceAfterRestore()
{
    // 入队期间 fake GPU sink 收到 0 次；恢复完成后收到 1 次，字段逐项相等。
}
```

- [x] **Step 2: 暴露冻结的 Gameplay Topology Version**

`FluidGameplayOccupancyBridge` 增加只读：

```csharp
internal uint PublishedTopologyVersion =>
    HasValidSnapshot ? _publishedSnapshot.TopologyVersion : 0u;
```

属性只读取完整 published snapshot，不能读取 staging metadata。

- [x] **Step 3: 实现组合 Gameplay Query**

`TryGetAmount` 先把 Global Cell 转 Chunk：Restoring 且位于当前 Warm Region时从 Archive Store 读取 `uint` 并饱和到 byte；其余状态读取 GPU Gameplay Bridge。Cold Region 外返回 false，避免远处角色和反应继续运行。

`SnapshotBounds` 使用 Warm Region world bounds 与有效 GPU bounds 的有界交/并规划结果，不得使用所有历史 Cold Chunk 的总 Bounds。`CopyOccupiedCells` 使用 caller-provided destination：先复制 GPU cells，再扫描 Restoring Archive；根据 Chunk Authority 跳过重复条目，无新建集合。

- [x] **Step 4: 实现写入相交范围和 replay gate**

用 `WorldPosition ± Radius` 转 Global Cell min/max，再转 Chunk min/max，以三重 for 循环检查请求覆盖的 Chunk。任一目标为 Cold/Restoring 时，整条原始 request 入 Pending Queue并触发最近目标恢复；Archive in-flight 时，任何不完全包含在 frozen retained bounds 内的 request 入队。

Replay 每帧最多处理初始化时固定的 `MaximumPendingWrites`，但一旦队首仍覆盖 Cold/Restoring Chunk就停止，保持 FIFO。GPU sink 拒绝时保留队首并在下一帧重试；不得 dequeue 后丢弃。

- [x] **Step 5: 分离 Material Query 与 GPU Reaction Query**

`ElementWorldRuntime`：

```text
_fluidDepositSink = _fluidChunkStreamingRuntime
_liquidOccupancy = _fluidChunkStreamingRuntime        // Material Query / Exposure
_liquidReactionOccupancy = _fluidGameplayOccupancy    // GPU-only Reaction
```

`PlanAndApplyGpuWaterFireReactions` 只消费 `_liquidReactionOccupancy`。这样 Restoring Archive 不会生成无法命中 GPU 粒子的 Consume/Convert Command；发布恢复后的 GPU Snapshot 后，既有反应链自然恢复。

- [x] **Step 6: 执行完整 Gameplay 回归**

运行：

```text
FluidChunkStreamingRuntimeTests
ElementWorldFluidRoutingTests
FluidGameplayOccupancyBridgeTests
LiquidGameplaySnapshotTests
LiquidCellReactionSystemTests
GpuMultiLiquidGameplayTests
ElementWorldExposureSystemTests
```

额外零 GC 测试覆盖 steady-state `Update`、Warm query、GPU query 和 pending queue idle path。

- [ ] **Step 7: 更新文档并 Commit**

文档已更新；当前工作区仍包含用户并行修改，本批未自动创建 Commit。

文档必须画出 `Projectile -> ElementWorldWriteRouter -> Streaming Deposit -> GPU/Pending -> Archive/Restore -> Gameplay Query -> Reaction/Exposure`，并解释为什么 Reaction 与 Exposure 使用不同的读视图。

```bash
git add Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidPendingWriteQueue.cs Assets/_Project/Scripts/ElementField/Fluid/Gameplay/FluidGameplayOccupancyBridge.cs Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs Assets/_Project/Tests/ElementField/ElementWorldFluidRoutingTests.cs Assets/_Project/Tests/ElementField/FluidGameplayOccupancyBridgeTests.cs Assets/_Project/Tests/ElementField/LiquidCellReactionSystemTests.cs "Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md"
git commit -m "feat(element-field): route gameplay through fluid chunk authority"
```

**Editor Gate:** 在 P7 与 P8 分别测试：角色踩 Water 获得 Wet、踩 Poison 获得 Poisoned、踩 Sticky 获得 Sticky；离开归档后返回仍能获得对应状态；归档/恢复窗口内命中液体球，最终只出现一份新增流体；Water-Fire、Poison-Fire、Sticky-Fire 在恢复完成后继续反应且 Amount 不重复。

---

### Task 7：完成容量诊断、Scene Wiring、Profiler 与阶段收口

**Files:**
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs`
- Modify: `Assets/_Project/Scripts/ElementField/Fluid/Runtime/GpuPbfFluidRuntime.cs`
- Modify: `Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs`
- Modify: `Assets/_Project/Tests/Rendering/P8ElementWorldSceneContractTests.cs`
- Modify: `Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md`
- Manual Editor wiring: `Assets/_Project/Scenes/PBF_FluidSandbox.unity`
- Manual Editor wiring: `Assets/_Project/Scenes/P7_DemoRun.unity`
- Manual Editor wiring: `Assets/_Project/Scenes/P8_BossField.unity`

**Interfaces:**
- Consumes: Tasks 1-6 完整链路。
- Produces: Inspector diagnostics、Profiler markers、Scene contract、玩家验收证据和明确剩余边界。

- [x] **Step 1: 添加无分配 Debug Counter 与 ProfilerMarker**

Streaming Runtime 暴露只读 serialized counters：

```text
Lifecycle State / Transaction Id
Archive Chunk Count / Archive Cell Record Count
Archived Particle Count / Restored Particle Count
Archive Readback Error Count / Restore Readback Error Count
Archive Rollback Count / Restore Rollback Count
Pending Write Count / Rejected Pending Write Count
Capacity Pressure Count / Oversized Chunk Restore Count
Last Archive Readback Bytes / Last Archive Duration
Last Restore Particle Count / Last Restore Duration
```

Profiler markers 使用固定字符串：

```text
GpuFluid.Streaming.Plan
GpuFluid.Streaming.ArchiveBuild
GpuFluid.Streaming.ArchiveCommit
GpuFluid.Streaming.RestoreExpand
GpuFluid.Streaming.PendingReplay
```

禁止在 marker scope 内做字符串插值或 boxing。

- [x] **Step 2: 更新 Scene Contract tests**

`P8ElementWorldSceneContractTests` 验证 P7/P8 保存的 `ElementWorldRuntime` 都引用同 GameObject 上的 `FluidChunkStreamingRuntime`，Streaming Runtime 引用对应 GPU Runtime 与 Gameplay Bridge，Profile 的 WarmPadding=1、Grace=2、Reserve=512。只验证 YAML contract，不把它冒充 Play Mode。

- [x] **Step 3: 用户在 Unity Editor 完成 Wiring**

对 Sandbox、P7、P8：

1. 在 ElementWorld Root 添加 `FluidChunkStreamingRuntime`。
2. 绑定同 Root 的 `ElementWorldRuntime`、`GpuPbfFluidRuntime`、`FluidGameplayOccupancyBridge`。
3. 把 `ElementWorldRuntime._fluidChunkStreamingRuntime` 指向该组件。
4. 确认 `ElementWorldProfile` 新字段为 1 / 2 / 512 / 65536 / 256 / 512。
5. 完成其余 Gate 前保持 `EnableFluidChunkStreaming=false`；准备执行本 Task Playthrough 时再开启。
6. 保存 Scene；不要改动当前 Enemy、Boss、Spell、NavMesh 和 Projectile Prefab 的用户工作。

- [x] **Step 4: Unity Test Runner 总回归**

运行 `Game.ElementField.Tests` 与 `Game.Rendering.Tests` 全量 EditMode。记录总 Passed/Failed/Skipped，并单列以下 focused fixtures：

```text
FluidChunkStreamingPlannerTests
FluidChunkArchiveBuilderTests
FluidChunkArchiveStoreTests
FluidPendingWriteQueueTests
FluidChunkTransferStateTests
GpuFluidChunkArchiveTests
FluidChunkRestorePlannerTests
GpuFluidChunkRestoreTests
FluidChunkStreamingRuntimeTests
GpuFluidParticleLifecycleTests
GpuPbfSolverTests
GpuPbfCollisionTests
P8ElementWorldSceneContractTests
```

- [x] **Step 5: Sandbox 数量守恒与失败注入**

执行以下矩阵并逐项记录：

| Case | Expected |
|---|---|
| Water/Poison/Sticky 各一滩离开并返回 | 每种恢复 ParticleCount 与归档前一致 |
| 同一 Cell Water+Poison | 两条 Archive Record，恢复后材质不串色 |
| 归档 Readback 强制 error | GPU 粒子保留，Store 不发布，下一次可重试 |
| Restore Reserve 强制不足 | 无半成品 Alive 粒子，Archive 保留 |
| Restore Status 强制 error | reserved indices 全归还，FreeCount 守恒 |
| Transfer 期间命中液体球 | Pending request 恢复后只执行一次 |
| 玩家在 Chunk 边缘来回移动 | Grace 内不反复 Archive/Restore |

- [x] **Step 6: P7/P8 玩家体验 Playthrough**

每张 Scene 至少完成：连续释放 3 个 Water、3 个 Poison、3 个 Sticky；离开两个以上 Warm Region；等待归档；继续施放一次三种液体；返回旧区域；触发 Wet/Poisoned/Sticky 与三类 Fire reaction。验收重点是新法术仍可生成、旧物质量恢复、无明显瞬移爆炸和无重复反应。

- [x] **Step 7: Profiler、Frame Debugger 与 Build Gate**

Profiler 分别记录静止、跨 Chunk、Archive callback、Restore 和连续施法：

- Script `GC Alloc` steady-state 为 0 B；Transfer 事件路径也不得出现本计划代码造成的 Managed Allocation。
- 没有同步 Readback stall；Render Thread 不出现 `WaitForCompletion`。
- `ArchiveBuild` 扫描固定 ParticleCapacity，不能随历史 Cold Chunk 数线性增长。
- GPU Archive/Restore Kernel 只在 Transfer 时出现；PBF Tick/Substep/Iteration 数不改变。
- Frame Debugger 确认仍由原 `GpuLiquidSurfaceRenderer` 绘制，仅当前 ActiveBounds 生成 Surface。
- Windows Development Build 验证 Compute/Shader 被打包、D3D11 与目标默认 Graphics API 路径可用。

不能预填毫秒收益；只记录实际机器、Graphics API、分辨率、复现步骤和测量值。

- [x] **Step 8: 完成 Living Explainer 收口**

追加完整 Debug 表：症状 → 假设 → Counter/Profiler 证据 → GPU/CPU 分层 → 根因 → 被拒方案 → 最终方案 → Regression → Remaining Boundary。明确：磁盘保存、Cold 离屏演化、局部无限生成仍未实现；本功能只声称“历史流体可归档并释放 GPU 容量”。

> **2026-09-07 Phase C Gate：** 完整 EditMode、DX12 Sandbox、D3D11 Development Player、P7/P8 自动生产 Smoke 与保存场景 Contract 已完成。Sandbox 目标 24 粒子恢复 24；Player 4096 粒子归档并恢复 4096；P7/P8 SubmittedDelta 分别为 2302/24，Dropped=0。Player Target 60 的 interval P99=17.946 ms，稳定 60 FPS 与 Restore 摊销进入 Phase D。Frame Debugger 没有保存人工 Capture；Scene Contract 与 Renderer 源码确认仍使用原 `GpuLiquidSurfaceRenderer`，因此不把它写成已取得的 Frame Debugger 证据。
- [ ] **Step 9: Final Commit**

```bash
git add Assets/_Project/Scripts/ElementField/Fluid/Streaming/FluidChunkStreamingRuntime.cs Assets/_Project/Scripts/ElementField/Fluid/Runtime/GpuPbfFluidRuntime.cs Assets/_Project/Tests/ElementField/FluidChunkStreamingRuntimeTests.cs Assets/_Project/Tests/Rendering/P8ElementWorldSceneContractTests.cs "Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md"
git commit -m "feat(element-field): complete fluid chunk streaming"
```

当前 P7/P8 Scene 已存在用户未提交修改，以上命令故意不暂存 Scene。用户保存 wiring 后先执行：

```bash
git diff -- Assets/_Project/Scenes/PBF_FluidSandbox.unity Assets/_Project/Scenes/P7_DemoRun.unity Assets/_Project/Scenes/P8_BossField.unity
```

只有用户确认其中变化可以整体进入同一提交时才单独暂存；否则保留为用户工作，不由本功能提交混入。

---

## 2. 固定 Debug 顺序

### 新液体仍无法生成

1. 看 GPU `FreeCount` 与 `DroppedParticleCount`。
2. 看 `ArchiveChunkCount` 是否增长；若不增长，检查 Warm Region、Grace 和 Transfer State。
3. 看 Archive Readback Error/Store Capacity/Rollback counters。
4. 若旧远处粒子已释放但本地仍满，记录为“局部容量上限”，不得继续扩大 Streaming 范围掩盖。

### 返回旧区域没有液体

1. Store 是否仍包含目标 Chunk 与 Cell Records。
2. 是否进入 Restoring，required ParticleCount 是否超过可恢复容量。
3. TransferStatus 的 Success/TransactionId/ParticleCount 是否匹配。
4. Restore commit 是否发布新 Topology Version。
5. Gameplay Bridge 是否已发布覆盖该 Topology 的 Snapshot。
6. GPU 已恢复但无 Surface 时，再查 Renderer ActiveBounds 与 Fluid Density/Marching Cubes。

### 物质量重复

1. 检查 Chunk Authority State。
2. Restoring 期间 Query 是否只读 Archive。
3. Topology Gate 前是否提前 Remove Archive。
4. Pending Write 是否 dequeue 两次。
5. Reaction 是否错误读取 Composite Archive 而非 GPU-only Snapshot。

### FreeCount 损坏

1. 检查 Release/Restore Rollback 的 CAS 与 TransactionId。
2. 对比 ActiveCount + FreeCount 是否等于 ParticleCapacity。
3. 扫描 FreeIndices 是否存在重复 slot。
4. 检查 Core/Auxiliary Kernel 是否对同一 slot 重复推栈。
5. 任何守恒失败立即停用 Streaming Feature Toggle，回退到原 GPU Resident 行为。

---

## 3. Rollback 策略

1. `ElementWorldProfile` 增加 `EnableFluidChunkStreaming`，默认在完成 Task 7 全部 Gate 前保持关闭；关闭时 `ElementWorldRuntime` 使用原 `GpuPbfFluidRuntime + FluidGameplayOccupancyBridge` 路径。
2. Task 1-2 失败只删除未接线的 Pure Streaming 文件并恢复 Profile 字段，不影响 GPU Layout。
3. Task 3 以后若 GPU Kernel/Free Stack 出错，关闭 Feature Toggle，保留 Layout v9 与新 Buffer 也必须保证旧 Spawn/Solver/Reaction 行为通过回归。
4. 不使用 `git reset --hard`、`git checkout --` 或覆盖 Scene；每个 Conventional Commit 形成可单独 revert 的边界。
5. Readback unsupported 时初始化明确关闭 Streaming，继续使用原 GPU PBF；禁止改成同步 `GetData`。
6. Archive Store 满时取消本次 Archive 并保留 GPU 粒子；禁止删除最旧快照或静默蒸发物质量。

---

## 4. Plan Self-Review Checklist

- [x] 方案只引入 Cell 级长期快照，粒子级数据只存在于单次 Transfer staging。
- [x] 同 Cell 多材质使用独立 key；Amount 使用 uint 并保留当前 GMU scale。
- [x] GPU/Archive 权威在四个状态中唯一，Archive 与 Restore 都有失败回滚。
- [x] Restore 使用 all-or-nothing Free Stack reservation，不会发布半个 Chunk。
- [x] Gameplay Query、GPU Reaction Query 与 Rendering 读路径已分离。
- [x] AsyncGPUReadback、Lease、callback、OnDestroy 与零 GC 边界已明确。
- [x] D3D11 8-UAV、Layout Version、Topology Version 和 Free/Active Counter 守恒已覆盖。
- [x] 计划包含 exact files、interfaces、test-first、Editor wiring、Profiler、Build 和 rollback。
- [x] 磁盘持久化、Cold 离屏演化和局部无限生成明确排除，没有扩大第一版范围。
- [x] 所有 Scene/Prefab 现有用户修改均要求保留；没有把当前 dirty worktree 当成本功能改动。
