

## 29. 2026-11-01 容器权威操作闭环接续（实施中）

沿用04:09方案、04:28用户“可以”及11:06接续：共享拾取/背包丢地转交、原选择/采集/倾倒，DS确认归属与数量、同ID只执行一次、重连保持、Client确认后更新。本段不是新需求讨论；未扩G破瓶/K固体/法术供料/炼金/死亡撤离结算。Owner阶段和全部失败证据保留。

按依赖分小段实施，复用既有目录/API（未在源码找到的类名不作为已有组件）：

1. **选择前置**：既有`OnlineOwnedWorkspace.ExecuteInventoryCommand`目前仅选杖/移卡，原`HeldItemInputController`数字4–0通过Toolbelt选已绑定道具。先追加SelectTool=3，ItemId必须是当前Owner该EqTool槽的真实容器；Intent.TargetSlot为0..6。不挪物品、不改组分；复用128条有界幂等收据、版本/Busy/身份门。Snapshot新增`SelectedToolSlotPlusOne`（0沿用原选杖推导，1..7对应工具0..6），共享SelectionVersion。选工具时最后SelectedWandId保留但拒绝施杖；切回同一杖也推进选择版本。Source完整校验真实绑定/选择版本后才安装，数字输入按确认更新，不做乐观资产修改。首段只接已绑定容器，不把它写成永久“仅EqTool合法”；原Run收取/倾倒接受当前装备背包内容器的规则继续保留。
2. **F/H数量操作**：复用现有收取Reservation与MaterialInventoryWorldPort、ContainerUseService/Deposit事务；DS从已认证Actor取得真实位置/世界与既有定义参数，拒Frozen/租约失效（现有World.CanAcceptWorldWrites不是Frozen门）。按现有速率/预算/采样/可见性/抛物线投放规则逐项接，不信任Client资产结果，不自行添加距离/掉落政策。必要新增意图字段/收据必须规范编码、Reset与版本门禁一致。
3. **共享拾取/丢地**：沿现有SinglePlayerInventorySession/ItemOwnershipStore/WorldItemPickup与正式掉落复制接线，保留原实例ID和组分；并发首成功者转移，失败原子回滚、不复制/销毁物品。跨Actor归属变化同步revision映射及双方Owner投影，不能沿用仅初始itemRevisions表。具体接线先按源码复核，容量/对象注册有界，不能用任意新淘汰规则掩盖限制。
4. 针对测试RED→最小实现→GREEN及受影响回归，再串行匹配构建/低负载实际操作；不把单个入口接通写完整闭环或N5完成。UI/选择/数量/竞争/失败/幂等/旧Epoch/重连分项记录。Editor保持打开；需要关闭必须先取得此次明确许可，不能沿用旧许可。

已核查原正式UseDefinition：采集8000 GMU/s、半径0.5m、Controller/Adapter reach3m；F按0.1s聚合、单批≤4096。倾倒2000 GMU/s、含气×4、0.1s聚合、单批≤1024，普通半径0.2m/气体0.8m，落点使用原抛物线规则而非重新套3m。以上是源码/资源现状，不是另行批准的新平衡参数。

协议选择前置预计应用v6（旧v5不得混用），Shared schema/生成/Codec/Reset、业务与Unity门禁统一检查。当前只允许在既有Core/Online、Run/Spells与Inventory、Networking/Protocol、Character输入、Multiplayer/Inventory目录扩展，没有新建功能Runtime文件夹；Editor/生成/运行由主Agent集中。

独立问题：Native5/6/7同样0.12s请求，整次脚本耗时约131/127/127ms而实际扣1/4/1；缺每个down/up、Client消费及CommandId/DS提交关联，不能把4补步/4扣次直接等同。先记录默认关闭的有界观测方案，不改射速。黑截图仅表示视觉未验收。原34中2失败/12未重跑独立留档；四人高负载/性能用户明确延期，不做Noita优化。Physics拒绝不重试，不提交推送。

### 12:04 UTC F/H本轮可验切片

先在已有Run工作区复用`InventoryMaterialTransferService`、`ContainerUseService`和真实Reservation，验证Owner库存/测试世界账本两侧数量及原失败；正式ElementField、Character输入和Native属于随后独立验证，不能由测试世界替身推定。Core增量仅含CollectMaterial/PourContainer值意图、GMU请求上限、镜头射线值和已提交数量/原失败收据。认证世界、真实Actor和几何重算由DS上下文提供，客户端不能指定世界写结果。

已选择技术接线：使用原`StepAuthority(dt,tick)`累计可信SO速率，单批采集4096/倾倒原SO上限；预算有界、不重复消费旧Tick，选择变更清空预算。原128条去重账本同时记下数量收据；Run内部使用稳定Source与单调请求号，避免每次重连占用旧服务32个Source槽。超过权威预算以RateLimited拒绝，未进入World事务时不伪造MaterialTransferFailure；实际事务返回原失败原因。上述是已批准速率、幂等和重连合同的实现，不新增玩法。

同阶段扩展严格Codec/Reset：旧选择/移卡消息不得夹带F/H字段，F/H预算及有限单位射线值需规范编码，失败不能声称转移数量。当前v6尚未Native匹配构建；Owner/选择已通过的历史证据和本轮RED/GREEN分开保存。

### 12:45 UTC 接续：持键/原预览、背包工具绑定、共享转交

本轮父任务明确继续原授权，纯技术兼容无需重复许可。先保留离散命令8/s+burst16，F采集/H倾倒分别按原各10Hz分桶；预解码64突发/32每秒门保留，身份/Epoch/时间/生命周期统一栅栏，数量仍由Run原权威Tick预算独立限制。覆盖持续合法10Hz与非法高频，不能以关闭限频或改材料速率凑通过。

Character新增既有Interaction目录内的执行模式输入桥，复用原F优先级/松键、H/LMB倾倒、原SO和确认Source；同一轨迹几何供DS落点与原LineRenderer预览使用，DS不读取相机。测试先通过真实InputSystem输入到Core意图，覆盖按键取消、失焦、切图、重连和预览一致性。尚未实施/验证的部分保留未完成。

只读核查证明现代正式背包不是“点击即切瓶”：原路径是ToolbeltSlotView拖放→SubmitToShortcut→ToolbeltService.TryBind真实移动/占槽交换或回包寻位→数字4–0选中；绑定不自动选中，满包原子失败。线上复用该路径，追加BindTool=6但不加新字段，使用ItemId/目标工具槽/ExpectedInventoryVersion；Run内部正确使用工具配置版本，不混用库存计数。当前槽换物同步推进选择版本并重置使用预算；没有新增SelectContainer字段，也不继承旧UI可能造成的F/H不同瓶状态。共享同实例丢地/首拾在前置验证后继续，尚不写成已完成。

Editor继续保留；当前先集中RED，不启动Native/构建，不改G/K/供料/炼金/结算，四人高负载延期。原Physics/撤销焦点门拒绝不重试。
