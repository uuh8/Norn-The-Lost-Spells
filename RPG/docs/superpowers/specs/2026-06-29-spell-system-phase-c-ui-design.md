# 法术编程系统 · 阶段 C：拖拽编程 UI 设计规格（Design Spec）

> 日期：2026-06-29
> 状态：设计已与开发者对齐，待写实现计划
> 前置：阶段 A（求值内核）+ 阶段 B（接运行时，左键运行法杖）已合入 main。本阶段加"玩家用 UI 拖拽编辑法杖"。
> 关联：总系统 spec `2026-06-28-spell-programming-system-design.md`（第 6.4 节预告了本阶段的分层张力）。

---

## 1. 目标（一句话）

做一个仿 Noita 的**法杖编程界面**：上方一条"法术调色板"（玩家拥有的法术），下方一个"法杖编程框"（从左到右的法术序列）；玩家**拖拽**把法术放进/移出/重排编程框，改动**实时写回现有 `WandLoadout`**，下次左键开火即按新序列运行。

## 2. 范围

### 做（MVP）
- 一个可开关的全屏覆盖**编程界面**：按键开关、打开时暂停游戏、解锁鼠标。
- **法术调色板（背包）**：从一个 `SpellLibrary` 资产铺出玩家拥有的法术，可拖。
- **法杖编程框**：固定容量的槽位，绑定现有 `WandLoadout`；支持从调色板拖入、框内拖动重排、拖出移除。
- **无限调色板模型**：调色板始终显示全部可用法术；拖进框 = 放一份（同一法术可重复放）；拖出框 = 删除。
- 改动**实时写回 `WandLoadout.Spells`**，无"应用"按钮。
- 顶部**只读** header：显示 `乱序=否`、`施放数 = WandLoadout.BaseDraws`。
- 法术格显示 `SpellDefinition.Icon`；**图标为空时用法术名兜底**（不报错）。

### 不做（明确划到 MVP 外）
- 世界掉落 / 拾取系统（背包是固定起始集）。
- 有限物品（Noita 式法术实体在背包与框间移动、数量有限）——本期用无限调色板。
- 多法杖切换、编辑"乱序/施放数"。
- 富 tooltip（悬浮显示伤害/法力等详情）。
- 存档持久化（运行时对 `WandLoadout` 的改动见下"已知特性"）。

## 3. 已定决策（本轮拍板）
1. **无限调色板**交互模型（非 Noita 有限物品）。
2. **按键开关 + 暂停**（`Time.timeScale = 0`）：打开时暂停、解锁鼠标、相机不转、左键用于拖拽（不施法）；关闭恢复。
3. **实时编辑**现有 `WandLoadout`（UI 改它、`SpellCaster` 下次开火读它），无应用步骤。
4. UI 用 **UGUI + EventSystem 拖拽**（与项目现有 UI 一致），非 UI Toolkit。
5. `乱序/施放数` **只读展示**。

## 4. 架构与数据流

```
[SpellLibrary SO]  玩家拥有的法术清单（Inspector 配固定起始集）
        │ 读取
        ▼
   SpellPaletteView（调色板/背包）──┐
                                    │ 拖拽（拖入=放一份；拖出=删除；框内=重排）
   WandFrameView（编程框）◄─────────┘
        │ 每次增/删/移 → WandEditOps 改 List → 写回
        ▼
   [WandLoadout SO]  法杖序列（既有）
        ▲
        └── SpellCaster.CastWand 开火时读取（既有，改动实时生效）
```

- **数据中介 = 共享的 `WandLoadout` SO**：UI 写、`SpellCaster` 读，二者不直接相互引用（沿用"SO 作共享运行时状态"的模式）。
- **依赖方向**：`Game.UI` 增加对 `Game.Skills` 的引用（用 `SpellDefinition`/`WandLoadout`/新增 `SpellLibrary`）。`Game.UI` 本就引用 `Game.Combat`，再引用数据层不破坏隔离原则（UI 只读取玩法数据类型，不驱动玩法逻辑）。

## 5. 新增数据资产

**`SpellLibrary`（ScriptableObject，放 `Game.Skills`）**
```
public class SpellLibrary : ScriptableObject
{
    public SpellDefinition[] Available;   // 玩家拥有、可拖进法杖的全部法术（MVP：Inspector 配固定起始集）
}
```
带 `[CreateAssetMenu(menuName = "Game/Skills/Spell Library")]`。调色板的唯一数据来源。

> `WandLoadout`（已存在）继续作为编程框的数据源，不改其结构。

## 6. 组件拆分（均在 `Game.UI`，UGUI）

| 组件 | 职责 | 关键点 |
|---|---|---|
| `WandEditorController` | 按键开关面板；`Time.timeScale=0` 暂停 + `Cursor.lockState` 切换；持有 Library/WandLoadout 引用；打开时构建调色板与编程框、关闭时收起 | 唯一持有"打开/关闭"状态；恢复时把 timeScale 设回 1、鼠标重新锁定 |
| `SpellPaletteView` | 从 `SpellLibrary.Available` 实例化一排可拖的 `SpellSlotView`（来源标记 = Palette） | 无限调色板：拖出只是"复制一个引用进框"，调色板本身不变 |
| `WandFrameView` | 渲染 `Capacity` 个槽位，前 `WandLoadout.Spells.Length` 个填充、其余空；接收放置 → 调 `WandEditOps` 改一个工作 `List<SpellDefinition>` → 写回 `WandLoadout.Spells` → 重建 | 容量上限 `Capacity`（默认 8，可序列化配）；放满后拒收 |
| `SpellSlotView` | 一个法术格：显示 `Icon`（空则显示 `DisplayName` 文本兜底）；实现 `IBeginDragHandler/IDragHandler/IEndDragHandler`；携带 `SpellDefinition` + 来源（Palette / Frame+index） | 拖拽时生成跟随光标的 ghost 图标（`raycastTarget=false`，挂在最上层 Canvas） |
| `WandHeaderView`（可并入 Controller） | 只读显示 `乱序=否`、`施放数 = WandLoadout.BaseDraws` | 纯展示 |

**拖放规则（落点判定由 `WandFrameView`/`SpellPaletteView` 作为 `IDropHandler` 处理）**
- 从**调色板**拖 → 落在编程框：在落点对应索引 `InsertAt`（未满才接受）。
- **框内**法术格拖 → 落在编程框另一位置：`Move`（重排）。
- 从**框内**拖 → 落在编程框**之外**（调色板区域或空白）：`RemoveAt`（移除）。
- 从调色板拖 → 落在调色板/空白：无操作（什么都不放）。

## 7. 纯逻辑可测的一小块

**`WandEditOps`（static，放 `Game.Skills`）** —— 对法术序列的纯编辑操作，便于 EditMode 单测：
```
static void InsertAt(List<SpellDefinition> spells, int index, SpellDefinition spell, int capacity)
static void RemoveAt(List<SpellDefinition> spells, int index)
static void Move(List<SpellDefinition> spells, int from, int to)
```
- `InsertAt`：index 钳到 `[0, Count]`；若 `Count >= capacity` 则不插入（满）。
- `RemoveAt`：index 越界则忽略。
- `Move`：from/to 钳到合法范围；自身到自身无操作。

EditMode 测试（`Game.Skills.Tests`）覆盖：插到中间/末尾、容量满拒插、越界安全、移动重排、移动边界。UI 拖拽与视觉由开发者 Play 验证（不单测 MonoBehaviour）。

## 8. 输入接入

给 `Assets/.../InputSystem_Actions.inputactions` 增加一个动作 **`ToggleWandEditor`**（Button，默认绑键盘 `Tab`；可后续在资产里改键）。`WandEditorController` 订阅其 `performed` 切换面板。Unity 导入 `.inputactions` 时会重新生成 C# 包装类（开发者在 Editor 完成；本 spec 视其为数据资产改动）。

> 约束：项目禁用旧版 `Input` 类，必须走 Input System 动作。

## 9. 边界与已知特性
- **暂停恢复要稳**：打开设 `Time.timeScale=0` + 解锁鼠标；关闭**务必**还原 `timeScale=1` 并重新 `Cursor.lockState=Locked`（在 `OnDisable`/关闭路径都兜底，防止卡在暂停/解锁态）。
- **空图标兜底**：`Icon == null` 时 `SpellSlotView` 显示 `DisplayName`，不留空白、不报错。
- **容量满**：编程框达 `Capacity` 时拒绝再放（给个轻提示/无声拒收，MVP 可无声）。
- **运行时改 SO 的特性**：在编辑器里运行时修改 `WandLoadout` 资产会写回磁盘、停 Play 不自动还原（Unity 已知行为）；构建版里仅运行时生效。MVP 接受；将来要做"每局重置/存档"再处理。
- **拖拽 ghost 的射线**：跟随光标的 ghost 图标必须 `raycastTarget=false`，否则会挡住 `IDropHandler` 的命中。
- **EventSystem 依赖**：场景需有 `EventSystem`（UGUI 拖拽必需）；若缺由开发者在 Editor 补上。

## 10. 测试与验证
- **EditMode 单测**：`WandEditOps`（插/删/移/容量/边界）。
- **开发者 Play 验证**：按键开/关面板、暂停与鼠标解锁/恢复、从调色板拖入、框内重排、拖出移除、空图标兜底显示名字、关闭后左键按新序列开火、容量满拒收。

## 11. 文件清单（预计）

| 程序集 | 文件 | 新增/改动 |
|---|---|---|
| Game.Skills | `SpellLibrary.cs` | 新增（调色板数据源） |
| Game.Skills | `WandEditOps.cs` | 新增（纯编辑操作） |
| Game.Skills.Tests | `WandEditOpsTests.cs` | 新增（EditMode） |
| Game.UI | `Game.UI.asmdef` | 改动（+ Game.Skills 引用） |
| Game.UI | `WandEditorController.cs` | 新增（开关/暂停/构建） |
| Game.UI | `SpellPaletteView.cs` | 新增（调色板） |
| Game.UI | `WandFrameView.cs` | 新增（编程框 + 写回 WandLoadout） |
| Game.UI | `SpellSlotView.cs` | 新增（单格 + 拖拽） |
| 输入 | `InputSystem_Actions.inputactions` | 改动（+ ToggleWandEditor 动作） |
| Editor（开发者） | 面板预制体/Canvas 布局、`SpellLibrary`/`WandLoadout` 资产连线、EventSystem | 开发者组装 + Play 验证 |

## 12. 分期内的任务切片（给实现计划参考）
1. 数据 + 纯逻辑：`SpellLibrary` + `WandEditOps` + EditMode 测试。
2. `Game.UI` 引用 Skills + `SpellSlotView`（显示 + 拖拽 + ghost）。
3. `SpellPaletteView` + `WandFrameView`（落点判定 + 写回 WandLoadout）。
4. `WandEditorController`（开关/暂停/鼠标）+ `ToggleWandEditor` 输入动作。
5. 开发者：Canvas/预制体布局 + 资产连线 + EventSystem + Play 验证。

## 13. Self-review 记录
- 占位符：无 TBD/TODO。
- 一致性：数据流（Library→调色板、WandLoadout↔编程框）与组件职责、文件清单互相对应；与总 spec 的"SO 作中介 + Game.UI→Skills"一致。
- 范围：聚焦单一实现计划可承载（拖拽编辑 UI），掉落/有限物品/多法杖/存档明确排除。
- 歧义：拖放三种情形（调色板→框 / 框内 / 框→外）行为已逐一明确；容量满、空图标、暂停恢复、ghost 射线等边界已写死。
