# Task 11：固体成块与气体混合回归

本归档记录 2026-10-07 用户五项反馈的开发检查。沿用已有 Unity Editor；没有启动 Play、通关 P7、运行 Boss、启动第二个 Unity 或保存/丢弃 Scene，没有 SubAgent。

## 症状与根因

- 固体在失败后停止生成：32 Chunk 有限池，以及 Navigation 批次失败把单发写格预算压到 16 格以下的永久重试死锁。修复请求批次缩减，保留单发 32 格预算；生产池 128，出生粒度 2 细格，不抹掉旧固体。
- 毒雾中无法生成蒸汽：将混合总量与本材料出生目标 16 错误比较；新条件分别约束同种出生目标与混合总容量 32。
- 薄气体固定偏向 Up：六面余量遍历顺序固定；使用坐标/Tick/分量确定性 Hash 轮换，生产 Wind/Buoyancy 均 0，真实 Diffusion 1/s。
- 小碎块和重复表面：出生约 0.5m 成块；连续同材料同平面 Greedy Meshing，保留孔洞、材质和部分高度。
- 显示停顿：0.1s 过渡容易提前结束；统一 0.3s SmoothStep、从实际可见值重定起点，模拟/上传频率不增加。

## 原生检查与编译

以下是 **Direct synchronous NUnit assertions in Unity Editor**，调用真实 Mesh/Physics/Texture3D API。不是官方 TestRunner XML，也不是 Play 场景通关或 FPS 证据。每份报告写明 Unity=6000.3.16f1、ScriptCompilationFailed=false。

| 文件 | 结果与口径 |
|---|---|
| `native-red.txt` | 初版测试 38/7；两处错误把稀疏扫描槽位数当成非空格数，下一份已修正测试，保留原始记录 |
| `native-red-corrected-fixtures.txt` | 正确 RED 38/7：真实重试失败、两种固体非连续立方体、混合蒸汽全部 Pending、薄量固定方向、384 顶点、显示提前停止 |
| `native-first-fix.txt` | 第一轮 45/1；淡出完成的浮点边界与绘制判据不一致，改为与实际显示权重同一结束判据 |
| `native-capacity-diagnostic-red.txt` | 扩展检查 48/1；满池在几何预检先拒绝，诊断计数漏记；容量预检前置，量仍完整保留 |
| `native-grain-gas-final.txt` | **50/0**；聚焦出生、块形状/Collider/尾量、容量回收、混合、方向、几何与密度插值 |
| `native-all-final.txt` | **89/0**；完整 solid/gas、状态/相变、实际生产资产、两场景接线回归 |
| `native-legacy-blend-red.txt` | **26/1**；已加载 P7 保留旧 0.1s 字段，修改 C# 初始值不会迁移对象，显示仍提前结束 |
| `native-phase-e-final.txt` | **27/0**；预算、消散、真实 Physics 空气缓存、Gas Texture/Halo/插值/回收及旧字段兼容 |
| `compile-summary.txt`、五份 `*-compile.txt` | 当前五个 Assembly 用现有 Unity References 编译通过；离线编译不代替 Shader/GPU/Play 验收 |

各模式 Fixture 有重叠，不相加宣称互不重复测试总数。成功出生 32 次、合并几何、静止库存、Gas 通量/极薄消散和密度缓存等测试，测得所覆盖线程/热路径的 0 Managed Alloc；尚未测整帧 Unity Profiler GC Alloc。完整 4³ 满格 Chunk 的表面由 384 顶点减到 24，三角形有向体积保持 1m³；孔洞与材质边界另有回归。

## 文件与审计

`files.txt` 精确列出 19 个修改文件。`before/after` 相对于本 Task 开始的真实工作区，保留此前 Task 和用户已有未提交修改；`task-changes.diff` 与 `manifest.json` 只归档本轮差异和 SHA256。`snapshot_scope.py` 不恢复、不删除源文件；`scoped-whitespace-check.json` 的作用域检查无诊断。

`material-world-audit-final.json` 是最终只读配置与接线快照，须检查时间戳及 Playing=false；配置字段与 Runtime 计数分开。P7/P8 共享生产资产，不用打开 Boss 进行复杂操控。固体库存与原生 Physics/Navigation 的初始预约仍有上限；128 不代表无限驻留材料已实现。

`material-world-audit-legacy-blend.json` 留存迁移前证据：已加载 P7 原字段为 0.1s，新加载 P8 为 0.3s。最终 Renderer 在读取时限定有效值至少 0.3s，旧字段不写回 Scene，审计分开记录 Configured/Effective 值；没有为应用修改要求重载、保存或丢弃当前关卡。

最终审计时间 `2026-10-07T08:18:57.660484Z`：Playing=false、ScriptCompilationFailed=false；P7/P8 都为 128 Solid Chunk、出生粒度 2、Wind=(0,0,0)、Effective Blend=0.3s，PresentationReferencesMatch/MaterialBindingsValid=true，两 Scene DirtyBefore/After=false。P7 Configured Blend 仍为历史 0.1s，Effective 已为 0.3s，符合不写回当前场景的兼容策略。

下一步人工正常 Play：多次冰/固化粘液命中、成块大小和体积碰撞；毒雾内再生成蒸汽并积累 Wet/Poisoned；随机开放方向扩散、极薄淡出和镜头移动时的连续性。然后用正常 Boss 战长窗口采 CPU/GPU、GC Alloc、Resident Chunk 数和 Frame P95/P99；本归档没有 Boss 实战帧率结论。
