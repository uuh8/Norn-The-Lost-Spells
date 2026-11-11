# 容器操作阶段（实施中）



### 2026-11-01 11:51 UTC节点：容器选择前置已Editor验证，F/H与转交未实现

已接通SelectTool=3的既有数字4–0已绑定容器选择、真实Owner/槽/版本/Busy及128条幂等收据；选工具不搬物不扣量、保留最后Wand却拒绝施杖，切回同Wand必须由服务器确认。Owner Snapshot字段12`SelectedToolSlotPlusOne`和SelectionVersion经Codec/Reset/Source完整验证安装，高频资源更新保留工具模式。正式Pump改跟踪选择版本，不能只看资产版本/最后WandId，否则成功选择不会发快照。收据重放预算保持，不靠强制重发掩盖。

真实结果依次保存：Run RED117/11；v6纯合同RED0/1；Codec/Driver首次因测试asmdef缺InputSystem引用Blocked，**没有运行测试/XML**；仅给Character.Tests添加直接测试依赖后实际RED100/38；首次集成267/16；最后7fixture集成**285/0（11:49:10–11:50:55）**，覆盖Run、Source、两Codec、Driver、旧Focus与正式发送策略；业务纯合同/Reset**16/0**，未用数据库。这些运行不相加，不宣称当前全仓或Native通过。

两Run失败是V2测试前置未发OutputReleased、预约仍1；补真实输出结束并增加1→0断言，生产Busy没放宽。Driver补实际焦点/Ready前置、EditMode按既有惯例明确调用OnDisable，原请求/不乐观改Source/自有Action关闭与共享Action保留断言不动。DS新增工具模式旧Wand意图前置拒绝。此前撤销两处本轮新增Application.isFocused检查的补丁被自动审批拒绝（认为违背保持生产焦点门要求），未执行、未重试；保留所有门后相关旧Focus回归已通过，此拒绝当前不阻塞选择切片。

Shared应用门禁为v6，v5不能混用；Proto按官方生成，Snapshot scalar12不能当nested，kind3及Slot严格校验，unknown/重复/非法varint/截断别名仍拒绝。v6内容清单在节点导出，未构建匹配v6 Native，旧v5成果作为历史保留。Editor一直保留，没有新的Native窗口或服务。保护文件Hash见selection-checkpoint.json；新发现的BGM_Base.mp3及.meta等用户工作区内容未触碰。

下一段直接继续已批准F/H权威数量事务与共享地面转交：现有InventoryMaterialTransferService/ContainerUseService/MaterialInventoryWorldPort可复用，但CanAcceptWorldWrites本身不是Frozen门，必须由已认证DS入口校验世界推进/租约。真实采集8000/倾倒2000、既有gas倍率/半径/预算不改。原服务允许bag/EqTool两种携带容器，此次“绑定容器选择前置”不作为永久收窄。共享地面注册/复制目前不存在，地面物品不可留个人Store，跨Actor导入原ID还须登记Revision；先用携带瓶丢地→队友拾取，不另加掉落/概率/注册赠送。

短按1/4/1已只读调查：脚本整次约131/127/127ms，但缺down/up消费与CommandId/DS提交链，不能断定脚本/帧调度/重复执行；观测方案已留档，尚未实现。黑截图仍视觉未验收；广域2失败/12未重跑独立保存。四人高负载/性能用户明确延期，Noita不实施；Physics拒绝不重试，不提交推送。

### 2026-11-01T12:43:36.191533+00:00 F/H数量事务与调用入口检查点

本轮沿04:09/04:28已批准容器范围落盘：Core CollectMaterial=4/PourContainer=5值意图与GMU/镜头射线、数量/原失败收据；Run在原128条幂等账本中复用真实采集/倾倒Reservation服务，验证版本、当前选中真实容器、Busy/Frozen/可信世界及有界Tick预算。稳定内部Source和单调请求号不随重连占用新来源槽；失败/空World/采样未命中不丢料，同ID重试只回原数量收据。配置引用原UseDefinition，未另设平衡值。

正式DS通过Core可选只读接口取得原采集/倾倒Adapter一致WorldId；Character请求不乐观修改Source，DS用真实Actor与原3m F采样/48点H重力轨迹重算落点。H起点差异实际RED偏差1.13109469m后，改为原镜头水平投影前移，不转Actor、不改速度/半径。Multiplayer直接读Run SO导致的CS0012编译阻断已记录并改Character布尔视图，未增加Runtime asmdef反向依赖。

实测证据（不得相加当作更多覆盖）：首次联合RED131/40（Run81/17、Codec50/23）；候选215/15（含6个旧测试反射重载歧义，按精确单参数签名适配，断言未改）；一次Blocked编译无测试/无XML；修复后候选230/1仅H起点RED；最终17fixture同一回归 **456/0**；真实World及原行为PlayMode **3/0**。Shared官方生成8文件Check、纯业务合同/Reset **19/0**，无数据库。详见`container-operations-2026-11-01/fh-checkpoint.json`及同目录XML/报告。

PlayMode新例走小型Sandbox真实ElementWorld Liquid/Transfer/Deposit、实际Run Owner与只读Source安装，验证混样采集30、原请求重放不重取、倾倒15、组分与World数量族账本平衡。该例直接提供可信几何上下文，没有KCP/真实键鼠/屏幕视觉，不能代替Native玩家路径。原Tab生命周期与原倾倒同范围回归分列在XML。

**未完成/接续**：当前仅可调用F/H意图与DS处理API，持键Input与H预览尚未接；当前起步选择仅已确认EqTool容器，原背包内容器选择仍需复用接入，不能改写成永久仅EqTool合法。旧库存通道`InventoryCommandRateBudget(16,8)`是离散命令8次/秒，不能直接承载原F/H各10Hz；接持键前必须在已批准原速率下适配网络预算并验证有界性，不改原采集/倾倒速率或靠限流掩盖。随后同实例丢地/共享首拾、跨Owner原子归属/注册复制、匹配v6构建和低负载Native仍待执行。Gameplay兼容Hash尚未为本轮刷新，旧v5产物未用于验证v6。

Editor全程保留并由测试恢复原P7_MainMenu；未启动Native/业务/MySQL或构建。保护文件Hash结果见JSON；Physics及撤销焦点门拒绝均未重试。未涉及G破瓶/K固体/供料/炼金/N6新结算，不提交/推送。128Key、DS已知资源错误、P7火时延、既有2失败/12未重跑继续保留；四人高负载/性能按用户决定延期，不标通过。N5仍未完成。

### 2026-11-01T13:42:56.572178+00:00 持键、原预览与背包工具绑定Editor检查点

沿本轮明确授权接通原现代路径：背包瓶拖到工具格→原真实绑定/交换或回包寻位→数字4–0确认选择。BindTool=6复用现有值字段与128收据账本，无新SelectContainer状态；UI不发本地写入、不自动选中、不乐观改Source。Run以内部ToolConfigurationVersion调用原ToolbeltService，先验证网络InventoryVersion，两个计数不混用；仅用私有固定事件scope隔离原服务的默认单机选择广播。当前槽换瓶推进一次在线SelectionVersion/清F/H预算；选中工具搬去空槽按原首根法杖fallback，不跟瓶自动跳槽。

新增脚本只在已有`Character/Interaction/`：`OnlineContainerInput.cs`负责本机Owner真实F/H/LMB Action、原F四类优先级与松键、独立有界10HzGMU、同瓶库存更新不清计时、失焦/Blocked/暂停/死亡/断线/重连/场景解绑；`OnlineContainerTrajectory.cs`供实际LineRenderer和DS共同计算原48点/.065s/.18m重力轨迹，未改相机水平起点、速度/半径或Actor朝向。Remote/Authority不绑定本地输入，新增.meta由Unity生成。该段未涉及G/K/供料/炼金/结算。

网络原离散命令16突发/8每秒保留，Collect与Pour各独立16突发/10每秒；三桶共享真实连接/Epoch/单调时间栅栏，预解码64突发/32每秒继续保留。真实Channel按已解码kind路由，软拒绝仍交Run相同幂等账本，数量权威Tick预算保持。持续各500次合法10Hz、非法洪泛、各生命周期清理均有行为用例，没有关闭限频或改实际材料速率。

实测按版本分开：首次联合RED **297/64**；候选 **335/27**（21个新Input用例默认Editor更新不推进Player Action，5个诊断测试attribute误归属，1个真空槽fallback缺口）；修复后同范围 **363/0**；最终17fixture同一回归 **561/0**；真实World/原倾倒/Tab PlayMode **3/0**。新增Input用例使用真实虚拟Keyboard/Mouse→InputSystem Action→Core意图，并在测试中保存/恢复包自带非持久`RUN_PLAYER_UPDATES_IN_EDIT_MODE`，未保存InputSettings、未削弱生产Focus/禁用/Ready门。实际LineRenderer末点对DS同几何无碰撞/首碰撞通过，仅证明CPU几何数据，非屏幕/GPU呈现。

原严格版本合同诊断现已实际通过：同一确认版本F成功后，H原版本StaleVersion且两侧数量不变；取得新Owner版本的新H命令成功。各10Hz输入不等于每批都可提交；本轮未暗中预测版本、放宽版本或合并请求。Shared生成8文件Check与纯业务兼容/Reset19/0，无数据库。

详见`held-bind-checkpoint.json`和同目录带版本标签的XML/报告；各子集/历史计数不得相加当更多覆盖。Editor始终保留并恢复P7_MainMenu，保护Hash结果见JSON；不提交/推送。此节点尚未构建/运行匹配v6 Native，GameplayHash尚待为最终源码刷新；下一步先准备真实低负载持键/绑定实操证据。共享同实例丢地/首拾/跨Owner原子注册复制仍未实现，原单机只有单Store准备/提交，不能写成已经共享。后续保持原落地支撑、同Scene 3m拾取与真实形状安置，不新增LOS、掉落/死亡/结算或淘汰策略。四人高负载/性能仍用户延期，Noita仅候选，旧失败/未测继续保留，N5未完成。

### 2026-11-01T14:27:58.678579+00:00 匹配v6构建与Native内存守卫中断（未通过功能验收）

只读当前手持诊断增量先RED **76/8**，8项均缺字段；最小映射后Driver/Replica针对回归 **89/0**，仅显式Capture生成三字段，不新增采样/Tick/协议。既有561/0、PlayMode3/0、业务19/0仍按各自范围记录，不相加。工具格使用独立IDropHandler，现有默认关闭UI报告额外给出真实可见ToolbeltSlot矩形/槽号（区分Compact），OS拖放仍走EventSystem，不调用OnDrop/写资产。新增外部脚本只在任务staging，修正旧脚本残留ProtocolVersion=5至6；没有为测试改变注册赠送规则，两个全新隔离账号各38件测试夹具，非正式获得玩法。

兼容预检14:06:32通过；真正Unity Native菜单Client **14:12:51成功**、Dedicated Server **14:17:36成功**，业务Release另构建0警告/0错误。协议6、GameplayHash `b9b89dae6cbf309c88d6a4cc001077d6a52abc144ed2069cfba51e7d6b391898`。两端产物保留，不需重复构建。真实OS登录/Hub走门/准备/开始后两端认证Connected；但 **14:21:34.347189 UTC** 实际可用3021119488B（约2.81GiB）触发原3GiB守卫，SimulatedMemory=false。守卫正常关闭自有A7668/B11628；DS29624、业务22492和私有MySQL随后正常收尾，cleanup.Errors=[]。独立核查PID/启动身份均已退出，Editor15628仍主菜单。

两端最后只读报告ClientInventoryReady=false/count0；尚未完成Owner安装，**没有执行背包拖放、H倾倒、F采集或预览屏幕验收**，不能把认证或构建成功当容器Native通过。原始失败、OS操作审计、guard/cleanup、DS日志与`native-1/result.json`全部保留。未降低阈值、未强杀或关闭用户Editor、未重复启动同组合。Editor收尾约5013MiB、系统空闲约10098MiB；建议在确认没有真实未保存内容且取得用户本次明确许可后正常暂关Editor再用保留产物短测，当前尚未获此许可/尚未执行关闭，不沿用旧许可。

共享同实例丢地/首拾/跨Owner原子注册复制依旧未实现；可独立接续已有授权。原单机落地位置由DS角色等效计算、Guid世界实例身份、完整支撑预检；拾取同Scene3m、真实形状入包，未见原拾取LOS规则，不自行新增。四人高负载性能用户延期，Noita不实施，Physics/焦点门拒绝不重试，既有失败未测持续保留。保护文件Hash检查全部不变，不提交/推送，N5未完成。

### 2026-11-01 14:36 UTC 用户观察：P8进入后退出核查

最近两轮可区分：历史v5 Owner native-7 A7292于10:56:11.858682、B7252于10:56:38.824331启动，10:57:28初始Owner一致、10:57:37消耗30→29、10:57:41同PID重连Epoch1→2且29保持均有owner-comparison，10:57:44脚本finally正常收尾；守卫reason=normal-owner-stop，未触低内存、未强杀。这只验旧Owner，不是F/H验收。

本轮v6 native-1 A7668于14:19:57.525704、B11628于14:20:28.538280启动；这是同一轮的两个窗口。两端14:21:33.645认证Connected，但Owner未装；14:21:34.347189实际可用3021119488B≈2.81GiB，原3GiB守卫向两PID发WM_CLOSE，14:21:36 guard-result显示RemainingOwnedPids=[]、ForcedTerminationUsed=false，随后cleanup.Errors=[]。客户端尾日志均走InputSystem Shutdown与PlayerConnection::Cleanup。此次退出由守卫发起，未执行拖瓶/F/H，不算通过；进程退出码没有留存，明确未知，不能编造0或用无异常推出0。

已准备native-2单图形端脚本但从未启动（无session.private.json、无该evidence目录）；当前无Native测试进程，Editor15628保持P7_MainMenu。没有再次开窗复跑。用户看到退出而主线程仍见旧构建状态，属于阶段回报延迟；当前有效阻塞是双图形端组合触及内存守卫，不是编译或工具调用卡住。详见p8-exit-audit.json及两个原始guard/cleanup/checkpoint。保持单端独立接续与本次Editor关闭许可边界，源码/证据不回滚，不提交推送。
