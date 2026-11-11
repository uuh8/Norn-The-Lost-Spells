# 容器首个切片：真实入局资产闭包（只读设计）

2026-11-01。用户04:28已批准：真实线上背包携带、P7共享唯一掉落/首个成功拾取、背包丢地转交、原选择/采集/倾倒、DS数量归属/幂等意图收据、确认后更新与同Run重连不重置。**不重新问上述许可。** G/K/法术供料、死亡/撤离/N6不在首段。

当前Editor有dirty资源。本轮只读源码和既有proposal、只写本staging文档；未修改代码/Schema/资产，未操作Editor/构建/测试/服务/Git，也未碰计时代码。

## 结论

第一个可独立实现/评审的切片应是：**业务中真实已拥有的背包/容器组分，经过同一入局冻结快照，原ID/位置/数量无损进入DS工作区。** 暂不启用玩家Drop/Pickup、材料动作或世界复制。这不是缩小最终获批范围，而是其先决数据链；没有真实资产闭包，后续只能靠免费fixture或伪造包。

当前代码的确定缺口：

- Shared/Protocol/Runtime/Business/OnlineOwnedAssetContracts.cs:8–21只有Schema1杖卡字段；Core/Online/OnlineOwnedAssetContracts.cs:12–31也为Schema1，需同时改，不能只改一个同名DTO。
- InventoryService.cs:35–53只查次数/位置并筛位置2/3；OwnedCombatAssetRules.cs:21–38拒绝非零X/Y/旋转及其他位置。
- OnlineOwnedWorkspaceFactory.cs:21/33–38限制99项、只收装备杖和安装卡；64–71随后创建真实Store。因此只加Proto不能让容器入局。
- OnlineInventorySession.cs:10–20还用旧24平面物品＋99执行物的总量上限，必需配套，否则大包在进入Factory前就被拒绝。

上面Unity路径均位于RPG/Assets/_Project/Scripts/；业务路径位于Server/Norn.Business/Features/Inventory/。

## 1. 首切片具体合同

### 1.1 在现有Owned合同新增，不新建第二套库存

建议Owned Schema=2；Shared与Core显式一致，映射仍由Multiplayer/Combat/OnlineOwnedAssetMapping.cs承担。旧Schema1只代表原杖卡数据，不能被解释为“空组分的新容器”；升级启用以整体兼容门禁为准，不能向旧DS发Schema2后静默降级丢字段。

OnlineOwnedItemSnapshot保留InstanceId/ContentId/Revision/ParentInstanceId和原位置字段，新增显式ContainerState：

- ContainerState.SchemaVersion=1；Components为最多8项{MaterialKey:byte, Quantity:ulong}。
- 空容器使用显式空数组，容器缺State/null/未知Schema拒绝；非容器携带State拒绝。
- 容量/mask/背包宽高/形状来自可信中立规则，不能由请求上传。组分键1..8、无重复/零量/未知键，checked求和≤容量且全部符合mask。
- 数据库Item.Revision保留入局原值；当前Run InventoryVersion/Composition.Version另有职责，不能互代。

新增位置支持原稳定编号：EquippedBackpack=4（根父ID空、Owner由Player绑定）、Backpack=1（ParentInstanceId必须是真实装备包）、EquippedTool=8（根父ID空、槽0..6）。X/Y/QuarterTurns按原InventoryShape/ItemOwnershipStore验证；不得把N1平面Slot推为二维坐标。

闭包包含真实装备包及其实际携带子链、工具、原杖卡。旧未支持的携带内容必须明确拒绝/报告，不能通过Where过滤悄悄少带物品。保留原SelectedWandInstanceId；工具/手持选择的运行期投影在后续UI切片接，不把容器ID塞入选杖字段。

### 1.2 后端必须拿到可验证的物品规则

ContentManifest.cs:18–23只有ID/Kind/Name，Resources/Online/GameplayManifest.json只有Hash。首切片需引入中立规则承载并从同一Catalog导出：ContentId、Kind/UseKind、形状占格、包宽高、ContainerCapacity/Mask；绑定同份GameplayHash。可扩现有ContentManifest版本/验证器，不允许后端从Hash猜规则或引用Unity类型。

容量上限从可信配置推导：原99项预算＋真实包本体＋包格内可容纳实体＋7工具；8×8且最少1格时保守171项。完整Profile还包括仓库/legacy，OnlineInventorySession总上限不能误用“入局闭包171”。两个上限分别定界、测试最坏编码和内存，不硬改成无限数组。

## 2. 必需的业务持久入口

**需要追加迁移和真实读取；不需要新增赠送入口。** 001_online.sql:23–39只存Items和Area/Slot；005_owned_combat.sql:7–21只加杖卡位置，没有容器组分或真实网格。

新迁移候选006_container_inventory.sql：

1. 增加真实网格X/Y/QuarterTurns及4/1/8位置/父链承载；调整旧CHECK/唯一键以区分legacy平面位置与真实网格。原(Owner,Kind,Parent,Slot)唯一键不能用于全部包内Slot=-1；格子重叠仍由完整形状候选规则裁决。
2. 增加ContainerState父行/Schema和组分子表，FK到真实Items，子表PK(InstanceId,MaterialKey)、Quantity为BIGINT UNSIGNED。Owner沿Items继承；读写与角色版本在同事务内。状态存在且零子行才表示合法空容器。
3. 不重写001–005脚本/Hash，不将legacy8槽自动展开为8×8，不发包/发瓶/补材料。无法解释的旧布局保留并显式报NeedsReview，不能清档。

读取：InventoryService.ReadSnapshotAsync(:26–57)在一致事务下读取真实位置/组分，组装新的Profile.Items与Owned闭包；FreezeCombatAssets(:60–65)沿原方法冻结Run/Actor/InventoryId并调用扩展规则。RunEntryService.cs:40–56继续角色行锁、版本/Ready检查、不可变EntrySnapshot和AssetLease，不另建第二份可写来源。

此切片没有业务HTTP“上传组分”API。后续玩家整备只提交已有物品/明确目标位置，由后端验证并改变位置；组分未来只能由受信来源或N6结算写入。隔离测试通过既有OnlineFactory/测试Schema准备真实Owner资产即可；不据测试数据宣称生产赠送/商店已实现。

## 3. 复用文件与边界

| 文件 | 首切片责任 |
|---|---|
| Shared/Protocol/Runtime/Business/{ContentManifest,OnlineContracts,OnlineOwnedAssetContracts}.cs | 中立规则、Profile及Owned组分/真实位置合同 |
| Server/Norn.Business/Features/Inventory/{InventoryService,OwnedCombatAssetRules}.cs | 一致读取与完整规则验证；不做注册发放 |
| Server/Norn.Business/Features/Runs/RunEntryService.cs | 复用冻结和租约；原则上只接新完整数据，不改生命周期 |
| Server/Norn.Business/Migrations/新006 | 真实位置/组分持久载体及legacy保护 |
| RPG/Assets/_Project/Scripts/Core/Online/OnlineOwnedAssetContracts.cs | Core值镜像，无Unity/网络实现权限 |
| Multiplayer/Runtime/OnlineInventorySession.cs、Multiplayer/Combat/OnlineOwnedAssetMapping.cs | 控制面总量/字段无损映射 |
| Run/Spells/OnlineOwnedWorkspaceFactory.cs | 完整验证后TryRestore；真实ContainerComposition创建，不发布半份Store |
| Run/Inventory/ItemOwnershipStore.cs、Run/Containers/ContainerComposition.cs | 尽量复用现有父链/形状/容器规范，不另写库存实现 |

本切片**无需新增KCP消息/Envelope/定时调度**：它走已有业务Profile→InstanceReady→Owned→Core控制面。后续客户端读模型需要扩inventory.proto的容器/包状态；玩家阶段才加Place/Drop/Pickup/采集/倾倒意图与收据、共享地面事实。不要在首切片顺手改变旧消息语义或启动完整动作链。

## 4. RED设计（尚未写测试、尚未运行）

| 既有测试文件/新增case候选 | 首先应观察的真实失败与GREEN断言 |
|---|---|
| Server/Norn.Business.Tests/OwnedCombatContractTests.cs | JSON往返真实包+混样容器：当前无组分字段/Schema2；新合同往返保持ID、父链、(X,Y,旋转)、Water/Poison两个ulong数量，显式空瓶不被null替代。 |
| OwnedCombatAssetRulesTests.cs | 当前:23/38会拒绝非零布局/包/工具；Schema2合法闭包通过。外人/缺父包/循环/重叠/未知或重复键/不兼容/超容/溢出全部拒绝；完整输入失败不筛掉坏行假装通过。 |
| OwnedCombatMigrationTests.cs（可新增容器专项文件） | 隔离真实MySQL中追加迁移，旧行/原ID/001–005 Hash保留；同包不同合法位置可存；孤儿State/重复键/负数拒绝；legacy槽不伪装网格；测试不向正式Schema迁移。 |
| OwnedCombatAssetsTests.cs＋RunEntryTests.cs | 当前ReadSnapshot只筛2/3会漏包/瓶，缺Schema应先失败。冻结含真实包与原组分，EntrySnapshot不可变；Lease期间新修改拒绝、原已提交收据仍可重取。无容器账户不生成物品。 |
| RPG/Assets/_Project/Tests/Run/OnlineOwnedWorkspaceTests.cs | 当前Factory:33–38拒绝合法包/瓶。映射后真实Store持有原ID/位置/混样量；空瓶合法；未知State/坏父链/重叠/Hash不符时workspace=null且无半导入；原杖卡资源回归保持。 |
| OnlineInventoryBridgeTests.cs/相关控制面测试 | Shared→Core→Factory数量与ulong边界无损；合法大包不被旧MaxWholeProfileItems错误拒绝，超出明确Profile/Owned各自预算拒绝。 |

断言禁止“旧Schema都通过”或“移除原父链/容量检查让新测试绿”。测试fixture使用已存在的四容器定义及真实Bag定义，不更改正式掉落概率、不模拟玩家已领免费包。未来运行由主Agent在Editor资源安全后集中协调。

## 5. 是否还有获批语义外空白

**首切片没有需要重问的玩法决策。** 数据类型、序列化、规则导出、迁移保护、上限和映射属于已批准目标的技术细化。

需明确保留而不擅自补的边界：

- 普通新线上账号目前无真实包/瓶；获得首份长期资产的生产渠道、是否任何注册赠送未批准。本切片读取已有合法资产，隔离fixture可验证技术链，不虚称新账号完整获取闭环。
- legacy平面库存的迁移是技术审计问题，不能静默制造包或任意摆放丢失物品；坏布局显式报告，正常已持有物品ID保留。
- A带入→丢地→B拾取已获批，后续DS必须区分OriginPlayer/EntryRevision与CurrentRunHolder；但持久转让/死亡/个人撤离/崩溃结算尚属N6。本切片不改EntrySnapshot/DB Owner来提前实现N6。
- 首段P7容器生成复用原表；不新增P8掉落、概率/预装材料或直接交易UI。F/H/G/K阶段边界按批准内容执行，不碰计时实现。

**本轮实际完成仅只读设计与staging文档。所有RED/GREEN、迁移、编译和玩家路径均未实施/未运行。**
