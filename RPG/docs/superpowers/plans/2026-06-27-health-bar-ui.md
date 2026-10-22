# 血条 UI 系统 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给每个敌人加头顶世界空间血条、给玩家加屏幕底部 HUD 血条，受击后实时掉血（敌人瞬变、玩家平滑滑动）。

**Architecture:** 新建 `Game.UI` 程序集（引用 `Game.Core` + `Game.Combat` + `UnityEngine.UI`）承载两个血条脚本。混合耦合：敌人条挂在敌人预制体上、直接引用同体 `HealthComponent`（自包含、随敌人生灭）；玩家 HUD 在屏幕空间 Canvas 上、Inspector 拖入玩家 `HealthComponent`。两者都订阅现有的 `DamageReceivedEvent`（按 `TargetId` 过滤）驱动填充更新，复用既有事件、不新增事件。

**Tech Stack:** Unity 6.3 / URP / C# / Unity Input System / UGUI（`Image.fillAmount`, `Type=Filled Horizontal`）/ `Game.Core.EventBus<T>`。

## Global Constraints

以下为项目级铁律，每个 Task 隐含遵守（逐条来自 CLAUDE.md）：

- 每个脚本必须声明与程序集匹配的命名空间：`Game.UI`。
- 模块隔离：跨模块通信只走 `Game.Core` 的 `EventBus<T>`；`gameplay 永不引用表现层`（本计划只让 UI 单向引用 Combat，反向无）。
- 性能：`Update()`/`LateUpdate()` 等每帧热路径内禁止 `new`、LINQ、装箱。状态在 `Awake` 一次性准备好、复用。
- 事件必须是 `struct` + `IGameEvent`；`OnEnable` 订阅、`OnDisable` 退订。本计划复用既有 `DamageReceivedEvent`/`DeathEvent`，不新增事件类型。
- 日志只用 `GameLog.Info/Warn/Error`，禁止 `Debug.Log`。
- 字段 `_camelCase`，公开成员/类型 `PascalCase`，接口 `IXxx`。
- **不创建 `.meta` 文件**（Unity 自动生成）；`.asmdef` 属于可编辑的纯文本配置，由本计划创建。
- Claude 只改 `.cs` 与纯文本配置；**编译、Canvas/预制体拼装、Play 模式测试由开发者在 Unity Editor 完成**。每个 Task 的"验证"步骤即开发者在 Editor 中的手动核对。
- 提交信息用 Conventional Commits，结尾加 `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`。

## File Structure

| 文件 | 职责 | 动作 |
|------|------|------|
| `Assets/_Project/Scripts/UI/Game.UI.asmdef` | 新程序集：承载需要 gameplay 数据的 HUD（引用 Core+Combat+UGUI） | Create |
| `Assets/_Project/Scripts/Combat/Damage/HealthComponent.cs` | 暴露 `Id`（供血条按 `TargetId` 过滤） | Modify |
| `Assets/_Project/Scripts/UI/EnemyHealthBar.cs` | 敌人头顶世界空间血条：直接引用同体 HealthComponent、按 id 更新填充、告示牌、死亡隐藏 | Create |
| `Assets/_Project/Scripts/UI/PlayerHealthBar.cs` | 玩家屏幕 HUD 血条：拖入玩家 HealthComponent、按 id 更新目标填充、Update 平滑滑动 | Create |

> 测试说明：本项目无自动化 PlayMode 测试基建（MonoBehaviour/UGUI 由开发者在 Editor 验证，见 CLAUDE.md）。因此各 Task 的"验证"为开发者在 Editor 编译 + Play 模式核对，而非 `dotnet test`/NUnit。逻辑足够简单（一次除法 + clamp），不值得单独 EditMode 测试。

---

### Task 1: 新建 Game.UI 程序集 + 暴露 HealthComponent.Id

**Files:**
- Create: `Assets/_Project/Scripts/UI/Game.UI.asmdef`
- Modify: `Assets/_Project/Scripts/Combat/Damage/HealthComponent.cs`

**Interfaces:**
- Consumes: 无（基础设施任务）。
- Produces:
  - 程序集 `Game.UI`，引用 `Game.Core`、`Game.Combat`、`UnityEngine.UI`。
  - `HealthComponent.Id` —— `public int Id => _id;`（`_id == gameObject.GetInstanceID()`，与 `DamageReceivedEvent.TargetId` / `DeathEvent.TargetId` 同源，供血条过滤）。

- [ ] **Step 1: 创建 Game.UI.asmdef**

新建 `Assets/_Project/Scripts/UI/Game.UI.asmdef`，内容：

```json
{
    "name": "Game.UI",
    "rootNamespace": "",
    "references": [
        "Game.Core",
        "Game.Combat",
        "UnityEngine.UI"
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

- [ ] **Step 2: 给 HealthComponent 暴露 Id**

在 `HealthComponent.cs` 现有公开属性区（`public float MaxHp => _maxHp;` 一行下面）追加：

```csharp
        public int Id => _id;   // = gameObject.GetInstanceID()，与 DamageReceivedEvent.TargetId 同源，供血条/表现层按 id 过滤
```

- [ ] **Step 3: 开发者在 Editor 验证**

打开 Unity，等待编译。验证：
- Console 无编译错误。
- 选中 `Game.UI.asmdef`，Inspector 的 **Assembly Definition References** 三项（Game.Core、Game.Combat、UnityEngine.UI）全部解析成功（不显示 "Missing Reference"）。若 `UnityEngine.UI` 未自动解析，在该面板手动点 `+` 添加后 Apply。

Expected: 编译通过、三个引用均解析。

- [ ] **Step 4: Commit**

```bash
git add "Assets/_Project/Scripts/UI/Game.UI.asmdef" "Assets/_Project/Scripts/Combat/Damage/HealthComponent.cs"
git commit -m "feat(ui): add Game.UI assembly and expose HealthComponent.Id

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: EnemyHealthBar —— 敌人头顶世界空间血条脚本

**Files:**
- Create: `Assets/_Project/Scripts/UI/EnemyHealthBar.cs`

**Interfaces:**
- Consumes:
  - `Game.Combat.HealthComponent`（`Id`、`CurrentHp`、`MaxHp`）。
  - `Game.Combat.DamageReceivedEvent`（`TargetId`、`RemainingHp`）、`Game.Combat.DeathEvent`（`TargetId`）。
  - `Game.Core.EventBus<T>`。
- Produces: `EnemyHealthBar` MonoBehaviour，序列化字段 `_fill`(Image)、`_billboardRoot`(Transform)、`_health`(HealthComponent，可空=自动取父)。

- [ ] **Step 1: 写 EnemyHealthBar.cs**

```csharp
using UnityEngine;
using UnityEngine.UI;
using Game.Core;
using Game.Combat;

namespace Game.UI
{
    /// <summary>
    /// 敌人头顶世界空间血条。挂在敌人预制体的世界空间 Canvas 子物体上：
    /// 直接引用同体 HealthComponent 拿血量（自包含、随敌人生灭、零路由）；
    /// 订阅 DamageReceivedEvent 按 id 把填充瞬间设为 RemainingHp/MaxHp；
    /// LateUpdate 做告示牌正对相机；DeathEvent 命中自己 id 时隐藏（敌人对象随后被销毁，血条自动消失）。
    /// </summary>
    public class EnemyHealthBar : MonoBehaviour
    {
        [SerializeField] private Image _fill;               // 填充图（HP_line，Image Type=Filled, Horizontal）
        [SerializeField] private Transform _billboardRoot;  // 需朝向相机的根（一般是血条 Canvas 自身）；空 → 用自身 transform
        [SerializeField] private HealthComponent _health;   // 自己敌人的血量；空 → Awake 自动 GetComponentInParent

        private Camera _camera;

        private void Awake()
        {
            if (_health == null) _health = GetComponentInParent<HealthComponent>();
            if (_billboardRoot == null) _billboardRoot = transform;
            if (_health == null)
                GameLog.Warn("EnemyHealthBar 未找到 HealthComponent，血条不会更新", "UI");
        }

        private void OnEnable()
        {
            EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
            EventBus<DeathEvent>.Subscribe(OnDeath);
        }

        private void OnDisable()
        {
            EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);
            EventBus<DeathEvent>.Unsubscribe(OnDeath);
        }

        private void Start()
        {
            _camera = Camera.main;
            if (_health != null) SetFill(_health.CurrentHp); // 初始按当前血量
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (_health == null || e.TargetId != _health.Id) return;
            SetFill(e.RemainingHp); // 敌人条瞬变，不平滑
        }

        private void OnDeath(DeathEvent e)
        {
            if (_health == null || e.TargetId != _health.Id) return;
            gameObject.SetActive(false); // 立刻隐藏空血条；敌人对象随后被 CharacterCombatFeedback 销毁
        }

        private void SetFill(float currentHp)
        {
            if (_fill == null || _health == null || _health.MaxHp <= 0f) return;
            _fill.fillAmount = Mathf.Clamp01(currentHp / _health.MaxHp);
        }

        private void LateUpdate()
        {
            if (_camera == null)
            {
                _camera = Camera.main; // 相机可能晚于敌人生成，重试缓存
                if (_camera == null) return;
            }
            // 告示牌：血条朝向与相机一致（正对屏幕，避免边缘透视扭曲）
            _billboardRoot.forward = _camera.transform.forward;
        }
    }
}
```

- [ ] **Step 2: 开发者在 Editor 验证编译**

打开 Unity 等待编译。Expected: Console 无编译错误（脚本预制体拼装在 Task 4）。

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Project/Scripts/UI/EnemyHealthBar.cs"
git commit -m "feat(ui): add EnemyHealthBar world-space health bar

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: PlayerHealthBar —— 玩家屏幕 HUD 血条脚本（平滑滑动）

**Files:**
- Create: `Assets/_Project/Scripts/UI/PlayerHealthBar.cs`

**Interfaces:**
- Consumes:
  - `Game.Combat.HealthComponent`（`Id`、`CurrentHp`、`MaxHp`）。
  - `Game.Combat.DamageReceivedEvent`（`TargetId`、`RemainingHp`）。
  - `Game.Core.EventBus<T>`。
- Produces: `PlayerHealthBar` MonoBehaviour，序列化字段 `_fill`(Image)、`_playerHealth`(HealthComponent)、`_lerpSpeed`(float)。

- [ ] **Step 1: 写 PlayerHealthBar.cs**

```csharp
using UnityEngine;
using UnityEngine.UI;
using Game.Core;
using Game.Combat;

namespace Game.UI
{
    /// <summary>
    /// 玩家屏幕底部 HUD 血条。挂在屏幕空间 Canvas 上：
    /// Inspector 拖入玩家 HealthComponent（拿 Max + 识别 id），订阅 DamageReceivedEvent 更新"目标填充"，
    /// Update 用 MoveTowards 平滑滑动到目标值（类原神掉血手感）。
    /// </summary>
    public class PlayerHealthBar : MonoBehaviour
    {
        [SerializeField] private Image _fill;                    // 红填充（HP_line，Image Type=Filled, Horizontal）
        [SerializeField] private HealthComponent _playerHealth;  // 玩家血量（Inspector 拖入）
        [SerializeField] private float _lerpSpeed = 3f;          // 平滑速度（填充比例/秒；越大越快）

        private float _targetFill = 1f;   // 事件设定的目标比例
        private float _displayFill = 1f;  // 当前显示比例（向目标逼近）

        private void OnEnable()  => EventBus<DamageReceivedEvent>.Subscribe(OnDamageReceived);
        private void OnDisable() => EventBus<DamageReceivedEvent>.Unsubscribe(OnDamageReceived);

        private void Start()
        {
            if (_playerHealth == null)
            {
                GameLog.Warn("PlayerHealthBar 未指定玩家 HealthComponent，血条不会更新", "UI");
            }
            else if (_playerHealth.MaxHp > 0f)
            {
                _targetFill = _displayFill = Mathf.Clamp01(_playerHealth.CurrentHp / _playerHealth.MaxHp);
            }
            ApplyFill();
        }

        private void OnDamageReceived(DamageReceivedEvent e)
        {
            if (_playerHealth == null || e.TargetId != _playerHealth.Id || _playerHealth.MaxHp <= 0f) return;
            _targetFill = Mathf.Clamp01(e.RemainingHp / _playerHealth.MaxHp);
        }

        private void Update()
        {
            if (Mathf.Approximately(_displayFill, _targetFill)) return; // 到位后不再计算
            _displayFill = Mathf.MoveTowards(_displayFill, _targetFill, _lerpSpeed * Time.deltaTime);
            ApplyFill();
        }

        private void ApplyFill()
        {
            if (_fill != null) _fill.fillAmount = _displayFill;
        }
    }
}
```

- [ ] **Step 2: 开发者在 Editor 验证编译**

打开 Unity 等待编译。Expected: Console 无编译错误（Canvas 拼装在 Task 4）。

- [ ] **Step 3: Commit**

```bash
git add "Assets/_Project/Scripts/UI/PlayerHealthBar.cs"
git commit -m "feat(ui): add PlayerHealthBar HUD with smooth lerp

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: Editor 拼装 + Play 模式验证（开发者执行）

> 本 Task 全部在 Unity Editor 完成（无 .cs 改动）。前置：四张图 `HP_enemy_frame` / `HP_frame` / `HP_frame_bg` / `HP_line` 已在 `Assets/_Project/Art/UI/`。

**Files:** 无脚本改动；改动落在预制体 / 场景 / Sprite 导入设置（由 Unity 序列化）。

- [ ] **Step 1: 导入设置 —— 把四张图设为 Sprite**

选中 `HP_enemy_frame`、`HP_frame`、`HP_frame_bg`、`HP_line`，Inspector：
- **Texture Type** = `Sprite (2D and UI)` → Apply。
- `HP_line` 作为填充图，确认能在 Image 上选为 Source Image。

- [ ] **Step 2: 敌人头顶血条（世界空间 Canvas）**

在一个敌人预制体（如剑士/法师 Enemy）下：
1. 右键预制体根 → `UI > Canvas`，命名 `HealthBar`。
   - Canvas **Render Mode = World Space**。
   - 缩放调小（如 `Scale = 0.01,0.01,0.01`），`Rect Transform` 定位到头顶上方（如本地 `Y ≈ 2.2`）。
2. Canvas 下加三个 `UI > Image`，从下到上层级顺序：
   - `BG`：Source Image = `HP_frame_bg`（或 `HP_enemy_frame` 的暗底）。
   - `Fill`：Source Image = `HP_line`；**Image Type = Filled**，**Fill Method = Horizontal**，**Fill Origin = Left**。
   - `Frame`：Source Image = `HP_enemy_frame`（边框压在最上）。
3. 在 Canvas（`HealthBar`）上 `Add Component > Enemy Health Bar`：
   - `_fill` ← 拖 `Fill` Image。
   - `_billboardRoot` ← 留空（默认用 Canvas 自身）或拖 `HealthBar`。
   - `_health` ← 留空（自动 `GetComponentInParent`）或拖预制体根上的 HealthComponent。
4. Apply 预制体。对另一个敌人预制体重复（或把 `HealthBar` 子树复制过去）。

- [ ] **Step 3: 玩家屏幕 HUD 血条（屏幕空间 Canvas）**

在场景中：
1. 若没有就建 `UI > Canvas`（**Render Mode = Screen Space - Overlay**），命名 `PlayerHUD`（可与准心 Canvas 共用，但血条建议独立子物体）。
2. 在底部放一个容器（锚点设到屏幕底部中间），加三个 `UI > Image`：
   - `BG`：`HP_frame_bg`。
   - `Fill`：`HP_line`；**Image Type = Filled, Horizontal, Origin = Left**。
   - `Frame`：`HP_frame`（压最上）。
3. 在容器（或 `PlayerHUD`）上 `Add Component > Player Health Bar`：
   - `_fill` ← 拖 `Fill` Image。
   - `_playerHealth` ← 拖**场景里玩家对象**上的 HealthComponent。
   - `_lerpSpeed` ← 默认 3（手感快慢按需调）。

- [ ] **Step 4: Play 模式验证**

进入 Play：
- 每个敌人头顶有血条，且**始终正对相机**（移动/转视角时不歪）。
- 打敌人 → 敌人头顶血条**瞬间**下降到对应比例；打死 → 血条立即隐藏，敌人播死亡动画后消失。
- 让敌人打到玩家（或临时调试触发）→ 屏幕底部玩家血条**平滑滑动**下降。
- Profiler 确认无每帧 GC Alloc（血条更新走事件、不每帧分配）。

Expected: 三项表现符合，零 GC。

- [ ] **Step 5: Commit（开发者，确认无误后）**

```bash
git add -A
git commit -m "feat(ui): assemble enemy/player health bar prefabs and HUD

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**1. Spec coverage（对照设计草案逐条）：**
- 敌人头顶血条框 → Task 2（脚本）+ Task 4 Step 2（拼装）。✅
- 玩家屏幕底部血条（类原神）→ Task 3 + Task 4 Step 3。✅
- 被攻击掉血 → 两个脚本订阅 `DamageReceivedEvent` 更新填充。✅
- 混合耦合（敌人直接引用 / 玩家事件驱动 + 拖引用）→ Task 2/3 实现。✅
- 敌人条全程常驻 → Task 2 无显隐逻辑，仅死亡隐藏。✅
- 玩家条平滑滑动 → Task 3 `MoveTowards`。✅
- 新建 Game.UI 程序集 + 暴露 Id → Task 1。✅
- 资源映射 / Sprite 导入 → Task 4 Step 1。✅
- 死亡时血条自动消失 → Task 2 `OnDeath` 隐藏 + 敌人对象销毁。✅

**2. Placeholder scan：** 无 TBD/TODO；所有 .cs 步骤给出完整代码；Editor 步骤给出逐项操作。✅

**3. Type consistency：**
- `HealthComponent.Id`（Task 1 定义）在 Task 2/3 用作 `e.TargetId != _health.Id` 过滤，签名一致。✅
- `DamageReceivedEvent.TargetId`/`RemainingHp`、`DeathEvent.TargetId` 字段名与现有事件定义一致（已核对源文件）。✅
- `_fill.fillAmount`（UGUI `Image.fillAmount`）依赖 Image Type=Filled，已在 Task 4 拼装步骤要求。✅
- 命名空间统一 `Game.UI`，与 asmdef `name` 匹配。✅

无遗漏，无占位符，类型一致。
