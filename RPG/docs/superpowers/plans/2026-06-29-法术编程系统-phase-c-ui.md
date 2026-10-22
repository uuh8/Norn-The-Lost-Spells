# 法术编程系统 · 阶段 C：拖拽编程 UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 做一个仿 Noita 的法杖编程界面：按键开关（暂停 + 解锁鼠标），上方"法术调色板"（来自 `SpellLibrary`），下方"编程框"（绑定现有 `WandLoadout`），玩家拖拽增/删/排序，改动实时写回 `WandLoadout`，下次左键开火即用新序列。

**Architecture:** 纯编辑逻辑（`WandEditOps`）与数据（`SpellLibrary`）放纯净的 `Game.Skills`（可单测）；UI 用 UGUI + EventSystem 拖拽放 `Game.UI`。`SpellSlotView`（格子）只通过接口 `IWandDragHandler` 与总控 `WandEditorController` 通信，拖放裁决集中在总控（调色板→框=插入；框内=移动；框→外=移除）。编程框通过共享的 `WandLoadout` SO 与 `SpellCaster` 解耦联动。

**Tech Stack:** Unity 6.3 / C# / UGUI（`UnityEngine.UI`，含 EventSystem 拖放接口）/ Input System（`InputActionReference`）/ NUnit EditMode（仅 `WandEditOps`）。

**前置：** spec `docs/superpowers/specs/2026-06-29-spell-system-phase-c-ui-design.md`。阶段 A/B 已合入 main（`SpellDefinition`/`WandLoadout`/`SpellCaster` 等就绪）。

## Global Constraints

- **分工**：Claude/子代理只写 `.cs` 与纯文本配置（`.asmdef`）并提交；编译、跑 EditMode 测试、搭 Canvas/预制体、连资产、Play 验证都由开发者在 Unity 手动做。"运行/编译/Play"步骤标 **(开发者)**，子代理在该步停下不执行、不编译、不跑测试、不臆造结果。
- 子代理**不创建 `.meta`**（Unity 生成）。
- 本阶段 UI MonoBehaviour 部分**无自动化测试**（靠开发者 Play 验证）；唯一可单测的是纯逻辑 `WandEditOps`（Task 1，EditMode）。
- 命名空间匹配程序集：`Game.Skills` / `Game.UI` / `Game.Skills.Tests`。命名：私有字段 `_camelCase`，公有 `PascalCase`。
- UI 文本兜底用 `UnityEngine.UI.Text`（项目未用 TMP，避免额外依赖）。
- 开关界面用 `InputActionReference`（开发者在 Inspector 指派动作），不碰生成的 `InputSystem_Actions` 包装类。
- 暂停/鼠标恢复必须兜底：关闭与 `OnDisable` 都还原 `Time.timeScale=1` + `Cursor.lockState=Locked`。
- 拖拽 ghost 的 `Image` 必须 `raycastTarget=false`（否则挡住 `IDropHandler`）。
- 全文写死的拖放语义（实现严格照此）：调色板格→编程框格 = `InsertAt`；编程框格→另一编程框格 = `Move`；编程框格→框外(无 IDropHandler 接收) = `RemoveAt`；调色板格→框外 = 无操作。空格不可拖；只有编程框格是放置目标。

## File Structure

| 程序集 | 文件 | 职责 |
|---|---|---|
| Game.Skills | `SpellLibrary.cs` | 调色板数据源（SpellDefinition[]） |
| Game.Skills | `WandEditOps.cs` | 纯编辑操作（InsertAt/RemoveAt/Move） |
| Game.Skills.Tests | `WandEditOpsTests.cs` | EditMode 单测 |
| Game.UI | `Game.UI.asmdef` | +Game.Skills +Unity.InputSystem 引用 |
| Game.UI | `SlotKind.cs` | 枚举 Palette/Frame |
| Game.UI | `IWandDragHandler.cs` | 格子↔总控的拖放契约 |
| Game.UI | `SpellSlotView.cs` | 单格：显示 + 拖 + 放 |
| Game.UI | `SpellPaletteView.cs` | 调色板（从 Library 铺格） |
| Game.UI | `WandFrameView.cs` | 编程框（绑定 WandLoadout + 应用编辑） |
| Game.UI | `WandEditorController.cs` | 开关/暂停/鼠标/ghost/拖放裁决/输入 |

---

### Task 1: 纯编辑逻辑 + 调色板数据（Game.Skills，可单测）

**Files:**
- Create: `Assets/_Project/Scripts/Skills/SpellLibrary.cs`
- Create: `Assets/_Project/Scripts/Skills/WandEditOps.cs`
- Test: `Assets/_Project/Tests/Skills/WandEditOpsTests.cs`

**Interfaces:**
- Produces:
  - `class SpellLibrary : ScriptableObject { public SpellDefinition[] Available; }`
  - `static WandEditOps.InsertAt(List<SpellDefinition> spells, int index, SpellDefinition spell, int capacity)`
  - `static WandEditOps.RemoveAt(List<SpellDefinition> spells, int index)`
  - `static WandEditOps.Move(List<SpellDefinition> spells, int from, int to)`

- [ ] **Step 1: 建 `SpellLibrary.cs`**

```csharp
using UnityEngine;

namespace Game.Skills
{
    /// <summary>玩家拥有、可拖进法杖的全部法术（调色板/背包的数据源）。MVP：Inspector 配固定起始集。</summary>
    [CreateAssetMenu(menuName = "Game/Skills/Spell Library", fileName = "SpellLibrary")]
    public class SpellLibrary : ScriptableObject
    {
        [Tooltip("调色板里可拖的全部法术")]
        public SpellDefinition[] Available;
    }
}
```

- [ ] **Step 2: 建 `WandEditOps.cs`**

```csharp
using System.Collections.Generic;

namespace Game.Skills
{
    /// <summary>
    /// 对法杖法术序列的纯编辑操作（插/删/移），供编程框 UI 调用。纯逻辑、无 Unity 依赖、可 EditMode 单测。
    /// </summary>
    public static class WandEditOps
    {
        /// <summary>在 index 处插入 spell；index 钳到 [0, Count]；已达 capacity 则不插入（满）。spell/spells 为 null 忽略。</summary>
        public static void InsertAt(List<SpellDefinition> spells, int index, SpellDefinition spell, int capacity)
        {
            if (spells == null || spell == null) return;
            if (spells.Count >= capacity) return;
            if (index < 0) index = 0;
            if (index > spells.Count) index = spells.Count;
            spells.Insert(index, spell);
        }

        /// <summary>移除 index 处元素；越界忽略。</summary>
        public static void RemoveAt(List<SpellDefinition> spells, int index)
        {
            if (spells == null) return;
            if (index < 0 || index >= spells.Count) return;
            spells.RemoveAt(index);
        }

        /// <summary>把 from 处元素移动到 to 处；from/to 钳到合法范围；from==to 或元素不足 2 时无操作。</summary>
        public static void Move(List<SpellDefinition> spells, int from, int to)
        {
            if (spells == null) return;
            int count = spells.Count;
            if (count <= 1) return;
            if (from < 0 || from >= count) return;
            if (to < 0) to = 0;
            if (to >= count) to = count - 1;
            if (from == to) return;
            SpellDefinition s = spells[from];
            spells.RemoveAt(from);
            spells.Insert(to, s);
        }
    }
}
```

- [ ] **Step 3: 建 `WandEditOpsTests.cs`**

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Game.Skills;

namespace Game.Skills.Tests
{
    public class WandEditOpsTests
    {
        private static SpellDefinition Spell() => ScriptableObject.CreateInstance<SpellDefinition>();

        [Test]
        public void InsertAt_Middle_ShiftsRest()
        {
            var a = Spell(); var b = Spell(); var c = Spell();
            var list = new List<SpellDefinition> { a, b };
            WandEditOps.InsertAt(list, 1, c, 8);
            Assert.AreEqual(3, list.Count);
            Assert.AreSame(a, list[0]);
            Assert.AreSame(c, list[1]);
            Assert.AreSame(b, list[2]);
        }

        [Test]
        public void InsertAt_IndexClampedToEnd()
        {
            var a = Spell(); var b = Spell();
            var list = new List<SpellDefinition> { a };
            WandEditOps.InsertAt(list, 99, b, 8);
            Assert.AreEqual(2, list.Count);
            Assert.AreSame(b, list[1]);
        }

        [Test]
        public void InsertAt_AtCapacity_Rejected()
        {
            var list = new List<SpellDefinition> { Spell(), Spell() };
            WandEditOps.InsertAt(list, 0, Spell(), 2);
            Assert.AreEqual(2, list.Count); // 满，不插入
        }

        [Test]
        public void RemoveAt_Valid_Removes()
        {
            var a = Spell(); var b = Spell();
            var list = new List<SpellDefinition> { a, b };
            WandEditOps.RemoveAt(list, 0);
            Assert.AreEqual(1, list.Count);
            Assert.AreSame(b, list[0]);
        }

        [Test]
        public void RemoveAt_OutOfRange_Ignored()
        {
            var list = new List<SpellDefinition> { Spell() };
            WandEditOps.RemoveAt(list, 5);
            Assert.AreEqual(1, list.Count);
        }

        [Test]
        public void Move_ForwardReorders()
        {
            var a = Spell(); var b = Spell(); var c = Spell();
            var list = new List<SpellDefinition> { a, b, c };
            WandEditOps.Move(list, 0, 2); // a 移到末尾
            Assert.AreSame(b, list[0]);
            Assert.AreSame(c, list[1]);
            Assert.AreSame(a, list[2]);
        }

        [Test]
        public void Move_SelfNoop_And_ToClamped()
        {
            var a = Spell(); var b = Spell();
            var list = new List<SpellDefinition> { a, b };
            WandEditOps.Move(list, 1, 1); // 自身→自身：无操作
            Assert.AreSame(a, list[0]);
            Assert.AreSame(b, list[1]);
            WandEditOps.Move(list, 0, 99); // to 钳到末尾
            Assert.AreSame(b, list[0]);
            Assert.AreSame(a, list[1]);
        }
    }
}
```

- [ ] **Step 4: (开发者) 跑 EditMode 测试**

Test Runner → EditMode → Run All。预期：`WandEditOpsTests` 7 个 + 既有 22 个全部 **PASS**；编译无错。

- [ ] **Step 5: 提交**

```bash
git add Assets/_Project/Scripts/Skills/SpellLibrary.cs Assets/_Project/Scripts/Skills/WandEditOps.cs Assets/_Project/Tests/Skills/WandEditOpsTests.cs
git commit -m "feat(skills): add SpellLibrary SO + pure WandEditOps (insert/remove/move) with tests

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Game.UI 依赖 + 单格视图（SlotKind / IWandDragHandler / SpellSlotView）

**Files:**
- Modify: `Assets/_Project/Scripts/UI/Game.UI.asmdef`（references +Game.Skills +Unity.InputSystem）
- Create: `Assets/_Project/Scripts/UI/SlotKind.cs`
- Create: `Assets/_Project/Scripts/UI/IWandDragHandler.cs`
- Create: `Assets/_Project/Scripts/UI/SpellSlotView.cs`

**Interfaces:**
- Consumes: `Game.Skills.SpellDefinition`（已存在）。
- Produces:
  - `enum SlotKind : byte { Palette, Frame }`
  - `interface IWandDragHandler { void BeginSpellDrag(SpellDefinition, SlotKind, int, PointerEventData); void UpdateSpellDrag(PointerEventData); void EndSpellDrag(PointerEventData); void DropOnFrameSlot(int frameIndex); }`
  - `SpellSlotView.Bind(SpellDefinition spell, SlotKind kind, int index, IWandDragHandler handler)`；属性 `Spell/Kind/Index`。

- [ ] **Step 1: 改 `Game.UI.asmdef`（加两个引用）**

整文件替换为：

```json
{
    "name": "Game.UI",
    "rootNamespace": "",
    "references": [
        "Game.Core",
        "Game.Combat",
        "Game.Skills",
        "UnityEngine.UI",
        "Unity.InputSystem"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 2: 建 `SlotKind.cs`**

```csharp
namespace Game.UI
{
    /// <summary>法术格来源：调色板（背包，源）/ 编程框（法杖序列，可放置目标）。</summary>
    public enum SlotKind : byte
    {
        Palette = 0,
        Frame = 1,
    }
}
```

- [ ] **Step 3: 建 `IWandDragHandler.cs`**

```csharp
using UnityEngine.EventSystems;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 法术格与总控之间的拖放契约。SpellSlotView 只依赖此接口（不直接依赖 WandEditorController），
    /// 以便各自独立编译；拖放裁决集中在实现方（WandEditorController）。
    /// </summary>
    public interface IWandDragHandler
    {
        void BeginSpellDrag(SpellDefinition spell, SlotKind origin, int index, PointerEventData eventData);
        void UpdateSpellDrag(PointerEventData eventData);
        void EndSpellDrag(PointerEventData eventData);
        void DropOnFrameSlot(int frameIndex);
    }
}
```

- [ ] **Step 4: 建 `SpellSlotView.cs`**

```csharp
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 一个法术格（调色板格 / 编程框格通用）。显示图标（空则用名字兜底）；
    /// 有法术时可拖；编程框格可作为放置目标（IDropHandler）。拖放结果交给 IWandDragHandler（总控）裁决。
    /// 需要格子上有 raycastTarget=true 的图形（如本 _icon 的 Image）才能接收拖拽/放置事件。
    /// </summary>
    public class SpellSlotView : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
    {
        [SerializeField] private Image _icon;
        [SerializeField] private Text _nameLabel; // 图标为空时兜底显示名字

        private SpellDefinition _spell;
        private SlotKind _kind;
        private int _index;
        private IWandDragHandler _handler;

        public SpellDefinition Spell => _spell;
        public SlotKind Kind => _kind;
        public int Index => _index;

        /// <summary>绑定数据并刷新显示。spell 为 null = 空格（编程框里的空槽）。</summary>
        public void Bind(SpellDefinition spell, SlotKind kind, int index, IWandDragHandler handler)
        {
            _spell = spell;
            _kind = kind;
            _index = index;
            _handler = handler;
            Refresh();
        }

        private void Refresh()
        {
            bool hasSpell = _spell != null;
            bool hasIcon = hasSpell && _spell.Icon != null;

            if (_icon != null)
            {
                _icon.enabled = hasIcon;
                if (hasIcon) _icon.sprite = _spell.Icon;
            }
            if (_nameLabel != null)
            {
                bool showName = hasSpell && !hasIcon; // 有法术但无图标 → 名字兜底
                _nameLabel.enabled = showName;
                if (showName)
                    _nameLabel.text = string.IsNullOrEmpty(_spell.DisplayName) ? _spell.name : _spell.DisplayName;
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_spell == null || _handler == null) return; // 空格不可拖
            _handler.BeginSpellDrag(_spell, _kind, _index, eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (_handler == null) return;
            _handler.UpdateSpellDrag(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (_handler == null) return;
            _handler.EndSpellDrag(eventData);
        }

        public void OnDrop(PointerEventData eventData)
        {
            if (_kind != SlotKind.Frame || _handler == null) return; // 只有编程框格是放置目标
            _handler.DropOnFrameSlot(_index);
        }
    }
}
```

- [ ] **Step 5: (开发者) 编译**

回 Unity 编译。预期：`Game.UI` 解析到 Game.Skills + Unity.InputSystem，全工程编译无错。（本任务无自动化测试。）

- [ ] **Step 6: 提交**

```bash
git add Assets/_Project/Scripts/UI/Game.UI.asmdef Assets/_Project/Scripts/UI/SlotKind.cs Assets/_Project/Scripts/UI/IWandDragHandler.cs Assets/_Project/Scripts/UI/SpellSlotView.cs
git commit -m "feat(ui): SpellSlotView (display + drag/drop) + drag contract; ref Skills/InputSystem

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: 调色板与编程框视图（SpellPaletteView / WandFrameView）

**Files:**
- Create: `Assets/_Project/Scripts/UI/SpellPaletteView.cs`
- Create: `Assets/_Project/Scripts/UI/WandFrameView.cs`

**Interfaces:**
- Consumes: `SpellSlotView.Bind(...)`、`IWandDragHandler`、`SlotKind`（Task 2）；`SpellLibrary`、`WandLoadout`、`WandEditOps`（Task 1/既有）。
- Produces:
  - `SpellPaletteView.Rebuild(IWandDragHandler handler)`
  - `WandFrameView.Rebuild(IWandDragHandler handler)`；`WandFrameView.ApplyInsert(int index, SpellDefinition spell)`；`WandFrameView.ApplyMove(int from, int to)`；`WandFrameView.ApplyRemove(int index)`；属性 `Capacity`。

- [ ] **Step 1: 建 `SpellPaletteView.cs`**

```csharp
using UnityEngine;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 调色板（背包）：从 SpellLibrary 铺出可拖的法术格。无限调色板——拖出只是复制引用进编程框，调色板本身不变。
    /// </summary>
    public class SpellPaletteView : MonoBehaviour
    {
        [SerializeField] private SpellLibrary _library;
        [SerializeField] private Transform _container;     // 法术格父节点（建议挂 Layout Group）
        [SerializeField] private SpellSlotView _slotPrefab;

        public void Rebuild(IWandDragHandler handler)
        {
            if (_container == null || _slotPrefab == null) return;

            for (int i = _container.childCount - 1; i >= 0; i--)
                Object.Destroy(_container.GetChild(i).gameObject);

            if (_library == null || _library.Available == null) return;

            for (int i = 0; i < _library.Available.Length; i++)
            {
                SpellDefinition spell = _library.Available[i];
                if (spell == null) continue;
                SpellSlotView slot = Object.Instantiate(_slotPrefab, _container);
                slot.Bind(spell, SlotKind.Palette, i, handler);
            }
        }
    }
}
```

- [ ] **Step 2: 建 `WandFrameView.cs`**

```csharp
using System.Collections.Generic;
using UnityEngine;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 编程框：渲染 Capacity 个槽位（前 N 个 = WandLoadout.Spells，其余空格）。
    /// 编辑（插/移/删）经 WandEditOps 改一个工作 List → 写回 WandLoadout.Spells；视觉重建由 Rebuild 单独触发
    /// （在一次拖放结束时由总控统一调用，避免拖拽中销毁正在拖的格子）。
    /// </summary>
    public class WandFrameView : MonoBehaviour
    {
        [SerializeField] private WandLoadout _wand;
        [SerializeField] private Transform _container;
        [SerializeField] private SpellSlotView _slotPrefab;
        [SerializeField] private int _capacity = 8;

        private readonly List<SpellDefinition> _work = new List<SpellDefinition>(16);

        public int Capacity => _capacity;

        public void Rebuild(IWandDragHandler handler)
        {
            if (_container == null || _slotPrefab == null) return;

            for (int i = _container.childCount - 1; i >= 0; i--)
                Object.Destroy(_container.GetChild(i).gameObject);

            int count = (_wand != null && _wand.Spells != null) ? _wand.Spells.Length : 0;
            for (int i = 0; i < _capacity; i++)
            {
                SpellDefinition spell = i < count ? _wand.Spells[i] : null;
                SpellSlotView slot = Object.Instantiate(_slotPrefab, _container);
                slot.Bind(spell, SlotKind.Frame, i, handler);
            }
        }

        // ── 编辑操作（仅改数据、写回 SO，不重建视图）──
        public void ApplyInsert(int index, SpellDefinition spell)
        {
            LoadWork();
            WandEditOps.InsertAt(_work, index, spell, _capacity);
            WriteBack();
        }

        public void ApplyMove(int from, int to)
        {
            LoadWork();
            WandEditOps.Move(_work, from, to);
            WriteBack();
        }

        public void ApplyRemove(int index)
        {
            LoadWork();
            WandEditOps.RemoveAt(_work, index);
            WriteBack();
        }

        private void LoadWork()
        {
            _work.Clear();
            if (_wand == null || _wand.Spells == null) return;
            for (int i = 0; i < _wand.Spells.Length; i++)
                if (_wand.Spells[i] != null) _work.Add(_wand.Spells[i]);
        }

        private void WriteBack()
        {
            if (_wand == null) return;
            _wand.Spells = _work.ToArray();
        }
    }
}
```

- [ ] **Step 3: (开发者) 编译**

回 Unity 编译。预期：全工程编译无错。（本任务无自动化测试。）

- [ ] **Step 4: 提交**

```bash
git add Assets/_Project/Scripts/UI/SpellPaletteView.cs Assets/_Project/Scripts/UI/WandFrameView.cs
git commit -m "feat(ui): SpellPaletteView (from SpellLibrary) + WandFrameView (binds WandLoadout, applies edits)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: 总控 WandEditorController（开关/暂停/鼠标/ghost/拖放裁决/输入）

**Files:**
- Create: `Assets/_Project/Scripts/UI/WandEditorController.cs`

**Interfaces:**
- Consumes: `SpellPaletteView.Rebuild`、`WandFrameView.Rebuild/ApplyInsert/ApplyMove/ApplyRemove`、`IWandDragHandler`、`SlotKind`（Task 2/3）；`WandLoadout`（既有）；`InputActionReference`（Input System）。
- Produces: `WandEditorController : IWandDragHandler`（实现四个拖放回调）；`public void Toggle()`。

- [ ] **Step 1: 建 `WandEditorController.cs`**

```csharp
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using Game.Skills;

namespace Game.UI
{
    /// <summary>
    /// 法杖编程界面总控：按键开关面板（Time.timeScale=0 暂停 + 解锁鼠标）；持有跟随光标的 ghost；
    /// 集中裁决拖放（调色板→框=插入；框内=移动；框→框外=移除）。实现 IWandDragHandler 供法术格回调。
    /// </summary>
    public class WandEditorController : MonoBehaviour, IWandDragHandler
    {
        [Header("References")]
        [SerializeField] private GameObject _panelRoot;        // 整个编程界面根（开关其 active）
        [SerializeField] private SpellPaletteView _palette;
        [SerializeField] private WandFrameView _frame;
        [SerializeField] private WandLoadout _wand;            // 仅用于 header 显示施放数
        [SerializeField] private Image _dragGhost;            // 跟随光标的拖拽影像（raycastTarget 关、置顶层、默认隐藏）

        [Header("Header (只读展示)")]
        [SerializeField] private Text _shuffleLabel;          // 显示 "乱序：否"
        [SerializeField] private Text _castCountLabel;        // 显示 "施放数：N"

        [Header("Input")]
        [SerializeField] private InputActionReference _toggleAction; // 开关界面动作（开发者在 Inspector 指派，如 Tab）

        private bool _open;

        // 拖拽状态
        private SpellDefinition _dragSpell;
        private SlotKind _dragOrigin;
        private int _dragFromIndex;
        private bool _dropHandled;

        private void OnEnable()
        {
            if (_toggleAction != null && _toggleAction.action != null)
            {
                _toggleAction.action.performed += OnTogglePerformed;
                _toggleAction.action.Enable();
            }
        }

        private void OnDisable()
        {
            if (_toggleAction != null && _toggleAction.action != null)
                _toggleAction.action.performed -= OnTogglePerformed;
            if (_open) RestoreGameState(); // 兜底：禁用时仍打开则恢复时间/鼠标，避免卡死
        }

        private void Start()
        {
            if (_panelRoot != null) _panelRoot.SetActive(false);
            _open = false;
        }

        private void OnTogglePerformed(InputAction.CallbackContext ctx) => Toggle();

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        private void Open()
        {
            _open = true;
            if (_panelRoot != null) _panelRoot.SetActive(true);
            RebuildViews();
            RefreshHeader();
            Time.timeScale = 0f;                    // 暂停
            Cursor.lockState = CursorLockMode.None;  // 解锁鼠标用于拖拽
            Cursor.visible = true;
            if (_dragGhost != null) _dragGhost.enabled = false;
        }

        private void Close()
        {
            _open = false;
            if (_panelRoot != null) _panelRoot.SetActive(false);
            RestoreGameState();
        }

        private void RestoreGameState()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (_dragGhost != null) _dragGhost.enabled = false;
        }

        private void RebuildViews()
        {
            if (_palette != null) _palette.Rebuild(this);
            if (_frame != null) _frame.Rebuild(this);
        }

        private void RefreshHeader()
        {
            if (_shuffleLabel != null) _shuffleLabel.text = "乱序：否";
            if (_castCountLabel != null)
            {
                int baseDraws = _wand != null ? _wand.BaseDraws : 1;
                _castCountLabel.text = $"施放数：{baseDraws}";
            }
        }

        // ── IWandDragHandler ──
        public void BeginSpellDrag(SpellDefinition spell, SlotKind origin, int index, PointerEventData eventData)
        {
            _dragSpell = spell;
            _dragOrigin = origin;
            _dragFromIndex = index;
            _dropHandled = false;

            if (_dragGhost != null)
            {
                _dragGhost.enabled = true;
                _dragGhost.sprite = spell != null ? spell.Icon : null;
                _dragGhost.rectTransform.position = eventData.position;
            }
        }

        public void UpdateSpellDrag(PointerEventData eventData)
        {
            if (_dragGhost != null && _dragGhost.enabled)
                _dragGhost.rectTransform.position = eventData.position;
        }

        public void DropOnFrameSlot(int frameIndex)
        {
            if (_dragSpell == null || _frame == null) return;
            if (_dragOrigin == SlotKind.Palette)
                _frame.ApplyInsert(frameIndex, _dragSpell);
            else
                _frame.ApplyMove(_dragFromIndex, frameIndex);
            _dropHandled = true;
        }

        public void EndSpellDrag(PointerEventData eventData)
        {
            // 框内法术拖到框外（无 IDropHandler 接收）→ 移除
            if (!_dropHandled && _dragOrigin == SlotKind.Frame && _frame != null)
                _frame.ApplyRemove(_dragFromIndex);

            if (_frame != null) _frame.Rebuild(this); // 一次拖放结束统一重建（Destroy 延迟到帧末，安全）

            if (_dragGhost != null) _dragGhost.enabled = false;
            _dragSpell = null;
            _dropHandled = false;
        }
    }
}
```

- [ ] **Step 2: (开发者) 编译**

回 Unity 编译。预期：全工程编译无错。（本任务无自动化测试；行为在 Task 5 Play 验证。）

- [ ] **Step 3: 提交**

```bash
git add Assets/_Project/Scripts/UI/WandEditorController.cs
git commit -m "feat(ui): WandEditorController (toggle/pause/cursor + drag ghost + drop resolution)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: Editor 组装 + Play 验证（开发者）

纯 Editor 工作，由开发者完成。

- [ ] **Step 1: 输入动作** —— 在 `InputSystem_Actions` 资产里加一个 `ToggleWandEditor`（Button，绑键盘 `Tab`）。
- [ ] **Step 2: SpellLibrary 资产** —— Create → Game/Skills/Spell Library；把 5 个法术（火球/增伤/加速/散射/三重）拖进 `Available`。
- [ ] **Step 3: 格子预制体** —— 一个 `SpellSlotView` 预制体：根上 `SpellSlotView` + 一个 `Image`（图标，`raycastTarget=true`）+ 一个 `Text`（名字兜底，默认可隐藏），把 `_icon`/`_nameLabel` 指好。
- [ ] **Step 4: 面板** —— Screen Space Overlay Canvas 下建 `PanelRoot`：上方调色板容器（挂 Horizontal/Grid Layout Group）+ 下方编程框容器 + 顶部两个只读 Text（乱序/施放数）+ 一个置顶的 ghost `Image`（`raycastTarget=false`，默认 disabled）。场景需有 `EventSystem`（没有就 GameObject → UI → Event System）。
- [ ] **Step 5: 接线** —— 在某个常驻物体上挂 `WandEditorController`，指好 `_panelRoot/_palette/_frame/_wand/_dragGhost/_shuffleLabel/_castCountLabel/_toggleAction`；`SpellPaletteView` 指 `_library/_container/_slotPrefab`；`WandFrameView` 指 `_wand`(现有 WandLoadout)/`_container/_slotPrefab/_capacity`。
- [ ] **Step 6: Play 验证** —— 按 Tab 开/关面板（暂停 + 鼠标解锁/恢复）；从调色板拖法术进编程框；框内拖动重排；把框内法术拖到框外移除；空图标的法术显示名字；关面板后左键按新序列开火；编程框放满 `_capacity` 后拒收；ghost 跟随光标。
- [ ] **Step 7: 提交** —— Unity 生成的 `.meta` + SpellLibrary/预制体/面板/场景改动（开发者或让 Claude 代提交）。

---

## 阶段 C 完成后的产物
玩家可在游戏内按键打开法杖编程界面，拖拽法术编辑法杖，实时改变开火效果——法术编程系统从"开发者在 Inspector 配"升级为"玩家可玩可演示"。

## 不在本阶段范围
世界掉落/拾取、有限物品、多法杖切换、编辑乱序/施放数、富 tooltip、存档持久化、触发递归、法力资源系统。

## Self-Review
- **Spec 覆盖**：开关+暂停+鼠标(Task4)、调色板来自 SpellLibrary(Task1/3)、编程框绑 WandLoadout 实时写回(Task3)、无限调色板拖入/框内重排/拖出移除三情形(Task2 OnDrop + Task4 裁决)、容量满拒收(WandEditOps.InsertAt + _capacity)、空图标名字兜底(SpellSlotView)、只读 header(Task4)、Game.UI→Skills+InputSystem(Task2)、纯逻辑 WandEditOps+测试(Task1)、ToggleWandEditor 动作(Task5 开发者)。✅
- **占位符扫描**：无 TBD/TODO；每个代码步骤为完整文件/JSON。✅
- **类型一致性**：`IWandDragHandler` 四方法签名在 SpellSlotView 调用、WandEditorController 实现处一致；`WandFrameView.ApplyInsert/ApplyMove/ApplyRemove/Rebuild/Capacity`、`SpellPaletteView.Rebuild`、`SpellSlotView.Bind`、`WandEditOps.InsertAt/RemoveAt/Move`、`SpellLibrary.Available`、`WandLoadout.Spells/BaseDraws` 在定义处与调用处全程一致。✅
- **编译顺序**：Task2 的 SpellSlotView 只依赖同任务的 `IWandDragHandler`/`SlotKind`（不前向引用总控）；Task3 依赖 Task2 + 数据；Task4 依赖 Task2/3——逐任务可编译。✅
