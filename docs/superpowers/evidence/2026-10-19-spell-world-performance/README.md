# 施法、寿命、水面与地图性能验收

2026-10-19。本轮四项处理完成；没有改写用户扩建后的Hub、P7、P8场景文件。

## 结论与证据

| 项目 | 结果 | 原始证据 |
|---|---|---|
| 快速施放 | 实际Owned事务提交Cast Delay从0.8缩至0.68秒；整轮末尾若Reload更长，最终等待仍由Reload决定。动画出手独立且保留 | `spell-world-cast-runtime-confirmed.xml`、`spell-world-final-editmode.xml` |
| 短寿命 | 按用户确认改为每张×0.5；5秒基础寿命两张1.25秒、三张0.625秒，Prefab默认值与显式值在真实弹体上均正确到期 | `spell-world-lifetime-red.xml`、`spell-world-final-editmode.xml`、`spell-world-cast-runtime-confirmed.xml` |
| Water | 10份Built-in Water/Water_Mobile材质接入现有URP装饰水；GUID、Normal、颜色、UV输入保留 | `water-material-repair.txt`、`water-preservation.json`、`water-render.json`、`water-compatibility-overview.png` |
| P7 | 不施法暖态模拟平均5.67→1.75ms，约下降69%；FireFall 2.66→0.41ms；邻居准备2.24→0.89ms | `performance-comparison.json`、Native Profiler归档 |
| P8 | 已单独测试，空闲模拟约0.001ms，没有复现P7同等级模拟瓶颈；Exposure和Rendering仍有独立开销 | `performance-comparison.json` |

短寿命原公式并非神秘非线性：5−1.6×2=1.8秒，5−1.6×3=0.2秒，在同速下射程相差9倍。此前称第三张已经到0.01秒下限不准确，已更正。新倍率属于指数衰减，每多一张稳定再减半；仍保留最终0.01～600秒安全钳制。

快速施放与快速周转分别服务“轮内两次施放之间的等待”和“读完后重新开始的装填”。StarterA旧配置默认Reload沿用基础Cast Cooldown，因此单发每次都读完整轮时，很容易被同样长的Reload盖住。此次没有把两个机制合并，也没有让点击绕开Animator释放点；卡片说明明确各自范围。

## 验证范围

- EditMode：`spell-world-final-editmode.xml` **123/123**，覆盖冻结/倍率/独立Scope/非法输入、Cast Delay与Reload、编译、Copy/Always Cast、火焰跨Chunk守恒与零分配、世界模拟、三图水材质。
- 本轮核心PlayMode按每条用例最新结果合并为 **39/39**，详见 `latest-playmode.json.Core`：16项研究台与据点/Home/P7流程、14项Owned V2输入/动画/来源/连续施法、3项Runtime弹体、3项真实地图Profiler采样、2项P7/P8实际左键+G+回程、1项真实火焰世界与GPU Renderer暖态检查。连续输入测试包含各30秒的快速/重载配置；它们不是“快速施放卡”的端到端间隔测量。
- `actual-fire-world-and-render.json` 记录65帧，FireFall、GpuFire.ParcelLifecycle、GpuFire.Draw均有真实样本且各自GC为0；整帧仍有其他分配，不宣称全帧零GC。
- 三个正式Scene SHA-256与本轮开始备份完全相同；结果保存在 `performance-comparison.json.FormalSceneFilesUnchanged`。材质修复没有重建场景、移动角色/传送门或恢复已退役实验区。
- 修复后的正式Hub按水面实际材质选取范围、在临时PreviewScene相机中实际渲染，1280×960图像无错误紫色像素；保留原夜景光照。此URP装饰水没有移植旧GrabPass折射、岸边泡沫、波形位移与实时平面反射，不是完整复刻供应方Built-in效果。

## 保留失败与未验证边界

所有首批失败保留，没有用最后一批通过覆盖原始记录。寿命首次PlayMode两项失败来自测试模板被刻意设为inactive，克隆也未激活，因此不会收到Unity Update；激活本次测试克隆后，真实倒计时与LifetimeExpired均通过，没有改生产Prefab活动状态。

第一次完整玩家批次28通过/2传送测试超时。瞬移后增加正常帧/EOF和F入口状态前置检查（门资格、焦点、松手保护、站点优先）后，两个真实F流程重测通过；没有绕过Runtime权限直接调用传送。P8普通物品投掷的测试在释放G后等待LateUpdate提交点再断言，实际左键、G容器/普通物品与回程都通过。该结果不支持声称最初两项超时均已确定为生产传送缺陷。

额外误选整个派生 `DungeonInputRepairPlayModeTests` fixture 时，NUnit还继承执行了10项基于旧Review布局的背包检查，6通过、4失败。原始结果在 `spell-world-travel-retry.xml`，逐条保留在 `latest-playmode.json.AdditionalInheritedReviewCases`。其中实验区检查依赖正式Hub已退役的组件；另三项拖拽检查本轮未深入归因，因此不能声称背包所有路径通过，也不能把它们未经本轮前基线核对就宣称为既有失败。本轮未修改背包Runtime，不为这些检查重新启用旧实验区。

性能是Editor下正式场景入口、60帧预热、20帧Profiler预热、90帧空闲采样。P7帧时间中位数约27.8→19.8ms、P8约18.5→16.3ms；Editor重绘、GPU等待、后台服务和自然演变的材料状态会影响短样本，不能保证Standalone达到固定FPS，也不是同一World Tick逐帧重放的严格基准。没有独立GPU计时或目标机测试，没有重跑全项目所有测试；上轮另外记录的历史失败不在此处消失。

## 复查与回退

`summarize.py` 可从 `TestResults/SpellWorldPerformance/{before,after,after-final}` 的TSV/JSON重建统计。只汇总同一Main Thread Marker，父子Marker和工作线程Semaphore等待不能相加冒充CPU总耗时。Native原始`.data`归档保存在 `TestResults/SpellWorldPerformance/native-profiles.zip`，索引、体积与CRC验证状态见 `native-archive.json`；原始数据先完整校验后才收起未压缩副本，避免在工作区堆积数GB生成物。

本轮开始的场景和法术源码备份位于 `Temp/Spells/RuntimeRepairBackup/before-scenes-and-spell-sources.zip`，10份材质及meta也在同一备份目录下原路径保存。回退只恢复本轮对应源文件/材质引用，不覆盖用户新地图或更早目录整理。实现原理、逐文件Assembly职责、取舍和Editor技术见法术开发解析Task 9。
