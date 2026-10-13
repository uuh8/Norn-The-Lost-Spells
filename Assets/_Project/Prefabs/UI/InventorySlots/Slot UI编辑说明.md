# Slot UI 编辑入口

所有模板在 `Assets/_Project/Prefabs/UI/InventorySlots/`，双击对应 `.prefab` 进入 Prefab Mode 编辑，再保存资产。不要修改 Play 临时生成的 Grid cell / SpellStorageSlot 实例，那些修改退出 Play 会消失。

| 要设计的部分 | Prefab |
| --- | --- |
| 背包空格背景 | BackpackCell.prefab |
| 附近物品空格背景 | NearbyCell.prefab |
| 仓库目录空格背景 | WarehouseCell.prefab |
| 背包中实际物品的图标、数量/容量、占格填色 | BackpackItem.prefab |
| 附近实际物品的图标、次数、占格填色 | NearbyItem.prefab |
| 仓库方格里的物品图标、名称、数量/容量 | WarehouseItem.prefab |
| 装备法杖内的程序槽（包括空槽、法术 Icon/Caption、序号） | WandProgramSlot.prefab |
| Spells 中独立法术储存槽 | SpellStorageSlot.prefab |
| 仓库三根法杖选择卡 | WandSelectorSlot.prefab |
| 背包底部 4–0 道具槽 | BackpackToolSlot.prefab |
| 配置了紧凑 4–0 道具行的工作区（现有仓库已删除该行） | WorkspaceToolSlot.prefab |

模板集为 `Assets/_Project/Resources/InventorySlotPrefabs.asset`。可在 Inspector 单独替换每种 Prefab 引用，修改一种不会改变另一种。Controller 的 Slot Prefabs 为空时自动读取默认集；Hub、正式远征、P7/P8 以及商店内嵌库存均走同一套组件。当前无需保存场景接线。

快捷法术栏的**整体内边距**在 Scene 的 `Spells / SpellStorageView / Inner Padding` 调整，X 为左右、Y 为上下，默认均为 8 个 Canvas 参考单位。外框/原 Viewport 的位置保留；Play 时在 Viewport 内生成 `SpellStorageRuntime_InnerClip` 并把 Content 放进去，方格上下居中。编辑 `SpellStorageSlot.prefab` 负责单个槽的样式；编辑 Inner Padding 负责整条栏和装饰框之间的距离。12 个真实槽不会因内边距或可见列数变化而减少。

## 哪些参数归美术，哪些归库存几何

- **固定槽**：编辑背景 Image、添加自己的 Frame 子对象，编辑 `Item/Icon`、`Item/Caption`。Runtime 只绑定 Sprite、文字与显隐，不修改这些子对象的 RectTransform、颜色、字号、对齐。`WandSelectorSlot` 另有 `Item/WandResource`、空位提示和 `Frame`；选中 Sprite 可在 InventorySlotView 的 Selected Sprite 中替换。
- **形状物品**：编辑 `ShapeVisual_RuntimeGeometry/IconRotation_RuntimeGeometry/Icon` 和 `ShapeVisual_RuntimeGeometry/Caption`。ShapeVisual 的根矩形由真实形状实心区域决定；IconRotation 根负责四分之一旋转。修改它们下方 Icon/Caption 的局部 anchors、offsets、Sprite/Color、字体，代码会保留。正常/选中/错误轮廓颜色在 InventoryItemView Inspector 配置。
- **占格填色**：`OccupiedCellTemplate` 是禁用的模板子对象，修改其 Image 的 Sprite/Color。保持其 GameObject 禁用，Runtime 会按真实实心格复制并启用，空洞不会填上。
- **法术次数**：形状物品的 `ShapeVisual_RuntimeGeometry/SpellCount`、仓库卡的 `SpellCount`、程序/储存槽的 `Item/SpellCount` 专门显示有限次数，默认左上角28号 Bold 白字与黑色 Shadow。无限次数不显示。直接设计这些 Text 的 anchors/字号/颜色，Runtime 保留；不要删除 InventoryItemView 的 Spell Count 引用。普通 Caption 继续用于容器容量和其他物品，法术卡隐藏它。
- **根 Slot**：它的位置和尺寸由库存坐标与 CellSize 管理；法术储存槽和道具槽由各自 View 排成正方形。储存真实12位保持不变，区域宽度不足时少显示几个完整方格，横滚看其余；4–0 永远有7个真实位置。不要通过缩小根格子制造网格间距；用子 Frame 的 Stretch、offset 和 Sprite Border 设计边框，才能继续保持正确 Hit Test。
- **仓库法杖选框**：`WandSelectorSlot` 的 Selected Sprite 已改为 `Art/UI/Function_UI/icon_frame_square_elite.png`；手持栏继续使用自己的 Frame_Selected，两者分别配置。
- **紧凑道具样式**：WorkspaceToolSlot 的根 Image 使用独立装饰边框，Icon/Value 子对象仍可单独编辑；与上方连续填色网格区分。ToolbeltView 仅计算七个动态槽根的等边尺寸，不改你编辑的子对象。
- **组件引用**：保留 InventorySlotView 的 Target/Card/Index/Empty/Selection、InventoryItemView 的 Icon/Label、ToolbeltSlotView 的 Icon/Label 引用。可以重命名子对象或增加装饰，移动/替换引用对象后要在 Inspector 重新拖入引用；运行时不靠子对象名字猜测含义。
- **字体**：未提供字体或 Unity 默认 LegacyRuntime 时，物品文字使用当前中文 Font；明确提供的自定义 Font 资产会保留。字号、Best Fit、颜色、对齐仍归 Prefab。

## 三根法杖行继续在 Scene 编辑

`InventoryCanvas_背包/InventoryWorkspaceRoot_背包/MiddlePart/WandProgrammingRegion/BackpackRuntime_Wands/Viewport/Content/Wand1（或 Wand2/Wand3）` 下的 `Attributes`、`Frame`、`Frame/WandIcon` 保持作者 Scene 排布，**不会为了抽 Slot 而替换这些行**。

Content 的 Vertical Layout Group 管直属 Wand 行，Content Size Fitter 改 Content 的高度。父行尺寸改变时，Stretch 子对象的最终矩形自然改变，这是 anchors 的作用；局部 offsets/anchoredPosition 不应被重设。本轮已移除以前每次 Refresh 强写 200×64、(12,-8) 和 Attributes 左边距224的代码，也停止旧安装工具重排已存在的 Icon。

不用重新运行旧的背包/仓库安装菜单。新的 `Tools/SinglePlayer/UI/Prepare Independent Slot Prefabs` 只补缺少的模板，保留已有同名 Prefab；需要干净非 Play Scene，从已保存 Hub 生成独立 Review 与练习图共享 Prefab，**不保存正式 Scene**。单纯精修现有 Prefab 不需要重新生成。

## 容器容量颜色（M6-T17）

BackpackItem、NearbyItem、WarehouseItem、BackpackToolSlot、WorkspaceToolSlot 的 Icon 下新增 `CapacityFill`。修改它的 **Image.color** 可分别设计容量颜色；`Filled / Vertical / Bottom` 和 `fillAmount` 由数据表达储量，运行时保留作者 tint。原 Icon 的 Rect、Sprite、Frame 和拖拽区域不改。不要删除此层后用容量文字替代；其共享 Material 是 `Assets/_Project/Resources/ContainerIconFill.mat`，Shader 通过图像 alpha 保留容器轮廓、用亮度保持细节。零容量隐藏填充，半满只填下半部分，满瓶整图着色。

元素报价在 `Assets/_Project/ScriptableObjects/SinglePlayer/Hub/SinglePlayerTrading/Shop.asset` 的 Offers/Icon 单独换素材；采集的冰在 SinglePlayerContainers/ice.asset。冰球的图标应引用 PNG 中真正的子 Sprite，Multiple Sprite 下不能引用原 Single Sprite 的旧 fileID。

交互提示在正式 Hub 的 `Nearby pickup list/Entry 1…5/F and item name` 编辑。Panel/每行/文字的 Rect 不再被 NearbyPickupListView 改写；Text 的 **Align By Geometry** 解决字体 glyph 与 typographic line height 不一致造成的视觉偏下。站点/门共用 Entry 1，附近物品复用其余行，旧 `Station entry` 已淘汰。F 优先站点/门；移出站点区域后用于拾取。
