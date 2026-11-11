# N5 下一小段：四人、持续材料、性能与故障 Gate 只读合同审计

2026-11-01；仅审阅仓库计划、源码、序列化配置与已有证据。没有操作前台、进程、网络配置、服务、Unity、测试或构建，没有改仓库或读取凭据。此文件是 task 目录内的 staging 报告。

## 结论与建议

已授权的 N5 后续范围包含四人分散持续投放/反应、一人重连大 Baseline、可复现丢包/Jitter、调度/HOL 与有效性能测量；目前这些组合 Gate 尚未通过。现有默认关闭诊断已经能做运动消息故障、真实 KCP Datagram 确定性丢失、材料页完整性/版本/末页等待/历史覆盖，但没有现成的完整 UDP 丢包+确定性 Jitter 的正式房间一键入口，也未发现可直接复用的网络诊断 proxy。

当前可审阅的最小下一段应为：**P7 一台 DS + 两台 Native Client 的真实行走 Interest 出入区/当前世界恢复，同时先补齐有效 Native 测量基线**。它可以关闭“正式玩家位置驱动 Interest”与“测量链路可用”的有限范围；不把它称四人压力通过。随后才按原组合依次加入第3/4名玩家、持续负载、一人重连大 Baseline和 Datagram 故障。

不要提高128永久 Key、不减少材料数量/改物理/降低反应权威，不通过改 Tick、画质或施法资源参数凑过。既有明确容量失败应保留为失败；长期 Key 淘汰、地图容量政策、独立可靠流等需另提策略方案。

## 1. 原计划的准确门槛及数值归属

参考文件缩写：

- **N5计划**：`docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md`
- **原总案**：`docs/superpowers/plans/2026-10-20 Norn多人联机系统总体执行方案.md`
- **现总计划**：`docs/superpowers/plans/2026-10-31 Norn项目总计划（更新中）.md`

| 约束 | 准确内容/数值 | 位置与性质 |
|---|---|---|
| 必须组合压力 | 四人分散持续投放与反应 + 同时一人重连大 Baseline + 可复现丢包/Jitter；移动/施法/附近材料及数量收敛，队列和恢复时间有界 | 原总案264；N5计划19、81、95。**已授权，尚未验收** |
| 时钟与调度 | 移动逻辑/快照30Hz、Mirror调度60Hz、材料模拟/普通变化汇总10Hz；不能让移动等材料凑包；业务Send/Batch/UDP/Socket分别计量 | 原总案259、261；N5计划13、19。不是线包频率承诺 |
| 优先级 | 操作/关键战斗反馈/附近材料优先；远处可延迟，不能漏数量、停止必要权威反应或丢可靠收据；进入KCP前调度，不能事后超车 | 原总案257、260 |
| 必须记录的性能指标 | CPU/GPU帧P95/P99/最大尖峰、复制各阶段耗时、DS Tick超时、GC、池/队列峰值、实际线速、RTT/Jitter/丢包/重传、最老消息等待、提交到可见延迟 | 原总案262–263；**没有给出这些指标全部统一的最终硬阈值**，新耗时阈值须由明确硬件/画质/材料规模基线确定 |
| N5持续时长/施法数 | N5和§7.1没有指定“四人必须持续N分钟”或“每人N次/秒”的固定数值 | 不能自行发明并写成原合同；需把实际测量工况与通过线分开 |
| 历史运动注入档 | RTT 0/100/200ms，Jitter 0/20/40ms，丢包0/1/3% | 原总案511，属于N3运动验证档位，**不是已证明的全通道UDP环境** |
| 普通消息/UDP目标 | 普通消息体≤900B目标；UDP≤1200B目标，须测真实Header/加密成本 | 原总案199，目标不是任意网络保证 |
| 首轮实验容量 | 4玩家、64存活敌人、256权威Projectile；每人128输入历史/256离散指令；每Client256控制消息/最多4MiB世界待发页 | 原总案201，**实验上限，不是实测容量证明** |
| N5初始分页预算 | 900B含头、初始每页至多32条数字记录、每连接待发页4MiB/在途4页/每轮最多2页 | N5计划78，**旧计划初值，须对照下节当前实现，不能写成已完成** |
| 数量合同 | `Injected+Imported=Resident+Pending+Vented+RuleConsumed+Exported`；Fire byte强度与三相uint数量分开 | N5计划9、89；当前只有Fire异步变化的Native证据不能覆盖全部三相守恒 |

下面这些明确数值来自**原总案N8性能/部署阶段**，可以作为后续基线的已记录起始目标，但不能冒称 N5 当前已满足：

| N8起始目标 | 数值 | 原总案行号 |
|---|---:|---:|
| 预热后稳态采样 | 至少60秒 | 582 |
| 持续游玩采样 | 10分钟 | 582 |
| 服务器Tick P95 | ≤20ms | 583 |
| 服务器Tick P99 | <33.3ms | 583 |
| 新增稳态Tick/Codec/复制热路径 | 0B GC Alloc；离散生命周期事件分配单列，全帧另报 | 583 |
| 普通小场景稳态 | ≤64KiB/s/Client，不含首次/重连Baseline | 584 |
| Baseline初始限速 | 256KiB/s/Client | 584 |
| 同机四Client的证据性质 | 只用于联调；测DS容量须分离客户端渲染负载，不能据同机结果承诺服务器规格 | 585、590 |

关键区分：**没有已批准的“客户端GPU P99<某毫秒”“控制延迟<某毫秒”“大Baseline恢复<某秒”最终数值**。原总案263要求基线实测确定；发生超标时先记录，不应临时修改阈值凑通过。N8中的60秒/10分钟不应悄悄改名为N5已存在的固定Soak门槛。

## 2. 当前源码/配置实值：不等同于旧计划初值

| 当前实现 | 实值/边界 | 源码/资源行号 |
|---|---|---|
| 当前World Owner永久Key与默认Chunk payload | `DefaultChunks=128`，`DefaultWords=16384` uint words（64KiB） | `Scripts/Multiplayer/World/WorldReplicationCoordinator.cs:16`；Bootstrap158–161实际采用这些默认值 |
| 每连接世界发送token bucket | **18,000B/s**，burst **3,600B** | Coordinator85；`WorldReplicationPeerState.cs:19,31,35,42–48`。不是256KiB/s当前配置 |
| 每连接每轮 | 最多2条预算项，订阅控制与数据页共享；单个冻结payload全部页入Transport后才等安装ACK | PeerState7–9、76、86–98；Channel143–167 |
| 安装ACK语义 | 不是逐页收据；不能将“4页在途”解释为发4页后等整个Chunk安装ACK，6页真实Baseline已覆盖 | PeerState7–9；既有 `BaselineLongerThanFourPagesContinuesBeforeInstallAck` 测试 |
| Client ingress | 4个固定900B槽，每个Pump最多处理2页；满则明确拒绝 | `WorldReplicationNetworkChannel.cs:15,27–28,95,174–178` |
| Codec | MaxPayloadBytes=900，PageWords=160，MaxWords=32768，MaxPages=205；实际默认Chunk maxWords仍为16384 | `Networking/Protocol/WorldPageCodec.cs:14`；Coordinator16 |
| 字节预留 | 编码世界页另计72B保守framing/KCP/IPv4/UDP预留；实际Batch/Socket仍需测 | Channel164–167 |
| Interest | 2m参考块、XZ半径10、Y半径3，窗口含所在块为42m×14m×42m；按相交native Chunk同步 | `Resources/Online/WorldReplicationProfile.asset:15–17`；`OnlineWorldReplicationProfile.cs:14–19` |
| 材料捕获 | 权威30Hz外部时钟，每3个server tick捕获一次；World所有权和每连接复制分离 | `OnlineWorldBootstrap.cs:517–522` |

目前源码不能据此宣称“4MiB是整条复制链路全部内存上限”或“Mirror/KCP内部队列已受统一上限限制”。PeerState注释9明确排除此结论；Pool/Owner/Assembler/冻结payload/Transport应分别测量。

128是永久Key元数据界限，包括tombstone/离区后保留的版本槽，不能当成可循环复用的128个当前可见Chunk；`WorldReplicationCoordinator.cs:29`与既有 `FixedClientKeyCapacityIsNotRecycledByInterestLeave` 测试相符。`native-recovery-gates/summary.json:176,209`保留过触发Commit:Capacity的失败及尚未批准的新容量/淘汰/地图策略。

## 3. 已有证据与未测范围

**已经覆盖的有限范围：**

- 最新N5相关EditMode结果427/0，包含4个认证Actor活动并集、远处Fire源保留、超过旧6个Projectile来源预算、Region容量明确失败、四连接各自Interest窗口等算法/装配测试。测试源码：`Tests/ElementField/WorldAuthorityActivityTests.cs:47,74,88,105`；`Tests/Core/WorldInterestCoordinatorTests.cs:53,236`。这些不是四个Native客户端压力。
- P7两Client正式Interest离区/evict/再进入、SubscriptionEpoch旧页栅栏及逐连接ACK已有Native检查点；此前依赖显式DS位置夹具/暂停比较，尚未证明全部真实玩家键盘越界。`interest-subscription/summary.json:266–270`保留其边界。
- Native缺Base/Checksum恢复、旧页单次重放、两个非原点Solid Key的多页/History覆盖/tombstone跨Generation已有记录。`native-recovery-gates/summary.json:65–74,98,148,154`；计划总记录§20（现总计划362–366）。2×128格/每Key8192GMU、6页、80轮两Key共160条变更覆盖128ring，属于固定检查点，不是持续物理压力。
- 最新两图原菜单/Hub真实输入、同进程重连与消耗不补满、Fire实际Store格变化、A返回Hub/B继续已交付。现总计划§22（405–424），尤其416明确只证明异步Legacy强度变化；N5计划227–248同样保留边界。
- Core方法范围和Codec已有正校准GC事件零分配测试；这不覆盖Native全帧。

**仍未覆盖：**

- 四Native分散持续材料投放/反应 + 同时一人重连大Baseline + 可复现丢包/Jitter的组合；真实满载Activity、连续变化下Interest/公平性、完整三相与Pending数量账。
- 材料Reliable与关键可靠业务共同经过KCP时的实际HOL、最大控制等待、队列峰值/持续增长、实际Datagram/Socket/重传和可见延迟。
- 有效校准的Native CPU/GPU尾帧、DS Tick P95/P99、60秒及更长连续采样、完整路径GC/峰值内存，及同机渲染争用之外的DS容量。
- 同样场景的P8大页恢复、远端/Linux/IL2CPP/D3D12、N4完整战斗体验、N6可靠结算均不因本段通过。

明确机器证据：`native-recovery-gates/summary.json:170`为`PerformanceMeasured=false`，206列持续/四人/HOL/校准性能仍未测；`resume-2026-11-01/summary.json:385`仍排除four-player/stress/full-frame performance；现总计划424明确N5Complete=false。

## 4. 现成默认关闭的应用级故障机制

| 机制 | 默认门/可用入口 | 准确作用与限制 |
|---|---|---|
| `DatagramLossGate` + `NornKcpTransport` | bool Enabled默认false；`ConfigureLabLoss(bool)`；N0 `NornNetworkManager.ConfigureLocalLoss`显式启用 | 每20次RawSend丢1次，即5% Datagram；位于KCP之下的应用进程Raw IO，能触发KCP真实重传。`DatagramLossGate.cs:7–14`、`NornKcpTransport.cs:15,28,80,121`。只在当前Windows IPv4 Native Raw IO分支生效；不是任意平台。DS共用计数器，不能声称每连接均匀精确5%。正式Room没有现成命令接线，N0 helper不是Room入口。 |
| N0 TransportLoad实验 | 显式命令行 `-nornTransportLoadLoss` 后调用已有Loss API | `TransportLoadProbe.cs:49,71`；Profile为30 requests/s、warmup3s、短6s/长10s/Server14s（Profile10–16）。属于N0合成负载，不能替代N5 Gameplay；无Jitter。 |
| `MovementFaultQueue<T>` | 默认RTT/Jitter/Loss全0；显式验收config装Probe，再写`fault-0/100/200` | `OnlineMovementAcceptanceProbe.cs:79–85,101–104`；档位(0,0,0)/(100,20,1)/(200,40,3)。仅运动输入/State应用消息，**不作用Cast/库存/世界Reliable页，不证明KCP重传/HOL**。 |
| Movement确定性实现 | 128固定槽；RTT/2每向+±Jitter，固定seed1307/2701 | `MovementFaultQueue.cs:8–20,26–31`；Channel24–26、67–85。配置clamp RTT0–1000ms/Jitter0–200ms/Loss0–50%，这些是安全边界，非批准的验收档位。队列满也计Dropped，不能把该计数全当注入丢包率。 |
| `NORN_N5_RECOVERY_PROBE` | Development Native DS、Authority、ENV非空且绝对路径；未设立即返回 | `WorldReplicationNetworkChannel.cs:194–209`；目标为认证Actor1当前连接，212–218、323–327。只做选定材料页Checksum/Base缺口、末页hold、history hold、旧页单次重放等；`344–345`列命令。不是全部Datagram的随机丢包/Jitter。 |
| Mirror库`LatencySimulation` | 代码存在，但其GUID `96b149f511061407fb54895c057b7736`未被任何第一方Scene/Prefab/Resources资产引用 | 库文件3–4、67–87：Reliable只延迟；Unreliable才丢失/乱序。默认latency100ms、jitter0.02（秒幅值）、loss2%、scramble2%，不是默认0配置。它用未固定seed的System.Random（99）及每消息new byte[]、可增长List（91–92、149、181–188）；未接入的库不等于已用。不能拿它证明可靠UDP丢失恢复或无分配性能。 |

KcpTransport当前NoDelay/Interval/窗口/Timeout（源码25–46）是传输调优参数，不是故障注入旋钮。本次不建议修改它们，更不涉及操作系统网络、路由、防火墙或VPN。

在`Tools/`、第一方Runtime Scripts及当前task脚本中未发现现成网络诊断proxy；名为ColliderProxy的是碰撞代理，不能混用成UDP代理。若最终需要真正Datagram Jitter，应先提出应用进程内显式、固定容量、固定seed的诊断设计，不能将运动消息Jitter误报成全通道网络Jitter。

## 5. 授权边界和下一段可验收交付

**已有授权内：** N5原合同的功能验证、测量计数/诊断、默认关闭的进程内故障注入、记录失败、保持既有协议/数量/权威与预算。给现有DatagramLoss API补正式房间显式诊断接线，或补固定容量测量报告，属于这些Gate的必要实现；仍须先形成具体中文小段方案，限定文件和清理范围，不能把此只读审计当作已实现。

**需要新策略/协议或另行确认：** 提高128/改永久Key回收策略、把容量失败改成静默漏量/减反应/删Chunk、改变Interest或材料语义、升级/更换Transport或新增独立可靠流、引入新的资产/容器/玩家规则。原总案264明确先实测HOL再评估Transport调整；总计划§22不授权容器待定语义，也未批准长期容量政策。

**建议下一小段（分清验收性质）：**

1. 只用一个P7房间、一个DS和两个Native，延续已验证正式菜单/身份/资产路径，实际键鼠让A越过既有Interest边界、B留原区域，再让A返回；以DS真实Actor位置、逐连接SubscriptionEpoch、旧候选拒绝、当前Baseline/安装ACK/实际Store复核。需要固定可见目标时可复用已批准显式材料夹具，但原暂停/有限Tick边界必须保留，不能宣称连续压力；不能直接设置DS坐标替代真实输入。
2. 先验收测量能力：保留硬件/画质/Scene/Build Hash与空负载基线，GC已知1KiB正校准必须有效；方法事件与全帧分开。随后在不改变游戏参数的轻量真实施法工况下取至少60秒样本。60秒是借用原N8明确采样目标的**本段建议**，不是声称N5原来有此固定阈值；具体输入节奏/材料范围要预先记录，不能遇到失败后改工况却仍称同一压力测试。
3. 诊断性能污染要先解决：现有`Measure()`只收240帧（Probe243–275），不能当60秒连续窗口；它暂停本Probe的1Hz JSON，但`OnlineMaterialReplicationProbe`仍每250ms建数组/JSON/写盘（271、273、286），未监听SamplingEvent。性能窗口应明确暂停这些诊断IO或分开功能/性能run，并另留同负载功能证据；否则必须将诊断成本单列，不能误报生产全帧GC。缺CPU/GPU/阶段/队列/Socket计量时明确列缺项，不以无效0值交付。
4. 该段通过只关闭“真实行走Interest/恢复”和“有效双Native测量基线”。下一段再在不扩128/不改玩法的相同可描述负载下加第3/4席位；资源不足或Key满就保留失败。再接入已存在5% Datagram Loss门、最后补真正Datagram Jitter和一人重连大Baseline组合，逐项记录有界队列与关键业务延迟。四Client同机只能称联调压力，不能据此承诺DS容量。

当前无需以“完成四人全部Gate”为名先决定容器共享/转交或新协议。该低并发小段可独立于容器产品选择推进；本报告没有发起任何执行操作。
