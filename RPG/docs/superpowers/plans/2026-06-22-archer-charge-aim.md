# 弓箭手蓄力瞄准射击（轻量准心）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans。步骤用 checkbox 跟踪。Unity 编译/Play 由开发者手动完成；Claude 不声称"能跑"，只保证静态逻辑正确。

**Goal:** 弓箭手蓄力期间屏幕中心显示准心、角色转向相机水平朝向；松开时从屏幕中心发射线求命中点，蓄力箭直线（关重力）飞向该点——"看哪打哪"。普通点射不变。

**Architecture:** 保留现有轨道相机。准心显隐经 EventBus 解耦：蓄力态发 `AimStateChangedEvent`，`Game.Rendering` 的 `CrosshairUI` 订阅并 SetActive 准心。发射方向由 `PlayerChargeAttackState` 在松开时用 `Camera.ViewportPointToRay(屏幕中心)` + `Physics.Raycast` 求目标点算出，`Arrow` 加一个"本发是否走重力"开关以支持直线飞行。

**Tech Stack:** Unity 6.3 / C# / `Game.Core`（事件）/ `Game.Combat`（Arrow、ChargeAttackDefinition）/ `Game.Character`（base 属性、ArcherController、PlayerChargeAttackState）/ `Game.Rendering`（CrosshairUI）。

## Global Constraints
- **零回归**：Warrior、Phase 3 普攻、locomotion 不受影响。改动均为**追加**：`Arrow.Init` 加默认参数（旧调用不变）、base 加 `MainCamera` 属性、`ChargeAttackDefinition` 加字段。
- **asmdef 方向**：`Game.Rendering → Game.Core`（已确认引用）；`Game.Character → Game.Combat/Core`；`Game.Combat → Game.Core`；`Game.Core` 不依赖上层。准心 UI 经 EventBus，Character 不引用 Rendering。
- 不用 `Debug.Log`，用 `GameLog`；命名 `_camelCase`/`PascalCase`；声明命名空间。
- 热路径零 GC：状态 Update 每帧不 `new`/LINQ/装箱；`Physics.Raycast`（单命中 `out RaycastHit`）与 `Object.Instantiate` 仅在松开放箭时各一次（非每帧）。
- 事件须 `struct` + `IGameEvent`。
- 编译/Play 由开发者完成。

设计依据：本轮 brainstorm——轻量瞄准（保留轨道相机 + 准心 + 角色转向 + 精确命中准心）。

---

## File Structure
- **Create** `Assets/_Project/Scripts/Core/Events/AimStateChangedEvent.cs`（T1）
- **Modify** `Assets/_Project/Scripts/Combat/Arrow.cs` — Init 加 `useGravity`（T2）
- **Modify** `Assets/_Project/Scripts/Combat/ChargeAttackDefinition.cs` — 加 `AimMaxDistance`（T2）
- **Modify** `Assets/_Project/Scripts/Character/PlayerControllerBase.cs` — 暴露 `MainCamera`（T3）
- **Modify** `Assets/_Project/Scripts/Character/ArcherController.cs` — 加 `_aimMask`（T4）
- **Modify** `Assets/_Project/Scripts/Character/States/PlayerChargeAttackState.cs` — 瞄准转向 + 准心事件 + 射线求向直线发射（T4）
- **Create** `Assets/_Project/Scripts/Rendering/CrosshairUI.cs`（T5）

提交编译顺序：T1、T2、T3 各自独立；T4 依赖 T1/T2/T3；T5 依赖 T1。

> 注：`Core/Events/` 子目录若不存在，新建即可（纯放 `.cs`，`.meta` 由 Unity 生成）。

---

## Task 1：AimStateChangedEvent（Game.Core）

**Files:** Create `Assets/_Project/Scripts/Core/Events/AimStateChangedEvent.cs`

**Interfaces:** Produces `struct AimStateChangedEvent : IGameEvent { bool Active; }`。

- [ ] **Step 1：创建文件**

```csharp
namespace Game.Core
{
    /// <summary>
    /// 瞄准状态变化（弓箭手蓄力进入/退出瞄准）。表现层（准心 UI）据此显隐，
    /// gameplay 与 UI 经 EventBus 解耦——发布方 Game.Character，订阅方 Game.Rendering。
    /// </summary>
    public struct AimStateChangedEvent : IGameEvent
    {
        public bool Active; // true=进入瞄准（显示准心）；false=退出
    }
}
```

- [ ] **Step 2：自检**：`struct` + `IGameEvent`；命名空间 `Game.Core`；无 UnityEngine 依赖。
- [ ] **Step 3：提交**

```bash
git add Assets/_Project/Scripts/Core/Events/AimStateChangedEvent.cs
git commit -m "feat(core): add AimStateChangedEvent for crosshair toggle"
```

---

## Task 2：Combat 追加（Arrow 重力开关 + AimMaxDistance）

**Files:** Modify `Assets/_Project/Scripts/Combat/Arrow.cs`、`Assets/_Project/Scripts/Combat/ChargeAttackDefinition.cs`

**Interfaces:** `Arrow.Init(..., bool useGravity = true)`；`ChargeAttackDefinition.AimMaxDistance`。

- [ ] **Step 1：Arrow.Init 加重力开关**

签名末尾加 `bool useGravity = true`：

```csharp
        public void Init(byte attackerTeam, int attackerId, float damage, DamageType type,
                         Vector3 velocity, Collider shooterCollider, bool useGravity = true)
```

在设 `linearVelocity` 之前加一行（紧接 `_rb`/`_collider` 兜底取值之后）：

```csharp
            _rb.useGravity = useGravity; // 瞄准直射(false)：关重力走直线命中准心；普通箭(true)：保留抛物线
```

（其余不变。默认 `true` → 现有调用方[普通箭/Phase4 旧蓄力]行为不变。）

- [ ] **Step 2：ChargeAttackDefinition 加 AimMaxDistance**

在 `Type` 字段前（或文件内任意 Header 处）加：

```csharp
        [Header("瞄准 (蓄力精确命中)")]
        [Tooltip("屏幕中心射线最大距离；未命中任何碰撞体时取此距离的远点作为目标")]
        public float AimMaxDistance = 100f;
```

- [ ] **Step 3：自检**：`useGravity` 默认 true、仅追加；旧 `Init` 调用（PlayerBowAttackState、本阶段前的 PlayerChargeAttackState）无需改即可编译；AimMaxDistance 纯数据。
- [ ] **Step 4：提交**

```bash
git add Assets/_Project/Scripts/Combat/Arrow.cs Assets/_Project/Scripts/Combat/ChargeAttackDefinition.cs
git commit -m "feat(combat): Arrow gravity toggle + ChargeAttackDefinition.AimMaxDistance"
```

---

## Task 3：基类暴露 MainCamera

**Files:** Modify `Assets/_Project/Scripts/Character/PlayerControllerBase.cs`

**Interfaces:** `public Camera MainCamera => _mainCamera;`

- [ ] **Step 1：加属性**（放在 `Animator` 等组件属性附近）

```csharp
        public Camera MainCamera => _mainCamera; // 暴露给瞄准（屏幕中心射线 + 转向相机朝向）
```

- [ ] **Step 2：自检**：`_mainCamera` 已是基类字段；仅追加属性，不改生命周期。
- [ ] **Step 3：提交**

```bash
git add Assets/_Project/Scripts/Character/PlayerControllerBase.cs
git commit -m "feat(character): expose MainCamera on PlayerControllerBase for aiming"
```

---

## Task 4：ArcherController 瞄准层 + PlayerChargeAttackState 瞄准逻辑

**Files:** Modify `ArcherController.cs`、`PlayerChargeAttackState.cs`

**Interfaces:** Consumes T1 `AimStateChangedEvent`、T2 `Arrow.Init(useGravity)`/`AimMaxDistance`、T3 `MainCamera`；`ArcherController.AimMask`。

- [ ] **Step 1：ArcherController 加 `_aimMask`**

在 `[Header("Charge Attack")]` 块内加字段：

```csharp
        [SerializeField] private LayerMask _aimMask = ~0; // 瞄准射线可命中层（务必排除 Player 层，免射到自己）
```

加属性（与其它 public 属性放一起）：

```csharp
        public LayerMask AimMask => _aimMask;
```

- [ ] **Step 2：PlayerChargeAttackState —— Enter 发准心事件**

在 `Enter()` 的空连段守卫**之后**、`CrossFadeInFixedTime(_archer.ChargeDrawHash...)` 处，CrossFade 后加：

```csharp
            _player.Animator.CrossFadeInFixedTime(_archer.ChargeDrawHash, CrossFadeDuration, 0);
            EventBus<AimStateChangedEvent>.Publish(new AimStateChangedEvent { Active = true }); // 显示准心
```

（守卫失败走 `TransitionToMovement`→`Exit`，Exit 发 false，不会误显。）

- [ ] **Step 3：PlayerChargeAttackState —— Update 蓄力期间转向相机**

在 `Update()` 的 `if (!_released) { ... }` 分支里、累计蓄力之后加一行调用：

```csharp
            if (!_released)
            {
                float max = _archer.ChargeData.MaxChargeTime;
                _chargeElapsed += Time.deltaTime;
                if (_chargeElapsed > max) _chargeElapsed = max;

                HandleAimRotation(); // 蓄力期间角色转向相机水平朝向

                if (!_player.IsAttackHeld)
                    Release();
                return;
            }
```

并加方法：

```csharp
        /// <summary>蓄力期间把角色平滑转向相机的水平朝向（只 yaw；箭的实际方向另在松开时按射线算，含俯仰）。</summary>
        private void HandleAimRotation()
        {
            Camera cam = _player.MainCamera;
            if (cam == null) return;
            Vector3 f = cam.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return;
            Quaternion target = Quaternion.LookRotation(f);
            _player.transform.rotation = Quaternion.Slerp(
                _player.transform.rotation, target, _player.RotationSpeed * Time.deltaTime);
        }
```

- [ ] **Step 4：PlayerChargeAttackState —— 重写 SpawnChargedArrow 为射线求向 + 直线发射**

整方法替换为：

```csharp
        private void SpawnChargedArrow(ChargeAttackDefinition data)
        {
            if (_archer.ArrowPrefab == null || _archer.ArrowSpawnPoint == null)
            {
                GameLog.Warn("弓箭手 ArrowPrefab/ArrowSpawnPoint 未配置，无法生成箭矢", "Combat");
                return;
            }

            Transform sp = _archer.ArrowSpawnPoint;

            // 屏幕中心发射线求命中点：命中 → 该点；未命中 → 相机朝向 AimMaxDistance 远点
            Camera cam = _player.MainCamera;
            Vector3 targetPoint;
            if (cam != null)
            {
                Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
                targetPoint = Physics.Raycast(ray, out RaycastHit hit, data.AimMaxDistance,
                                              _archer.AimMask, QueryTriggerInteraction.Ignore)
                    ? hit.point
                    : ray.GetPoint(data.AimMaxDistance);
            }
            else
            {
                targetPoint = sp.position + _player.transform.forward * data.AimMaxDistance;
            }

            // 从生成点直线指向目标点（含俯仰）；退化兜底用角色前向
            Vector3 dir = targetPoint - sp.position;
            if (dir.sqrMagnitude < 1e-6f) dir = _player.transform.forward;
            dir.Normalize();

            GameObject go = Object.Instantiate(_archer.ArrowPrefab, sp.position, Quaternion.LookRotation(dir));
            Arrow arrow = go.GetComponent<Arrow>();
            if (arrow == null)
            {
                GameLog.Warn("ArrowPrefab 上没有 Arrow 组件", "Combat");
                return;
            }

            float damage = Mathf.Lerp(data.MinDamage, data.MaxDamage, _ratio);
            float speed = Mathf.Lerp(data.MinSpeed, data.MaxSpeed, _ratio);
            byte team = _archer.Health != null ? _archer.Health.TeamId : (byte)0;
            int attackerId = _player.gameObject.GetInstanceID();
            // 瞄准直射：关重力，直线命中准心点
            arrow.Init(team, attackerId, damage, data.Type, dir * speed, _player.CharacterController, false);
        }
```

- [ ] **Step 5：PlayerChargeAttackState —— Exit 发准心事件（隐藏）**

`Exit()` 内加（任何离开蓄力态都隐准心）：

```csharp
        public override void Exit()
        {
            _player.AttackBufferCounter = 0f;
            EventBus<AimStateChangedEvent>.Publish(new AimStateChangedEvent { Active = false }); // 隐藏准心
        }
```

- [ ] **Step 6：自检**
  - `PlayerChargeAttackState` 顶部 `using Game.Core;` 已在（GameLog 用）→ `EventBus`/`AimStateChangedEvent` 可用。
  - Enter 发 true（守卫后）、Exit 发 false（覆盖所有退出，幂等）；蓄力期 `HandleAimRotation` 只改 yaw；发射方向用射线（含俯仰），与身体 yaw 解耦。
  - `Physics.Raycast(out RaycastHit)` 单命中、不分配；`_archer.AimMask` 来自 T4 Step1。
  - 放箭 `Init(..., false)` 关重力直射；普通箭路径未动。
  - asmdef：仅 `Game.Character → Game.Combat/Core`。
- [ ] **Step 7：提交**

```bash
git add Assets/_Project/Scripts/Character/ArcherController.cs Assets/_Project/Scripts/Character/States/PlayerChargeAttackState.cs
git commit -m "feat(character): charge aim — face camera, screen-center raycast, straight aimed shot + crosshair events"
```

---

## Task 5：CrosshairUI（Game.Rendering）

**Files:** Create `Assets/_Project/Scripts/Rendering/CrosshairUI.cs`

**Interfaces:** Consumes T1 `AimStateChangedEvent`。订阅 → SetActive 准心。

- [ ] **Step 1：创建文件**

```csharp
using UnityEngine;
using Game.Core;

namespace Game.Rendering
{
    /// <summary>
    /// 准心 UI：订阅 AimStateChangedEvent，按瞄准状态显隐准心 GameObject。
    /// 经 EventBus 与 gameplay 解耦——不引用 Game.Character。SetActive 一个 GameObject，无需 UGUI 程序集引用。
    /// </summary>
    public class CrosshairUI : MonoBehaviour
    {
        [SerializeField] private GameObject _crosshair; // 准心根物体（默认隐藏）

        private void Awake()
        {
            if (_crosshair != null) _crosshair.SetActive(false);
        }

        private void OnEnable()  => EventBus<AimStateChangedEvent>.Subscribe(OnAimStateChanged);
        private void OnDisable() => EventBus<AimStateChangedEvent>.Unsubscribe(OnAimStateChanged);

        private void OnAimStateChanged(AimStateChangedEvent e)
        {
            if (_crosshair != null) _crosshair.SetActive(e.Active);
        }
    }
}
```

- [ ] **Step 2：自检**：命名空间 `Game.Rendering`（依赖 Core 已确认）；订阅/退订成对；仅 SetActive，无 UGUI 类型依赖。
- [ ] **Step 3：提交**

```bash
git add Assets/_Project/Scripts/Rendering/CrosshairUI.cs
git commit -m "feat(rendering): add CrosshairUI toggling crosshair on AimStateChangedEvent"
```

---

## Task 6：开发者 Editor 配置（不可由 Claude 代劳）

- [ ] **Step 1：准心 UI**
  - 建一个 Screen Space - Overlay 的 `Canvas`，下挂一个居中的准心 `Image`（十字/点）。
  - 在 Canvas（或一个管理物体）上加 `CrosshairUI` 组件，`_crosshair` 指向准心 Image 的 GameObject（或其父）。默认会被 `Awake` 隐藏。
- [ ] **Step 2：瞄准层**
  - 把弓箭手 `BowPlayer` 放到一个独立层（如 `Player`）。
  - `ArcherController._aimMask` 设为"包含环境+敌人、排除 Player 层"，避免屏幕中心射线打到自己。
- [ ] **Step 3：数据**
  - `ChargeAttackDefinition` 资产填 `AimMaxDistance`（如 100）。

---

## Task 7：Play 验收 + 提交资产（验收开发者做，提交 Claude 做）

- [ ] **Step 1：编译**：聚焦 Unity，Console 无报错。
- [ ] **Step 2：Play 验收**
  1. **点按**左键 → 仍是普通点射（无准心、保留抛物线）
  2. **按住**进入蓄力 → 屏幕中心出现准心，角色转向对准相机水平朝向
  3. 移动鼠标改变视角 → 准心始终屏幕中心、角色跟随转向
  4. 松开 → 蓄力箭**直线**飞向准心所指点（含上下俯仰），精确命中该处目标
  5. 准心所指为远处空旷 → 箭朝该方向直飞 AimMaxDistance
  6. 蓄力箭不会射到自己（_aimMask 排除 Player）；命中敌人扣血、命中后销毁
  7. 蓄力结束（放箭播完）→ 准心消失、回到 Idle/Run
  8. Profiler：松开瞬间一次 Raycast + 一次 Instantiate，状态 Update 每帧零 GC
- [ ] **Step 3：告知 Claude**：通过 → 提交资产；不通过 → 描述现象排查。

---

## Task 8：提交资产改动（Claude，在验收通过后）

- [ ] **Step 1：核对**：`git status --short` —— 新增 Canvas/准心（场景或预制体）、`SampleScene.unity`（CrosshairUI、_aimMask、layer）、`ChargeAttackDefinition` 资产（AimMaxDistance）、新脚本 `.meta`；无意外 `.cs` 改动；Warrior 资产未动。
- [ ] **Step 2：提交**（按实际 status 调整路径）

```bash
git add Assets/_Project/Scenes/SampleScene.unity Assets/_Project/ScriptableObjects Assets/_Project/Scripts/Core/Events/AimStateChangedEvent.cs.meta Assets/_Project/Scripts/Rendering/CrosshairUI.cs.meta
git commit -m "feat(rendering): wire crosshair Canvas + aim mask + AimMaxDistance (charge aim)"
```

完成后弓箭手蓄力重击具备"看哪打哪 + 准心"瞄准射击。
