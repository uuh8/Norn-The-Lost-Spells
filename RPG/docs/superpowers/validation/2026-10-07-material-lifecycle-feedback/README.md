# Task 10：六项生命周期与少量落水反馈验证

日期：2026-10-07；项目：`F:/Develop Project/RPG/RPG`；Unity：6000.3.16f1。

当前进度：六项代码调整及开发侧验证已完成，固体/气体阶段继续更新中。下一步由用户正常 Play 验收画面、状态与寿命，再录 Boss 长窗口 Profiler。没有自动通关 P7/Boss、进入 Play、保存/丢弃 Scene、使用 SubAgent 或提交 git。

## 1. 实际变化与根因

- Steam Diffusion 2→1/s、Buoyancy 1→0.6，真实 Wind 与 10 Hz Tick/渲染插值保留；毒雾 Diffusion=1/s。
- 只有每格混合量≤1 GMU、整 Chunk 平均浓度≤0.5%的尾雾进入消散。首次合格后至少 3 个活动秒，巡检复核；每 Tick 详细检查最多 2 块。冻结区不清理、不追补离开时间，Slot 复用重置资格，真实移除记显式消耗并回收空 Chunk。
- Steam 仅低于/等于规则 Cold Threshold=10°C 时可冷凝，常温 20°C 不匹配。输入速率 max(0.5 GMU/s, 库存×0.05/s)，Cooldown=8s。配方 8 Steam→1 Water+7 背景消散；全部必须原子提交，目标满、取消、源版本变化不损失源量。
- 生产 Ice→Water Disabled；DriedSlime 没有自动融化规则，后续另做受热/破坏条件。Steam→Wet 最大强度 4，直接 Water 为 10。
- 极少量自由液体≤8 GMU/行、没有较大邻接自由流条时，用球体积公式缩放水滴，不设夸大最小半径；同一行两个快照不重叠半滴。接地、大流条/液团、Stable 仍走连续场。
- 小滴 Motion 在 GPU 裁页前保存完整可见域，近 GPU/远 CPU 同用一次实例；两条连续场跳过相同标记。池满保留完整连续场，CopyFrom/裁页只处理稀疏索引，CPU 首次回退不双画。
- P7/P8 FireDecay 1→2 GMU/Tick，直接伤害和角色 Burning 不改。
- 扩大回归时发现当前 MaterialCatalog_Default 只有四个旧材料，路由引用四个新增固体/气体会被初始化拒绝。补齐已有 Definition 的 GUID 引用，没有改身份编号、手写 meta 或重装场景。

设计/数据/Assembly/数学与 Debug 解释见 [living explainer](<F:/Develop Project/RPG/RPG/Assets/_Project/Docs/2026-10-06 元素世界固体气体与材料转换完整解析（更新中）.md>) Task 10，以及 [实施计划](<F:/Develop Project/RPG/RPG/docs/superpowers/plans/2026-10-06 元素世界固定固体与气体扩展实施计划.md>)。

## 2. 验证证据及边界

| 证据文件 | 结果与口径 |
|---|---|
| native-lifecycle-final.txt | 76 Passed / 0 Failed；生产资产、事务/消散、实际 Physics、落水体积/出生/跨 Tick/容量兜底、原连续场 |
| native-phase-e-final.txt | 24/0；有界模拟、真实消散、冻结恢复、零线程 Managed Alloc、Gas History/Halo/Slot 复用 |
| native-liquid-regression-final.txt | 17/0；既有 Stable 液体候选、完整数量与预算回归 |
| native-all-final.txt | 80/0；固气基础、几何/碰撞、Runtime、Steam/Toxic Exposure、实际 P7/P8 Scene Contract、转换 |
| audit-final.json | Playing=false，ScriptCompilationFailed=false；两关接线/材质有效、DirtyBefore=false/DirtyAfter=false |
| compile-summary.txt、五个 *-compile.txt | 五个 Assembly 当前源码使用真实 Unity References 的 Roslyn 编译通过；不是另起 Unity Editor |
| pure-checks-report.txt | Editor 外当前 ElementField 纯数据逻辑 17/0，含低产率提交/回滚、冻结与 Slot 复用、真实删除 32 块尾雾后零 Managed Alloc |
| scoped-whitespace-check.json | 仅本 Task 人工源码差异的空白检查；不代表整个已修改工作区的 git diff --check |

Native 报告由已有 Editor 桥直接同步调用 NUnit 断言，使用当前 Unity API/实际资产，不产生官方 TestRunner XML，不重载当前场景。各模式有重复 Fixture，不能把四批相加称作独立测试数。受测线程零 Managed Alloc 与整个游戏帧的 Unity Profiler GC Alloc 是不同口径。

Shader 检查针对当前 FixedSolid、RealGasVolume、Liquid Continuous Field 的 Unity ShaderCompiler 消息，无 Error；未实际运行所有设备/Keyword Variant，也未录制 GPU/FPS。少量水滴使用 Icosphere 球面近似与现有 CartoonVolumeScale，不能把公式体积冒称完整 GPU Mesh Volume 测量。

## 3. 失败、排除与最终修正

- `native-lifecycle-initial.txt`：68/5。两项测试误以 intValue 读取 float TickRate；三项恒量测试未结束出生 History，0→当前量渐入被误判为丢量。修正测试建场，另补出生渐入，保留真实 History/数量行为。
- `native-lifecycle-intermediate.txt`：75/0；之后增加液体 Shader 消息检查，审查修正 CPU 首批回退双画，最终 76/0。
- `native-all-initial.txt`：76/3。真实 Catalog 缺四种材料，Gas Exposure 与 P7/P8 Route 初始化拒绝。补齐既有引用，增加 Steam Exposure 参数用例，最终 80/0。
- 不采用固定大水滴、Shader 假透明库存、提高 Tick Rate/Texture3D 分辨率、按距离删浓雾或清空旧材料抢容量。

新增 CPU 固定成本：Motion 约 4 MiB，两个索引约 0.5 MiB。GPU 模式复用原 Drop Buffer；Compute 不可用时初始化额外约 2 MiB Structured Buffer。每帧 CPU 插值与实例上传、透明 Overdraw 仍应在实机检查，0 Managed Alloc 不等同成本为零。

## 4. 文件差异、复现与回滚

`files.txt` 是写入前已有文件的清单，`before/` 保留本 Task 前内容；`after/` 保存最终源码/资产和文档。新增两个 C# 与 Unity 自动生成的 meta 单列在 `snapshot_scope.py`。两个关联文件未修改，manifest 标为 unchanged_reference。文档没有完整写入前快照，manifest 明确标记，不能把完整当前文档当作本轮新增文件删除。

`manifest.json` 使用 SHA256 区分前后，`task-changes.diff` 仅包含此次人工代码/数据变化及新文件，不包含此前未提交工作。回滚应按本轮差异逐处撤销；不要重置整个仓库或恢复旧 Scene。尾雾参数 MaximumCellGmu=0 可关闭新消散；小滴池不足自动回连续场；恢复冷凝旧配方时同时回退 InputUnits/DissipatedUnits，确保规则有效。

开发复现：用 `phase-native-checks.request` 依次请求 `lifecycle`、`phase-e`、`liquid-regression`、`all`，前一结果结束并归档后再发下一请求。Playing/compiling 时桥等待，不主动切换 Play。源码编译可运行 `python compile_current_sources.py`；纯数据补充可运行 `dotnet run --project pure_checks/PureChecks.csproj -c Release --no-restore`。这些调用不能代替人工视觉和 Boss 负载验收。

人工下一步：蒸汽运动/Wet、毒雾极稀薄淡出、落水正面/俯视连续性、固体维持 Collider、火焰寿命。然后在正常 Boss 战采 CPU/GPU、GC Alloc、Chunk 计数与 Frame P95/P99；没有同条件 Before/After 实战记录前不报告 FPS 提升数值。
