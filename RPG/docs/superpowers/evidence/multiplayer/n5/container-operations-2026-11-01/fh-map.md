# 容器选择与 F/H 真实接线图（只读，2026-11-01）

范围：仅磁盘源码/正式参数与引用，不运行Unity、服务或测试，不修改仓库。下文 A=RPG/Assets/_Project/，脚本路径默认相对 A/Scripts/。这是实现证据与建议，未声称当前线上F/H已经可用。

## 先明确选择语义

- **可以选当前装备背包内的瓶，不要求先成为EquippedTool。** Character/Interaction/MaterialCollectionController.cs:67–73 的OnSelect验证ItemKind.Container及IsCarriedContainer（:99–102），后者明确接受本玩家EquippedTool，或父包为本玩家EquippedBackpack的Backpack位置。只设置_containerId（SelectedContainerId）/_backpackId并重置预算，发ContainerSelectionChangedEvent；不搬资产、不改Toolbelt.SelectedSlot。Run/Inventory/InventoryMaterialTransferService.cs:46–50与Run/Containers/ContainerUseService.cs:200–203也保留两种合法来源，不能自创“只许EqTool”。
- **鼠标没有专用双击选择/装备语义。** UI/Inventory/InventoryItemView.cs:283–288每次左击调用ClickItem，无clickCount分支。SinglePlayerInventoryController.cs:663–675首击SelectItem；只有非online override且_backpackWorkspace==null的旧布局，才对包内容器发ContainerSelectionRequestedEvent（:671–672）。现代Workspace/在线只选详情；同物第二次点击走:682–702，包内通常为90°旋转，不是自动装备。线上再次点击受AllowLocalCommand限制，不能把查看推成写权限。
- **数字4–0是手持快捷选择。** HeldItemInputController.cs:24–30创建七个Action，键位映射Core/Events/ToolbeltEvents.cs:6–9（held槽3..9=4..0）；:54–58发HeldItemSelectionRequestedEvent。Session:357–361→ToolbeltService.TrySelect。ToolbeltService.cs:128–136 Resolve仅从已绑定EquippedTool找工具，数字不会直接挑任意包内瓶。MaterialCollectionController.cs:56–65收到ToolbeltChanged：工具槽选容器则调用OnSelect，非容器/切回杖会清旧瓶。
- **当前未找到专用循环容器按键。** MaterialCollectionController.SelectNext(:103–118)是无HeldItemInput的旧路径自动fallback：未选瓶/旧选择失效时调用（:83–87、159–160），从真实携带容器中找下一只，仅改_containerId。_items[256]只用于该legacy扫描，超量报SamplingLimit。NearbyPickupInput的鼠标滚轮轮换附近地面物（:18–26）；UI.SelectNextMember(:737起)轮换仓库同类实例，两者不是容器循环键。
- **H实际来源有优先级。** ContainerUseController.cs:80–81：Toolbelt.SelectedSlot>=3则取SelectedItemId，否则取Collection.SelectedContainerId；所以旧布局直接选包内瓶后，杖模式下仍可H倾倒该瓶。H/左键手持分支最后都要求当前目标为Container（:105–106）。

## 正式参数与输入

A/ScriptableObjects/Items/Containers/Use/UseDefinition.asset:15–31实际值：CollectionRate=8000 GMU/s、CollectionRadius=0.5m；PourRate=2000 GMU/s、GasPourMultiplier=4、GasDepositRadius=0.8m、MaximumPourBatchGmu=1024；ForwardDistance=1.5、PourSpeed=8、PourLift=2、DepositRadius=0.2；ThrowSpeed=24、ThrowLift=6。不是类中的默认2000采集/100倾倒。Prefab SinglePlayer/PracticeInventoryWorkspace.prefab:18732引用GUID 2bfb10ab1395706499a9f9546d8d0cd9，与UseDefinition.asset.meta一致。

| 入口 | 实际控制流/边界 |
|---|---|
| F采集 | MaterialCollectionController.cs:46绑Keyboard/f；Update:79–98持续按住采样，0.1s聚合，首按允许早于0.1s提交，只在整数预算>0时请求；每批min(4096,_budget)，发送后清预算。8000/s通常约800/批。暂停、切瓶、失败、上下文抢F均清零，无无限失败债务。 |
| F上下文优先 | :88–93：Hub站点、Portal、NearbyPickup存在或本帧刚拾取则不采，并要求先松F；NearbyPickupInput.cs:25实际拾取时记录LastPickupFrame。F不是无条件材料命令。 |
| F目标 | :120–149：Camera中心射线，Physics NonAlloc32命中（满缓冲拒绝）；将只读AimExchange发给材料Owner查真实格数量。Reach默认3m，SampleRadius取正式SO0.5；不是把相机100m射线当可采距离。气体无Collider，所以只靠Raycast不够。找到key5/6会走原固体分支，F液体/气体与固体并非同一目标资产形态。 |
| H倾倒 | ContainerUseController.cs:54绑Keyboard/h；LateUpdate:94–121（execution order1000，相机之后），H或HeldItemInput.CanPour的左键持续输入。0.1s聚合，预算始终封顶1024；普通约200/批，含任意气体的瓶按整瓶采样速率×4，约800/批。G瞄准优先时不倾倒。 |
| H目标 | :185、192–215：从相机前向×PourSpeed+向上PourLift生成解析抛物线，48点、每步0.065s、SphereCast半径0.18，取最近碰撞/末端。服务:41–43明确Pour不套旧3m限制；实际接收仍受World区域、采样、容量/几何约束。 |
| 预算精度 | Run/Containers/ContainerTransferOps.cs:10–18 的CollectionRateAccumulator保留double小数部分，累计后取整数GMU；非法rate/dt或rate×dt>uint.MaxValue清零。世界/资产真实量仍ulong/uint，非float组分账本。 |

F请求是Core/Events/MaterialTransferRequestedEvent.cs:21–28（InventoryId/WorldId/SourceId/ItemId/BackpackId、RequestId/Budget、ExpectedVersion、CollectorPosition/SampleCenter/Radius、SolidItem）。H是Core/Events/ContainerUseRequestedEvent.cs:5–14（Kind=Pour、同类身份/版本/预算、ActorPosition/TargetPosition/ThrowVelocity/Radius）。两个Controller各Awake生成Guid SourceId，各用++_sequence，不共用线上CommandId。结果按SourceId关联，失败清输入预算；不拿旧结果补量。

## 资产与世界提交链

单机装配是Character/Interaction/PracticePlayerInventoryBinding.cs:19–21向玩家GetOrAdd Held/ContainerUse/MaterialCollection，并ConfigurePractice同一SinglePlayerInventorySession；不能把它当在线Authority装配。

Session.cs:254创建ContainerUseService；对应_materials由同Profile建立。:282–284订阅Core请求，:410–430仅处理同InventoryId，拒RaidWriteLocked/不允许实验世界写，然后调用服务并公布结果。WorldActionsAllowed(:156)为AwayRuntime或允许的实验区域，不是在线认证/Frozen门。

**F：** InventoryMaterialTransferService.cs:20–67验证非重入、Store资源忙、Inventory/World/Source及正RequestId/Budget；每服务最多32个Source，按Source持请求高水位。重复/旧ID拒绝；高水位在ExpectedVersion/归属检验之前推进，失败重试不能原ID重做。随后验证当前携带瓶（或合法固体背包），ContainerReservation预约目标容量，发MaterialWorldTransferRequestedEvent。

ElementField/Materials/MaterialInventoryTransferAdapter.cs:84–100仅接同WorldId且未Handled，检查真实World可写、Radius<=0.5、中心距Collector<=3m，再调MaterialInventoryWorldPort。Aim查询(:28–80)将相机射线截到角色3m球内，最多256中心步/4096邻采样，并做可见性；不是授予写权限。

MaterialInventoryWorldPort.cs:25–57整段持World事务互斥，含Sample、目标TryReserve/Commit/Cancel；:59–102按真实样本与min(Budget,FreeCapacity)混样分配，检查版本/数量、Stage扣世界量、几何/拓扑验证，失败逆序Undo，成功destination.Commit后记录export并发布变更。容量512个journal地址/8192访问格（:12），每实际样本格还须角色3m内/可见（:135–143）。混样不兼容直接拒绝，不能过滤成免费提纯。异常锁存Faulted，不声称任意异常都已完整回滚。

ContainerReservation.cs:36–69在Store锁内复制8组分预留，:92–104 Commit才AddReserved及推进Store版本；Cancel仅释放未提交预留，成功后Cancel不是撤销。

**H：** ContainerUseService.cs:32–70同样32 Source高水位、ExpectedVersion、携带资格、有限向量/半径0<r<=1和正预算；Pour必须有真实Composition。MaterialAmountOps.SplitBudget(:64)按现有组分比例、RequestId%8轮转余数，配置ContainerSourceReservation后发MaterialWorldDepositRequestedEvent（:164–170）。服务本身没有按时间限GMU速率，不能信客户端预算作为DS防作弊。

ContainerDepositAdapter.cs:17–27检查WorldId/唯一Handled/重入/World可写；MaterialInventoryDepositPort.cs:18–50持World互斥，:52–110先校验全部组分/路由并Source.TryReserve，再Stage所有接收格、几何/拓扑验证，失败Undo；全部成功才Source.Commit、记import并发布。512项/8192访问格；原输入r<=1，Port允许<=2以支持旧飞行物重试，F/H不能无条件继承更大半径。ContainerSourceReservation.cs:18–62锁住实际瓶、校验CanRemove，Commit才RemoveReserved并推进版本；拒收保留瓶量。

## 当前线上禁用/缺接线事实

1. **当前没有找到名为OnlineInventoryUiGateway的类/文件。** 本轮在Scripts所有.cs按名称和全文搜索无匹配。现有“gateway”是UI/Inventory/SinglePlayerInventoryController.cs:561–613的SubmitOnlinePlace：online override只发SelectWand/PlaceCard类型相关法术槽移动，不走本地InventoryCommand；回执:615–625不乐观改Source。Core/Online/OnlineInventoryCommands.cs:7仅SelectWand=1/PlaceCard=2。InventoryNetworkChannel.cs:173–210做认证Actor/Epoch、Codec、pending、限流并送Authority。不存在可直接调用的线上F/H执行方法；OnlineOwnedWorkspace.ExecuteInventoryCommand(:244–294)目前只处理上述两类。
2. OnlineInventoryPresentationSource.cs:176–181从真实EquippedTool生成七个binding；:241仍将SelectedSlot设为当前选中装备杖的槽，:369–374 SelectedItemId=SelectedWandId。即当前binding真实、选中状态只表述杖0..2；不能假称已经能数字选择在线工具。
3. 原输入控制器都硬绑定SinglePlayerInventorySession。MaterialCollection.Update:81–82、Submit:157及ContainerUse.CanInput:77/Submit:125未就绪即返回；Held.CanUseWorld:19也依赖该Session。WizardPlayer.prefab中三控制器GUID均未找到；Registry.cs:59–70仅实例化PlayerPrefab/配置执行模式。本轮未见在线路径GetOrAdd这三者，不能靠解除一个enabled条件就获得在线F/H。
4. 世界Replica有明确写权阻断：ElementField/Runtime/OnlineMaterialWorldBinding.cs:117–119移除MaterialInventoryTransferAdapter与ContainerDepositAdapter，后续拆CPU模拟Owner。ElementWorldRuntime.Authority.cs:23–24的CanAcceptWorldWrites排除Replica/Faulted；Port和Adapter各自再检查该门。
5. **Frozen需在线入口另裁决。** 同文件:21明确DebugPause/Frozen只停Solver，旧实验收取/投放仍可写；CanAcceptWorldWrites不等于业务租约/Frozen。OnlineWorldBootstrap.cs:207–213只挂起模拟。不可把现有Port当成在线Frozen授权门。

## 可复用的小段建议（尚未编码）

- 保留“直接选当前装备背包内的瓶”和数字快捷瓶两条来源；先明确定义Owner本地SelectedContainerId与工具选择事实，不能新增EqTool专属限制。输入读现有稳定Source/真实规则，避免扫描固定256数组阻塞665项闭包。
- 客户端只提出F/H值意图与预览，DS从已认证Actor/Epoch取得真实位置/World身份/受控参数，复核距离/视线、当前库存版本、Frozen/租约和按Authority时间的速率预算。旧Controller的10Hz/预算限制是UX，不是服务器安全边界。
- 在现有OnlineOwnedWorkspace内部用同一私有Profile组合原InventoryMaterialTransferService/ContainerUseService，复用两侧Reservation及World Ports事务，不制造第二套库存。这里只是可复用落点；当前并无这些线上组合入口。
- 网络重试须沿线上“一个CommandId一个最终收据”机制保持结果；旧Run服务只给DuplicateRequest，不回放原成功收据，且客户端Mono重建Guid会占新Source槽。应由DS提供稳定有界来源并协调Epoch/高水位，不能把新SourceId当重放绕过。
- 第一小段只接F液体/气体采集与H Pour；原F会自动转固体、G投掷/K固体释放是额外分支，不能因复用Controller把尚无固体数量合同/飞行物复制的路径一并开放。成功后由既有完整Owner快照与世界复制展示，不客户端乐观扣量或补满。
