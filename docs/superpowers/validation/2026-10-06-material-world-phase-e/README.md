# 固体、气体 Phase E 开发侧验证（2026-10-06）

本轮完成已授权的开发侧性能收口：零整数通量不查询目标 Terrain、Texture 按 World Key 保持缓存身份、完整 Halo 批量读取、只读计数和 P7/P8 接线审计。没有启动 Play、通关 P7、进入 Boss、保存／重载当前用户场景，也没有修改数量、Shader、活动半径或既有火焰／液体样式。

## 1. 当前证据与范围

| 归档 | 实际结果／含义 |
| --- | --- |
| phase-e-initial-red.txt | 初始 1 Passed／3 Failed：Terrain 冗余、缺少批量入口、回收使远块重传 |
| phase-e-diagnostics-red.txt | 11 Passed／1 Failed：明确复现缺少上传/漏表示计数入口 |
| phase-e-native-checks.txt | 13 Passed／0 Failed：本轮数据、预算、分配、Halo／Texture 回归 |
| material-world-regression.txt | 46 Passed／0 Failed：固气真实 Owner、Physics、Exposure、转换、生产资产与两关接线 |
| liquid-candidate-regression.txt | 17 Passed／0 Failed：旧 Stable 候选识别及局部依赖 |
| scene-budget-audit.json | P7/P8 各一个 World；固气 Owner、Renderer World、Material／Shader、Exposure、转换配置有效；审计前后 Dirty 标志不变 |
| baseline-hashes.json | 本轮开始时六个目标文件的 SHA256 |
| final-hashes.json、source/ | 最终相关源码／文档／配置 Hash；修改和新增脚本快照 |

76 项是通过当前 Unity Editor 直接调用同步 NUnit Test／TestCase 方法的断言用例总数；这些 Fixture 无 SetUp/TearDown/UnityTest。脚本先 Refresh，确认 `ScriptCompilationFailed=False` 且没有官方 TestRunner 正在运行，再执行当前 Assembly；报告记录 Unity 6000.3.16f1 与 UTC 时间。它们使用真实 Store／Runtime／Texture3D／Physics 等 API，却不经过官方 TestRunner 的完整生命周期；本轮没有官方 XML、PlayMode 成绩或 Profiler GC/FPS 成绩。

初始回收复现的临时 World 未设置 CellSize，后续补齐真实测试 Settings；缓存断言在两版均复现了重传，最终测试同时确认实际 Draw。另一次 Halo 检查误把 -1 内部坐标交给 ComposeGlobalCell，修正的是测试期望地址构造。没有通过删除生产坐标校验来让测试通过。

## 2. 可复现的成本与不变量

- 1 GMU 气格本轮所有面不足 1 GMU：阻挡回调由 7 次变为 1 次，总量仍为 1，不创建邻 Chunk。
- 回收一处气体、另一处相隔十 Chunk：不变气体多余上传由 1 次变为 0，仍绘制剩余气体。
- Chunk 8、Halo 1、两材料：旧逐 Voxel 数量定位 2000 次，新批量数量定位 54 次；另外保留中心／26 邻的 27 次版本检查。1000 个 RGBA32 像素与旧公式逐个相等。
- 角邻改变会更新原块 Halo；回收后复用缓冲归零。8 槽 Texture 池满，回收中间一块后生成新毒气：仍 Draw 8 块，只上传新块。
- 8 块静止冰暖机后 300 Tick：4096 GMU 保持、无新增表面重建、受测线程 Managed Alloc=0。
- 96 处冻结气体暖机后 300 Tick：192000 GMU 保持、无 Terrain 回调／Solver 格处理、受测线程 Managed Alloc=0。
- 96 处活动气体暖机后 300 Tick：每 Tick 处理 1～8 格，总量 192000 GMU 保持、受测线程 Managed Alloc=0。恢复扫描保留自己的预算与游标，不宣称完全没有扫描工作。
- 暖机后 300 次完整密度 Pack：受测线程 Managed Alloc=0；未发生 Current 提交时批量读仍看到旧量，提交后看到新量。

以上是计数、数据与受测线程分配证据，不是 CPU/GPU 毫秒。Texture3D 的 GPU Upload、Draw 与透明叠层成本仍需正常画面 Profiler。当前配置的 256×10³×4＝1024000 Bytes 仅为无 Mipmap 的 Gas RGBA32 Texel 数据，不含 Driver／CPU 副本。

## 3. 显式检查入口与回滚

只读菜单：`Tools/Element World/Audit Solid Gas Budgets and Presentation`，结果 `Temp/ElementPerformance/material-world-audit.json`。Edit Mode 只临时 Additive 打开未加载的 P7/P8；Play Mode 只读取已加载世界。数量、活动、Pending、转换、重建和上传诊断从已有字段获取，不扫描全世界材料格。当前归档是 Edit Mode 配置审计，World 尚未初始化，计数 0 不表示已运行。

定向断言入口：写入 `Temp/ElementPerformance/phase-native-checks.request`，内容分别为 `phase-e`、`all`、`liquid-regression`；每次独立请求并等待新时间报告，禁止把旧通过报告用于新编译错误。该开发工具会创建并销毁自己负责的临时测试对象；不会要求保存或重载用户 Scene。

回滚只恢复本 Task 的精确源码差异，新增／移动脚本保留 `.meta` 与 GUID，保护工作区原有改动。旧 Phase D 归档保持原样。本轮最终快照与 Hash 用于确认实际版本，不是授权对整个工作区执行 Reset。

## 4. 进度与待验收

A–C 固体／气体基础、D 局部转换已实现；E 本轮开发侧优化、诊断、文档与同步回归已交付。Phase E 的玩家可理解的条件／出口场景布局、视觉／俯视／隔墙气体、NavMesh 实际绕行、P8 五分钟累积、重试清理、完整 GPU 帧时间和整场 GC Alloc 仍未验收。没有新的 Boss FPS 或“无限累积始终 60 FPS”结论。

详细原理、文件责任和 Runtime 链见[学习文档 Task 6](<F:/Develop Project/RPG/RPG/Assets/_Project/Docs/2026-10-06 元素世界固体气体与材料转换完整解析（更新中）.md>)；当前门禁见[实施计划 Phase E](<F:/Develop Project/RPG/RPG/docs/superpowers/plans/2026-10-06 元素世界固定固体与气体扩展实施计划.md>)。
