# 在线容器、真实背包与共享拾取 Implementation Plan（staging）
 
> 更新：2026-11-01 03:39 UTC用户确认后。仅为待审技术提案，尚未改仓库/代码。主Agent后续按获批段在原工作区实施，不另建spec/worktree，不自动提交。
 
**Goal：** 把容器提前携带、副本共享掉落、背包丢地与队友拾取接入真实线上背包；随后在同一DS资产上完成F采集、左键/H倾倒与同Run重连。
 
**Architecture：** 业务服冻结真实线上资产及租约；DS维护当前Run唯一物品归属、组分与共享地面注册表；Client只发意图、显示已提交事实。复用Game.Run数量事务与ElementField Port，Replica不装配可写SinglePlayerInventorySession。
 
**Tech Stack：** 保持Unity6000.3.16f1/URP17.3、Mirror96.11.3、Protobuf3.34.2/protoc34.2、Shared netstandard2.1及现有.NET10/MySQL8.4，不升级依赖。
 
**授权事实：**
 
- 02:41用户：“容器可以自己提前带入，也可以在副本中拾取”。两者并行，不重问。
- 03:39用户：“符合，全队共享同一个物品，抢拾是正常的。反正玩家也可以随时从背包扔到地面让其他玩家拾取”。共享唯一世界实例、首个成功拾取归属、背包Drop→他人Pickup转交均已确认，不再待定；不加个人生成、自动分配、直接交易UI。
- N5计划84–90行批准数量事务目标；仍不等于本提案新增Schema/协议/跨模块接口已审批。注册赠送、死亡/撤离/持久结算没有新增决定。
 
## 1. 收敛后的建议实施段
 
**推荐范围改为真实背包方案。** 上一版“仅7道具位、拾到空道具位”不能满足用户的背包丢地路径，已撤下；它只能算技术样例，不能当本需求的交付替代。
 
本段纳入：真实EquippedBackpack=4、Backpack=1父链与二维形状/X/Y/旋转；原7个EquippedTool=8及4–0选择；原Tab背包；容器背包内移动、装备/回包；共享地面拾取；背包丢弃及队友拾取；F容器采集、左键/H倾倒。仅扩大本段容器支持，不顺带恢复整套商店、金币/其他奖励或全物品使用能力。
 
单机已有规则与控件保持。线上当前N1的BackpackCapacity=8是平面8槽，不是8×8：001_online.sql:31–39、005_owned_combat.sql:7–21、OnlineContracts.cs:69–74。必须把真实包实例和实际格子迁移进来；不能造一只临时包，把N1 Slot硬换X/Y，或把有工具位说成已有背包。
 
### 当前实际操作对照
 
| 入口 | 当前源码证据 | 本段建议 |
|---|---|---|
| Tab及背包丢地 | SinglePlayerInventoryController.cs:110–113为DropSelected；448–455拖出面板；489–514拖到附近区，均发InventoryCommandKind.Drop | 纳入原按钮/拖放→DS Drop。容器完整落地，壳/组分保留，队友F或原附近拖拽Pickup进入真实背包。不是G投掷。 |
| F | MaterialCollectionController.cs:46；79–97为连续采集，NearbyPickupInput/站点/门优先；无选瓶时有固体分支 | 本段接容器采集及共享物品F拾取，保持上下文优先和松键边界。固体分支不因接F自动宣称已联机。 |
| 4–0＋长按左键/H | HeldItemInputController.cs:24–32工具选键/左键；ContainerUseController.cs:54、106–121 | 原工具选择与容器倾倒同段接入。4–0仍映射真实工具装备，不复制资产。 |
| G | ContainerUseController.cs:55、58、111；ContainerUseService.cs:72–93为ThrownContainer在途/碰撞破碎 | 建议下一段：投掷物、DS物理和破碎料量另验。用户批准的背包转交不依赖破瓶。 |
| K；旧J说明 | ContainerUseController.cs:56/103为放固体；当前Runtime Scripts只读搜索未见J绑定，固体采集现走F分支 | K及固体完整流、附着供料建议后段。不能把历史“J采固体”写成当前已存在键位。 |
 
G/K/固体/附着分段是待审实施范围，不是永久删功能。Tab只阻塞本机输入、不暂停DS；失焦/关UI/换手/松键停止旧持续动作。
 
## 2. 最小新增跨模块合同
 
以下是拟扩字段/接口，均须本轮范围审批，不是现有实现。
 
| 层 | 提案 | 必须保留的边界 |
|---|---|---|
| 中立物品规则 | Shared导出ItemRules：Kind/UseKind、形状占格、背包宽高、容器CapacityGmu/AllowedMaterialMask，稳定ContentId，与同份GameplayHash关联 | ContentManifest.cs:18–23当前仅ID/Kind/Name；GameplayManifest.json仅Hash。后端不能凭Hash知道容量/形状；不引用UnityEngine。 |
| 真实持久位置/组分 | 追加新迁移，暂名006_container_inventory.sql：支持真实包根4、包内1父链/X/Y/旋转、工具根8；容器状态Schema及最多8项MaterialKey/ulong Quantity；FK/唯一位置及角色锁 | 001–005脚本/Hash不重写。旧N1平面位置保留为legacy，明确映射/审计；缺真实Bag的旧行不能自动变成合法二维位置。原UQ(Kind,Parent,Slot)与包内Slot=-1冲突，须随新迁移修约束，空间重叠仍按完整形状候选验证。 |
| Hub线上整备 | 扩原业务库存请求/DTO：装备真实背包、容器网格放置/回包、工具绑定；CommandId/ExpectedVersion/真实InstanceId/明确目标位置 | 验证当前账户Owner、内容/父链/形状/空间、租约、版本；成功增版本并失效Ready。请求不得上传组分、容量、伪造新实例。 |
| EntrySnapshot | InventoryService＋RunEntryService在同一角色事务导出装备包、携带容器/工具、原杖卡闭包及组分，冻结原ID/Revision/布局 | RunEntryService.cs:40–56已有锁版本、快照、AssetLease。业务快照当前仅2/3位置（InventoryService.cs:35–53）；新增真实闭包，不拷单机档、不发Starter。 |
| DS当前Run | OnlineOwnedWorkspaceFactory完整验证新Schema后导入真实包/容器；同Profile.Items接材料服务与背包/工具规则 | Factory.cs:7、33–38当前不读本地且只承认杖卡；失败不发布半份Workspace。保持Run→Core/Combat/Skills依赖，不引用Mirror或ElementField实现。 |
| 原UI读源 | OnlineInventoryPresentationSource增加真实包/工具/组分，支持原Tab/附近区/操作提示；HeldSelection与选择版本纳Owner快照 | 当前TryCopyContainer:273恒false。原SelectedWandInstanceId不塞容器ID；当前手持选择仅Run状态；UI不直接改所有权/数量。 |
 
新账号当前仅一杖一卡。真实包/容器必须来自线上已有合法资产；缺少就显示真实空状态，不自动赠包、赠瓶、照搬单机两瓶10000水。隔离验收可构造明确的测试账户/schema业务fixture，不算正式获取政策；普通账户的长期获取/带出闭环仍有N6边界。
 
快照上限按真实包格数推导并锁定编码/内存预算：原99杖卡＋包本体＋包格内可容纳实体＋7工具的保守上界；若当前8×8、每物最少1格，上界171项，不是上一版106。仍先验证原64KiB是否足够；全部数组/Codec/解码上限同改，不截断，不擅自缩包。
 
## 3. 共享生成、背包Drop与跨人Pickup
 
复用正式P7 Stage1Loot–Stage4Loot.asset:66–77现有容器条目：0.12、1份、四容器池内等概率，容器按ItemOwnershipStore.cs:215–219生成空组分。不改概率/保底/预装料；只导出容器条目，不连带启用金币/全奖励。P8没有对应容器表，先验携带/转交/采集倾倒；需要P8自产再单列内容配置，不伪称现有。
 
DungeonRunContent.cs:35–62强依赖单机Session；OnlineDungeonTools.cs:315–316当前剥离拾取/奖励Owner。不能把这些单机Owner整棵接回。导出可信条目/敌人映射，由DS适配器读取当前遭遇的权威死亡事实，一NPC只抽一次，忙时重试同一结果不重抽。OnlineDungeonRuntime.cs:55–74已有当前NPC状态/死亡掩码语义。
 
**新增DS世界物品注册表/领域转移服务：**
 
- 全队看到一个世界物品、一份稳定InstanceId/组分；WorldItemId在WorldEpoch内唯一，不为各玩家克隆个人Store。
- 背包Drop：校验请求人当前Run持有、版本、材料/施法预约无冲突；DS按角色真实位置/地面检测求合法附近落点；候选验证后同提交“从背包移出＋进入共享地面”。客户端不自选远处坐标。原组分、壳、ID不变，不触发G破瓶。
- Pickup：校验WorldItemId/Revision、DS距离与存活/Ready/Frozen；预留玩家真实包格、世界实例并验证完整候选；同提交“移出地面＋进入拾取者背包”。首个**成功提交**者拿走；先发请求但满包者不能锁死或吞掉落。
- 竞争失败/满包/几何拒绝保留物品原位置和组分；重复同命令返回原收据。Drop后A和B都可拾，同一实例只能被取走一次。原工具引用在Drop后失效并回到有效手持，不重置原Mana。
- WorldItemPickup.cs:79–113仅有单Store唯一代理/原子移动；不能四个Store各注册同ID。拟Game.Run领域服务负责跨“个人Store↔共享地面”的候选与提交；Multiplayer只处理认证/传输，Client代理只读。
 
**需分清长期来源与当前持有者。** A的入局物品丢地后B合法拾取，当前Run持有者变B；不能继续用“DB Owner仍A”拒绝B使用，也不能在N5直接改持久Owner。保留OriginPlayer/EntryInstanceRevision、当前RunHolder/Location及单调转移记录，ItemId不换，EntrySnapshot不修改。该运行期来源账用于校验守恒和未来N6接入，不冒充可靠持久转让journal。实际落库/个人撤离/崩溃后的结果另审。
 
## 4. 拟意图、收据和状态
 
建议扩原inventory.proto/InventoryNetworkChannel及同一命令账本；不新做交易UI或另一套背包。
 
| 合同 | 拟字段与校验 |
|---|---|
| 命令kind | 保留SelectWand/PlaceCard；加PlaceCarriedItem、BindTool/SelectTool、DropWorldItem、PickupWorldItem、CollectContainer、PourContainer与停止动作。所有新kind在审批后定编号，不复用旧含义。 |
| 身份/版本 | uint32 ProtocolVersion/ConnectionEpoch/ExpectedInventoryVersion；非零uint64 CommandId/GameplayHash/WorldEpoch。Player/Actor/Run从当前认证连接/绑定取得，不由Client挑选。 |
| 目标位置 | 原32位hex ItemId/BagId，WorldItemId:uint64、ExpectedWorldItemRevision:uint32；Grid X/Y/QuarterTurns或ToolSlot。Drop无“接收者PlayerId”；转交严格走共享地面。 |
| 材料输入 | 有限AimOrigin/Direction、动作序号/状态。DS用真实角色、受控相机偏移/碰撞/半径重算目标；客户端样点只是提示。DS按原速率/批量与权威Tick算数量上限，不直接信Budget/组分/CollectorPosition/Radius。Frozen/断线/停止不积追赶债务。 |
| Receipt | 原CommandId/Epoch、结果、ServerTick、提交InventoryVersion、ItemId/WorldItemId、世界修订/移除版本；材料另含实际ulong总量和最多8项byte MaterialKey/ulong Quantity。失败无提交。 |
| Owner状态 | 初次/重连/结构变化发完整包/工具/组分快照；连续数量可提小型ContainerState（BaseInventoryVersion→InventoryVersion＋该瓶完整组分）。精确Base匹配才原子安装，缺口请求当前完整快照，旧包丢弃；客户端不乐观扣量。 |
| 全队地面状态 | WorldEpoch、WorldItemId/Revision、ContentId、位置、增加/移除；实际组分由DS保有，成功Owner取得真实状态。重连基线包含当前地面列表/移除水位，不能复活旧地面物。 |
 
当前命令512B、完整快照64KiB。数量小更新是待审的新语义，避免每个10Hz小批重建整个卡/包快照；不能盲用原16burst/8每秒领域预算限制合法10Hz输入。按原配置复核预算、固定缓冲/在途上限，不无限发请求/堆pending。若选全量先行，须量出带宽/分配/延迟，不能直接宣称单机一致。
 
Shared DTO、Core投影、Schema/Generated、Codec/Reset/unknown fields及应用兼容版本同步升级；旧Client/DS显式拒绝。只从.proto生成Shared唯一输出，不手改Generated。网络新增字段、消息ID/预算与数据语义整体审，不用N5目标暗批合同。
 
## 5. 数量、版本与恢复不变量
 
1. 一份实例只有一个当前位置：个人背包/工具或共享地面；跨人转交仅经同一个地面实例。物品总数和每键组分不得在Drop/Pickup改变；没有直接交易/自动分配/个人生成旁路。
2. 组分仅已有键1..8、最多8项、无重复/零量/未知键；checked整数求和≤可信容量，全项符合mask；容器不堆叠。液面/粒子不能充当量。
3. 同Epoch/CommandId同内容返回原收据，异内容拒绝；先去重再Busy/版本，拒绝也记录。沿OnlineOwnedWorkspace.cs:164–214。旧Epoch拒绝，换连接只换通信状态，不清余额/归属。
4. DB Items.Revision、Run InventoryVersion、Composition.Version、世界物品Revision、材料Chunk Revision职责不同。A Drop、B Pickup各推进相关个人版本与世界状态，不能拿Chunk版本或渲染帧代替物品提交。
5. 复用InventoryMaterialTransferService.cs:53–67、ContainerUseService.cs:61–70：来源/目标预留→World Stage→几何→Commit→Ledger→Publish；失败Undo/Cancel不扣料、不发虚假Delta。带预约的容器不能在另一命令中同时丢地。
6. 同Run新Ticket绑定原Workspace/共享注册表，导出当前包、当前持有者与组分，不能从EntrySnapshot重放。OnlineOwnedWorkspace.cs:64–68、OnlineCombatWorld.cs:87–97/154–170已有当前读源/新Epoch基础，需扩真实包/容器及地面基线。
7. Tab不暂停DS；失焦/切UI/换手停止连续动作。Replica只安装当前身份，重连丢旧候选/收据，等新Owner快照再开容器输入；切图解绑旧源/Driver/地面代理。
8. N5证明当前Run持有/数量/转交与重连；没有证明MySQL已可靠保存消耗/新物品/转交。死亡/放弃/提前撤离/崩溃回滚及可靠跨人结算由N6另审，不顺带修改现有结果。
 
## 6. 文件与可审阅交付段
 
路径缩写R=RPG/Assets/_Project/Scripts/；文件名为候选，审批后在详细实施计划锁定。
 
| Task | 主要修改/拟新增 | 必须验证的交付 |
|---|---|---|
| 1 真实整备/入局 | Shared/Protocol/Runtime/Business/{ContentManifest,OnlineContracts,OnlineOwnedAssetContracts}.cs；Server/Norn.Business/Features/Inventory/{InventoryService,OwnedCombatAssetRules}.cs、Features/Runs/RunEntryService.cs；新006迁移/规则导出 | 隔离真实MySQL：旧平面位置不伪装格子、真实包父链/重叠/容量/Owner/Lease/版本、Ready失效、入局原ID原量、无免费发放。 |
| 2 DS/UI真实库存 | R/Run/Spells/{OnlineOwnedWorkspaceFactory,OnlineOwnedWorkspace}.cs；Run/Inventory/OnlineInventoryPresentationSource.cs；Core/Online/OnlineInventoryPresentationSnapshot.cs；Multiplayer/Combat/OnlineOwnedAssetMapping.cs；原Tab/附近区gateway | 真包/工具/混样投影，UI移动/丢弃不本地提交；未知/超容/坏父链拒绝；全量与小更新乱序保旧/恢复；HUD与手持同源。 |
| 3 共享生成/转交 | 在线导出与OnlineDungeonDefinition/Runtime的P7容器条目；拟R/Run/Inventory/OnlineWorldItemTransferService及Character DS适配器；原附近列表网络读源 | 原P7表不重抽；两人同抢仅一人成功、满包者不吞物；A背包Drop→B拾取同ID同料→B可使用/可再丢→A可拾；重复/断线/旧地面消息不复制。 |
| 4 容器动作/协议 | R/Character/Interaction/{HeldItemInputController,MaterialCollectionController,ContainerUseController}.cs抽共同读源与命令出口；拟OnlineMaterialActionDriver；Multiplayer/Inventory Channel/Codec；Shared/Protocol/Protos/inventory.proto及Reset | 原4–0/F/左键/H输入→认证DS→真实Port→双端World；重复/异内容/旧Epoch/越权/超频/满瓶/空瓶/Busy/Frozen/几何失败；正校准GC与有界发送。 |
| 5 Native完整段 | 主Agent统一Editor/测试/构建/服务；更新N5计划/living explainer | P7真实携带/原概率生成/抢拾/背包转交/采集倾倒，同PID新Ticket恢复当前Owner与消耗余额；P8单列携带/转交/动作。正式概率不改必掉凑证据，未做G/K/结算明确未验。 |
 
每段真实失败→最小实现→相关回归；Shared改动跑生成一致性/业务构建与相关真实MySQL测试，Unity Codec/Reset/ID碰撞、Edit/Play及匹配Native配套。保护正式Scene/Build Scene List/用户布局/真实档与进程，保留失败。**本轮仅只读准备，以上均未运行。**
 
## 7. 给用户确认的核心正文
 
建议本轮接通真实在线背包与容器的提前携带、P7共享拾取、背包丢到地面和队友拾取，保留同一实例及原组分；再接原4–0选择、F采集与左键/H倾倒，并验证同进程重连后当前归属、包内状态和已消耗数量不重置。技术上新增真实背包位置/容器组分的业务Schema与入局快照，扩原背包读源以及移动、Drop、Pickup、材料意图/收据；DS统一检查身份、空间、距离、版本和数量事务。G投掷破瓶、K/固体与法术供料下一段，和本轮背包转交分开；不加交易UI、自动分配、个人掉落或注册赠送，也不提前做N6死亡/撤离/持久结算。
 
已确认的来源、共享抢拾与地面转交不再提问；当前待审只是上述实施范围与新增技术合同。仅7道具位不能替代此范围。
 
本轮只写本staging文件，无仓库/代码/规则修改，无Editor/前台/服务/测试/构建/Git变更，无凭据/真实存档读取。
