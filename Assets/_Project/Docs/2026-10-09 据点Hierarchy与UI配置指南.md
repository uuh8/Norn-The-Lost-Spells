# 据点 Hierarchy 与 UI 配置指南

## 2026-10-10：物品与连续拖拽（当前入口）

`__UI/WorldPickupPrompt_附近拾取`是共享世界提示，Entry的背景引用PickupItemEntry.png；Script的Screen Width、Screen Offset可调整可读尺寸和位置，保持正向Billboard。普通背包GridViewport新增InventoryDragAutoScroll，Edge Size/Speed调整拖拽边缘自动滚动。三杖Fold已经移除，下方旧折叠描述仅属于历史版本。

纯模型位于Art/Prefabs/Item，实际Scene摆放应使用Prefabs/Items的业务包装。投放点建议放到`__Gameplay/WorldItems_可拾取物品`，每个点有独立SpawnId；完整形状、生成ID、Catalog、隔离Scene和按键教程见[物品配置指南](<F:/Develop Project/RPG/RPG/Assets/_Project/Docs/2026-10-10 物品定义与场景拾取配置指南.md>)。正式F拾取，E交互/采集，4–9/0选择道具，G投掷。

## 2026-10-10：实际交互区域与背包大格（最新入口）

商店、仓库和实验区在`__HubInteractions`下各自多一个Area子物件，使用BoxCollider的Edit Collider配置范围。完整步骤、Local Space缩放解释与物品系统建议见[据点交互区域配置与物品系统建议](<F:/Develop Project/RPG/RPG/Assets/_Project/Docs/2026-10-10 据点交互区域配置与物品系统建议.md>)。不要再以HubSpatialLayout的旧2m半径调整这三个正式区域。

背包GridViewport里的InventoryGridView现在Cell Size=72、Fit To Viewport关闭，横纵Scrollbar处理溢出；左列容量文字独占顶行。下文“网格内容适配Viewport”是旧状态；当前普通包容量仍由装备定义8×8决定，视觉尺寸不决定资产空间。中心准星在PlayerHUDCanvas_统一手持栏下；商店与仓库的Canvas根保持启用，只隐藏内部Panel。关闭整个根会停掉其控制器的事件订阅，导致E请求成功却打不开面板。交互提示Canvas可以继续隐藏。

## 2026-10-10：用户重排后的背包层级（当前配置入口）

下面取代后文旧“背包/仓库合并”层级示例。你已有的LeftPart/MiddlePart/RightPart外框保持；新增控件都在对应内层，不在Update改父面板。

```text
__UI
  InventoryCanvas_背包
    InventoryWorkspaceRoot_背包
      LeftPart
        BackpackRegion / GridViewport / Content（InventoryGridView）
        NearbyItemsRegion / ... / Content（NearbyItemsView）
        GoldRegion / Gold_Number（实际钱包）
      MiddlePart
        WandProgrammingRegion
          BackpackRuntime_Wands / Viewport / Content
            Wand1 / Attributes、Fold、Expanded / Program / Viewport / Content
            Wand2 / ...
            Wand3 / ...
        Spells（你新增的黄色区域，实际父层级以当前Scene为准）
          SpellStorageRuntime_Viewport / Content（SpellStorageView，运行时12格）
        InventoryBar / BackpackRuntime_Tools / Slot4 ... Slot9、Slot0
      RightPart（你现有内容，本轮保留）
  InventoryCanvas_仓库（独立保留，不绑定Tab）
  ShopCanvas（保留）
  PlayerHUDCanvas_统一手持栏 / HeldItemRow_1至0 / Slot1 ... Slot9、Slot0
```

改左右/中间边界：调你原RectTransform；改网格可见范围：GridViewport边距；网格内容适配Viewport、实际8×8来自bag/StarterBag定义；不要用UI尺寸直接改变资产容量。三杖标题位置/字号在Attributes，折叠按钮在Fold；每根程序槽由WandProgramPanelView按真实容量创建，背景可改prefab样式，不能手工删序号模拟缩容量。十格HUD的位置在HeldItemRow，整体缩放由独立CanvasScaler控制。道具绑定在运行时拖真实可使用物品，不在Inspector填显示图标当作配置；当前已实现使用能力的物品是容器。

黄色区域的十二格是额外真实储存位置，容量来自ItemCatalog的Spell Storage Capacity，UI宽度只决定卡片可视范围，窄时横向滚动。七道具位对应ToolbeltView的Slots[0..6]，最后一位显示0；它们引用普通包里的物品，不扩大背包空间。HUD共有十位，最后0对应手持索引9。Spells外框、七个用户道具格及右侧面板几何没有重排。正式Scene本批已单Undo接线但尚未保存，请你自行Ctrl+S后试玩；测试截图中的248金币和测试法术属于内存测试Profile，没有写入正式玩家资产。

旧WandRegion/WandDetails和旧WandResourceHud保留但停用，方便Undo与追踪；不再作为新背包配置入口。旧“接线背包左中与九格HUD”菜单属于上一批，不用于本版。新增作者菜单“接线12法术位与七道具位（Undo，不保存）”只做补充接线；当前已应用，不需要再点击。所有运行时动态槽只在Play看到，不会写回用户Scene。下文第1–4节保留为最初Hierarchy整理记录，布局冲突时以上方当前配置入口为准。

本轮让正式据点的作者层级接近 P7：在编辑状态一眼找到系统、角色、地图、UI，不改变输入、资产、传送和界面画面。Scene 是 `Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity`。主要界面仍然是 UGUI，使用 Screen Space Overlay Canvas；每个 Canvas 保留自己的 CanvasScaler、绘制次序和业务组件。不要为了层级整齐把不同 Canvas 的子物件直接混到一个 Canvas 中。

## 1. 从哪里找界面

```text
SinglePlayer_Hub
├─ __Systems                         据点系统与 EventSystem
├─ __Gameplay                        只含 WizardPlayer，出发时一起搬迁
├─ __Level                           地图、灯光、ReflectionProbes
├─ __Cameras                         实际相机与 Cinemachine 相机
├─ __HubInteractions                 仓库、商店、实验平台、三个入口
├─ __ElementWorld                    本次据点的材料世界与表面 Renderer
└─ __UI
   ├─ InventoryCanvas_背包法杖与仓库
   │  └─ InventoryWorkspaceRoot_背包与仓库
   │     ├─ WarehouseRegion_仓库
   │     ├─ BackpackRegion_携带背包
   │     ├─ NearbyItemsRegion_附近物品
   │     ├─ WandProgrammingRegion_法杖编程
   │     │  ├─ WandEquipmentSlots_三根法杖
   │     │  ├─ WandStats               所选法杖属性文字
   │     │  ├─ Program                程序槽的 ScrollRect
   │     │  ├─ Available              可用法术的 ScrollRect
   │     │  ├─ Tools                  背包真实道具的投影
   │     │  └─ LegacyInventoryPanel_旧绑定载体（保留）
   │     └─ ItemDetailsRegion_物品详情
   ├─ PlayerHUDCanvas_法杖状态
   ├─ ContainerHUDCanvas_容器状态
   ├─ ShopCanvas_据点商店
   │  └─ ShopRoot_商店界面
   └─ InteractionCanvas_交互提示
```

背包与仓库是**同一套库存工作区的两种模式**：Tab 打开随身背包，附近物品可见；靠近仓库按 E，仓库列表可见，其他列重排。分别标出区域方便编辑，不创建第二份库存。商店是独立 Canvas，靠近商店按 E 打开。

也可以用 `Tools → Norn → 据点界面` 的“选择背包区域 / 仓库区域 / 附近物品区域 / 法杖编程区域 / 商店面板”菜单直接定位；菜单仅选择与 Ping，不改激活状态、布局、库存或暂停。

## 2. 怎样编辑，以及目前的边界

在非 Play Mode 下展开 `__UI`，选择对应面板或区域；需要预览时激活总面板，Scene 视图切到 2D 后按 F 聚焦。编辑完成由你自行保存 Scene。正式当前 Scene 的整理没有自动写盘；若尚未保存，Ctrl+Z 可撤销整组整理。不要在 Play Mode 编辑后指望场景改动自动保留。

| 要调整的内容 | 作者入口与注意事项 |
| --- | --- |
| 面板底图、颜色、边框、标题字体 | 区域自己的 Image / Text；可直接在 Inspector 修改。当前正式启动仍有统一中文字体设置，Text 的 Font 会被相关业务组件刷新；不要据此认为单个 Text 的 Font 永久生效。 |
| 总面板、商店按钮、标题、详情等静态控件的 RectTransform | 对应 Canvas 和面板中的子物件；ScrollRect 的 Viewport 决定可视裁切，Content 决定可滚动内容范围。布局组件会驱动的属性不能靠手动拖动长期覆盖。 |
| 背包/法杖两列的位置与尺寸、程序/法术/道具分区的模式位置 | **当前由 InventoryWorkspaceView.Arrange 在打开时重排**。现阶段单改这些 RectTransform 会被覆盖，不能当成已支持自由作者布局。后续应把两种模式的坐标开放成 Inspector 配置，再让代码读取；本轮没有扩大成布局系统重构。 |
| 格子大小、物品外观 | 背包的 `GridViewport/Content` 有 InventoryGridView 的 Cell Size；卡片使用 SinglePlayerInventoryController 的 Item Prefab。此 Prefab 是共享资产，修改会影响其他使用者。实际格子和物品按当前装备背包、形状、旋转及库存生成，编辑状态可能为空。 |
| 三个法杖格、程序槽、道具条 | 对应区域的 Content 与 WandInventoryView、InventoryWorkspaceView；真实槽和卡片在运行时生成。部分尺寸仍由 Refresh 中的布局逻辑控制，修改运行时 Clone 不会保存。 |
| 商店商品、可出售物品、背包 | ShopRoot 下的 `Offers`、`Owned`、`ShopBagViewport`；数据来自真实商店与库存，EntryTemplate 是运行时列表的模板，不是额外商品。 |
| 屏幕提示、准星、法杖 Mana | InteractionCanvas 的 Prompt、PlayerHUDCanvas 的 Crosshair / Wand1～Wand3。数值和交互文本由运行逻辑更新，作者只负责容器、字体样式与屏幕排布。 |

右侧的保护与材料附着控件按所选物品及场景状态显示；隐藏的旧面板仍挂着现有 WandInventoryView，并保留其他序列化绑定，不能直接删除。`Program`、`ProgramTitle`、`Available`、`PaletteTitle`、`ToolsTitle` 等内部名字目前用于 Runtime 查找，暂时保留；主要区域已经使用真实组件引用定位，可读名字不参与库存识别。

## 3. 系统与生命周期为什么这样分组

`__Systems/SinglePlayerInventory` 是现有据点逻辑的承载对象：SinglePlayerInventorySession 持有真实资产与保存入口，SinglePlayerShopSession 管理交易，HubInteractionController 判断玩家距离和站点，SinglePlayerRunCoordinator 负责出发/返回场景，HubExperimentWorldLifetime 管理离开据点释放材料世界、返回重建空世界。分组只是作者导航，没有新增写入口或跨 Assembly 引用。

输入由 WizardPlayer 的既有 P7 Controller 与 Input System 接收；Tab/E 分别进入背包与近距离交互，EventBus 把命令/状态交给库存、商店和 UI。UI 持有已序列化的组件引用，重命名 GameObject 不改变它的引用身份。Editor 整理使用 `Undo.SetTransformParent` / `SetParent(..., true)` 保留世界 Transform，新父节点为单位 Transform；同时核对 UI 世界四角，避免肉眼看似整齐却移动了画面。

出发代码使用玩家的 `transform.root` 搬迁对象，因此 `__Gameplay` **只能放玩家**，不要把库存、Canvas、站点、材料世界塞进去。`__ElementWorld` 是真实材料根对象，离开后会释放；返回时生命周期组件会创建新空世界，现有 Runtime 会将其命名为 `Container Material World`。这是重建后的新实例，与库存持久化无关。不要为保持名字一致而让材料跨冒险保留。

## 4. 作者工具与回归

`SinglePlayerHubHierarchySetup` 位于 Editor Assembly，只在显式请求或菜单执行。新建正式据点的装配末尾也调用分组；旧对齐/布点/反馈工具已支持查找子层级。整理工具本身不重新装配地图、不修改站点数据、不执行旧 UI 排版，不删除未知用户对象，也不保存用户当前 Scene。重复调用不新增重复分组；非单位或未激活分组会明确报错，避免悄悄重置用户自定义状态。

本轮的验证层次与结果见同目录验收记录及 `TestResults/SinglePlayer/M6-T1-Hierarchy`。该目录的 before-tree / review-tree / current-tree 是实际 Editor 层级导出；prepared / current-applied 记录对象位置、组件引用、Canvas 次序、重复整理与正式磁盘 hash。画面与操作是否适合你的手工配置，仍需你在 Editor 确认。
