# N5 四端最小 Native 阶段独立只读审查

2026-11-01。只读范围为 `docs/superpowers/evidence/multiplayer/n5/four-player-native-2026-11-01/` 的 `p7/{world-first,three-cast-d-profile,load-stop}`、根目录连续JSONL与阶段计划。不读任何`.private`配置或认证日志；未操作前台、进程、服务、Unity、测试、构建或源码。此报告在授权task目录内暂存。

## 可成立的最小交付结论

真实四个认证Actor完成同一P7 Run入场，四端各看到3个remote；四连接材料安装ACK可分别关联，ABC有真实Fire Store原始采样，D有经过校准的240帧短测量。资源监测出现严重可用内存不足，阶段已停止并清理。**不能宣称四人同时持续施法、D施法、性能通过、四端Store逐格一致、完整故障/HOL Gate或N5完成。**

## 1. 原始证据与四端身份

流式解析原始 `four-player-trace.jsonl` 共1967行，0条JSON解析失败，未输出大量原始明细。SHA-256为：

`8e06df0ea86b3a11a547e341c37a610545e6ca2aa5990c1db596116870a60cb9`

根OS输入JSONL Hash：`5e368cc45d3e1e25919a760b1a9fa4911c3d2e63c5682b91d3971bf6d801dc32`；OS采样JSONL Hash：`a1ace1eb7edcca778b1d12a42e977ce7fecf592f6754f6545f947d5d67c0fd0c`。

三个检查点的共同身份：Room `76f649d6b04842b8af34e690d09b7a41`，Run `bb6bc31651f74ebba39c943349e411ff`，Instance `a4619d4639c94e5f92b905103cf146b1`，WorldEpoch `5194249043625871581`；DS PID22408、StartTicks639291009255082366。三个DS报告都是Members/Clients/Accepted=4、WorldReady/Ready/CanSimulate=true、Frozen=false。

| Native角色 | PID（实际OS输入记录） | Actor / Epoch | 三个检查点remote | 自己的卡ID | 最后次数 |
|---|---:|---|---|---|---:|
| A | 27472 | 1 / 1 | 3 / 3 / 3 | `4b2e8dbec3874e1682e4f6d48b840a2d` | 24 |
| B | 14800 | 2 / 1 | 3 / 3 / 3 | `21dd6c7bca414ccdaedc94f8bbfd3ac1` | 28 |
| C | 14280 | 3 / 1 | 3 / 3 / 3 | `2cfe4f09f4754e97aeb9ecfc57c769c8` | 15 |
| D | 33436 | 4 / 1 | 3 / 3 / 3 | `21f9d9a028824fbdacf2c251e8eacca8` | 30 |

Portal报告Authenticated=true、Phase=Connected；movement Ready=true，自己卡来源为InstalledClientSource。DS最后当前AuthorityWorkspace独立读出 `[24,28,15,30]`，ActualSnapshotTickAvailable=true，采样Tick2706。各端也独立观察到相同四Actor聚合次数。

从原trace每端Ready且RemoteCount=3的首末有效源时间取交集：03:36:01.2018606Z至03:37:24.5448911Z，**83.343031秒**。这表示有原始报告覆盖的四端共同就绪区间；不是四人持续施法时长，也不是连续83秒性能测量。关闭阶段remote逐渐降为2/1/0，与顺序清理相符，不能将清理样本当四端运行失败。

## 2. 逐连接材料ACK

`MaterialPeerAck0..3` 始终分别关联以下ConnectionId，ConnectionEpoch均1，WorldEpoch均匹配，SubscriptionEpoch均1，所有记录的Generation/Revision/SnapshotId/ConnectionId均为有效非零值：

| Actor槽 | ConnectionId | world-first累计ACK | three-cast-d-profile | load-stop |
|---|---:|---:|---:|---:|
| 1 | 35675379 | 4 | 197 | 810 |
| 2 | 35675389 | 4 | 206 | 772 |
| 3 | 35675378 | 4 | 206 | 790 |
| 4 | 35675388 | 4 | 206 | 887 |
| 合计 | — | 16 | 815 | 3259 |

每个检查点四槽累计数之和均与MaterialAcks精确相等。world-first的四个Domain空基线，每槽4个完整stamp的Generation/Revision/SnapshotId/Checksum/Words/Resident都与对应DS Owner一致，共16项，不仅是一个全局ACK总数。

持续变化后，各槽保留的最新逐Key ACK条目数分别12/12/12/12以及32/29/29/32。此时与报告当前Owner完整stamp相同的条目分别7/9/9/9及11/10/12/22；其他条目可能落后于正在推进的Owner，**没有全部精确收敛在同一冻结边界的证明**，不能将所有ACK数为正写成世界逐格完全一致。

## 3. 真实Store、卡量与控制证据

原trace仅有ABC Store：A350份、B330份、C343份；D没有store源，三个检查点也均无D store文件。这与D为减少测量污染显式未启Store采样相符，不能补造或将D ACK替称D逐格Store原档。

ABC非空Fire采样数为250/252/256，最大CellCount为25/20/22，最大Fire强度合计为1283/1249/1213，均在同WorldEpoch。实际同格 `(Domain0,Storage0,X=-12,Y=0,Z=-21,Material2)` 有衰减：A行471→478为59→53；B行469→475为61→55；C行470→474为61→57。它们证明实际ReplicaStore变化，而非只增加网络Header计数。Fire是Legacy强度，不是三相GMU守恒或D Store内容证明。

必须区分当前数据和根脚本输入因果：

- 原OS输入行21/22/23仅记录A/B/C依次各2秒Attack：03:36:34.093720、03:36:37.027246、03:36:40.052148开始；不是同步输入。
- A在根A脚本开始前已经30→29→28→27→26。原trace行946的03:36:33.9264939Z已为26，稍后根脚本阶段才到24。因此不能把A全部消耗6次解释成该2秒动作。
- C在根C动作后为28，此后03:36:50起又多次扣至15。额外变化在原始数据中真实存在，但这份根OS日志未逐次解释这些后续输入，不能用三次根脚本mouse动作独立归因所有卡量变化。
- D自己的卡一直30，根OS记录中没有D Attack；不能将D观察到ABC卡量变化或全局输出数当成D施法。
- `three-cast-d-profile/ds.json`源时间03:36:33.9095744Z尚早于三段根脚本，仍为A26/B30/C30/D30；同目录client源时间约03:36:42则为24/28/28/30。检查点标签不能充当同刻证据。

## 4. D校准有效的短窗口，性能未通过

D在`three-cast-d-profile/d-movement.json`中Calibrated=true、RecorderValid=true、Frames=240。FrameTimes前240个值给出239个相邻帧间隔，总跨度 **3.9850903秒**。它们是实际帧间隔，不是CPU/GPU独立耗时或DS Tick耗时。

| 度量 | 独立计算 |
|---|---:|
| 平均帧间隔 | 16.674018ms |
| P95 nearest-rank，ceil(0.95×239) | 17.7786ms |
| P99 nearest-rank，ceil(0.99×239) | 18.2089ms |
| 最大帧间隔 | 18.7371ms |
| 非零GC帧 | 5/240 |
| GC采样合计 / 峰值 | 4926B / 1260B |
| GC分布 | 235帧0B，3帧1260B，2帧573B |
| D方法范围Tick | TickCounterValid=true，120个MeasuredTicks，0个TickAllocationEvents |

若使用线性插值百分位，P95/P99分别17.76978/18.205898ms；与nearest-rank不同是算法定义差异，不是原数据冲突。

这明确不是全帧0GC。D方法范围零事件也不等于D全帧零分配，更不等于DS零分配：三个DS检查点TickCounterValid=false、MeasuredTicks=0，尚无有效DS Tick性能采样。

**重要时效边界：** `load-stop/d-movement.json`保存的FrameTimes、FrameGcBytes、FrameSpawnTotals、FrameDamageTotals及Frames与前一检查点完全相同。原trace行1061首次出现有效240帧时，故障配置仍0/0/0；到行1434才首次观察100/20/1。后续报告是“当前故障字段 + 上次无故障测量数组”，不能把该窗口的P95/P99/GC当成fault-100条件下重新测量的结果，也不能作为60秒持续性能窗口。

## 5. 内存触线与监测联动不足

OS采样共171条。原始行80在03:35:54.302193Z首次AvailableBytes<3GiB，当时约1.86410GiB；行81在03:35:55.350281Z最低 **480,940,032B = 0.447910309GiB = 0.480940032十进制GB**。写“0.448GB”应注明实际按GiB换算，避免单位混淆。

最低点列出的四Client RSS约2.606/4.519/4.171/3.971GiB，Private约5.672/5.386/5.505/5.717GiB；这些是具体进程指标，不能将Private/RSS相加当物理内存占用。全局CPU最高84.5%；最低内存时CPU23.2%，CPU不满不能排除内存压力。该采样进程列表仅四Client，未单独拆出DS内存；全局Available仍包含整机其他负载。

触线早于world-first报告以及后续根脚本/短测量；load-stop在03:37:24前后，OS最终采样03:37:30.081326Z已回升16.87477GiB。因此可表述为“最终因资源边界结束并清理”，**不能写自动触线立即停测**。阶段计划已有<3GB停止扩充/卡顿停止要求，但监测与执行停止之间存在明显时间差，应明确承认联动不足，不能将这台机器的当前四窗口共驻视作已通过持续资源预算。

## 6. Resync与故障Gate疑点

整个原trace12份DS报告MaterialFaulted=false、MaterialFailure为空；永久Key数最多32，未触128Key上限。复制没有显式Fault不代表不存在频繁候选失败或恢复抖动。

ResyncAccepted时间线：03:36:33.909为0；03:36:48.939为19；03:37:03.973为41；03:37:18.992为60；顺序关闭期间最终03:37:28.860为68。第一份19早于D报告首次显示fault-100（03:36:59.505）。**不能把全部Resync归因D的运动故障配置**。现有结构化字段只保留最新关联stamp/slot，并没有逐次拒绝原因、缺页队列水位或每Key恢复等待，无法仅凭这些报告确定是何种机制导致，应保留为下段离线诊断重点；不能宣称恢复开销有界或压力收敛通过。

D的fault-100实际报告为RTT100/Jitter20/Loss1，属于MovementFaultQueue的输入/状态应用消息配置；26份相应D报告的平面位置始终 `(-6.15,-8.02)`，没有该故障期间D受控移动/施法的原始证明。DroppedMessages最高122不能当真实UDP丢包率或KCP重传统计。原材料Reliable仍没有本轮丢包/Jitter/HOL组合验收。

## 最终建议

本段按“四个认证Native同时入场 + 四连接材料ACK + ABC Fire实际Store + D一次校准短窗口 + 资源触线后终止”收束，明确整体性能/压力/故障Gate未通过。优先保留内存停止时间线、未归因输入与Resync增长，不修数据、不扩大128Key、不以调整画质/玩法/协议凑结果；下段先解决测量与停止联动、再以资源可承受规模离线追踪Resync原因。本审计未发现需要继续操作运行中进程的事项，也没有权限阻塞。
