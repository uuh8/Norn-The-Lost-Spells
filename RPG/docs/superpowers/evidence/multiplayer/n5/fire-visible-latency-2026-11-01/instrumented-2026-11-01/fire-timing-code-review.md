# N5 Fire 关联计时代码审查

审查日期：2026-11-01。状态：未发现确定的 P0/P1；发现 1 项 P2，建议在 Native 观测前修正。此为只读静态审查，不是 N5、GPU 可见性或性能验收。源码、工作区和既有证据均未修改，未启动 Editor、测试、Native、服务或构建；未读取私有认证配置或原始认证日志。

审查依据为两层 AGENTS、`fire-visible-latency-2026-11-01/next-measurement-plan.md`、本轮 `instrumented-2026-11-01` 的 RED/GREEN 报告，以及指定的 9 个 Runtime 源码文件和 Core/ElementField/Multiplayer/Rendering/RunPlayMode 测试。已参考 `before.json` 中对应源码的起始 hash；它没有保存起始文件全文，因此没有将整个 HEAD diff 当成本轮改动。本报告只判断明确新增的计时路径及其与当前既有流程的交互。

## 确定发现

**P2 — Trace 挂载后仍分配启动 UTC 字符串。**

位置：`RPG/Assets/_Project/Scripts/ElementField/Diagnostics/OnlineMaterialReplicationProbe.cs:236–239`，尤其第 237 行。

收到有效 `start` 后，`TryAttach(_trace)` 已把 `Active` 发布，`_startTimestamp` 也已取样，随后执行 `DateTime.UtcNow.ToString("O")`。这是一次确定的字符串分配，发生在该报告自己定义的窗口起点以后；与第 188 行“所有 JSON/路径/缓冲分配在采样前或 Detach 后”和窗口只写值的目标不一致。它不影响默认关闭状态，也不是每事件分配，但可能把一次观测分配带进起始帧；现有生命周期测试只验证窗口内没有最终报告文件，不能发现此分配。

最小修正：把 `_startedUtc` 格式化移到 `TryAttach` 之前，保留挂载后的 raw Stopwatch 起点及现有持续时间语义；不要把 UTC 当作跨进程耗时依据。主控已确认会在当前回归结束后统一处理，本审查未代改。修正前不要声称“整个采样窗口诊断路径只有值写入/零分配”。

## 已核对且未见确定缺陷的边界

- `WorldReplicationTimingTrace.cs:51–100`：默认 `Active=null`；数组只由显式构造分配。`Record(in …)` 先按完整 Key/可选 WorldEpoch 过滤，再写固定前缀；容量满后拒绝新事件并饱和累计 `DroppedCount`，不覆盖前缀。事件、Header 和 installed G/R/Actor 均为值或完整 64 位字段，没有热路径集合、装箱、IO 或格式化。
- `WorldReplicationTimingTrace.cs:137–165`：Windows 用 QPC-before → Stopwatch → QPC-after 夹持，保存两种频率、原始值和 offset 区间；decimal 的 floor/ceiling 不会把取整区间缩窄。失败返回 `WindowsQpcCalibrated=false`，没有伪造零 offset。跨进程使用仍须由采集方确认同一机器/有效校准，并保留区间与 start/end 差异；不能直接跨减 Unity realtime。
- `WorldReplicationCommitOwner.cs:115–123`：Journal、payload 和历史发布都成功以后才记录 `Committed`；无变更提交没有事件。这里没有 Gameplay Tick 上下文，0 只是缺失值，不能当实际 Gameplay Tick。`WorldReplicaStore.cs:152–171` 在 Current/Header/版本已发布后记录 `Installed`，幂等早退和失败候选不记录安装；第 180–188 行只读 Header API 不暴露已退出 Interest 的占据。
- `WorldReplicationCoordinator.cs:163–241,396–447`：冻结、准备、等待 ACK、原超时重启和预算拒绝分别记录实际分支；只读 Tokens 不 refill/扣款。记录不推进页游标，不重复校验或额外扫描 Source。`ReceiveControl` 和四个 Resync 原因维持原短路及预算次序，接受事件使用真实 pending Header。
- `WorldReplicationNetworkChannel.cs:83–101,167–220`：正式身份/订阅/inbox 拒绝和接收时点仍走原路径；`PageSent` 与 `ReplySent` 位于 Mirror Send 返回后，含实际页索引、编码字节和认证 Actor。这里表示提交给 Mirror 可靠入口，不表示 socket 已发出。未知认证/Codec 失败包没有可信目标 Header，代码明确只累计原 `Rejected`，没有给它们伪造目标 Key。
- `GpuFireParcelRenderer.cs:379–470`：计时不增加 ShouldEmit 调用，不推进额外 PRNG/visualSequence，也不改变镜头筛选、预算、扫描轮转或 Seed 输出。第 486–495 行取得实际安装 Header 和本地 PresentationVersion；第 529–565 行保存真实 Seed 事件，Uploaded/Draw 沿该值快照关联，不在 Draw 时回读新版 Header。Uploaded 位于 SetData 和 Spawn Dispatch 返回后，Draw 位于实际 RenderPrimitivesIndirect 调用后；两个时点都只是 CPU 提交。
- `OnlineMaterialReplicationProbe.cs:215–264`：start 前完成配置/缓冲与状态文件写入，窗口中不轮询文件；Finish 先 Detach 再导出，CreateNew 防止覆盖已有最终报告。OnDisable/OnApplicationQuit 使用相同幂等 Finish，旧 Owner 不能 Detach 新 Owner。已见的采样事件消费者只暂停显式 Portal/UI 诊断；新增 Material/Movement 早退也只影响显式探针，未见正式 Gameplay 暂停。

## 本轮结论必须保留的局限

1. 主控明确本次 Native Case 为全新 P8、两玩家、单次 A 短按火球，开始前目标 Chunk 无 Fire；不满足便拒绝该 Case。在此前提下，当前实现可以声称“目标 Key 的某个已安装版本出现首个实际 Seed/CPU Draw”，并保留实际 Seed Cell/量。它没有 TargetCell 过滤，不能在同 Chunk 有既有火或多火源时把 Chunk 首 Seed 解释成本次指定 Cell/火球；不建议为本次窄 Case 扩 API。
2. 当前 Stage/字段没有 SurfaceImpact、沉积接受、材料 apply 的独立事件，也没有 Cast/Projectile 事实 ID。链路从 Owner `Committed` 起可以分析；命中→入队→实际应用→提交前等待仍是数据缺口，ActorId 表示连接身份，不能冒充一次施法的因果 ID。爆炸 VFX 与屏幕火焰可见也没有被这些 CPU 事件测得。
3. `GpuFireParcelSurfaceTests.cs:19–192` 的新增计时用例通过反射装配真实 ReplicaStore、读桥和 CollectSeeds，不创建 GPU 资源。它们证明 CPU 选种/拒绝/版本关联且输出未变；没有正向执行 SetData、Spawn Dispatch 和真实 Draw，不能证明 Uploaded/Draw 全链路，更不能证明 GPU 完成。Native 需实际收集这些事件，并确认它们的 Header、PresentationVersion、Cell 和页链一致。
4. Core 用例验证 ring/时钟和值字段；本轮 PlayMode 用例以小 G/R 验证最终 JSON 生命周期。完整 ulong 值在内存事件有测试，但最终 JSON 的最大值 round-trip 没有直接覆盖。若将 JSON 导入 JavaScript，应避免 Number 对超过 2^53 的 ID 丢精度。此为后续工具边界，未发现当前 C# 导出主动截断。
5. 默认关闭和 Record 的源代码未见额外堆分配，但这些新增测试没有给出已正校准的 GC.Alloc event recorder 开销证据，也没有给出启用/关闭的实际计时扰动量。不能把静态审查或旧 GC API 的 0 当成本轮性能测量。若已有 Movement Measure/其他显式采样协程先启动，本次 Active 早退不会取消那个协程；应由主控保证只开一个测量窗口。

## 已读的实际测试证据

以下是现有报告结果，不是本审查重新运行：Core/Header RED 0 passed / 13 failed 后 GREEN 13 / 0；behavior-hooks RED 5 / 13；probe-lifecycle RED 0 / 4；timing GREEN 31 / 0；formal-and-timing GREEN 9 / 0。`core-and-header-red-first-blocked-report.json` 是脚本编译阻止、0/0，不算真实 RED 运行；该失败证据仍保留。读取时 `regression-progress.json` 为 `Running-n5-and-rendering-regression`；完整 N5+Renderer 回归与 Native 观测由主控协调，本报告不提前宣称通过。

审查版本 SHA256（主控后续修正第 237 行后，此 Probe hash 将变化）：

```text
34e06328e69382ffd0b3ca2d4272c39605868dc96a8d84586c764e92b891753d  Core/Online/WorldReplicationTimingTrace.cs
5f99f4b90f8329922920ef04faa4d2d09efc92905b0449c501ed4fc88ca3652d  Core/Online/WorldReplicationCommitOwner.cs
c58c16c66f4d37815e7c39fefd2e58ac89bed1d743484585a8db1143a88f83cf  ElementField/Replication/WorldReplicaStore.cs
f3f15994c433d0b8ce648339f909daab8863f4cf76c7d8a074c4b8a44c6d36ce  ElementField/Diagnostics/OnlineMaterialReplicationProbe.cs
85022fca5263f5cd77e372cfce662dd791384c6c9eb4b15b4ca4a013f68b291a  Multiplayer/Diagnostics/OnlineMovementAcceptanceProbe.cs
2d5540fc0d357a037fdc85297131fb8639bd47c48a688f72213b7af3662c70ae  Multiplayer/World/WorldReplicationCoordinator.cs
ffefc05f298b794c84bb9c04edf93fe0cc20f103805891283904cd98611a988d  Multiplayer/World/WorldReplicationNetworkChannel.cs
2186a81bd4788b23ab4908f22c0ed98b5c9918315bfce3bde037ff4833b23d77  Multiplayer/World/WorldReplicationPeerState.cs
3b8a7122b481c473737ea5d8e30808842ba38098eaf224f1dbcad445cdcc1b92  Rendering/ElementField/GpuFireParcelRenderer.cs
```

没有剩余读取/交付 blocker。默认 Windows 沙箱首次无法建立 split writable root；随后针对读取命令的自动审批已允许，未修改任何仓库文件。剩余工作是主控修正该 P2、结束既定回归，再按以上窄 Case 与诚实数据边界进行 Native 测量。
