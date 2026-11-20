# Rapid 长期 Busy 与施法提交通知边界解析（更新中）

## 1. 本轮问题与结论边界

用户明确：截图中的 Rapid 通常在**长按连射到低蓝**后长期 Busy；本轮要修持有者，不再通过放宽拾取门处理症状。用户之后会自行游玩验收，当前不方便复测。

已确认的程序是 Rapid 模板、四 Damage 位于 0–3、空槽 4、Fire 位于 5，ProgramRevision2，无 Trigger/AlwaysCast。旧 checkpoint 中 Fire13、Damage各7、Mana120/CD0；包内三张 Termination 并非安装卡。

**本轮取得了明确的永久 Busy RED，并修复了资源提交后的通知异常边界。** 这条缺陷使用同一无 Trigger Rapid 就能重现，不需要空 Trigger。另一方面，旧现场没有保留 Ticket/Draw/Driver 明细及触发异常的日志，因此仍不能证明旧截图当时具体是哪位消费者抛错。不能把故障注入和正常 Native 压力测试混称为“原图完整重演”。

对应[实施计划](<../../../docs/superpowers/plans/2026-11-16 Rapid长期Busy持有者根因修复计划.md>)；证据在 `docs/superpowers/evidence/rapid-busy-20261116/`。上一轮的地面收纳豁免保持，未再放宽任何 Busy 条件。

## 2. 为什么先排除“低蓝等于永久锁”

四 Damage 先花 40 Mana，Fire 还需 20。低于 60 Mana 起手时可能没有 Fire 输出，但已支付的 Modifier 仍按原程序语义推进 Draw/reload，产生 `AdvancedWithoutEmission`；Driver 将该轮记为 `Rejected/EmptyOutput`。

这解释低蓝持续空放，不能单独解释库存长期 Busy。正常零输出图只有根 Scope、零 Outputs，Complete 后不需要等待不存在的世界观察，Collect 会回收票据。

本轮补了此前未充分覆盖的控制：

- 真实 Caster 暖执行后在局改槽，再连续 320 轮低蓝/零输出，跨越 256 项资源请求收据窗口；逐轮验证新 `MoveOwned` 和所有忙门。
- 每发先由权威 Tick 自然回满蓝，使 Fire 真正经历 `13→0`；之后纯 Modifier 程序继续运行。不能用长按推定已经耗尽 Fire。
- 独立 Native 原 Mouse、正式在局改槽得到 Revision2；分别先松后 Tab、持键开 Tab 后松，停手后至少 8 秒意图不增长，再用真正的旧物编辑收据判断是否解锁。
- 原构建 `0e211…` 的两轮各 50 秒持键，分别实际观察到 97、95 个低蓝 EmptyOutput，Fire 仍为正数。停手后没有新意图、权威四门清空，编辑成功。这是未复现控制，不能倒推用户当时没有故障。

## 3. 真正复现的永久 Busy 持有者

旧代码把资源事务与同步通知放在同一条异常传播链：

```text
Prepare：库存和法杖持有候选预约
  → Commit：Mana/次数/Draw 已提交，Replay 已建立
  → InventoryChanged 或 CastResourceResult 通知
  → Caster 才发布已暂存的 OutputReleased
  → Owner 接到观察，Collect 移除已完成票据
```

`EventBus.Publish` 是同步多播，某位消费者抛异常会截断其后所有消费者。如果错误发生在 Commit 后，Caster 会进入原生成失败 catch，清掉还没送出的输出观察，再尝试 Cancel。可是资源已经提交、`_pending` 已结束，Cancel 不能把已发生的提交撤销，于是留下：

```text
Fire 已扣一次；Mana/CD 继续恢复
Ticket：Committed
Replay：NotObserved
ActiveReservations：1
八秒乃至更久后，新 MoveOwned：Busy
```

这正是“看起来已经停手，资源也恢复了，但仍不能编辑”的永久持有状态。单靠时间不会调用正确的输出观察，不能用超时清票据代替生命周期修复。

有效 RED 保存在 `all-notification-boundaries-red.xml`：

| 异常点 | 原行为 | 本轮验证 |
|---|---|---|
| Rapid / 普通杖 Commit 后 InventoryChanged | 两例均在 8 秒后新 MoveOwned=Busy | 原费用、同一 Projectile、实际输出观察、预约归零 |
| Complete 结果的早订阅者 | Adapter 漏收，已付资源票据不结束 | 故障后 Adapter 仍收到原结果 |
| OutputReleased 的早订阅者 | 真正 Owner 漏收，新编辑 Busy | 故障后 Owner 仍接受同一观察 |
| 输出出生事实消费者 | 后续消费者漏收，Driver 错报失败 | 后续消费者收到一次原事实，原出手不被重施法替代 |

五例原先均失败。最后一例是同一通知边界的事实丢失，不把它单独说成永久 Busy 根因。故障注入只属于 fixture 的订阅，结束精确退订，没有手工 Observe、Cancel、清票据或改资源。

## 4. 修复：提交结果与通知消费者分开负责

Core 新增 `EventBus<T>.PublishFact`，专门投递已经确定的结果/事实。每位消费者分别执行；单个失败保留事件类型、接收方法及异常日志，随后继续把同一事实交给后面的消费者。原 `Publish` 不变，Prepare/Complete/Cancel 命令执行本身仍按原规则失败。

订阅列表变化后才重建缓存；每次投递持有局部快照，保留顺序、重复订阅、回调中增删和重入语义。稳定投递不逐次调用 `GetInvocationList`。这是设计约束，本轮未将它宣称为已测得全帧 Zero-GC。

接入点仅为：

- OnlineOwnedWorkspace 与 SinglePlayerInventorySession 的施法后库存通知、施法结果。
- OwnedWandCastAdapter 的输出观察和生成失败通知。
- CastSpawnTransaction 已提交的出生/声音事实，包括延迟输出的即时分支。

不能把权威接收失败说成世界健康。OnlineCombatWorld 的出生接收器若发生未预期异常，会先锁存 `Faulted`、关闭世界写门、保留现场，再交事实层记录异常；原容量/受控表失败也保持 FailClosed。没有销毁已支付输出、补发新投射物、退款或放宽版本/Reservation。

## 5. 新的拒绝证据

Development/Editor 的新 Busy 拒绝日志，除原 Timeline/Queued/ItemsResourceBusy/CastPending/ActiveReservations 外，还会只读描述最多四张活票据：程序修订、PendingLayer、各层状态、未观察输出编号及是否持有本次 Prepared。描述不调用 Collect/Cancel/Synchronize，不输出账号、InventoryId 或凭据；健康路径不生成这些字符串。

如果用户后续仍遇到 Busy，可以区分“仍有已提交未观察输出”与“真正准备锁/材料锁/动作排队”，不再根据一行 UI 文字猜持有者。

## 6. 实际验证与交付接续

- 有效 RED：5 failed，保留原拒绝和事实截断。
- 当前 Character fixture：15 passed；含故障五例、真实 Caster、双 Owner、排队/起手控制、320 轮与 Fire 耗尽。
- 最终 EditMode：Core/Run/Character 193项，联机出生故障、Frozen、队列与重绑定32项，共 **225 passed / 0 failed**。
- 最终 PlayMode：owned施法、单机提交后通知异常、共享库存与容器，共 **33 passed / 0 failed**。有限Fire13→12、Mana100→80、同一真实输出与后续编辑均有断言，未用手工清票据恢复。
- 配套 DS、库存测试Client、正式菜单Client均构建成功，三份Build Report均0 errors，GameplayHash同为 `5ba19c06864d62a6b2a90dc558dc8dfc2748cca52358eceee7dd367f069fa47b`。第一次DS打包发生Editor Texture分配OutOfMemoryError，原失败保存在 `build-memory-failure.json`；冷启动Editor并在各构建前回收未引用资产后，三份构建成功。未修改系统内存配置。
- 修后独立Native：**32 checks passed**。两轮分别长按50.192/50.189秒，观察到98/101个真实低蓝EmptyOutput，Fire仍分别28/26；处理MouseUp后至少8秒无新施法意图，均取得新MoveOwned成功收据。新鲜DS报告Timeline/Queued关闭、ItemsResourceBusy/CastPending为false、ActiveReservations=0；原正常退出及持久收据通过。
- Native日志：Client未见Error/Exception；DS仍有两条既存Built-in Resource初始化错误，本轮未解决，不能把定向检查通过写成发布零错误。原生硬件输入、主观战斗手感及完整区域2仍不在本轮验收范围。
- 第二位只读Reviewer核对断言位于通知回调之后，最终XML未见AssertionException，避免PublishFact误吞测试断言形成假绿。源码8文件SHA与通过回归时冻结的manifest一致。
- 旧截图唯一现场触发者仍未证明；用户已明确现在不方便复测。本轮修完并完成限定验证后停止排查，实际游玩留给用户。

## 7. 本轮最终交付与停止位置

默认 `Builds/OnlineRooms/Server/NornRoomServer.exe` 和 `Builds/OnlineAccounts/Menu/NornMenuClient.exe` 已替换为上述配套构建，分别对361/379个文件逐一核对SHA256；旧目录保留可逆备份。只重启已记录身份的本机业务服，新PID51580，`http://127.0.0.1:5080/health/ready` 实际返回ready；MySQL PID37068及启动时间未变，未操作用户账号资产。

Editor留在保存过的P7_MainMenu、非Play、无编译失败或Dirty Scene；自有Native Client/DS均已退出。79个保护文件字节不变，本轮生成的测试Scene恢复为构建前字节，原生成副本保存在ignored staging。没有提交或推送。

最终机器记录为 `docs/superpowers/evidence/rapid-busy-20261116/acceptance.json`。本轮可复现缺陷修复和限定验证完成，按用户要求到此停止；`ReportedRapidIncidentUniquelyAttributed=false`、`UserGameplayAccepted=false`、`ReleaseReady=false`保持，后续实际游玩由用户安排。
