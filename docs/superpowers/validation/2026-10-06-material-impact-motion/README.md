# Task 9：实际固体/气体命中与气体连续动感

2026-10-06，Unity 6000.3.16f1。处理固化粘液逐格生成、毒雾地面命中延迟、气体扩散阶跃/静态棉花感及普通火球残留过大。延续用户已授权的固体/气体修复；不启动 Play，不通关 P7/Boss，不保存 Scene，不执行 Installer，不使用 SubAgent。

## 1. 复现、根因与方案

真实 Prefab + Raised Floor 的首 Tick：DriedSlime 只有 704/1024 GMU，ToxicVapor 为 0/16384；第二发固体在共享预算不足时露出半团。根因是半格偏移仍与厚地板相交、固化粘液的实际 Radius 比上轮 Fixture 小，以及整批发布并不等于每条请求完整发布。现在找邻接空气锚点，普通固体用剂量适配半径，预算不足整发撤回并延后。现有 Ice/DriedSlime 的 1024 GMU 在正常空间首 Tick 完成；超过完整 Tick 写格预算的大型自定义固体请求仍沿用有界逐批处理，尚未做跨 Tick 预构建/统一发布。

Gas 的初始连接回到实心中心时全部出生失败。现在先找空气锚点，首团只放到命中外侧。出生扫描每 Tick 2048、单请求 1024；4×1×4 的空气 Tile 用一次保守 Box 采样证明 16 个格，有墙则回退细格，仍共用 128 次 Terrain 查询。首 Tick 实际 ToxicVapor 至少落位 8192 GMU，量未丢失，薄墙回归通过。

Renderer 原先直接跳变 10Hz 的快照。现在同一 RGBA32 Texture 的 RG/BA 保存过渡前/后两材料密度，0.1 秒逐帧插值，新出生立即显示，过渡中再次更新从当前可见值接续。云内 Noise 加入小幅动态采样扰动，scaled Time 随暂停停止。CPU 风/浮升仍搬运真实库存与 Exposure，Shader 不回写 Gameplay。GPU Texture/Fetched 数量不增加；生产 CPU 历史约新增 1,024,000 Bytes，Shader ALU 增加，尚无实战 GPU ms 结论。

普通 Fireball 的火元素 1200→400 GMU，Radius 0.6→0.4m；玩家/普通敌人/Boss 普通火球均引用共享 Prefab，直接伤害/Burning/瞬间爆炸参数未改。此为用户明确要求的剂量调整。

## 2. 原始证据

| 报告 | 结果及含义 |
|---|---|
| red-all.txt | 67/4；真实地面出生及同帧半个固体的复现 |
| intermediate-all.txt | 71/1；立方 Tile 在地板交叉下仍仅出生 1696 GMU，不能替代最终通过结果 |
| red-temporal.txt | 16/3；缺少逐帧显示、密度 62→125 阶跃、无历史打包 |
| red-fade.txt | 19/1；当前量化为零时遗漏前态淡出 |
| green-all.txt | 72/0；实际 Prefab/Physics、蒸汽真实位移、数量/转换/冻结/薄墙/Shader/两场景接线 |
| green-phase-e.txt | 21/0；逐帧/暂停/中断/淡出、Halo/缓存/回收、空气 Tile Ring 及受测热路径 |
| green-liquid-regression.txt | 17/0；既有 Stable Liquid 候选/局部依赖 |
| before-scene-audit.json、after-scene-audit.json | 两关卡只读审计，Playing/Compilation/DirtyBefore/After 与实际共享资产 |

共 110 项同步断言通过。方式为当前 Unity Editor 中的 **Direct synchronous NUnit assertions**，真实 Physics/Shader/资产读取，不是官方 TestRunner 完整生命周期 XML。空气 Tile 真实 Physics/缓存槽轮换及历史打包暖机后 300 次为 0 线程 Managed Alloc；不等于整个游戏帧 Profiler GC Alloc=0，也不代表 Boss FPS。

开发过程中的 ulong/int 乘法编译错误与测试字段拼写已修正。显示 RED 报告只采用编译成功后的断言失败。显示实现曾暂存并恢复原 Renderer，以明确运行原始阶跃行为，最终 after/ 保存修复版本。

## 3. 快照、预算与回滚

`files.txt` 是本轮 15 个代码/数据/测试文件列表；before/after 与 SHA256 JSON 保存精确版本。只回滚该 Task 的具体差异，不 Reset 项目，不覆盖用户其他未提交修改，不恢复旧 Scene。计划和 living explainer 的 Task 9 记录全部修改文件责任、坐标空间、RGBA 双快照、性能成本和后续边界。

Solver 的 10Hz、活动/冻结协议、Terrain 128 次预算与 Chunk/Texture 容量保持。Solid 首 Tick 批次仍最多 32 格/8 Dirty Chunk；特殊 Actor/地形/容量拒绝会等待。大剂量气体仍可跨 Tick 落位，首次可见剂量已有针对性下限。CPU/GPU ms 与长期累积成本须用实际 Profiler 评估。

## 4. 接下来人工 Play

1. 单颗和同时两颗冰/固化粘液命中地面、墙边，检查整团及时出现及碰撞。
2. 毒雾地面命中看首团可见延迟，前/侧/俯视检查扩散连续性与真实中毒范围。
3. 单颗 Steam 看上升、风向漂移、云内轮廓卷动，暂停后应停止。
4. 单颗普通 Fireball 看较小的残留火团；正常 Boss 战随后录制 CPU/GPU Profiler。

当前进度：四项代码修复及开发检查完成；画面、手感和 Boss 长时帧率尚待用户正常游戏确认。
