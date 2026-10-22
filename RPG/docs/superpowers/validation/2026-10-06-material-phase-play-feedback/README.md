# 固化粘液碰撞与蒸汽长时间出水：本轮验证

日期：2026-10-06。Unity 6000.3.16f1。用户已退出 Play Mode 后修改代码/数据；不自动重启当前关卡，不通关 P7，不进入 Boss，不保存用户场景。

## 结果与证据范围

- `red.txt`：46 Passed / 4 Failed。复现残留 Collider 缺失、Steam 16384 vs Water 1600 GMU、500 GMU 原冷凝 30s 后剩 140 GMU、首 Tick 立即创建 1 GMU 水。
- `boundary-red.txt`：新增边界检查发现残留 Collider 的液体支撑高度重复叠加，以及完整残留格仍接收 Gas。单颗蒸汽集成箱当时只给 8 个 Liquid Chunk 页，空间不足留下 332 GMU Steam；最终集成箱单独配置 64 页验证耗尽，生产上限未提高。
- `green.txt`：56 Passed / 0 Failed，包括实际 Physics 命中/清除/Actor 避让、有限冷凝与落地、单次 1600 GMU 扩散+冷凝的逐 Tick 守恒、水系账本、目标满容量保留源、残留超容量回滚、冻区返回，以及 P7/P8 实际 Profile。
- `phase-e-regression.txt`：13 Passed / 0 Failed，气体密度/缓存/预算回归。
- `liquid-regression.txt`：17 Passed / 0 Failed，原有 Stable 液体候选回归。
- `scene-audit.json`：修复后的显式只读接线/预算审计；若 `Playing=false`，零运行计数仅表示未初始化，不是 Play 性能证据。
- `live-before-audit.json`、`live-liquid-before.json`：修改前用户实际运行样本。Gas 合计包含多种材料；不是隔离的单颗 Steam 对照实验，不能据两个时间点的数量增长断言冷凝重复创建。

总计 86 项为现有 Editor 内直接同步 NUnit 断言与原生 Unity API 检查，非官方 TestRunner 生命周期/XML。单次 Steam 集成检查暖机 30 Tick 后测量余下 270 Tick 的 Gas/Liquid/Conversion 调用，0 线程 Managed Alloc。没有新增实际 Boss FPS/GPU 或整帧 Profiler GC 结果。

## 修复原则

固化粘液 Collider 与实际细格高度一致，同时使用现有部分容量适配器，避免将剩余湿粘液堵成整格墙。Ice 保持整格阻挡；满残留 Gas 细格阻挡；地形 Raycast 跳过部分残留的重复支撑贡献。干燥仍通过同一原子事务生成残留和 Steam，并校验全部体积。

Steam 球量从 16384 改为 1600 GMU；冷凝速率为 max(12,库存×0.4) GMU/s，普通提交最少 32 GMU，完整尾批允许较小。冷凝继续扣真实 Steam 并生成同量本地 Water，由既有重力落地。温度/活动范围/空间不足/工作预算会使转换等待，不使用统一 Lifetime 删除物质。

## 快照与回滚

`before-hashes.json` 对应最初 12 个待改文件的真实 Before Snapshot。额外 Before 文件说明：Gas Runtime/Baker 在修改前补存；Audit 与 Scene Contract 从本日 Phase E 既有原始快照补存；Setup 的 Before 按本轮精确旧行重建，不能把其时间戳当作原始采集时间。两个卡片说明资产在修改前保存。

`after-hashes.json` 与 `after/` 保存本轮最终代码/数据，便于按精确差异审阅。不要把快照整批复制回工作区或执行 git reset；工作区有本轮以前的未提交工作。Scene、GUID、法术伤害/图标/名称未作为本轮修复对象。

## 用户 Play 验收与后续

重新进入 Play，先验证固化粘液实际阻挡/可站立；仅释放一颗蒸汽球后停止施法，观察本地冷凝、落地、云团逐渐减少并停止新增水；再看冰融化和毒气中毒。如果高柱/悬空外壳持续，提供截图及 Tools/Element World/Audit Solid Gas Budgets and Presentation 报告，比较真实水系账本与渲染形状。

当前功能修复与开发侧回归已完成；阶段仍“更新中”，下一步为上述视觉/实际移动复测，再继续正常 Boss 战累积负载的 Profiler 收口。
