# 单机容器既有来源只读审计（2026-11-01）

范围：源码、磁盘正式 Scene/Prefab/SO、已有测试记录。未启动 Unity、测试、服务、构建或前台操作，未修改仓库、协议或真实存档。两层 AGENTS、RPG/CLAUDE.md 已读；已有 dirty 内容保留。默认 exec 因 split writable roots 配置拒绝启动；require_escalated 只读调用获准，无未决权限阻塞。

以下 A=RPG/Assets/_Project/，路径均相对 F:/Develop Project/RPG。

## 已实现且正式接线

1. **可以在正式单机商店购买采集瓶。** A/ScriptableObjects/World/Hub/Trading/Shop.asset:85–95：OfferId/ContentId=`containertest.bottle`，买20、卖6、库存5。ShopId 虽叫 sandbox.shop，正式 SinglePlayer_Hub.unity:767704–767706、SinglePlayer_Home.unity:28403–28405 都引用该 SO（GUID 5800ce341bdfc984ab3dfef5d783a150），不能因命名判为测试工具。商店当前没列 `item.container.container_01/02/03`。`containertest.ice` 虽在相邻商品中，实际 Kind=4(Material)，不是容器。

2. **新档已发两只带水采集瓶，位置是仓库。** A/ScriptableObjects/World/Hub/Inventory/StartingInventory.asset:74–91：两项 Definition GUID 82eb63dcaae464344b954178ca82b7d0，各 MaterialKey=1、Quantity=10000、Location=0(Warehouse)。GUID 对应 Items/Containers/Definitions/Item_Container_Bottle.asset.meta:2，ContentId 见 .asset:15。正式 Hub:767620、Home:28327 引用此 StartingInventory。Scripts/Run/Runtime/SinglePlayerInventorySession.cs:220–226 只在读取结果 Missing 时调用新档模板；读取失败不赠送，已有档不会补两瓶。Scripts/Run/Runtime/SinglePlayerStartingInventory.cs:47–58 创建真实组分并验证容量。

3. **P7 敌人已能真实掉容器。** 正式 A/Scenes/P7_DemoRun.unity:24418–24428 接 DungeonRunContent、四关和四张 StageLoot。A/ScriptableObjects/World/Dungeon/Stage1Loot.asset 至 Stage4Loot.asset 各:66–77 相同容器池：采集瓶、Container_01/02/03；条目概率0.12、数量1，池内等概率抽一种。不是每只怪必掉，也不是每种12%。Scripts/Run/Hub/PracticeInventoryWorkspace.cs:33–35 绑定；Scripts/Run/Loot/DungeonRunContent.cs:35–62 校验 Catalog 并给各敌人挂 Adapter；DungeonLootDropAdapter.cs:71–102 首次真实死亡抽样，:110–117 忙时重试同一结果。SinglePlayerInventorySession.cs:768–797 创建独立 InstanceId、WorldItem 归属及代理。ItemOwnershipStore.cs:215–219 将新容器规范化为空组分，因此掉落表的 MaterialQuantity=256 不会自动装入容器。

## ContentId 与获取边界

| 稳定 ContentId | SO（A/ScriptableObjects/Items/Containers/） | 容量GMU | 当前单机来源 |
|---|---|---:|---|
| containertest.bottle | Definitions/Item_Container_Bottle.asset:15,33 | 15000 | 新档仓库两瓶水、商店空瓶、P7掉落空瓶 |
| item.container.container_01 | Item_Container_01.asset:15,34 | 15000 | P7掉落 |
| item.container.container_02 | Item_Container_02.asset:15,36 | 30000 | P7掉落 |
| item.container.container_03 | Item_Container_03.asset:15,38 | 60000 | P7掉落 |

四项已注册同一 Catalog：A/ScriptableObjects/World/Hub/Inventory/Catalog.asset:23,26–28；SO GUID 分别 82eb63dcaae464344b954178ca82b7d0、4dc60ead7e217804596d585f45d165cd、db7b59e8d8665304c8d92d38cd48b089、560e77355094d474d828d687fbd845fe。

三只 A/Prefabs/Items/Container_01/02/03.prefab:47–51 已带 WorldItemSpawnPoint 和对应 Definition，但原 Prefab SpawnId 为空。WorldItemSpawnPoint.cs:34–43 明确要求非空唯一 SpawnId。本轮查 Hub/Home/P7/P8 正式 Scene 及 SinglePlayer Prefab 未见三只容器 Prefab/GUID 的固定摆放引用；不能把 Prefab 存在当地图固定供给。P7随机死亡掉落的正式接线则已确认。固定四关布局里的八个 Explore material 节点是普通材料/食物等，不是八个容器。P8 沿 Boss 既有流程（PracticeInventoryWorkspace.cs:33），未查到 P7式容器掉落绑定；最终奖励 DungeonFinalRewardPickup.cs:69–79 是有限法术，不发容器。研究台与 ContainerSandbox 不属于这里的正式获取来源。

## 库存、进出副本和组分

购买走 Scripts/Run/Trading/ShopTradeService.cs:91–115：创建唯一实例，优先入请求背包，按请求允许程度回仓库；:32–52 先保存完整候选再采用。SinglePlayerShopSession.cs:19–21 限 HubReady 且当前 Shop 站点，:56–62 提交并 Adopt。满包且不允许回仓库会失败。

HubPracticeSession.cs:179–206 出门先保存；:286–304 同一 Repository 重载，回 Hub 恢复原资产；:323–328 在 P7/P8 创建同一份携带资产工作区。SinglePlayerInventorySession.cs:159–162 副本禁仓库访问，所以初始仓库瓶需先放到携带背包。退出 :799–845 只清本图未拾取地面物和已投出物，已拾取容器/剩余组分随原实例保存，失败不换图（HubPracticeSession.cs:219–224）。死亡清除随身内容/装备但保留装备背包本体、仓库、钱包（Scripts/Run/Hub/PracticeDeathSettlement.cs:5–17）。

WorldItemPickup.cs:93–115 执行时重新验证距离/Scene/归属，再迁移同一实例；不是截图或附近列表直接发物品。ContainerComposition.cs:9–20 为 ulong 数量/容量与材料mask，:29–58 控制增减；四容器 mask=410（键1/3/4/7/8）。ProfileSaveMapper.cs:28–42 保存组分/容量且跳过投出物，不能切图补满。

## 在线与验证范围

Online/ContentManifest.json:8–35 已注册四容器 ID，但不是完整在线容器玩法。Scripts/Run/Spells/OnlineOwnedWorkspaceFactory.cs:7,33–38 当前执行投影仅装备杖/安装卡闭包；Inventory/OnlineInventoryPresentationSource.cs:231–237 仅接受装备杖/安装卡/法术专区，:273–274 TryCopyContainer 恒 false。不能把本地新档瓶、商店或 P7单机掉落直接当在线初始资产，更不能因此修改资产协议。此处是边界审计，不是新方案。

历史：RPG/TestResults/SinglePlayer/M6-T18-Dungeon/final-verification.md（2026-10-14）记录四类真实掉落、忙时固定抽样、离图保留、56个去重 PlayMode 通过和作者保存正式 P7；同时明确非人工战斗通关。本轮实际读取 editmode-green.xml：61 passed/0 failed/0 skipped，结束 2026-10-13 18:05:27Z，属于历史证据。现有 DungeonEconomyPlayModeTests.cs:103、119、170 对应死亡、回程、忙时测试；回程专项明确断言的是法术与钱包，不能扩大成本轮容器全链路已复测。当前版本编译、购买体验、容器跨图/在线使用均未重新运行。

建议仅为后续方案基线：保留上述稳定ID与已有三条单机来源，在线补接时复用真实归属/组分事务；方案确认前不另造免费补发、研究台发瓶或新商店。
