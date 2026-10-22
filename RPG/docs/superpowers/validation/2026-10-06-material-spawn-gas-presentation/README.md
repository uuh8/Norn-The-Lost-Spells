# Task 8：固体生成与气体云团修复验证

2026-10-06，Unity 6000.3.16f1。本轮沿用已授权固体/气体计划，处理单颗固体逐格慢生成、气团方块/静止以及小气团冷凝出大水滩的问题。没有执行安装工具、保存 Scene、启动 Play、自动通关 P7 或进入 Boss。

## 1. 根因与最终行为

- Solid 原先每条请求每 Tick 只落一格；1024 GMU/64 GMU 需要 16 Tick。现在缓存中心向外的 Sphere 邻域，用预分配 Journal 一批落位、同 Chunk 一次发布 Collider，发布失败完整回滚到 Pending。普通冰/固化粘液在下一次 10Hz Tick 完成 16 格，场地/Actor/容量阻挡仍延后。
- Gas 原先每个 0.25m 细格可装 4096 GMU，超过等体积水的有效浓度约 64 倍。现在最大 32 GMU、出生半浓度 16 GMU；单 Steam 1600 GMU 的初始气团约 1.563m³，同质量水约 0.392m³。相变质量、Steam/ToxicVapor 剂量与伤害保持。
- Gas 的六面 Flux 加入 World Space 风速及 sub-GMU 时间 Credit，真实数量和 Exposure 一起漂移。大气团出生预检到命中中心的六面连接，遇墙/预算不足保留 Pending。封闭气团完整通量无法移动时休眠，仍能查询浓度，局部地形/数量变化再唤醒。
- 现有 Texture3D、完整 Halo 和 16 步 Ray Marching 保留；世界空间连续 Value Noise 调制 Extinction，实际 Material 从 6 调到 1.6。无新 Texture Fetch、GPU Readback 或每格 GameObject。

## 2. RED / GREEN 原始证据

| 文件 | 结果与用途 |
|---|---|
| red.txt | 54 Passed / 9 Failed；首 Tick、低量静止、出生容量与实际配置的初始行为复现 |
| intermediate-green.txt | 中间回归；不能作为最终源码证明 |
| boundary-red.txt | 65 / 1；生产 16384 GMU ToxicVapor 在薄墙后错误出生 |
| sealed-wind-red.txt | 15 / 1；封闭风场静止库存仍反复进入 Solver |
| green-all.txt | 最终 66 / 0；数量、转换、Physics、Shader、场景接线、回滚及冻结 |
| green-phase-e.txt | 最终 16 / 0；有界工作、封闭风场休眠、线程分配、密度缓存/上传/回收 |
| green-liquid-regression.txt | 17 / 0；既有 Stable Liquid 候选与局部依赖回归 |
| before-scene-audit.json、after-scene-audit.json | P7/P8 Profile/Owner/Renderer/Exposure 的只读接线检查及 DirtyBefore/After |

最终共 99 项检查通过。执行方式是现有 Unity Editor 中的 **Direct synchronous NUnit assertions**，使用真实 Unity Physics、Shader 和资产读取；没有完整 TestRunner 生命周期/XML。暖机后的气/液/冷凝 270 Tick、Credit 索引复用及既有缓存/静止路径测得 **0 线程 Managed Alloc**。这不能代替整个游戏帧的 Profiler GC Alloc、GPU 时间、Boss FPS 或视觉验收。

## 3. 预算、快照与回滚

Solid 每 Tick 最多写 32 格、触及 8 个 Dirty Chunk；Gas 每 Tick 出生扫描与 Terrain 查询各最多 128。活动/恢复/转换预算沿用既有配置。大毒气球真实剂量为 16384 GMU，低浓度需要更多格，允许 Pending 分多个 Tick 落位。低浓度及 Noise ALU 的实际 CPU/GPU 成本须后续 Profiler 评估。

`files.txt` 列出 17 个本轮代码/数据/测试文件，`before/` 与 `after/` 保存修改前后内容，两个 hashes JSON 记录 SHA256。只回滚本 Task 的精确差异；不要整项目 Reset，不覆盖用户其他未提交修改，不使用重新安装或旧 Scene 文件回滚。计划与完整原理同步在本轮实施计划和“元素世界固体气体与材料转换完整解析（更新中）”的 Task 8。

## 4. 下一步人工 Play 验收

1. 重新进入 Play，分别只放一颗冰球和固化粘液球，检查命中后的及时生成、体积碰撞和后续相变。
2. 分别释放 Steam/ToxicVapor，从正面、侧面和俯视观察柔和云团及缓慢漂移；走到气团的新/旧位置比较中毒范围，确认表现与 Gameplay 位置一致。
3. 只放一颗 Steam，停止施法，观察气团缩小、较小液体落地并最终停止新增；修改共享 Profile 的风速/浓度需重新进入 Play。
4. 正常 Boss 战录制 CPU/GPU Profiler，比较活动气体格、出生 Pending、Collider Cooking、Density Upload、Transparent Overdraw 与 Frame P99。当前没有新的 Boss 实战帧率结果。

当前进度：代码及开发侧检查完成，整个固体/气体阶段仍在进行；下一步完成画面/手感及正常游戏负载验收。
