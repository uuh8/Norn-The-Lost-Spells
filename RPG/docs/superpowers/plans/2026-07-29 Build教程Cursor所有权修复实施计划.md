# Build 教程 Cursor 所有权修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan inline. 本计划禁止使用 SubAgent。

**Goal:** 修复 Standalone Build 从 Main Menu 进入 P7 后，Gameplay Guide 已打开但 Cursor 被玩家控制器再次锁定，导致无法点击“下一页”的问题。

**Architecture:** `RunPauseCoordinator` 成为 Gameplay Scene 的唯一 Cursor 状态所有者：无暂停时 `Locked + Hidden`，存在任意 `RunPauseReason` 时 `None + Visible`。`PlayerControllerBase` 只负责角色输入与 FSM，不再在 `Start()` 写入全局 Cursor 状态，从根源移除不同 `MonoBehaviour.Start()` 执行顺序造成的竞态。

**Tech Stack:** Unity 6.3、C#、UGUI、Unity Input System、NUnit EditMode / PlayMode Test、`Cursor.lockState`、`Cursor.visible`

> **执行记录（2026-07-29）：** 回归测试已先写入；生产代码与文档已完成；`Game.Run.Tests`、`Game.Character.Tests`、`Game.UI`、`Game.Run.PlayMode.Tests` 静态编译均为 0 Warning / 0 Error。项目被当前 Unity Editor 实例占用，BatchMode Test Runner 未实际执行，因此下面 Test Runner 与 Standalone Build Gate 保持未勾选。

## Global Constraints

- 只修复 Cursor；不修改 `Product Name`、Logo 或其他 PlayerSettings。
- 不使用 Script Execution Order 掩盖竞态。
- 不在 `Update/LateUpdate` 每帧重复写 Cursor。
- 保留 `RunPauseReason` 多所有者暂停语义。
- 不改动用户工作区中的其他未提交内容。

---

### Task 1：为 Gameplay Cursor 状态增加回归测试

**Files:**

- Modify: `Assets/_Project/Tests/Run/RunPauseCoordinatorTests.cs`

**Interfaces:**

- Consumes: `RunPauseCoordinator.RequestPause(RunPauseReason)`、`ReleasePause(RunPauseReason)`
- Produces: 对初始化、暂停和恢复三种 Cursor 状态的可执行回归约束

- [x] **Step 1：写入旧实现会失败的初始化测试**

在创建 Coordinator 前先模拟 Scene Transition 留下的 `CursorLockMode.None + visible=true`，然后 `AddComponent<RunPauseCoordinator>()`。断言 Coordinator 初始化后接管 Gameplay Cursor：

```csharp
[Test]
public void Awake_InitializesGameplayCursorToLockedAndHidden()
{
    Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
    Assert.That(Cursor.visible, Is.False);
}
```

- [x] **Step 2：补充暂停/恢复状态测试**

```csharp
[Test]
public void RequestAndReleasePause_OwnsCursorForUiAndGameplay()
{
    _coordinator.RequestPause(RunPauseReason.GameplayGuide);
    Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
    Assert.That(Cursor.visible, Is.True);

    _coordinator.ReleasePause(RunPauseReason.GameplayGuide);
    Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
    Assert.That(Cursor.visible, Is.False);
}
```

- [ ] **Step 3：运行测试，确认 RED**

优先在 Unity Test Runner 执行 `Game.Run.Tests.RunPauseCoordinatorTests`。旧实现没有初始化 Cursor 所有权，第一条测试应失败；若命令行无法在已打开的 Unity 项目上运行，只记录静态编译，保留给开发者执行真实 Test Runner，不能谎称 RED/PASS。

---

### Task 2：把 Gameplay Cursor 所有权集中到 RunPauseCoordinator

**Files:**

- Modify: `Assets/_Project/Scripts/Run/Runtime/RunPauseCoordinator.cs`
- Modify: `Assets/_Project/Scripts/Character/Controllers/PlayerControllerBase.cs`

**Interfaces:**

- Consumes: Scene 加载边界留下的 Cursor 状态、`RunPauseReason`
- Produces: `RunPauseCoordinator.Awake()` 初始化 Gameplay Cursor；Player Controller 不再覆盖 UI Cursor

- [x] **Step 1：在 RunPauseCoordinator.Awake 初始化 Gameplay 状态**

```csharp
private void Awake()
{
    ApplyGameplayCursorState();
}
```

其中 `ApplyGameplayCursorState()` 只在非暂停状态写入：

```csharp
private static void ApplyGameplayCursorState()
{
    Cursor.lockState = CursorLockMode.Locked;
    Cursor.visible = false;
}
```

- [x] **Step 2：复用统一方法恢复 Gameplay**

`RestoreGameplayState()` 继续恢复进入暂停前的非零 `Time.timeScale`，随后调用 `ApplyGameplayCursorState()`，避免相同 Cursor 规则散落。

- [x] **Step 3：删除 PlayerControllerBase.Start 的 Cursor 写入**

玩家 Controller 仍在 `Start()` 初始化朝向和 FSM，但不再读写全局 Cursor。注释说明 Cursor 由 `RunPauseCoordinator` 根据 Pause Reason 统一管理，防止 Guide/Wand/Pause Menu 与玩家生命周期发生执行顺序竞态。

- [ ] **Step 4：运行测试，确认 GREEN**

执行相关测试和 `.csproj` 静态编译。只有 Unity Test Runner 的真实结果可以写成 Test PASS；`.csproj` 只证明语法、类型与 Assembly 引用成立。

---

### Task 3：文档、Build 验证与回滚边界

**Files:**

- Modify: `docs/superpowers/plans/2026-07-28 P8-B双地图体验打磨实施计划.md`
- Modify: `Assets/_Project/Docs/2026-07-28 P8-B双地图体验打磨完整解析.md`

**Interfaces:**

- Consumes: Cursor 竞态的症状、根因、修复与验证结果
- Produces: 面向初学者的 Unity 生命周期与全局状态所有权 Debug 记录

- [x] **Step 1：记录完整 Debug 链**

记录：

```text
Main Menu 解锁 Cursor
-> Scene Transition 保持解锁
-> Gameplay Guide Start 申请暂停并显示 Cursor
-> PlayerControllerBase.Start 无条件重新锁定
-> Build 中 Start 顺序暴露竞态
```

- [x] **Step 2：静态验证**

运行：

```powershell
dotnet build Game.Run.Tests.csproj --no-restore
dotnet build Game.Character.Tests.csproj --no-restore
dotnet build Game.UI.csproj --no-restore
```

期望三项均为 0 Warning / 0 Error。

- [ ] **Step 3：Unity Editor 与 Standalone Build Gate**

开发者验证：

1. Main Menu Cursor 可见；
2. 点击开始进入 P7；
3. Guide 出现时 Cursor 可见、可点击上一页/下一页；
4. 点击“开始游戏”后 Cursor 锁定并隐藏，Mouse Look 恢复；
5. Tab Wand Editor、Escape Pause Menu 仍能解锁，关闭后重新锁定；
6. P7 -> P8 后 Gameplay Cursor 正常；
7. Standalone Build 重复上述路径。

回滚时恢复两个脚本和测试，不使用 Script Execution Order 或逐帧 Cursor 强制写入作为替代补丁。
