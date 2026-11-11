# Interest 完整接线独立只读审查

日期：2026-10-31。仓库：`F:\Develop Project\RPG`。

结论：在本次读取的源码和已批准范围内，未发现可以确认的 P0/P1/P2 阻断缺陷。发现一项 P3 Development 诊断恢复问题，主控已明确决定 deferred minor；这不是正式材料订阅状态机的问题。本结论不是 Interest、完整 N5、Native 体验或发布验收通过。

## 1. 审查范围和证据性质

- 先读外层 `AGENTS.md`、`RPG/AGENTS.md`、现有 `RPG/CLAUDE.md`；以当前两层 AGENTS 和用户批准计划为准，不把旧 CLAUDE 的历史阶段说明当作当前状态。
- 核对《2026-10-31 Norn项目总计划（更新中）》第16节，以及 N5 计划末段“已确认Interest的下一段具体实施边界”。范围是独立 2m、XZ10/Y3 的发送窗口、相交原生 Chunk 整块发送、每连接每 Key 独立订阅代次，以及必要协议/配置/验证；不扩大 DS 模拟规则或缓存淘汰规则。
- 只读检查 Coordinator、PeerState、NetworkChannel、Profile、Bootstrap/Driver、Core Interest、Assembler、ReplicaStore、CommitOwner/Revision、Schema/Codec/Reset/兼容版本、现有 Editor 创建/Hash 输入与相关测试源码。检查了工作区状态和 `source-before.json`；未回滚、修改或清理仓库。
- 以 `source-before.json` 的对应 Hash 比较：Coordinator、PeerState、Channel、Core Interest、Assembler 本阶段有变化；`WorldReplicaStore.cs` 与本阶段起点相同。新增 Profile 为本阶段新增文件。该清单是 Hash 起点，不包含每个文件的旧内容快照，因此没有把起始 Hash 误当成可逐行 diff 的旧源码。
- 本审查没有运行 Editor、测试、构建、协议生成、业务服务或 Git 写操作。只在指定 task 目录保存本报告。默认只读 shell 因 Windows split writable roots 无法启动，随后经审批使用只读 `require_escalated`；没有未解决的权限阻塞。

## 2. 确定问题

### P3：显式 Interest 诊断一次失败后不能由后续合法请求恢复（deferred minor）

位置：`RPG/Assets/_Project/Scripts/Multiplayer/Runtime/RoomInstanceBootstrap.cs:128–146`。

`_interestProbeError` 在无认证 Actor 或非法命令时被置为错误字符串（133–134、140–141行），但每次新请求开始时没有清除。后续请求即使认证条件已经满足、命令也是 `home`/`away`，142行仍要求旧 `_interestProbeError == null` 才执行 `RestoreMovementSnapshot`，因此永久跳过本进程后续诊断位移。错误只有进程重新创建字段时才恢复。

最小复现方案（交主控执行；本审查未执行）：

1. 启动显式启用 `NORN_N5_INTEREST_PROBE` 的 Development Dedicated Server。
2. 在 Actor0 完成认证前投递一次 `away`，或者在正常认证后投递一次非法命令。
3. 等 Actor0 正常认证后投递合法 `away`/`home`。
4. 观察新 request 被消费，但 Actor0 位置不变，旧诊断 error 保留。

影响限于显式 Development 诊断夹具的失败恢复，不改变材料、订阅或正常玩家规则。正常“先认证，再直接发合法命令”的本轮验证路径不受影响。可在每个新请求开始时重置请求错误再判断，但本报告不修改源码。主控已确认本轮将其列为 deferred minor，不为此扩大夹具重构或追加构建。

## 3. 主要静态核对结果

1. **空间与配置。** `OnlineWorldReplicationProfile` 只存 2m 和 XZ10/Y3，窗口推导为42×14×42m；Bootstrap一次读取 Resources/Online 配置并验证，没有缺配置时回退全量。`OnlineCombatTools.cs:248–249` 将 Profile 原始资产纳入 GameplayHash。资源文件实际为2/10/3。Core 使用 double/floor 计算参考块，随后验证真实 float Bounds 非塌缩；相交使用严格正体积 AABB，不只看 Chunk 中心。DS Channel 从当前认证 Registry 的 `Body.position` 更新位置（119–120行），不是消息自报位置。
2. **DS 订阅切换。** `WorldReplicationCoordinator.cs:111–128、150–207` 使用固定每 peer/ownerKey 的 desired/announced/epoch 状态。退出取消该 Key pending 和 ACK 基础；重入增代并要求 Baseline。快速跨界在发送前压缩状态；若旧 Enter 已公告而新代已期望，先 Leave 公告旧代、再 Enter 新代，生命周期优先于页。新连接对初始范围外已知 owner Key 发送 Leave，可清除重连保留的历史缓存。未引入事件队列或 Key 淘汰。
3. **可靠顺序与预算。** PeerState 的 `TryMarkControlSent` 与 `TryMarkPageSent` 共享 token/burst/每轮两条限制。Channel 在 Codec 成功和预算预留后才送 Mirror，并使用同一 Reliable 通道；材料 Pump 位于既有战斗/进度/移动 Pump 之后。这不证明 KCP 内部队列、HOL 或压力性能。Coordinator 的 announced 只在预算预留成功后推进。
4. **旧代控制早拒。** `ReceiveControl` 先检查当前连接、pending Key/Header 与订阅代，再消耗 ACK/Resync 窗口预算（209–229行）。旧订阅 ACK/Resync 不会清当前 pending 或占合法预算。订阅 epoch 在 MaxValue 时显式 Faulted，未发现回绕增代路径；未根据静态逻辑声称极值测试已跑。
5. **Client 清理与重入。** `ReceiveSubscription` 只由可信生命周期激活 Key；同代重复 Enter 不取消半包，已关闭同代 Enter 被拒绝。Leave 保留永久 Key/Header/Installed/version，实际调用 InterestSink 释放占据，取消 Assembler 候选并撤销 payload 可读资格。新代首份必须 Baseline，合法同版本可重新原子安装。数据页自身不能分配 Key、激活订阅或建立信任。
6. **收件箱。** Channel 在接收回调处理生命周期并压缩4槽 inbox，Pump 再次核对连接及当前订阅。`PurgeClosedPages` 的压缩目标始终位于已扫描逻辑位置内；4槽环形索引没有发现静态覆盖未来未读页的路径。此处没有专门环形压力实测。
7. **重连与停止。** 新 ConnectionEpoch 取消候选、关闭旧订阅资格，保留已经安装的 payload/世界版本；没有改成全量释放。新连接初始范围外 Leave 再清实际历史占据。Bootstrap 换 Manager 先 Configure(null,null)/禁用旧通道，再配置新通道，Coordinator 跟随原 World；StopWorld 先停止 Driver、解绑通道、Stop Coordinator，再 Detach 材料和释放 Scene 对象。已停止 Coordinator 拒绝延迟页。
8. **协议。** Schema 追加 fixed64 field21 `subscription_epoch` 和 kind6/7，原1–5语义保留；应用兼容版本为4。Codec 对非零代次、kind6/7 零材料字段、字段白名单、重复字段及正确 fixed64 wire 预检；Reset 清新字段和 unknown fields。未手改生成文件。本审查没有执行生成一致性或构建。

## 4. 已读实测记录与仍未覆盖事项

本阶段 `green-report.json` 保存149 passed / 0 failed（2026-10-31 13:43:11 UTC），`full-report.json` 保存419 passed / 0 failed（2026-10-31 13:52:17 UTC）；均为主控运行并归档的结果，不是本审查运行。主控另报告8个协议文件生成一致性检查通过。本审查没有将历史 N4/N5 或先前 Native 结果扩大成当前 Interest 验收。

现有新增测试明确覆盖未 Enter 页拒绝、Leave 清真实数量/候选、旧高候选后新代低合法 Snapshot、同版本重装、重复生命周期、旧代控制预算、Key 固定容量与 Stop。审查检查点时未见快速跨界未发送控制压缩和 SubscriptionEpoch Max 的专项；主控已交测试 Agent 补充并串行执行，可能直接 PASS，不应伪称新增 RED。本报告不预判其结果。4槽 inbox 环形 Purge 专项压力仍未测。

报告签出时，正式 P7/P8 PlayMode、业务版本门禁、最终配套 Native 构建和实际 DS 位置驱动出入/重入验证正在由主控继续，不由本报告担保。四人压力、长期探索达到128 Key、丢包/HOL、完整键盘玩家路径、原生全帧 GC/Profiler、Linux/公网/IL2CPP/D3D12 等没有通过本次只读审查获得验证；既有 Built-in Resource 启动错误和 N5Complete=false 边界保持。

下一步由主控汇总新专项/全回归、正式地图与业务门禁以及匹配 Native 实测；若出现实际失败，保留失败证据并按当前批准合同修复。当前没有因本审查发现的重要缺陷阻止继续这些验证。
