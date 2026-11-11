# 容器共享Pickup / 背包Drop复用图（只读，2026-11-01）

本轮仅查源码/资源/计划并写本staging文件；未改源码/测试/资源，未操作Editor、构建、服务或Git。以下“拟”均是主Agent分配前的接线建议，不冒充已有API或已运行测试。

## 结论

**现有单Store已经有保持原实例/组分的候选移动、地面预准备和去重命令；缺的是跨个人Store与共享地面的唯一归属事务，以及公开地面事实/附近读源。** 当前不能直接把单机WorldItemPickup绑定四名玩家，也不能把Drop后的地面项继续留在Owner工作区后照常捕获快照。

限定Runtime检索Run/Character/Multiplayer/Core后，没有InventoryCommandAuthority、ItemOwnershipAuthority、WorldPickupDropPort三个符号。它们可以是拟提取的职责名；当前真实实现是SinglePlayerInventorySession、ItemOwnershipStore、WorldItemPickup和OnlineOwnedWorkspace。

## 1. 授权和当前接续

计划根目录P=RPG/docs/superpowers/plans/。

- P/2026-10-31 Norn项目总计划（更新中）.md:442–444：02:41两来源＋03:39共享一物、首个成功拾取、背包丢地转交已经确认。
- 同文件:475–477：04:28已批准该闭环必需的最小线上投影、意图/幂等收据、Owner状态及兼容接线，不再重问许可。
- 同文件:640–651：最新Owner投影v5低负载Native结果与下一操作段；当前数据/Owner链已存在，不能重复重建。第651行明确继续共享拾取/丢地/原选择采集倾倒。
- P/2026-10-29 N5共享材料世界与分块恢复实施计划.md:359–370记录同一接续。总计划早前:388/422/446的“待确认”是被后续确认覆盖的历史，不能重新阻塞。

本轮不新设掉落/概率/刷点，不做注册赠送、直接交易、自动分配、个人掉落或N6结算。G破瓶/K/固体/法术供料仍在首段之外。现成P7表是来源，不等于正式在线已接通。

## 2. 现成API和不可直接复用处

以下S=RPG/Assets/_Project/Scripts/。

| 层 | 现成入口/行号 | 复用方式与缺口 |
|---|---|---|
| 单机命令/通知 | S/Run/Runtime/SinglePlayerInventorySession.cs:492–547，OnCommand→Execute；成功且Version变化时:502–503发InventoryChanged | 可提取命令分派/结果规则，不能把整个单机会话装回Replica。Pickup由代理消费，Session故意忽略Pickup(:494)。 |
| 原背包Drop | 同文件:562–601 | 取原RuntimeItemState；先创建全部隐藏代理/验地面，再TryApply同实例的新WorldItem位置。失败销毁预备代理，成功后绑定/激活。没有Create新瓶或补组分。现实现只在一个个人Store内移动，不能直接转交B。 |
| Store位置候选 | S/Run/Inventory/ItemOwnershipStore.cs:93–144 TryMove/TryApply；:129–138保留同Composition引用并禁止通用入口改材料 | 保持原InstanceId及原组分。TryApply不能新增/移除已有实例，不能拿两个TryMove当跨Store原子转移。 |
| 可用预准备 | 同文件:186–197 CompletePrepared/PreparedInventory；:205–213 TryPrepareMaterialItem(add=true)；:222–237 TryPrepareMaterialChange(replacement=null可移除容器) | 可作为容器跨“个人↔共享地面”的低层基础；仅internal，同Run程序集可用。必须先把双方候选/世界槽都准备成功，再提交，失败Cancel；不能先Remove成功再TryAdd失败。现API没有版本参数，外层Authority需先校验ExpectedVersion。 |
| Store排他/版本 | 同文件:201–204材料锁；:253 Commit→Version++ | 源/目标预约互斥，包装服务需检查资源Busy/版本耗尽，且提交后才发事件。Candidate持有引用不是第二个公开Owner。 |
| 代理/距离 | S/Run/Runtime/WorldItemPickup.cs:76–87 CanBind/TryBind；:93–115 TryCollect | 注册键是(Store,ItemId)，不是全局唯一ItemId；给多个Store各放一份会复制。TryCollect再验同Scene/距离/WorldOwner，成功才关闭代理，满包保地面。可复用校验/视觉生命周期，不当多人Authority。 |
| 自动落包 | S/Run/Hub/NearbyPickupController.cs:88–119 | 按原Bag规则遍历旋转/空格并用Store候选验证；容器不堆叠。当前硬依赖SinglePlayerInventorySession及在同一Store中读地面项。需抽离查询/命令出口，不能让Client调用TryCollect改本地Store。 |
| 地面放置 | S/Run/Runtime/WorldItemGroundPlacement.cs:10–64 TryPlace；:66–89 TryBounds；Session:603–643 CreateProxy/TryGround | 复用原模型与中心/四角支撑、非分配射线、排除角色/拾取体的逻辑。DS Port须以可信角色姿态求落点；Client不指定任意远处位置。模型Bounds依赖Renderer，headless是否仍取得可用作者Bounds需要专项Play验证，不能凭代码声称DS已经可用。 |
| 原F输入 | S/Character/Interaction/NearbyPickupInput.cs:17–25 | F与滚轮、站点/门优先和LastPickupFrame已有；字段是具体NearbyPickupController，需接线上Nearby查询/意图适配器。保留同次F不顺带采料的门禁。 |
| 原Tab拖出/按钮 | S/UI/Inventory/SinglePlayerInventoryController.cs:110–113、448–455、489–514 | 都转InventoryCommandKind.Drop，**不是G投掷破瓶**。当前线上BeginDrag(:327–334)及SubmitOnlinePlace(:573–607)只许自有Spell/PlaceCard，容器Drop/Pickup尚过不了gateway。 |
| 已有命令账本 | S/Run/Spells/OnlineOwnedWorkspace.cs:195–241 | 同Epoch/CommandId先查原收据、异内容冲突、记录Busy拒绝，先去重再版本/Busy。扩同一链，不新建互不认识的命令号。现Apply(:244–254)仍仅SelectWand/PlaceCard。 |

## 3. Owner快照的关键约束

1. OnlineOwnedWorkspace.TryCapturePresentation(:75–87)要求Store每个ItemId都在_itemRevisions，且根位置属于当前玩家的装备/包/工具/法术专区。**地面WorldItem留在A的Store会导致捕获失败；新B拿到ID但没登记Revision也会失败。** OnlineCombatWorld.cs:188–192随后Faulted停模拟，因此这是接线前必须测试的真实组合门。
2. 拟增加受控的Workspace import/export步骤：接收原RuntimeItemState/原Revision，预先校验ID冲突与预算；提交时登记接收者_revision元数据，移出者Store不再拥有地面实例。历史Revision可以保留作来源记录，但不可让同ID同时存在于两个人Store。不能为了过捕获检查生成新ID或重置组分。
3. OnlineCombatWorld.cs:177–195已比较各Actor的OwnedInventoryVersion/选杖，变化时SendToOwner；重连Epoch变化也会发新完整快照。成功Drop提升A版本，成功Pickup提升B版本，即可复用这一触发；重复成功不额外改版本，失败不伪造Changed。
4. 同文件:203–215认证/CanAdvance/Ready→OnlineCastDriver.ExecuteInventoryCommand→回Receipt；版本冲突可请求补快照。Driver:271–280检查当前World/Actor状态后转Workspace。完成领域提交后发布已有InventoryChanged(:Workspace292–293)，不直接给Client补物品。
5. 原UI:615–643已区分Receipt和真正安装的Source版本；快照先到或收据先到均可处理。Drop/Pickup沿同模式，收据不乐观删包/加瓶，身份/Epoch匹配且Owner快照安装后再刷新完成状态。

## 4. 共享地面/正式P7目前缺什么

- 对Multiplayer/Networking/Shared Protos限定检索WorldItem/world_item/PickupWorld/WorldPickup/GroundItem等，结果0：未见生产共享地面ID注册、增删/基线或拾取Receipt消息。材料Chunk复制不是物品复制，不能借Chunk tombstone或NPC ActorId表示一个地面瓶。
- S/Character/World/OnlineDungeonDefinition.cs:44–50仅世界Prefab、玩家出生、NPC、遭遇；没有掉落表/物品注册字段。正式资源Assets/_Project/ScriptableObjects/World/Online/P7.asset:15–18指向P7_DemoRun及已导出WorldPrefab。
- Editor/Networking/OnlineDungeonTools.cs:170–186先克隆再RemoveActorsAndOwnership；:315–316明确删WorldItemSpawnPoint/WorldItemPickup/奖励等；AllowedWorld(:317–323)也不保留DungeonRunContent。因此P7单机容器表存在不代表正式Online P7会生成容器。
- 单机S/Run/Runtime/SinglePlayerInventorySession.cs:768–797 TryRegisterAwayWorldItem会创建**新生成物**的ID并登记spawn账，且绑定单机会话；不能用于A Drop→B Pickup（该路径不得Create新ID）。P7旧DungeonRunContent/DropAdapter依赖SinglePlayerInventorySession，未来复用内容时只适配既有表/死亡事实，不连回整套单机Owner。

最小阶段可先用“已有真实携带瓶→Drop→共享地面→另一玩家Pickup”打通，不增加地图物品/概率。这样能验证转交协议和数量不变量，但**不能据此宣称P7敌人既有容器掉落已正式接入**；那项接线单列后续，用原表原规则，不新设计掉落。

## 5. 最小接线方案（供主Agent定文件所有权）

### A. 先做Run领域事务与DS场景Port，暂不碰新掉落

拟ItemOwnershipAuthority管理本World唯一地面注册表（ItemId→原State/Revision、Pose、地面Revision、当前归属）；拟InventoryCommandAuthority可直接扩现Workspace命令分派/账本，不必强制再造同名类；拟WorldPickupDropPort只准备/取消/激活地面表现与可信落点，不写个人库存。

Drop：身份/版本/原持有与Busy检查→预备源Store移除、地面槽与隐藏视觉/落点→全部通过后提交源移出+共享登记→登记提交版本→Changed/公开地面事实/收据。失败原包/组分/版本不变。

Pickup：地面ItemId/Revision/距离检查→预备目标真实Bag位置与目标Revision元数据→占住该世界槽→完整验证→同步提交世界移出+目标入包→Changed/移除事实/收据。按成功提交顺序竞争；A满包失败不消耗世界物，B可成功；同命令重试返回原结果。

先限本段容器（无子闭包，明确非G/K），不拿“单容器搬运”假装支持整包/整杖子卡的跨人迁移。原通用Drop支持带子物，未来若扩其他物品须整体闭包事务，不能把子卡留在A。

两次提交间不调用外部回调；所有事件在领域归属完成之后发送。现CompletePrepared要求预备token仍归该Store， Port提交阶段应只发布已准备对象。若提交后不变量破坏，显式Fault/保留真实归属诊断，不能伪称失败并免费返满。

### B. 再扩原命令/公开地面事实，不复用私有Owner快照作全队地面列表

- 原Core/Online/OnlineInventoryCommands.cs:7、Shared inventory.proto仍只有SelectWand/PlaceCard。追加DropContainer/PickupContainer意图，沿原CommandId/Epoch/ExpectedInventoryVersion；目标BagId/X/Y/旋转（或明确自动落包），WorldEpoch/ItemId/ExpectedGroundRevision；不接Client组分、容量或接收者PlayerId。
- 以原InstanceId作为共享物品ID即可，不必另造NPC ActorId；WorldEpoch＋逐物地面Revision区分同瓶丢/捡/再丢，拒绝旧Pickup/移除页回灌。原DB EntryRevision/OriginPlayer与当前RunHolder分开留存，仅运行期来源账，不写N6持久转让。
- 拟公开地面基线/增删事实：WorldEpoch、ItemId、GroundRevision、ContentId、Pose、当前地面状态。需要原Nearby容器详情时可携只读容器State（最多8项），不能广播持有人整份私有库存。重连取当前基线，不重播初始Seed或发新瓶。
- 新公开地面读源与Owner读源分离。当前IInventoryPresentationSource(:9–24)没有Nearby服务，INearbyPickupReadOnly只提供ID列表；原附近UI还会从个人_read.TryGetView查ID。需增加显式世界物品只读查询/适配，不能把Client地面假项塞入OwnerStore来哄UI。
- Client代理只显示/发意图；DS决定距离、目标空间与共享归属。当前v5现成Native不含新意图，后续Codec/Reset/ID碰撞/兼容门禁/配套构建由主Agent统一，不混用旧产物。

### C. 最小RED设计（本轮未写/未跑）

复用Tests/Run/WorldItemPickupTests.cs现有同ID/次数、失败保地面、重复代理、丢回再拾、带子卡/包回归；扩Tests/Run/OnlineInventoryCommandAuthorityTests.cs的去重/Busy/旧Epoch/占位回归，不减旧断言。

1. A有Water20+Poison7容器：Drop→B Pickup，始终同ID、逐键同量；任一观察点只有一个当前Owner，A/B Store不能同时持有该Composition。
2. A/B对同地面版本顺序/交错请求：仅一次成功；满包者不吞；相同ID同内容返回原收据，换内容冲突；Busy拒绝不会稍后同命令变成功。
3. 地面预备/几何失败、目标占格/旋转空洞、身份/版本/距离不符、World/Epoch旧包，均保持原归属/数量/版本且无虚假事件。
4. Pickup新ID后立即TryCapturePresentation成功，包含原Revision/组分；Drop后A捕获成功且不含地面项。此测试应先复现当前缺Revision/非法根导致的失败。
5. 两个当前Owner版本分别触发SendToOwner；失败/重复不放大；世界事实/收据/Owner快照乱序只影响等待显示，不造成领域重复；重连后取当前包/地面与当前持有者。
6. PlayMode使用原Tab Drop/附近区和F链验证网络意图出口，不调用Client本地TryCollect；单机原链照常。必要后续低负载双Native A Drop→B F→当前Owner/原量→同PID重连，分别记录数据与画面，不扩大成四人性能测试。

此图只为接续实现定位；主Agent尚未分配本段源码所有权，本轮不实施上述新类、接口、协议或测试。
