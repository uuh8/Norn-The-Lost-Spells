# N5 Native剩余恢复Gate证据

## 2026-10-31：Native多页、日志覆盖与跨Generation恢复Gate

### 1. 结果与授权范围

用户明确接续N5-2～N5-6已有合同：完整校验并原子安装才ACK、日志不足退当前Baseline、同Key删除重建提高Generation/Revision从1开始，旧代不得回灌。复用v4/Interest/正式真实Source，不改Schema、玩法、资产、Profile、场景、容量淘汰或世界重启规则。此次完成最小本机双Native恢复流水，**N5Complete=false**。

同一成功gates-4实例、World/Connection/Subscription不变，两个非原点Solid Key `(-3,0,-2)`、`(-1,0,-2)`，初始各128个真实非零格、8192 GMU、907words/6页。A末页前固定3秒暂停时已唯一发送5/6页，Client/真实Store新采样未发布目标、无该Key安装ACK；恢复后两Key双端逐页完整传输与实际安装/准确ACK均记录。没有声称Client已收到5页：这是DS发送位图＋Client实际保旧＋完整后ACK的证据，纯行为测试另逐页验证收齐前不写。

A全ACK后在TryFreeze前有限45秒暂缓，正式真实Solid Owner每0.25s对两Key各减1，共80轮；Owner实际history追加 **160** 条，capacity **128**，A在真实held检查时版本/格数量比较保持，B正常流转。45秒自动释放后A两Key实际冻结原因均 **HistoryGapBaseline**、Base0，每Key均900words/6页，逐连接/每Key pending Header与Client安装Header精确相等；不是强制RequireBaseline或靠总ACK推断。当前A/B实际格与Source每Key数量/格数和当前CRC/版本一致。

同Key真实移除→正式Capture tombstone并两端清零/ACK→同址重投。已记录Native **G1/R88 → G2/R1 Base0**恢复ACK，最终新代较低Revision数量/内容与DS一致；缓存真实旧G1/R88、900words/6页的首页显式重放一次，Client StaleVersion拒绝，旧控制未增加DS接受Resync或ACK，未回灌。Native数量为 16384→16224→删除第一Key→16304；不把新代Revision与旧代数字直接比较。

### 2. 四次实际流水、故障与采样修正

- gates-1：正z候选点未形成两大块，主稳定Key各72真实非零格，部分材料持续下落，128永久Key显式Commit:Capacity。失败全状态保留。SolidCells是扫描槽数，不当实际占据格数。此处只定位到测试投放点/环境支撑组合；尚未隔离为正式碰撞或材料物理缺陷，没有改物理、Profile或cap。
- gates-2：仅私有点改为(-5,1,-3)/(-1,1,-3)，同8192×2形成两Key各128格/6页。原35秒observer比B约41秒实际受限发送短，45秒自动释放A后恢复；该次没请求跨代，不能记全流程通过。
- gates-3：实际条件trace记录A旧版本/格和160/128/80轮true，B当时false；同进程自动释放后已完成跨代/重放，但缺raw covered A快照。独立核验又发现removed Client metadata报告早于实际tombstone ACK约0.5秒，尽管真实Store已清零。独立核验Passed=false原样保留，未补造快照，不当完整raw验收。
- gates-4：同源码/构建、数量和门槛，只将原八项全部满足的观察期限改为44秒（仍小于原45秒有界hold），删除后额外要求完整Client metadata/DS/实际Store/精确ACK一致才取样。covered和removed原始快照齐全，独立核验使用默认严格raw模式通过，未使用derived-covered。途中新MySQL未就绪导致业务在SchemaMigrator.OpenAsync退出、未建会话/Native；保存日志，确认PID和13384监听后只重试该尚未执行的启动，正式流水从未重复投放。

最终成功结论来自gates-4全raw，不用前三次失败、派生比较或稀疏旧报告替代。保留“DS已发送唯一页数”与“Client逐页接收数”的区别；完整安装ACK与实际Store才是最终发布证据。暂停只用于标注稳定比较，没有用暂停掩盖首场景128容量失败；成功种植/重投各实际运行至少20材料Tick，但仍不代表持续物理/无限探索、四人或性能通过。长期容量策略未决定。

### 3. 文件职责、真实测试与模型协作

- Core `WorldReplicationCommitOwner`只新增真实AppendHistory计数/容量观测；不把Capture、空提交、Tick当record。Multiplayer/World `WorldReplicationPeerState`在真实TryCopyDelta失败→当前Baseline处记录FreezeKind；选择/预算/超时不改。Coordinator与RoomInstanceBootstrap只读输出，Channel核对冻结Header全tuple才记录原因。
- Channel沿原RecoveryProbe扩展5条显式命令、有界45/3秒hold与240秒终止；两个peer各128个永久Key最新传输槽、4个ulong页位图，不保存无限事件。默认ENV未开无文件轮询/新增诊断对象。既有2秒timeout在3秒hold期间仍生效，所以恢复允许同一冻结快照重传，唯一页数与sendEvents分开，最终完整才安装ACK。
- P2只在隔离诊断：原ACK识别AND错误先纯提取保留行为，真实2cases 0/2 RED（合法新代R1 expectedTrue/actualFalse），再Generation优先字典序＋已记录Header单调保护。新增双Key6页/263真实历史record/128ring和tombstone→G2/R1旧代栅栏两个闭环先0/2 RED缺观测，之后4/0 GREEN。旧37cases/全部断言按起始Hash逐字保留。最终Coordinator35/0、27fixtures **427/0**、正式P7/P8 PlayMode **2/0**，不是整项目Run All。
- ElementField既有OnlineMaterialReplicationProbe增加默认关闭extended序列，正规World.TryEnqueueWrite与FixedSolid.TryRemove真实Owner，正常Capture，不替换Source/WorldEpoch/Generation；只操作本fixture材料，不写玩家库存/业务资产。源文件仍在原职责目录，无新源文件夹、跨asmdef依赖或Shared更改。QA JSON/采样不用于性能。
- 初次P2请求错类筛到0/0，保存empty-filter，不当RED；更正后才得到实际2/2失败。业务/Shared源码未变，本轮不重复全局ExpireDue的100项业务测试，避免历史实例污染；8文件固定生成一致性实际通过，Native用既有Test Schema、新账户/工作目录走真实Profile/Ready/Ticket/KCP。历史100/0不当本轮新结果。

匹配Windows x64 Development Mono Client构建 `2026-10-31T15:48:13.0291705Z`、真正Dedicated Server `2026-10-31T15:52:30.8724605Z`，各0 error。Shader编译长等待通过窗口/实际日志确认有效，未重启Editor。只读sol/xhigh审查未发现本段确定P0/P1/P2，报告review.md；之前P2已有Native G2R1证据修复，既有home/away P3仍留档未触发。每DS两条Built-in Resource错误原样保留；gates-1还有本次容量故障，不能把失败日志抹成只有两条。Source未改的HLSL warnings保留，数据恢复不代表渲染质量已验收。

### 4. 保护与接续

12保护文件、13旧正式资源＋已有Profile/Meta共15项Hash保持。后台运行单字段只在与本任务DS构建同刻落盘且改回后精确匹配起始Hash/HEAD时恢复；Physics也只恢复精确匹配已证副作用的字节。四组自有Native/业务按PID/启动时间/路径清理，本阶段MySQL认证关闭；用户Unity回P7_MainMenu、非Play/Compiling/Dirty，无关Recovery和所有既有工作区保留，未提交推送。所有失败、辅助脚本、读数、限制及短期凭据清理有记录。

下段按已批准N5继续完整键盘路径、同进程重连/消耗不补满、P8、四人持续与有效Profiler/GC；先评估所需玩家路径/测试条件，不把本轮稳定比较当其验收。128容量长期策略、世界重启、未确认玩家可见规则仍须讨论，N4/N6/远端Gate不变。炼金仅已确认计划记录，本轮未实现。

Ruling：保留128固定上限及失败 — 不以淘汰/加cap掩盖不稳定种植点 — 成本是地图规模策略仍待定。Ruling：同量换到现有支持区 — 只调整隔离fixture私有配置 — 不宣称正式物理缺陷修复。Ruling：按实际受限发送时序加强原始采样 — 不改预算/协议/数量 — 成本是补一组完整流水；早期派生/稀疏缺口和失败保留，最终gates4为完整raw严格核验。Ruling：默认不开诊断、有限命令/缓存 — 复用正式链路 — 不得用于性能/无限运行结论。
