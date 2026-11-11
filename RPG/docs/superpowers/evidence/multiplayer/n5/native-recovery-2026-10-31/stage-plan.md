# N5 v4 Native故障恢复：最小两场景闭环

2026-10-31用户明确授权继续既有N5 Native缺版本/日志覆盖/损坏恢复，允许最小可重复小段。依据N5计划N5-2冻结快照/缺版本Resync/完整校验与安装后ACK、N5-4真实pending关联及预算。现有Interest/v4合同不变，128 Key不淘汰，DS两条资源错误和诊断错误锁存P3保留。

本段先覆盖两种默认关闭隔离故障：

1. **Checksum破坏。** 真实A/B认证、同Scope Baseline安装并逐peer ACK后，显式DS Channel在TryPreparePage之后、Codec.TryEncode之前只对A选中的首个Delta翻转首页一个Word，保持Header/Checksum/其余页不变。不得改Owner或Peer冻数组。预期完整payload拒绝、旧数据/版本保留、关联Resync，DS RequireBaseline按当前提交恢复。B正常。
2. **BaseRevision缺口。** 同样只对A选择一个BaseRevision>1的Delta，对同一Snapshot全部页统一令BaseRevision减1，保证wire仍合法(newRevision>base>0)。预期MissingRevision拒绝，旧版保留，Resync只关联实际当前pending，当前Baseline恢复。不能只改首page后让余页建立合法候选。

恢复收敛后可显式重放缓存旧故障首页1次，验证拒绝且不回灌：恢复版高于旧Delta时应StaleVersion；版本相同则是Base依赖MissingRevision，不硬编码错误语义。过期控制不得让无pending的DS重开恢复或消耗合法pending预算。重放是单包网络故障诊断，不属于正常发送调度/性能验收；不引入自动循环重试或新协议。

只读观测新增在现有Coordinator：ResyncRequested/Accepted、Baseline/Delta安装计数、最后被拒候选/此前已安装Header与可读性/Failure、最后接受Resync、每peer最后准确ACK Header。它们必须记录真实路径事件，先真实闭环测试RED后补最小观测，保留原数量/版本断言。RoomAdmissionProbe与RoomInstanceBootstrap只稀疏输出；不把聚合数当逐连接ACK。

显式Native注入开关只在Development DS私有环境路径启用，私有一次性命令先确认Armed再产生材料变化；发送端篡改编码输入后恢复临时message，记录原/注入Header和实际已送页，不改权威材料、玩家资产或正式场景。现有ElementWorld材料Probe第二次写入/五真实Tick+暂停检查点复用，Alpha/Beta均不移动，诊断错误锁存P3的home/away接口不发请求。正常运行没有文件轮询、额外发送或故障逻辑。

预期验证：两个真实Owner/Codec/Assembler/Store闭环RED→GREEN、原相关27fixtures回归、正式地图必要回归；匹配v4 Native构建；两房间分别运行checksum/missing-base，逐端当前Header/CRC/wordcount与DS一致，记录拒绝瞬间旧Header与恢复Baseline、同World/Connection/Subscription不变、每连接ACK Header与计数，再单次旧页拒绝。所有失败/阻塞/日志保留。

本段不扩Native日志覆盖(需要独立有界长提交场景)、丢包/HOL/四人/持续/性能、键盘完整路径、同进程重连、消耗不补满或P8；不能以此写N5完成。文件复用Multiplayer/World、Diagnostics、Runtime与既有测试；无新源目录/Schema/依赖。唯一Editor/测试/生成/构建/服务/Git由主控串行；只操作自有进程，不提交推送。维护总计划/N5解析/证据并核对12保护文件、13派生资源与现有独立Profile。
