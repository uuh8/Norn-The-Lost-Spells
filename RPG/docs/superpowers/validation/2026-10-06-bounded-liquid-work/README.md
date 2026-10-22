# 液体恢复预算与局部稳定化验证（2026-10-06）

用户要求停止通过 P7 通关进入 Boss 的复杂验证，按理论评估直接实施。本轮仅修改共用的 Grid Liquid 调度与稳定化逻辑；验证使用 Unity 编译和官方 EditMode Test Runner，没有执行 Boss Playthrough，也没有新的 FPS／GPU 计时结论。

## 已实施的优化

| 问题 | 修改前的行为证据 | 实现后约束 |
| --- | --- | --- |
| 每次出队重复给恢复扫描发预算 | 一次 BeginStep 后扫描进度为 16384 格 | 整个 Tick 共用 1024 格扫描预算，保留游标与恢复标志 |
| 切换活动域同步扫描全部休眠格 | 一次调用唤醒 2048 格 | 每 BeginStep 检查最多 128 条；区域不变且本批检查完成后停止轮询 |
| 远处同种材料变动打断稳定化 | 远处每 Tick 加水，平静 6×6 湖经过 300 Tick 未能压缩 | 检查实际读取的局部 Chunk 版本；局部变化仍取消候选，容量不足保留动态数量 |

材料量仍由原 Store／Stable Owner 持有。既有渲染、液体厚度、火焰样式、状态查询、密度与通量参数未在本轮修改。分批唤醒使大量旧材料的运动分批恢复，数量和接触查询保持可读；新命中的格走原 Wake 入口。性能上限描述针对恢复扫描和休眠检查，不代表整个场景总成本已固定。

## 实际验证

- RED：官方 EditMode 4 个检查，3 Failed／1 Passed；三个失败对应上表问题。局部顶边界变化的已有保护通过。
- 首次 GREEN：五个受影响 Fixture，58/58。
- 最终 GREEN：2026-10-06 14:19:30—14:19:32 Asia/Shanghai，官方 EditMode 60/60，Failed／Skipped=0。
- Fixture：LiquidFluxSolverDeadbandTests、StableLiquidRegionCandidateTests、StableLiquidExpansionTests、StableLiquidSectorTests、LiquidSlopeTransportTests。
- 分批恢复／出队及增量依赖发现的初始化后路径，GC.GetAllocatedBytesForCurrentThread 检查为 0；没有声称整个游戏 Profiler GC Alloc 已通过。
- Unity 已重新编译 Game.ElementField 与 Game.ElementField.Tests。实际 DLL 时间与 SHA256 写入 evidence.json。

完整失败与通过成绩保存在 red-results.xml、first-green-results.xml、editmode-results.xml；changes.patch 是相对于本轮开始前副本的差异，避免将已有未提交修改混入本轮。after/ 保存四个本轮 Runtime／Test 源文件的结束快照。

## 边界与回滚

本轮未测真实 Boss 帧率、GPU Overdraw 或新 Fog，也未宣称固体／气体扩展已经实现。地形 Static Version 仍使用既有全局保守失效。休眠检查是有限预算，不保证所有旧格在返场的同一个 Tick 开始运动；待检查量越大，总恢复时间越长。

修改前副本位于 Temp/ElementPerformance/2026-10-06-bounded-work-before/。回滚仅恢复本轮目标文件或按 changes.patch 逐项撤回，不能执行整目录 reset、还原场景或覆盖其他修改。后续固体／气体工作仍记录在对应实施计划中；本记录只给已完成优化提供证据。
