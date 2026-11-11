# Native5/6/7 短按次数差异：只读证据复核与最小观测点

2026-11-01。仅供主 Agent 内部交接。本调查已读两层 AGENTS.md，沿 systematic-debugging 先查证据与控制流；未读取任何 private/session 账号、密码、Token、secret 文件，未启动测试、Editor、服务或游戏，未改仓库源码、参数或计划。唯一新文件为本暂存文档。

## 结论先行

现有证据确认三轮请求相同 0.12 秒攻击，输入脚本整次 mouse 操作实际分别约 131/127/127ms，选中同一卡片在 Client 已安装 Source 与 DS Authority Workspace 中均从 30 变为 29/26/29。Native6 的资源差异确实存在于权威当前值，不能仅归为 UI 展示误差。

现有证据**不能**把 Native6 的 4 点次数消耗归因于“脚本实际持键数秒”、帧/输入队列调度，或同一施法重复执行。它能排除该次脚本动作在 Python 层耗时数秒的解释；仍缺 OS 边沿、Input System 消费时刻、Client CommandId 与 DS 资源提交之间的关联。不能把次数差额直接等同于四个不同施法命令，也不能宣布游戏执行去重已经被破坏。

## 1. 已有证据对齐

证据公共根：

`F:\Develop Project\RPG\RPG\docs\superpowers\evidence\multiplayer\n5\container-owner-projection-2026-11-01\`

下表取各 `native-5` / `native-6` / `native-7` 的 `p8-os-input.jsonl` 原始 UTC字符串。每轮只有一条 `Action=mouse`，均为 Client a，`Result.AttackSeconds=0.12`。

| 轮次 | StartedUtc → FinishedUtc | 整操作时长 | 同一卡片初值 → Client/DS末值 | Client快照序号 / InventoryVersion | 末次Actor1 Epoch |
|---|---|---:|---|---|---|
| Native5 | 10:45:44.090161 → 10:45:44.221149 | 130.988ms | 30 → 29 / 29 | 2 / 3 | 1 |
| Native6 | 10:51:01.647355 → 10:51:01.774472 | 127.117ms | 30 → 26 / 26 | 5 / 6 | 1 |
| Native7 | 10:57:31.358820 → 10:57:31.485710 | 126.890ms | 30 → 29 / 29 | 3 / 3 | 2（已发生重连） |

初值来自对应 `p8-a-fresh-profile.json` 的 `wandtest.fire`，通过 InstanceId 与选中法杖 ParentInstanceId 对应末次 `ActorInventories[ActorId=1]`；没有把备用杖的 5 次卡混入。

- Native5 卡 `da29e46563d14984936d658ae1793dcd`；A末次 10:46:39.461640Z，DS末次10:46:41.2913988Z。
- Native6 卡 `4f54765628a540a6a8e15880bc30b732`；A末次 10:51:22.7379045Z，DS末次10:51:24.1080551Z。
- Native7 卡 `4e32ea71d797449da40dd20e35bcf7f1`；A末次10:57:41.4322268Z，DS末次10:57:43.1875705Z。
- 三轮 B的同组选中卡末值均30，没有 B攻击的 mouse audit。

Client的 `ActorCharges` 与真实安装库存 `Items` 同样为29/26/29，DS `AuthorityWorkspace.Items`同一实例也一致。Native6的Client序号5、库存版本6支持发生过更多资源版本变化，但序号/版本本身不是逐次施法账本，不能拿它替代 CommandId/Commit 记录。ResourceVersion含Mana/cooldown推进，也不能当施法次数。

末次 `AttackHeld=false` 只证明采样时已松开，不能说明攻击期间何时消费了 up。Native5的A末次为 `Frozen=true/Suppressed=true`；Native7末次已是 Epoch2，不能将它们的末次状态当作相同输入时间窗的逐帧对照。Client诊断的 `ActualSnapshotTickAvailable=false`，只有ProbeSampleTick；不能将末次观察tick冒充库存原始提交tick。

## 2. 脚本实际做了什么、audit能证明什么

已读当前暂存 `native-timing-input.py`。当前文件 SHA256：

`e4611d616b7a3203fdf5383e6aaf4e89c724d388feef37d1ca2f7f73eb9d9766`

`Run-N5OwnerNative5.ps1:116`、`Run-N5OwnerNative6.ps1:116`、`Run-N5OwnerNative7.ps1:116` 均调用：`Input 'a' 'mouse' @('--seconds','0.12')`。

当前输入脚本的关键顺序：

1. `focus()`完成后，104行取得 `started`。
2. 122行 `mouse(2)`：`guard()`确认owned foreground，`SendInput`发送一条 left-down，并断言返回1。
3. 124–125行在down返回后计算 `end=time.monotonic()+a.seconds`，循环 `guard();time.sleep(.03)`，系统调度可能让sleep稍晚返回。
4. 126行 `finally: mouse(4)`，同样校验foreground、SendInput left-up，并断言返回1。
5. 175行才取得 `FinishedUtc`，176行写audit；没有在持键窗口内写JSON或请求UI报告。

因此 Started→Finished包住down/up函数调用及持键循环，约127ms结果不支持Python被挂起数秒后才发送up的解释。`AttackSeconds`仅是请求值；现有audit没有独立的down-send-return / up-send-return monotonic时间戳、SendInput返回计数、游戏收到事件时刻，UTC本身也不是专用的单调计时证据。当前脚本也没有一份已保存的历史脚本Hash来独立证明三轮运行时字节完全相同，当前源码与audit格式一致不能替代该证明。

脚本每次攻击会调用 `mouse(2)`一次、`mouse(4)`一次；没有mouse重复循环。但真实Input System仍可能晚于SendInput注入才消费，其他输入设备也可能触发Attack；现有记录不能识别该时刻的全部设备事件。

## 3. 日志与帧证据的限制

检查各轮 `a-last.log` / `b-last.log`。没有可关联的 Attack performed/canceled、CommandId、Intent、Receipt或资源提交记录；不带边界的`Cast`搜索仅命中启动阶段的UDP `broadcast`文字。可见Warning为 `[UI] PlayerHealthBar 未指定玩家 HealthComponent，血条不会更新`，不提供本次次数差异的原因证据。日志没有攻击窗口的有效帧耗时序列。

三轮 `a-last-movement.json` 都为：

- `Frames=0`、`Calibrated=false`、`TickCounterValid=false`；
- `FrameTimes`长度240，但所有值为0；
- DS末次 `InputPendingCounts=[0,0,0,0]`。

这些不证明攻击期间没有卡顿或队列积压：Probe只有在显式Measure窗口采样（`OnlineMovementAcceptanceProbe.cs:244–254`），本轮数组是未采样的预分配槽。DS的InputPendingCounts、SupersededContinuousInputs属于运动输入队列的末次/累计统计，不能代替Cast意图队列，也没有与这次点击关联的时刻。

## 4. 当前源码控制流与去重边界

### 输入到新命令

- `Settings/InputSystem_Actions.inputactions`：Attack为Button，interactions为空；绑定包括Mouse leftButton、Keyboard Enter及其他设备。配置没有显式重复Press interaction，但这不构成运行时事件次数证据。
- `PlayerControllerBase.cs:621–651`通过 `_inputSubscribed`防重复注册，Attack只订阅performed；`WizardController.cs:185–188`在Prediction模式转给 `OnlineCastDriver.CaptureAttackPress()`。
- `PlayerControllerBase.cs:455`的 `IsOnlineAttackHeld`直接读取当前 `Attack.IsPressed()`，附带本机输入/焦点/抑制资格。
- `OnlineCastDriver.cs:478–509`：performed设置 `_queued=true`；StepPrediction每步读held。queued被消费后，若后续held仍为true且timeline/cooldown已允许，则会合法创建**另一个新CommandId**。新命令为 `(ConnectionEpoch << 32) | ++_clientSequence`。
- `OnlineWorldBootstrap.cs:485–494`在当前输入步模拟并发出意图；移动Prediction replay不调用此施法入口。
- `AuthorityTickDriver.cs:38–67`在Update中推进固定步；`FixedStepClock.cs:6–18`为30Hz，每帧最多补4步。同一Render帧可能多次读取相同Input System held状态。这只是可检查的机制，不是本轮已发生的事实。

当前 `CombatDefinition.asset:20–21`地面/空中release均0.26666668s。单个timeline不会因为4次30Hz补步就释放4次；不能见“补4步上限”和“少4次”就推导因果。实际二进制与配置仍应以本轮匹配构建证据为准。

### 网络与权威

- `OnlineCombatWorld.cs:198–202`将IntentProduced交给channel；`CombatNetworkChannel.cs:155–163`经Mirror Reliable发送。
- `CombatNetworkChannel.cs:91–103`验证当前连接、GameplayHash、ConnectionEpoch和速率；允许合法同ID重试进入上层ledger，以便返回原收据。
- `OnlineCombatWorld.cs:217–221`检查Actor/slot epoch，再交给 `TryQueue`。
- `OnlineCastDriver.cs:512–526`先查固定receipt ledger：相同CommandId+Epoch直接返回旧收据，不重新排队；`Validate:530–535`拒绝 `CommandId <= _lastCommand`、不匹配Epoch/来源/程序等。
- `StepAuthority:540–570`拒绝同/旧tick，先推进资源与已排队任务；timeline越过release才调用一次 `CastOwnedProgram`并产生Released/Rejected收据。
- `AuthorityActionTimeline.cs`用 `_released`、`_lastTick`防同timeline重复跨阈值；不是每次Step都出手。
- `SpellCaster.cs:394–402`进入真实owned程序；`WandExecutionTransactionService.cs:126–141,190–221`预约和提交uses，检查prepared ownership和wand reservation，root提交更新执行与Replay。资源层不是拿网络CommandId做任意全局幂等；网络同ID重放主要在Driver ledger/timeline阻挡。

静态结论：当前源码已有同ID重复包、旧tick和timeline单次release保护；持续held产生多个不同ID仍属于另一条路径。不能仅凭这些保护就宣布实际Native6没有重复执行，亦不能仅凭余额26宣布同ID保护失效。

## 5. 最小默认关闭观测链（仅建议，本轮不实施）

主线容器操作继续，不需要为此中断当前Native或改施法冷却/次数/输入时长。未来若独立复核，建议只增加一个默认false、显式单轮启用的观测开关；不添加生产协议字段，不改变施法行为。

所有客户端/DS观测使用预分配固定struct环形缓冲（例如256/512槽）、满时记dropped，不在Tick格式化字符串、写JSON、打印GameLog或分配集合；由现有显式probe命令在窗口结束后一次导出。默认关闭不注册InputSystem observer，游戏热路径只做已关闭分支判断。记录本机单调tick/frequency与一次UTC锚点，不能把不同进程的realtime原点直接相减冒充精确网络延迟。

最小四个边界：

1. **OS脚本**：生成非敏感OperationId，记录down/up各自 `perf_counter_ns`调用前后、SendInput返回计数、foreground PID；保存脚本Hash。只在最后flush audit，保持原来的down/up和调度逻辑。
2. **Client消费与命令创建**：opt-in只读观察Attack performed和canceled，记InputAction时间/设备ID/控制路径、phase、frameCount、held边沿及本地PressOrdinal；在IntentProduced前记Actor/Epoch/CommandId、PressOrdinal、来源QueuedPress或HeldRepeat、frameCount/local tick、cooldown/timeline/queue状态。若确需证明事件在Input System哪个update消费，可加同开关下的输入update序号/类型；不改事件或缓冲。
3. **DS TryQueue**：按现有Actor/Epoch/CommandId记Receive与判定NewQueued/CachedReceipt/Stale/Rejected，附serverTick和当前pending命令。网络包重复不应被误数为新命令。
4. **DS真实释放/资源结果**：在timeline开始/越阈值及CastOwnedProgram返回处，按同CommandId记tick、emitted、Receipt结果、该选中卡before/after charges、inventory version前后；需要深入事务时再补ticket/layer Commit标记，先不要扩展所有系统。Client安装快照可选地记Sequence/Version/该卡次数，用来单独衡量显示延迟。

按下列判据即可分层：

- 脚本down→up实测显著超0.12s：Python/OS注入调度层；
- 脚本正常，但Client实际消费up晚，或held在多个允许出手tick仍为true：Input System/帧调度层；
- 一个PressOrdinal关联多个不同CommandId，且DS每个只release/commit一次：Client请求生成/持续held路径；
- 同Actor/Epoch/CommandId在DS出现多次真正release/commit：权威重复执行；
- 一个CommandId只release一次，却单卡一次扣多个uses：owned程序/资源预算层；
- DS只有一次扣减而Client不同：库存安装/展示层。

操作ID与Unity按钮事件没有现成wire关联。单一受控点击窗口可按脚本边沿与InputAction事件时间序列对照；若同时有人类/别的设备输入，必须保留未知，不能凭相近UTC强配。Client→DS则可直接使用已有CommandId/Epoch/Actor，无需另发关联协议。

## 6. 交付与剩余未知

已完成现有非敏感证据和当前源码只读复核。已确认真实资源差异、脚本整动作短时长和逐事件追踪缺口。第二层原因仍未知，需要上面最小观测链；不建议先改冷却、强制单发或去重代码，也不继续盲跑短按来碰运气。

最初读取大型JSON时输出过量导致只读exec会话回收协议错误，后续改为定向JSON摘要并正常完成；没有任何游戏输入、服务启动、DB写入或源码编辑执行。没有审批拒绝或待批准操作。所有未来运行仍由主Agent集中协调。
