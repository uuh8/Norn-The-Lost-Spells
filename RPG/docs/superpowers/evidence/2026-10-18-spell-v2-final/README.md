# 法术编程V2基线交付证据

2026-10-18，Unity 6000.3.16f1，复用当前已打开Editor；没有另开Editor争抢项目，也没有自动提交、创建worktree或派SubAgent。用户“你直接做完”授权继续完成基线接入，人工理解/手感Gate没有冒充通过。

## 可以直接体验的入口

正式Scene：`Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity`。走入截图红框长桌外侧的金色区域，F打开研究台，Esc关闭，Tab仍是原背包。研究台编辑真实卡实例，执行格子只读；支持多排分组、后置Modifier联动、Copy来源、Scroll/Slider、当前推演及最近实发。副本不提供完整研究界面。新卡从商店、掉落与奖励获得，没有免费填满库存或自动解锁永久知识。

基线已包括连续Draw、跨次deck/discard、Wrapping、Scope最终Bake、独立Trigger、根预付、Mana/次数/资格、独立Cast/Reload/Spread/Shuffle、动态Alpha/Gamma、寿命/终止触发、AlwaysCast、32槽和跨Scene有界回放。Condition/SelfWet、CarryParent和固定槽Copy未选择。

## 验证结果及边界

| 证据 | 实际结果 |
|---|---|
| `tests/v2-delivery-editmode.xml` | 全量Skills/Run/Character的128个明确fixture：949通过、14失败；这是最终两条授权/输入边界补修前的全量快照 |
| `tests/v2-four-station-contract.xml` | 四站点、暂停、保存与迁移原件补跑34/0 |
| `tests/v2-final-playmode.xml` | 相关Runtime/材料/Boss/库存/研究台首轮73/2；两条失败是旧E键用例 |
| `tests/v2-hub-station-f-regression.xml` | 原空间断言保留，改按工程当前F键后2/0 |
| `tests/v2-final-research-native-profiler.xml` | 授权、复杂Scope/Copy/Wrap/固定格、正式入口、P7往返和Profiler9/0 |
| `tests/v2-final-owned-input.xml` | 最新Owned V2的真实输入、30秒快慢杖、终止/取消/延迟出生、UI按住隔离10/0 |
| `tests/v2-final-formal-roundtrip.xml` | 输入补修后再次验证正式F/Esc/Tab和P7往返回放2/0 |
| `tests/v2-ui-delivery-final.xml` | 最终UI补跑15/0，新增技术预算拒绝的可见简短原因，并再次包含完整研究台、原工作台及原生Profiler |
| `latest-related-playmode.json` | 上述相关PlayMode按最新测试名去重后80项通过、0失败；这是分批汇总，**不是一次全仓库PlayMode运行** |
| `formal-layout-preservation.json` | 原629个RectTransform改变0、删除0；Scene.meta相同 |
| `initial-protected-hashes.json` | 本案前保护哈希；InventoryController源码仍为同一哈希，正式Hub在接入前亦与初始哈希完全相同 |

`known-existing-editmode-failures.json`保留14项既有失败，涉及旧作者字段/提示、图标路径、分类和仓库目录、Hub旧世界/初始资源约定。这14项在正式Hub接入前的全量检查也失败；没有为凑绿恢复用户已删的旧UI或重设当前初始资源。所有原始失败报告保留，未宣称全仓库通过。

晚期RED/GREEN另保留：未消费的旧成功许可跨更新失败请求复用，RED0/1后修为同时核对最新请求号；暂停UI按住鼠标在恢复后变连续施法，RED0/1后增加等待松开/新按下状态。正常战斗换杖仍可持续按住；已发Trigger不因编辑或切杖改写。

## 实际画面

- `screenshots/formal-hub-region-player.png`：正式位置、无碰撞金色边缘与F提示。
- `screenshots/research-complex-scope.png`：七卡双层结构，点增伤只高亮同组两枚弹。
- `screenshots/research-complex-copy.png`：Gamma借用当前待抽目标，原卡不移动。
- `screenshots/research-complex-wrap-fixed.png`：Wrap与固定卡使用同一格子样式。
- `screenshots/research-return-replay.png`：P7真实发射后回据点读取冻结记录和实际释放状态。

截图来自自动测试的独立内存资产，不代表正式新档赠送这些卡，也不代表普通玩家已完成盲测。

## 性能证据

`performance/ContinuousFastInputFor30Seconds.txt`及Heavy对应最后一轮实际Input System按住输入：快杖491发、中位0.06332秒；重杖97发、中位0.31223秒。真实速度受帧量化影响，不保证配置0.05秒就恰好20Hz。

`performance/native-hot-scopes.txt`用原生Profiler的GC.Alloc子树，阳性数组对照检出41184B；暖机后的调度空转/等待、资源Tick、格子Refresh与Zoom四类Marker均0B。仅证明所列Marker，不能外推整帧零GC。`native-hot-scopes.data.zip`可解压后在Unity Profiler载入；原始.data数百MB，压缩包约64MB。GetAllocatedBytesForCurrentThread在本机明确new数组也返回0，已弃用它作为零分配证明。

`performance/128-512.txt`为技术压力场景：128条合成指令构建128输出，不是128槽玩家法杖；四批从冻结输出直接生成512枚Arrow，包含Rigidbody/CCD。多轮集中生成131.5—220.7ms，仍有明显尖峰。没有宣称重VFX/材料组合、目标机器或Standalone达到发布级性能；池化和极端内容预算仍是明确的后续优化。

## 迁移与备份

`migration/`保存独立Schema13源样例、Schema14候选和报告；没有读取玩家正式存档。真实保存升级时，合法旧主档或恢复自旧backup的来源会单独保留`profile.schema13.original.json`，不会被第二次滚动备份覆盖。未来Schema拒绝由旧程序静默加载。

`hub-and-assets-before.zip`是本案接入前的实际工作区资产，**包含用户此前未提交的修改，并非git HEAD**。其中`FinishBackup/SinglePlayer_Hub.unity(.meta)`对应正式Hub路径，其他SO按`FinishBackup/Assets/...`的相对路径恢复。`content-modified-assets.txt`列出既有SO的改动范围。新内容保留在独立SpellProgrammingV2目录。

回滚先退出Play。推荐先关Session的Use Spell Programming V2和研究入口，保留新Schema兼容代码；若要回旧内容，按备份清单恢复正式Hub/旧SO并使用兼容的迁移前Profile。不要对整个脏工作区reset/clean。不要在新卡仍存在于存档时直接删掉它的Catalog定义。

`verified-assembly-source-hashes.json`记录验证时457份相关程序集源码快照，供后续判断证据是否对应当前代码；它是程序集快照，不表示每份文件都由本案改动。`initial-dirty-status.txt`保留本案继续实施前的脏工作区清单。

人工手感/理解验收、完整玩家通关与平衡、Standalone及目标机器性能未做。完整原理、文件职责、Debug经过及Editor学习步骤见`Assets/_Project/Docs/2026-10-18 法术编程V2开发解析（更新中）.md`。
