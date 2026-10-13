# Slot 独立 Prefab 与法杖作者布局保留计划

> 用户已授权写完计划直接执行。本轮使用 executing-plans 自行实施；不使用 SubAgent、不 Commit、不自动保存或丢弃正式 Scene。

## 1. 目标与证据

玩家操作与库存规则保持不变，美术编辑能够持久生效。背包形状占格、附近物品形状展示、仓库单类方格、法杖程序、独立法术储存、道具栏、仓库法杖选择卡分别使用独立 Prefab。作者可以修改其中的背景、Frame、Icon、Caption，不能再被通用 InventoryItemView 的颜色与硬编码子布局覆盖。

证据：WandProgramPanelView.Refresh 每次调用 ConfigureIconLayout，强制 Icon 的 anchors/pivot、position=(12,-8)、size=(200,64)，修改 Attributes.offsetMin.x=224。Content 的 VerticalLayoutGroup 主要控制直属 Wand 行；这段代码才是覆盖子对象编辑的直接原因。旧 Editor 安装入口也调用同一方法，需要一起停止重排已经存在的 Icon。动态法术数量仍需调整 Program Content 的宽度，但不调整作者的 Attributes、Frame、WandIcon、Expanded 与父行位置。

## 2. 设计、取舍与资产

- Game.UI 新增 InventorySlotPrefabs（ScriptableObject）仅保存表现模板引用；新增 InventorySlotView 保存程序槽/选择卡的语义组件引用。真实库存仍由 Game.Run 管理，跨模块命令不变。
- Prefab 保存在 Assets/_Project/Prefabs/UI/InventorySlots/。空背景、形状物品卡、仓库物品卡、程序槽、法术储存槽、法杖选择卡、背包与内嵌道具槽分别独立，不用同一个 Prefab Variant 父级联动全部样式。
- 每种形状物品卡中预留 ShapeVisual（映射最大连续实心区域）、IconRotation（跟随物品四分之一旋转）、Icon/Caption 以及禁用的 OccupiedCellTemplate。代码只计算形状几何父层，Icon/Caption 的局部 anchors/offsets/颜色由 Prefab 控制。固定槽没有形状映射，子坐标完全保留。
- 不用“只抽成 Prefab、随后 Bind 仍重写”的假分离；新模板开启作者布局模式，只绑定 Sprite、文字、选中与显隐。旧测试/未迁移模板保留兼容路径，不能一刀删除仍被旧场景引用的分支。
- 根 Slot 的坐标与尺寸仍由容量、Grid CellSize 或 LayoutGroup 驱动，这是网格连续填充与正确 Hit Test 的必要条件。美术应设计槽内子对象和背景，不用根坐标表达间距。
- Controller 可显式引用模板集。兼容现有已保存场景时，首次访问加载唯一 Resources/InventorySlotPrefabs 资产并缓存，避免为接线自动保存场景；UI 模板资源是小型必需资产，代价是 Resources 始终入包。所有模板内部使用显式引用，避免运行时搜索对象名字来决定行为。
- 复用现有作者 UI Sprite、字体与 Frame_Selected；不需要用户提供新模型、纹理、Shader、音频。程序槽沿用当前深色底与数字。Editor 通过 PrefabUtility/AssetDatabase 生成资产与 meta，禁止手写 meta，不覆盖已有精修的同名模板。

## 3. 实施步骤与文件

- [x] RED：新增 Tests/RunPlayMode/InventorySlotAuthoringPlayModeTests.cs，模拟作者改 Attributes/Frame/Icon 后打开、重复刷新及装备交换，验证所有局部 RectTransform 保留；验证各类模板真实存在且程序/储存槽和仓库使用独立模板、内容子布局样式保留。已记录anchors和缺失Prefab的RED，另记录Caption层叠顺序RED。
- [x] 修正 Scripts/UI/Inventory/WandProgramPanelView.cs；清除 Editor/SinglePlayerBackpackSetup.cs、SinglePlayerWandPortalBossSetup.cs 的旧重排入口。已有 Icon 只补组件/绑定，不调整位置；缺失旧图标仅在安装时创建默认布局。
- [x] 新增 InventorySlotPrefabs.cs、InventorySlotView.cs；修改 InventoryItemView.cs 的作者布局绑定与形状父层；InventoryGridView.cs、NearbyItemGridView.cs、WarehouseCatalogGridView.cs 使用独立背景/物品卡。拖拽预览恢复 Prefab 原色。
- [x] 修改 WandProgramPanelView.cs、WandInventoryView.cs、SpellStorageView.cs 使用程序槽/储存槽/选择卡 Prefab；移除模板模式下的硬编码 Icon/Caption 样式和子 Rect 写入。ToolbeltView.cs、ToolbeltSlotView.cs 用独立模板替换表现，保留每个原槽根布局、SlotIndex 与真实拖出/拖入/容量显示。
- [x] 新增 Editor/SinglePlayerInventorySlotPrefabSetup.cs：显式请求 prepare，复制作者已保存 Hub 到独立 Review，只在 Review 创建/截取模板；不把正式三地图叠加、不修改/保存正式 Scene。模板保存后已有 Hub/正式远征/P7/P8/商店内嵌库存通过统一 UI 配置使用。旧安装工具不再每次重排子对象。实际十一Prefab生成，共享PracticeInventoryWorkspace从作者Hub克隆。
- [x] GREEN：更新旧 WandPresentationPlayModeTests 中“必须宽200”的过时断言，改为使用作者保存宽度；最终88项工作区/练习UI、8项旧UI兼容、1项Multiple Sprite实际选框用例通过，共97个不同PlayMode用例。三个60帧Profiler窗口相关marker为0 GC。原倒料测试未瞄准实验域，补Camera设置后复测通过，未放宽游戏域外拒绝规则。Selected Frame由TextureImporter子资产读取，补齐真实资产接线。
- [x] 更新单机箱庭 living explainer、验收记录与 Prefab 作者入口表；保存测试结果至 TestResults/SinglePlayer/M6-T14-SlotAuthoring，最终恢复单独已保存 Hub。详情见final-verification.md，M6整体继续更新中。

## 4. 验证、Editor 与 Debug

先通过当前 Editor 官方 Runner 记录新增测试 RED，再实现、生成模板并验证 GREEN。任何 Dirty 正式 Scene 都保留，不自动保存。回归重点：形状旋转与空洞点击、程序插入/交换、仓库池化滚动、容器 x/x、七位道具 SlotIndex、不同模板不互相染色。Profiler 指定 marker 0 GC；Editor 全帧本身的分配不能冒充本功能分配。

作者入口：Project/Assets/_Project/Prefabs/UI/InventorySlots；双击 Prefab，修改 Sprite/Color/子 Rect，退出 Prefab Mode 保存资产，重新打开库存查看。当前已生成的 Play 实例不是作者入口。法杖行继续在正式 Scene 编辑 Attributes、Frame、WandIcon；VerticalLayoutGroup 只管行级，ContentSizeFitter 只调整 Content 高度，父行尺寸改变仍会影响 Stretch 子对象的最终矩形，但代码不改作者局部配置。

失败先查 Controller 模板集引用、Prefab 的 Card/Target/Icon/Caption 指向、再查祖先 LayoutGroup 或 AspectRatioFitter；禁止以 LateUpdate 反复覆盖作者坐标纠正。回滚只撤销本轮代码/新增模板，保留作者已保存 Scene 与旧模板 GUID，不 reset/clean。
