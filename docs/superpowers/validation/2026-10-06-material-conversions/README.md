# Phase D 材料转换验证记录

本轮继续已批准的固体/气体扩展，交付局部水气/水冰转换、Sticky 两产物干燥、显式 Poison 挥发门禁、真实 GMU 事务和有界调度。未进入 Play Mode、未通关 P7、未进入 Boss 战、未提交 Git。

## 实际执行证据

| 文件 | 证据及边界 |
|---|---|
| phase-d-native-green.txt | Unity 6000.3.16f1 当前源码编译失败标志为 False，46 Passed / 0 Failed：26 项固气基础 + 20 项转换与接触检查 |
| liquid-stable-regression.txt | 旧 StableLiquidRegionCandidateTests 的同步断言回归，17 Passed / 0 Failed |
| phase-d-setup.txt | P7/P8 引用与生产密度安装报告；P7 仅在内存配置，P8 Additive 保存后关闭；备份路径已记录 |
| conversions-foundation-red.txt | 缺少转换基础协议时的首个失败 |
| conversions-cache-red.txt | 取消融冰后 Liquid 派生阻挡缓存仍处于开放状态 |
| conversions-residue-red.txt | 非阻挡残留没有占用 Liquid 容量 |
| conversions-bounded-cache-red.txt | 局部失效后残留 Dictionary 超过预分配容量 |
| conversions-environment-wake-red.txt | 冷冰从候选释放，升温后没有通知，融化量为 0 |
| source-hashes.json | 本次相关源码、测试、生产 Profile、P8 和文档的 SHA256；包含现有脏工作区内容，不表示这些文件全部改动都由本轮产生 |

这些报告由 `MaterialPhaseFocusedChecks` 在当前 Editor 直接运行明确无 SetUp/TearDown/UnityTest 的同步 NUnit 方法；部分用例构造真实材料 Owner、MeshCollider 和 CharacterController。它们不是官方 TestRunner XML，不覆盖完整 Fixture 生命周期、场景 Playthrough 或独立 Player Build。

1000 个完整水↔气循环（2000 次转换，20 个循环暖机）验证真实 Owner 总量保持 100 GMU，当前线程 Managed Allocation 为 0。该结果只覆盖数量事务热路径，不代表整场景 GC Alloc=0 或 GPU/Collider 性能已达标。

当前生产粗液体容量 510，Solid 每细格容量 64，粗格覆盖 8 细格；物质量精确、密度整数舍入约 0.39%。当前只支持整粗格纯水冻结，混合水体保留。Source / 两种产物任一步容量或几何失败，源不丢量；冻结活动域不补算墙钟时间。

## 复查入口

1. Editor 菜单：`Tools/Element World/Install Fixed Solids and Real Gases`。已加载 Dirty Scene 不自动保存；先解决编译错误才安装。
2. 显式原生检查：在项目 `Temp/ElementPerformance/phase-native-checks.request` 写 `all`、`conversions` 或 `liquid-regression`。检查自动 Refresh，待当前编译完成后执行；报告输出到同目录 `phase-native-checks.txt`。勿在另一个官方 TestRunner 任务活跃时并发运行。
3. Gameplay 诊断：World 的转换候选数、处理数、延期/溢出计数、显式族审计与 `MaterialWorld.Convert` Profiler Marker。

未验收项：冷热/干燥区玩家可见原因、相变动态观感、NavMesh 实际绕行、Boss 长时累积、整场景 GC、CPU/GPU 帧耗时。材料转换本身保持 GMU，封闭环境总量不会因此减少；未来实际减少库存需可理解的通风、排水或消耗规则。
