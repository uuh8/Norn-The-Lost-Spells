# Boss 环绕走位、移动施法与 Animator 分层：零基础教学

创建时间：2026-09-05

目标读者：没有系统学习过 Unity 角色动画、3D 向量和角色导航的初学者

对应功能：P8 Boss 双阈值追击、环绕走位、移动施法、`Animator Layer`、`AvatarMask` 与飞天 Bug 修复

项目版本：Unity 6.3，URP 17.3，`CharacterController` 负责角色碰撞移动，`NavMeshAgent` 负责路径规划

> 这份文档从最基础的概念开始。第一次阅读时不要求记住类名和字段名，先建立“谁决定行为、谁计算路线、谁真的移动、谁只负责画面”的心智模型。第二次阅读再结合最后的源码阅读路线逐步查看实现。

## 0. 读完后应该掌握什么

这个功能最终希望让玩家看到下面的结果：

1. 玩家离开安全区后，Boss 永久锁定玩家，不会因为离得远就脱战。
2. Boss 在较远处使用 `NavMesh` 绕开障碍追击玩家。
3. Boss 进入战斗距离后，不会只站在原地：
   - 太远时靠近；
   - 距离合适时围绕玩家左右移动；
   - 太近时斜向后撤。
4. Boss 施法期间仍能移动，下半身继续走路，上半身播放施法动作。
5. Boss 在斜坡、台阶、传送和转阶段期间始终能正确贴地，不会越走越高。

为了实现这些结果，系统实际上组合了四个相互独立的层次：

```text
行为决策层
Boss FSM：现在应该追击、战斗走位、施法、转阶段还是传送？
        ↓
移动意图与导航层
3D 数学产生期望方向，NavMeshAgent 计算可走路径和 Steering
        ↓
Gameplay 位移层
CharacterController.Move 写入真实 Transform，并处理碰撞与 Gravity
        ↓
动画表现层
世界速度转成本地速度 → Animator Parameters → Blend Tree / Animator Layer
```

这四层分开非常重要。例如，Boss 的 Gameplay 已经在横向移动，但如果 Animator 没有正确配置，玩家可能只会看到模型保持施法姿势在地面上“平移”。反过来，动画播放得很漂亮，也不代表 Boss 的碰撞体、NavMesh 位置和真实攻击逻辑一定正确。

---

## 1. 先认识 Unity 角色动画的基本零件

可以把一个 3D 角色想象成“外皮 + 木偶骨架 + 动作录像 + 播放控制器”。Unity 官方也把 `Animation Clip`、`Animator Controller`、`Avatar` 和 `Animator` 视为 Mecanim 动画系统的核心连接关系，可参考 [Unity 6 Mecanim 动画系统概述](https://docs.unity3d.com/cn/6000.0/Manual/AnimationOverview.html)。

### 1.1 Mesh：你看见的角色外皮

`Mesh` 是由顶点、三角形、法线、UV 等数据组成的几何表面。普通静态物体通常由 `MeshRenderer` 绘制；会随骨骼变形的角色通常由 `SkinnedMeshRenderer` 绘制。

“Skinned”可以理解为每个顶点都记录了自己受哪些骨骼影响，以及每根骨骼占多大权重。例如：

- 上臂附近的顶点主要受 `UpperArm` 骨骼控制；
- 肘部附近的顶点可能同时受 `UpperArm` 和 `LowerArm` 控制；
- 当两根骨骼旋转时，顶点按权重混合结果，因此关节不会像两块硬木头一样完全断开。

这个过程叫 `Skinning`。Animator 通常不直接移动每一个顶点，而是产生每根骨骼最终的姿势；`SkinnedMeshRenderer` 再利用骨骼矩阵让网格变形。

### 1.2 Skeleton 与 Bone：角色内部的层级骨架

`Skeleton` 是由许多 `Bone` 组成的父子 `Transform` 层级。一个简化的人形骨架可能是：

```text
root
└─ Hips
   ├─ Spine
   │  └─ Chest
   │     ├─ Neck → Head
   │     ├─ LeftShoulder → LeftArm → LeftHand
   │     └─ RightShoulder → RightArm → RightHand
   ├─ LeftUpperLeg → LeftLowerLeg → LeftFoot
   └─ RightUpperLeg → RightLowerLeg → RightFoot
```

父骨骼变化会影响所有子骨骼。假设 `Chest` 向右旋转，头、双肩、双臂都会一起转；双腿不会跟着转，因为它们不在 `Chest` 的子树中。这种层级关系就是之后 `AvatarMask` 能控制“只播放上半身”的基础。

每根骨骼保存的是相对于父骨骼的 `Local Position / Local Rotation / Local Scale`。这也是为什么修改父节点时，整个子树都会一起移动。

### 1.3 Animation Clip：随时间变化的动作数据

`Animation Clip` 可以理解成一段动作录像，但它保存的不是像素，而是随时间变化的动画曲线，例如：

```text
0.00 秒：右上臂旋转 5°
0.20 秒：右上臂旋转 45°
0.45 秒：右上臂旋转 80°
0.80 秒：右上臂回到 10°
```

Unity 每帧根据当前播放时间对曲线采样，计算这一帧所有骨骼的姿势。`Run_MagicWand`、`WalkLeft_MagicWand`、`WalkRight_MagicWand`、`Attack01_MagicWand` 都是不同的 Animation Clip。

一个 Clip 只回答“这一段动作中的骨骼如何变化”，它本身不会决定什么时候播放，也不会知道 Boss 正在追谁。

### 1.4 Avatar：不同人形骨架之间的翻译表

不同模型的骨骼名字和比例可能不一样：一个模型叫 `mixamorig:LeftArm`，另一个模型叫 `UpperArm_L`。`Humanoid Avatar` 会把这些模型自己的骨骼映射到 Unity 统一的人体定义，例如 Left Upper Arm、Right Lower Leg、Head。

有了这张映射表，同一套 Humanoid 动画就能被 `Retargeting` 到结构兼容的不同角色上。Avatar 不等于模型，也不等于动画；它承担“这个模型的哪根骨头对应标准人体的哪个部位”的翻译责任。

### 1.5 Animator Component：Scene 中真正执行动画的组件

`Animator` 是挂在 GameObject 上的 Runtime 组件。它通常持有：

- `Controller`：使用哪份 Animator Controller；
- `Avatar`：人形骨骼如何映射；
- `Apply Root Motion`：是否把动画中的根位移应用到 GameObject；
- `Update Mode`：动画在什么时间体系下更新；
- `Culling Mode`：不可见时是否继续计算动画。

在 Norn 的 Boss Prefab 中，Gameplay Root 上保存 `CharacterController`、`NavMeshAgent` 和 Boss 脚本，Animator 负责驱动角色视觉骨架。代码通过 `GetComponentInChildren<Animator>()` 缓存这个组件。

### 1.6 Animator Controller：动画的流程图资产

`.controller` 文件是一份 Project Asset。它保存：

- 动画状态 `State`；
- 状态之间的过渡 `Transition`；
- 控制过渡和混合的参数 `Parameter`；
- 一个或多个动画层 `Animator Layer`；
- `Blend Tree` 等混合结构。

当前 Boss 使用：

```text
Assets/_Project/Art/Animators/Enemy_Wizard.controller
```

Unity 的 Animator 窗口只是这份 Asset 的可视化编辑器。它可以在 `Window > Animation > Animator` 中打开。

---

## 2. Animator State Machine 与 Gameplay FSM 是两套系统

初学者最容易把这两个“状态机”当成一回事。

### 2.1 Gameplay FSM 管“角色正在做什么”

Boss 的 Gameplay 状态包括：

```text
Inactive         尚未开战
Approach         追向玩家
Decision         战斗走位并选择技能
Cast             施法计时、释放、恢复
PhaseTransition  转阶段
Teleport         传送
```

它们会影响真实规则：Boss 能否受伤、是否更新路径、是否释放法术、何时传送、是否持有 Action Lock。这套状态机由 C# 驱动。

### 2.2 Animator State Machine 管“角色看起来在做什么”

Animator 状态可能包括：

```text
Idle_Bow
CombatLocomotion
UpperBodyEmpty
UpperBodyCast
phase_Transition
Die_MagicWand
```

它们主要决定播放和混合哪些骨骼动画。

### 2.3 为什么不能强行一一对应

Boss 在 Gameplay 的 `Cast` 状态中，需要同时表现两件事：

- 下半身仍根据移动方向行走；
- 上半身播放施法动作。

如果规定“一个 Gameplay State 只能对应一个全身动画 State”，进入 Cast 后整个人只能播放施法 Clip，腿部移动动画会消失。这就是之前看见“角色保持施法姿势在地面平移”的原因。

当前设计允许状态交叉组合：

```text
Gameplay：Cast

Animator Base Layer：CombatLocomotion（腿部继续走）
Animator UpperBodyCast Layer：UpperBodyCast（上半身施法）
```

因此，`Action Lock` 也不等于 `Movement Lock`。当前 Action Lock 只保证 Cast、Teleport、PhaseTransition 等大型动作不会相互重叠；`BossCastState.Tick` 仍然每帧调用战斗移动，所以 Boss 可以边走边施法。

---

## 3. Animator Parameter：代码和动画图之间的数据接口

Animator Controller 不应该自己访问 Boss AI。C# 每帧把必要的结果写成参数，Animator 再消费这些参数。

当前重要参数如下：

| 参数 | 类型 | 谁写入 | 表示什么 | 谁消费 |
|---|---|---|---|---|
| `speed` | Float | `WizardBossController.SyncAnimator` | 当前真实水平速度大小 | Idle 与 CombatLocomotion 的过渡 |
| `moveX` | Float | `WizardBossController.SyncAnimator` | 角色本地空间中的左右移动分量 | 2D Blend Tree |
| `moveZ` | Float | `WizardBossController.SyncAnimator` | 角色本地空间中的前后移动分量 | 2D Blend Tree |
| `cast` | Trigger | `TriggerCastAnimation` | 这一刻开始一次施法表现 | UpperBodyCast Layer 的 Any State Transition |
| `phaseTransition` | Trigger | `TriggerPhaseTransitionAnimation` | 这一刻开始转阶段表现 | Base Layer 的转阶段状态 |

### 3.1 Float、Bool、Trigger 的区别

- `Float` 保存连续数值，适合速度、方向、瞄准角度。代码可以每帧更新。
- `Bool` 保存持续条件，适合“是否在地面”“是否处于战斗”。条件结束时必须写回 false。
- `Trigger` 表示一次性事件。代码调用 `SetTrigger` 后，Animator 在合适的过渡中消费它。它适合“开始攻击”“开始受击”这类瞬间指令。

如果用 Bool 代替 Cast Trigger，又忘记及时设回 false，Any State 过渡可能不断重新进入攻击。反过来，如果用 Trigger 表示持续移动方向，触发后无法表达方向一直变化。

### 3.2 为什么参数名必须完全一致

代码中的 `moveX` 和 Controller 中的 `moveX` 是通过字符串 Hash 对应的。大小写、下划线或拼写任意一个不同，Animator 都收不到正确数据。

当前代码把固定参数预先转换成整数 Hash：

```csharp
private static readonly int MoveXHash = Animator.StringToHash("moveX");
private static readonly int MoveZHash = Animator.StringToHash("moveZ");
```

这样做有两个原因：

1. 避免在每帧热路径中重复进行字符串查找；
2. 把参数接口集中在一个位置，便于检查。

---

## 4. 3D 坐标基础：世界方向和角色方向不是一回事

### 4.1 World Space：整个 Scene 共用的坐标系

Unity 3D 中通常使用：

```text
+X：世界右方
+Y：世界上方
+Z：世界前方
```

玩家和 Boss 的 `transform.position` 都是 World Space 位置。假设：

```text
Boss   = (0, 0, 0)
Player = (0, 0, 12)
```

玩家位于 Boss 的世界 +Z 方向 12 米处。

### 4.2 Local Space：以当前角色朝向为基准的坐标系

角色转身后，自己的“前方”也会改变。Local Space 仍约定：

```text
local +X：角色自己的右侧
local -X：角色自己的左侧
local +Z：角色自己的前方
local -Z：角色自己的后方
```

假设 Boss 面向玩家，但正沿圆周向世界 +X 移动。对 Boss 自己来说，这通常是“向右侧移”，所以动画应该播放 `WalkRight`，而不是世界意义上的“朝东走”。

这就是代码需要执行：

```csharp
Vector3 localVelocity = transform.InverseTransformDirection(worldVelocity);
```

`InverseTransformDirection` 把世界方向转换为角色自身方向。这里使用 Direction，而不是 Position，因为速度没有“位于哪里”的含义，只表示方向和大小。

### 4.3 为什么不能把世界 X/Z 直接传给 Blend Tree

如果直接把世界速度 X/Z 写给 Animator：

- Boss 面向世界 +Z 时，结果暂时正确；
- Boss 旋转 90° 后，同一个世界 +X 方向已经变成 Boss 的前方；
- Animator 仍会把它误判为向右走。

最终表现就是角色转身后左右、前后动画错乱。因此动画方向参数必须使用 Local Space。

---

## 5. 向量基础：从 Boss 指向玩家

### 5.1 两个位置相减得到方向向量

```csharp
Vector3 toTarget = targetPosition - bossPosition;
```

假设 Boss 在 `(2, 0, 3)`，玩家在 `(5, 0, 7)`：

```text
toTarget = (5,0,7) - (2,0,3)
         = (3,0,4)
```

这个向量同时表达：

- 方向：向 +X、+Z；
- 距离：`sqrt(3² + 4²) = 5`。

### 5.2 为什么先把 Y 清零

Boss 的追击和环绕是在地面 XZ 平面上做的：

```csharp
toTarget.y = 0f;
```

如果玩家站在高台上，而我们把高度差也放进水平移动方向，Boss 会得到一个向上的斜向量；它可能尝试把 Gameplay 位移朝空中推进。清零 Y 表示“距离判断和战斗移动只看地面投影”。

注意：这不代表 Boss 永远不需要处理 Y。水平 AI 和垂直 Gravity 是两个独立问题，后面会详细解释。

### 5.3 Magnitude 与 Normalized

`magnitude` 是向量长度，也就是距离：

```csharp
float distance = toTarget.magnitude;
```

归一化 `normalized` 会保留方向并把长度变成 1：

```text
(3,0,4) / 5 = (0.6,0,0.8)
```

单位方向乘以速度就能得到速度向量：

```text
velocity = direction × moveSpeed
```

如果不归一化，距离越远，向量本身越长，Boss 就会越跑越快。

---

## 6. 环绕玩家的数学：径向与切线

### 6.1 径向方向 Radial Direction

从 Boss 指向玩家的单位方向称为这里的径向方向：

```text
radial = normalize(playerPosition - bossPosition)
```

- 沿 `radial` 移动：靠近玩家；
- 沿 `-radial` 移动：远离玩家。

### 6.2 切线方向 Tangent Direction

在 XZ 平面中，把 `(x, 0, z)` 旋转 90°，可以得到：

```text
(z, 0, -x)
```

当前代码使用：

```csharp
Vector3 tangent = new Vector3(radial.z, 0f, -radial.x) * side;
```

为什么它是垂直方向？可以用 `Dot Product` 点积检查：

```text
radial · tangent
= x × z + z × (-x)
= xz - xz
= 0
```

两个非零向量点积为 0，表示它们互相垂直。沿切线移动时，Boss 不会直接靠近或远离玩家，而会沿玩家周围的圆周方向移动。

### 6.3 顺时针与逆时针

`side` 只取 `+1` 或 `-1`：

```text
+1：使用一个切线方向
-1：使用相反切线方向
```

Boss 每经过 `OrbitDirectionInterval` 会反转一次符号，因此不会永远只朝一个方向绕圈。当前数据约为 2 秒换向一次。

### 6.4 距离修正为什么要和切线混合

纯切线只能维持当前半径。如果 Boss 已经太远，它沿切线走一辈子也不会主动靠近；太近时同理。

因此当前规则分成三种模式：

| 与玩家距离 | 模式 | 主要方向 |
|---|---|---|
| 小于理想最小距离 | Retreat | 向外 + 少量切线 |
| 位于理想距离带 | Orbit | 纯切线 |
| 大于理想最大距离 | Approach | 向内 + 少量切线 |

修正方向和切线按下式组合：

```text
combined = correction + tangent × 0.45
finalDirection = normalize(combined)
```

`0.45` 是切线权重。它让靠近和后撤仍带一点斜向移动，避免 Boss 像沿直尺一样正对玩家前进或后退。

### 6.5 一个可手算的例子

假设 Boss 位于原点，玩家在 `(0,0,18)`：

```text
radial = (0,0,1)
tangent = (1,0,0)
```

若理想距离最大值是 16，18 米属于太远：

```text
combined = (0,0,1) + (1,0,0) × 0.45
         = (0.45,0,1)
```

归一化后，Boss 会以“主要向前、同时略向右”的方向接近玩家。若距离只有 8 米，则修正项改成 `(0,0,-1)`，Boss 会斜向后撤。

---

## 7. Hysteresis：为什么攻击进入距离和退出距离要不同

如果只使用一个 25 米阈值：

```text
24.99 米：进入战斗
25.01 米：回到追击
24.98 米：再次进入战斗
```

玩家和 Boss 每帧都在移动，浮点数也会有小误差，状态就可能在边界附近快速来回切换。这种现象叫边界抖动。

`Hysteresis` 的思路是让“进入”和“退出”使用不同阈值，并记住上一次状态：

```text
尚未进入战斗：距离 ≤ AttackEnterDistance 才进入
已经进入战斗：距离 > AttackExitDistance 才退出
```

当前 Boss 数据使用：

```text
AttackEnterDistance = 25
AttackExitDistance  = 35
```

于是 25～35 米成为稳定缓冲带：

- 从远处接近时，必须到 25 米内才进入战斗；
- 已经战斗后，即使回到 30 米也继续战斗走位；
- 只有超过 35 米才恢复追击。

这个状态记忆保存在 `_isCombatEngaged` 中。它是一个 Bool 类型的 Runtime State，由 `WizardBossController` 持有；`BossCombatMovementMath.ResolveCombatEngagement` 只接收旧值和距离，返回新值。纯逻辑不查询 Scene，因此可以稳定写 EditMode Test。

---

## 8. NavMeshAgent 与 CharacterController：一个算路，一个移动

### 8.1 NavMesh 是什么

`NavMesh` 是烘焙出来的可行走区域。它告诉 AI：

- 哪些地面能走；
- 哪些区域被墙或悬崖隔开；
- 从 A 到 B 应该经过哪些拐点。

NavMesh 并不是物理碰撞体，也不会自动替代 `CharacterController`。

### 8.2 NavMeshAgent 默认会做什么

普通情况下，`NavMeshAgent` 既维护内部导航位置，也会把结果写回 GameObject 的 Transform。Unity 的 `nextPosition` 文档说明，默认内部模拟位置会与 Transform 耦合；关闭 `updatePosition` 后需要调用方自己同步，可参考 [NavMeshAgent.nextPosition](https://docs.unity3d.com/ja/current/ScriptReference/AI.NavMeshAgent-nextPosition.html)。

如果同时让 Agent 和 CharacterController 修改 Transform，就会出现两个“移动权威”：

```text
NavMeshAgent：我认为角色应该在路径点 A
CharacterController：碰撞后角色只能停在位置 B
下一帧两边继续互相拉扯
```

可能表现为抖动、穿模、瞬移或 Agent 脱离 NavMesh。

### 8.3 Norn 当前的单一移动权威

当前架构明确分工：

```text
NavMeshAgent
  updatePosition = false
  updateRotation = false
  只输出路径、Local Avoidance 和 desiredVelocity

CharacterController
  接收速度
  执行 CharacterController.Move
  处理墙、坡面、台阶等碰撞约束
  成为唯一真实 Transform 位移写入者
```

移动结束后调用：

```csharp
agent.nextPosition = owner.position;
```

它把 Agent 的内部模拟位置重新对齐到 CharacterController 的最终位置。

### 8.4 Steering 是什么

路径规划给出一串拐点，`Steering` 是当前这一帧应该朝哪个方向移动的局部建议。`desiredVelocity` 已考虑：

- 当前路径方向；
- 停止距离；
- Agent 速度与加速度；
- 局部避障 `Local Avoidance`。

Boss Controller 读取这个建议，再交给 CharacterController。若测试环境没有 NavMesh，代码保留一条可诊断的直线降级路径；正式 P8 应使用 NavMesh Steering 绕障碍。

---

## 9. CharacterController 为什么不会自动受 Gravity 影响

Unity 官方明确说明：`CharacterController.Move` 不会自动使用重力；调用方传入的是这一帧的绝对位移增量，可参考 [CharacterController.Move](https://docs.unity3d.com/ja/current/ScriptReference/CharacterController.Move.html)。

### 9.1 Velocity 和 Motion Delta 的区别

假设水平速度为每秒 6 米，而当前帧 `deltaTime = 1/60` 秒：

```text
这一帧位移 = velocity × deltaTime
             = 6 × 1/60
             = 0.1 米
```

传给 `Move` 的应该是 0.1 米的位移，不是 6。

### 9.2 当前 Gravity 积分

Boss 保存一个可变的垂直速度 `_verticalVelocity`：

```text
verticalVelocity = verticalVelocity - gravityAcceleration × deltaTime
verticalMotion   = verticalVelocity × deltaTime
```

这是最基础的逐帧数值积分。当前 `GravityAcceleration = 20`。在约 60 FPS、`deltaTime ≈ 0.0167` 时，第一帧垂直速度大约减少 0.334 米/秒；之后继续累积，直到碰到地面。

### 9.3 Grounded Stick Speed 是什么

角色已经落地时，代码仍保留约 `-2 m/s` 的轻微向下速度：

```csharp
if (controller.isGrounded && verticalVelocity <= 0)
    verticalVelocity = -GroundedStickSpeed;
```

它的目的不是让 Boss 穿过地面，而是持续向地面提交很小的接触请求。CharacterController 会用碰撞阻止穿透，同时 `isGrounded` 更容易在相邻帧稳定保持 true。

如果落地时直接把 Y 速度永远设为 0，角色在斜坡、台阶边缘或浮点误差下可能短暂失去地面接触，产生细小悬空和抖动。

### 9.4 Step Offset、Slope Limit、Skin Width 为什么与飞天有关

CharacterController 不是简单的点：它是一个胶囊形状，并带有几项重要参数。

- `Step Offset`：允许胶囊自动迈上一定高度的台阶；
- `Slope Limit`：限制可攀爬的坡面角度；
- `Skin Width`：给碰撞检测保留一层容差，减少卡住和抖动。

Boss 持续水平移动并撞上坡面、台阶或重叠碰撞时，碰撞解算可以把胶囊向上修正。这个向上变化来自最终碰撞结果，即使输入速度的 `velocity.y` 已经清零，Transform 的 Y 仍可能改变。

这句话很关键：

```text
输入 motion.y = 0
并不保证
碰撞解算后的 transform.position.y 不变
```

---

## 10. 这次 Boss 飞天 Bug 的完整 Debug 过程

### 10.1 玩家症状

Boss 战开始后，Boss 逐渐离开地面，最后出现在高空。Phase 3、转阶段动画、新 Blend Tree 和 NavMesh 都可能成为第一眼怀疑对象。

### 10.2 第一组假设

最初需要并行考虑：

1. `Apply Root Motion` 是否错误开启；
2. 某个动画 Clip 的 Root Transform Position Y 是否把模型抬高；
3. `AvatarMask` 是否让上层动画覆盖 Root 或 Hips；
4. Teleport 是否采样到了错误高度；
5. NavMeshAgent 是否直接修改了 Transform；
6. Gameplay 移动是否缺少垂直约束。

Debug 的重点是逐层排除，而不是看到动画刚改过就直接认定是 Animator。

### 10.3 决定性证据：真正升高的是 Gameplay Root

`Editor.log` 中的导航诊断连续记录到 Boss 世界坐标：

```text
Y = 4.16
Y = 10.04
Y = 11.31
Y = 21.21
Y = 22.79
Y = 33.79
```

这说明 Boss 根 GameObject 的 Transform 确实在升高。如果只是骨架动画把模型抬高，Gameplay Root 的世界 Y 应保持在地面附近。

### 10.4 排除 Animator Root Motion

当前 Boss Prefab 的 Animator：

```text
Apply Root Motion = false
```

因此 Animator 不应把 Clip 的根位移应用到 Gameplay GameObject。动画导入设置仍可能让视觉骨架相对根节点漂移，但它无法解释日志中 Gameplay Root Y 连续升高。

### 10.5 排除 NavMeshAgent 双重写入

`EnemyNavigationMotor` 在构造时设置：

```csharp
agent.updatePosition = false;
agent.updateRotation = false;
```

Agent 只计算路线，真实 Transform 由 CharacterController 写入，所以 Agent 直接拖动 Boss 的假设不成立。

### 10.6 真正根因

旧的 Boss 移动只提交水平 Motion：

```text
NavMesh/环绕方向
→ velocity.y = 0
→ CharacterController.Move(horizontalMotion)
```

但 CharacterController 不会自动应用 Gravity。连续环绕增加了水平碰撞次数；胶囊被坡面或台阶向上修正后，没有后续向下速度把它拉回地面，抬升高度一帧一帧保留下来。

当 Boss 已经远离导航表面后，NavMesh 才开始报告“目标附近没有与当前 Enemy 连通的可用 NavMesh”。这个错误是因果链的后半段：

```text
缺少 Gravity
→ 碰撞抬升后不下降
→ Boss 离 NavMesh 越来越远
→ Sample/路径刷新失败
```

如果只扩大 `DestinationSampleRadius`，系统可能暂时更容易采样到下方 NavMesh，但 Boss 仍然没有 Gravity，最终仍会悬空。它只能遮盖症状。

### 10.7 最终修复

`WizardBossController.Update` 当前按以下顺序执行：

```text
1. 更新 Cooldown、Phase 和战斗状态
2. 当前 Boss State 执行水平移动/技能逻辑
3. ApplyVerticalMotion 统一施加 Gravity 或贴地速度
4. 把最终水平速度同步给 Animator
```

把 Gravity 放在 Controller 的公共边界，而不是复制到 Approach、Decision、Cast、PhaseTransition、Teleport 每个 State 中，有三个好处：

1. 所有参战状态都能贴地；
2. 以后增加新状态时不容易再次漏掉 Gravity；
3. State 继续只表达自己的主要行为，物理公共规则集中管理。

Teleport 是一次离散位置重置，因此抵达新位置时会把 `_verticalVelocity` 清零。否则传送前正在下落的速度会被带到新落点，Boss 可能在抵达瞬间产生异常下坠。

### 10.8 回归测试和证据边界

PlayMode 回归测试 `Approach_WhileUnsupported_FallsInsteadOfKeepingRaisedHeight` 会：

1. 把 Boss 放在离地 5 米处；
2. 激活追击；
3. 等待两个 Frame；
4. 断言 Boss 的 Y 小于开始高度。

这个测试能防止未来重构移动时再次完全删除 Gravity。它不能替代真实 P8 地形验证，因为测试没有覆盖实际 Terrain、全部 Collider、NavMesh Bake、Animator Layer 和所有传送候选点。

---

## 11. Blend Tree：用连续参数混合多段移动动画

### 11.1 Blend Tree 是什么

普通 Animator State 通常播放一个 Clip。`Blend Tree` 可以根据参数，同时计算多个 Clip 的权重并混合最终姿势。Unity 官方强调，Transition 和 Blend Tree 都能产生平滑动画，但解决的问题不同：

- Transition：在两个离散状态之间切换，例如 Idle → Attack；
- Blend Tree：在同一类连续动作之间混合，例如前走、后走、左移、右移。

当前配置属于 `2D Simple Directional`。这种类型适合一个中心周围的不同方向动作，并且同一方向不应放多段不同速度的动作；Unity 也允许可选的 `(0,0)` Idle，参见 [SimpleDirectional2D](https://docs.unity3d.com/cn/6000.0/ScriptReference/Animations.BlendTreeType.SimpleDirectional2D.html)。

### 11.2 当前四个方向的坐标

```text
                 moveZ = +1
                 Run Forward
                     ● (0, 1)

moveX = -1  ●                     ●  moveX = +1
          Walk Left              Walk Right
          (-1, 0)                 (1, 0)

                     ● (0, -1)
                 Walk Back
                 moveZ = -1
```

当前 `Enemy_Wizard.controller` 中保存的是：

| Blend Position | Clip |
|---|---|
| `(0, 1)` | `Run_MagicWand` |
| `(0, -1)` | `WalkBack_MagicWand` |
| `(-1, 0)` | `WalkLeft_MagicWand` |
| `(1, 0)` | `WalkRight_MagicWand` |

当 `(moveX, moveZ) = (1,0)` 时，右移权重最大；当它接近 `(0.7,0.7)` 时，Animator 会混合前走和右移姿势，表现为斜向移动。

### 11.3 为什么还需要 speed

`moveX/moveZ` 主要表示方向，`speed` 表示是否真的在移动以及移动大小。当前 Controller 使用：

```text
speed > 0.1：Idle → CombatLocomotion
speed < 0.1：CombatLocomotion → Idle
```

然后只有进入 `CombatLocomotion` 后，Blend Tree 才根据 moveX/moveZ 选择方向动画。

### 11.4 为什么参数要除以基础速度

代码将本地速度除以 `BossDefinition.MoveSpeed`，再 Clamp 到 `[-1,1]`：

```text
moveX = clamp(localVelocity.x / baseMoveSpeed, -1, 1)
moveZ = clamp(localVelocity.z / baseMoveSpeed, -1, 1)
```

假设基础速度 10，施法移动倍率 0.6，则实际移动速度约 6。方向完全向右时：

```text
moveX = 6 / 10 = 0.6
moveZ = 0
```

它会把 Blend 点放在中心与右侧 Clip 之间。这里同时把“移动倍率”映射成较弱的方向权重，动作视觉可能偏慢或混入中心附近的结果。未来若需要精准 Foot Sliding 控制，可以把方向归一化与 Animator 播放速度拆成两个参数：方向只使用单位向量，另设 `locomotionSpeed` 调整 State Speed 或 Clip Speed。

### 11.5 当前没有 `(0,0)` Idle 是否可以

当前 Blend Tree 没有中心 Idle，但它只有在 `speed > 0.1` 后才进入，因此理论上不会长期用 `(0,0)` 采样。Idle 单独放在 `Idle_Bow` State 中。

这种结构可以工作，但需要 Idle ↔ CombatLocomotion 过渡足够及时。如果参数已经归零，而过渡仍在等待 Exit Time，Blend Tree 可能短暂在中心附近混合四个方向，得到不直观的姿势。

---

## 12. Transition 与 Has Exit Time：为什么移动过渡通常不该等待动画播完

`Transition` 决定从一个 Animator State 何时、用多长时间切到另一个 State。条件可以来自 Animator Parameter。

### 12.1 Has Exit Time 的含义

开启 `Has Exit Time` 后，条件满足还不够；Animator 必须等待源动画播放到指定的 `Normalized Time` 才能开始过渡。Unity 官方说明，Exit Time 0.75 表示源动画第一次播放到 75% 时条件成立；如果同时有其他 Conditions，这些条件也要在那个时机满足，参见 [AnimatorStateTransition](https://docs.unity3d.com/cn/6000.0/ScriptReference/Animations.AnimatorStateTransition.html)。

它适合：

- 攻击挥到结尾后回 Idle；
- 转阶段动作播到结尾后恢复；
- 不希望被任意截断的表演动画。

它通常不适合：

- Idle → Run；
- Run → Idle；
- 根据输入立即改变的移动状态。

玩家已经开始移动，却要等 Idle Clip 播到 80% 才迈步，会产生明显输入延迟；停止后仍要等 Run 循环走到退出点，也会有滑步感。

### 12.2 当前项目需要检查的配置

当前磁盘中的 `Enemy_Wizard.controller` 显示：

- `Idle_Bow → CombatLocomotion` 仍启用了 `Has Exit Time`；
- `CombatLocomotion → Idle_Bow` 仍启用了 `Has Exit Time`。

它不会造成 Boss 飞天，但可能造成起步和停步响应迟钝。建议在 Editor 中关闭这两条移动 Transition 的 `Has Exit Time`，保留 `speed > 0.1` / `speed < 0.1` 条件，并使用约 0.1～0.15 秒的 Transition Duration 作为第一版视觉调参。

攻击或施法结束回空状态则可以保留 Has Exit Time，因为我们希望动作自然播放到释放阶段或结尾。

---

## 13. Animator Layer：怎样同时播放“腿在走、手在施法”

只有一个 Animator Layer 时，同一时刻通常由一个 State 或一个 Blend Tree 产生整套骨骼姿势。假设 Boss 正在播放全身跑步动画，这时直接切到全身施法动画，施法动画会接管腿部，角色看起来就会停下。反过来，如果继续只播跑步动画，代码虽然可以释放法术，但玩家看不到清晰的抬手动作。

`Animator Layer` 解决的是“同一副骨架的不同部分，在同一时刻由不同动画控制”的问题。可以把它想成图像软件中的图层：

- 下层先给整个人体一份基础姿势；
- 上层再覆盖或叠加自己允许控制的骨骼；
- Unity 按图层顺序把结果合成为最终姿势。

Unity 的 [Animation Layers](https://docs.unity3d.com/cn/current/Manual/AnimationLayers.html) 文档将 Layer 描述为管理不同身体部位或不同动画逻辑的方式；每层可以拥有自己的 State Machine、混合方式、权重和 `AvatarMask`。

### 13.1 当前项目的两层职责

`Enemy_Wizard.controller` 当前包含两层：

| Layer | 负责什么 | 当前内容 |
|---|---|---|
| `Base Layer` | 产生完整的基础全身姿势 | Idle、四方向移动 Blend Tree、受击、死亡、转阶段 |
| `UpperBodyCast` | 在基础姿势上覆盖上半身施法动作 | 空状态 `UpperBodyEmpty` 与施法状态 `UpperBodyCast` |

运行时的视觉组合可以写成：

```text
Base Layer：      腿走路 + 躯干随步伐摆动 + 手臂自然摆动
UpperBodyCast：             躯干施法 + 手臂施法
AvatarMask：                只允许这些上半身骨骼生效
最终姿势：        腿走路 + 躯干施法 + 手臂施法
```

这里有一个重要边界：`BossCastState` 中的移动代码负责让 Gameplay Root 在世界里移动；Animator Layer 只负责把这个运动“画成”腿部走路与上半身施法。即使动画看起来在走，代码不调用 `CharacterController.Move`，Boss 仍不会改变世界位置。反过来，代码移动但 Animator 参数错误，Boss 会平移或滑行。

### 13.2 Override 和 Additive 的区别

Animator Layer 常用两种混合方式：

1. `Override`

   上层在权重范围内用自己的姿势替换下层姿势。权重为 1 时，被 Mask 选中的骨骼主要采用上层结果。完整施法 Clip 通常适合 `Override`，因为我们希望手臂明确进入施法姿势。

2. `Additive`

   上层把相对于参考姿势的变化“加到”下层结果上。它适合呼吸、轻微后坐力、受伤抖动、瞄准偏移等增量动作。普通全身动作如果未经 Additive 基准姿势制作，直接设成 `Additive` 容易出现手臂扭曲、位移翻倍或姿势偏差。

当前 `UpperBodyCast` 使用 `Override`，这是合理选择。它要表达完整的上半身施法姿势，而不是给跑步姿势增加一点小偏移。

### 13.3 Layer Weight 是什么

`Layer Weight` 是这一层对最终姿势的影响强度：

- `0`：该层完全不影响结果；
- `0.5`：在下层和上层之间混合；
- `1`：完整应用该层，但仍受 `AvatarMask` 限制。

当前 `UpperBodyCast` 默认权重为 1。平时之所以不会永远保持施法姿势，是因为该层的默认 State 是没有 Motion 的 `UpperBodyEmpty`。收到 `cast` Trigger 后，它暂时进入带施法 Clip 的 `UpperBodyCast`，播放完成再回到空状态。

这与“不断在代码里把 Layer Weight 从 0 改成 1”相比更容易理解：Layer 始终启用，State Machine 决定当前有没有上半身动作。以后如果要做平滑举杖、受伤程度混合，才更适合动态控制 Layer Weight。

### 13.4 为什么用一个 Empty State

没有 Motion 的空 State 表示：“这一层当前没有额外姿势要贡献”。它有三个作用：

- 不施法时让 Base Layer 完整显示；
- 给 `Any State → UpperBodyCast` 提供一个稳定的待机落点；
- 施法结束后通过 Exit Time 自动清掉上半身覆盖。

如果 Layer 默认 State 直接就是施法 State，Boss 一出生就可能处于施法姿势。如果施法 State 没有返回路径，上半身又会永远卡在最后一帧。

---

## 14. AvatarMask 与骨骼控制：为什么只让上半身受施法动画影响

### 14.1 动画所谓“控制骨骼”到底是什么意思

一个 Bone 在 Unity 中本质上也是一个 `Transform`，拥有相对父节点的：

- `localPosition`：局部位置；
- `localRotation`：局部旋转；
- `localScale`：局部缩放。

Animation Clip 保存了这些属性随时间变化的曲线。Animator 在每帧计算当前时间点应该得到的值，再把它们写到对应 Bone。父 Bone 旋转时，所有子 Bone 都会跟着变化。例如胸部旋转会带动肩膀，肩膀又带动手臂和手掌。这就是 `Transform Hierarchy` 的父子传播。

因此，“让施法动画只影响上半身”不代表把动画 Clip 切成两半。Clip 仍然可能包含全身曲线，`AvatarMask` 在混合阶段规定哪些身体区域或哪些 Transform 可以把上层结果写入最终姿势。

### 14.2 Humanoid Mask 和 Transform Mask

Unity 的 [Avatar Mask](https://docs.unity3d.com/cn/current/Manual/class-AvatarMask.html) 提供两种常见配置方式：

1. `Humanoid Body Mask`

   按人形部位开关，例如 Head、Body、Left Arm、Right Arm、Left Leg、Right Leg、Root。优点是直观，适合标准 Humanoid Avatar；缺点是粒度较粗。

2. `Transform Mask`

   按实际骨骼层级逐个包含或排除。优点是精确，也能处理武器挂点、披风骨、额外道具骨；缺点是需要理解模型的 Skeleton 层级，换模型时还要确认路径兼容。

Boss 的移动施法第一版可以先使用 Humanoid Mask。目标不是“Mask 里勾得越多越好”，而是满足下面的职责边界：

```text
应该由上层施法控制：Spine / Chest / UpperChest / Head / 双臂 / 双手
应该继续由下层移动控制：Root / Hips 的平移、双腿、双脚、Foot IK
```

如果当前第三方 `ForBowRunning.mask` 的人体示意图确实是这个选择，它可以复用。如果它勾选了 Root、Hips 或腿部，施法 Layer 仍可能覆盖走路；如果它漏掉胸椎或肩膀，施法动作可能只有前臂在动，姿势会很僵硬。

### 14.3 Root、Hips、Spine 为什么容易混淆

对初学者来说，这三个概念尤其容易混在一起：

- `Root`：整个动画角色的根运动参考，可能携带整体位移或旋转；
- `Hips`：人体骨盆，是 Humanoid 骨架的身体中心，腿与脊柱都从它附近分叉；
- `Spine/Chest`：躯干链，适合作为上半身动作的起点。

移动施法时，通常希望 Hips 和腿保持 Base Layer 的步态，Spine 以上由施法 Layer 覆盖。若施法动作确实需要明显扭腰，可以把 Hips 纳入 Mask，但要逐动作验证，不应在没观察效果时盲目勾选。

### 14.4 当前 Mask 为什么必须在 Editor 中目视确认

项目复用了第三方 Bow 动画目录里的 `ForBowRunning.mask`。磁盘 YAML 能确认 Controller 引用了它，但 Humanoid 部位选择在 YAML 中压缩存储，直接读十六进制/位串不适合作为可靠的人类审查依据。

因此，正确验证方式是：

1. 在 Project 窗口选中 `ForBowRunning.mask`；
2. 在 Inspector 展开 Humanoid 人体图；
3. 确认上半身亮起、双腿和 Root 关闭；
4. 让 Boss 在 Play Mode 中边横移边施法；
5. 观察脚步是否继续、躯干和手臂是否完成施法；
6. 如果结果异常，再考虑复制成项目自己的 `BossUpperBodyCast.mask`，不要直接修改第三方共享 Mask。

最后一点很重要：第三方 Mask 可能还被别的 Controller 使用。直接修改它会让其他角色一起发生变化。复制到 `Assets/_Project/Art/Animators/` 再调整，所有权和影响范围更清晰。

### 14.5 骨骼 Mask 不能解决哪些问题

`AvatarMask` 只管理动画姿势混合，不能解决：

- Boss 在世界坐标中真的飞起来；
- `NavMeshAgent` 没有路径；
- `CharacterController` 穿墙；
- 法术释放时机错误；
- 攻击范围逻辑错误；
- 模型导入的 Rig 配置损坏。

看到角色“姿势不对”，先检查 Animator/Mask；看到 Collider、血条、世界坐标也一起升高，先检查 Gameplay Movement。区分“视觉骨骼问题”和“世界位移问题”是这次 Debug 最重要的能力之一。

---

## 15. Root Motion：动画位移和代码位移为什么不能同时抢控制权

### 15.1 什么是 Root Motion

有些 Animation Clip 不只记录手脚摆动，还记录角色根节点在一段时间内向前走了多少。Animator 把 Clip 的根位移应用到 GameObject，这种由动画驱动的整体移动叫 `Root Motion`。

可以把移动方案分成两类：

| 方案 | 世界位移由谁决定 | 优点 | 代价 |
|---|---|---|---|
| In-place + 代码移动 | Gameplay 代码、`CharacterController` | 速度和碰撞可控，适合动作游戏与网络同步 | 脚步速度和代码速度不一致时会滑步 |
| Root Motion | Animation Clip 的根位移 | 脚步与位移天然匹配，表演感强 | 导航、转向、打断和速度控制更复杂 |

当前 Boss 采用第一种：`Animator.applyRootMotion = false`，Gameplay 代码通过 `CharacterController.Move` 移动。Unity 的 [Root Motion](https://docs.unity3d.com/cn/2023.2/Manual/RootMotion.html) 文档说明了 Clip 中 Root Transform 与运行时 Root Motion 的关系。

### 15.2 为什么当前项目关闭 Apply Root Motion

这个 Boss 需要：

- 按攻击距离动态靠近、后撤和环绕；
- 用 `NavMeshAgent.desiredVelocity` 绕障碍；
- 受状态效果修改移动速度；
- 在 Cast 中持续移动；
- 传送后立即重新对齐位置；
- 由代码保证 Gravity 和贴地。

这些都要求 Gameplay 对世界位移拥有确定控制权。如果同时启用 Root Motion，Animator 和 CharacterController 可能在同一帧分别修改位置，导致实际速度不可预测、碰撞表现不稳定、导航代理与角色脱节。

### 15.3 视觉 Root 和 Gameplay Root

常见 Prefab 层级是：

```text
Boss GameObject              ← Gameplay Root，挂 CharacterController、AI、HP
└── Model / Skeleton Root     ← Visual Root，挂 Animator 或骨架
    └── Hips
        └── Spine ...
```

若只是 Visual Root 或某根骨骼偏移，Collider 与 Boss 主 Transform 可能仍在地面；若 Gameplay Root 上升，Collider、导航同步点、血条跟随点都会一起升高。

这次飞天日志记录到 Boss 主 Transform 的世界 Y 持续增加，因此不是“某个 Bone 把模型抬起来”的纯视觉问题。这个证据帮助我们排除了 Layer、AvatarMask 和施法 Clip 本身。

### 15.4 动画看起来移动，不等于 Gameplay 在移动

即使 Apply Root Motion 关闭，一个带前进位移的 Clip 仍可能在模型内部表现出相对根节点的偏移，具体取决于 Model Import Settings 中 Root Transform 的 Bake Into Pose 配置。新动画接入时要检查：

- Clip 是 `In-place` 还是带根位移；
- Root Transform Position (Y/XZ) 是否 Bake Into Pose；
- Root Transform Rotation 是否 Bake Into Pose；
- Loop Pose 是否适合循环步态；
- Apply Root Motion 是否符合项目的移动权威。

本项目的原则很清楚：普通移动动画服务于视觉表现；世界位移由 Gameplay Movement 统一执行。

---

## 16. 当前功能的完整 Runtime 链路

下面把每帧发生的事串起来。阅读时始终问自己四个问题：

1. 这一层决定了什么？
2. 它输出了什么数据？
3. 下一层怎样消费这些数据？
4. 谁最终修改了 Transform？

### 16.1 Boss 战启动与永久锁定目标

玩家离开安全区并开始 Boss Encounter 后，运行链路是：

```text
Encounter 系统发布 BossEncounterStartedEvent
    ↓
WizardBossController 收到事件并校验 BossId
    ↓
TryActivate(playerTransform)
    ├── 保存唯一 Target
    ├── 标记 Encounter Active
    ├── 初始化环绕方向与阶段
    └── RouteAfterAction()
            ├── 已进入战斗范围 → Decision
            └── 尚未进入战斗范围 → Approach
```

“永久锁定”指 Encounter 生命周期内不再靠 Detect Radius 丢失目标。它不等于每帧都处于攻击 State：距离仍决定 Boss 是追击、战斗走位还是释放技能，但不会因为玩家走远就回 Idle 或忘掉玩家。

### 16.2 每帧总入口

`WizardBossController.Update()` 的顺序是：

```text
检查暂停 / Encounter / 存活
    ↓
推进技能和传送 Cooldown
    ↓
计算待进入阶段
    ↓
用 AttackEnterDistance / AttackExitDistance 刷新 Combat Engagement
    ↓
阶段优先，或 Tick 当前 Gameplay State
    ↓
ApplyVerticalMotion() 统一处理 Gravity 与贴地
    ↓
SyncAnimator() 把真实水平速度变成动画参数
```

顺序很关键。先让当前 State 完成水平移动，再追加垂直移动，最后从真实移动结果同步 Animator。这样动画展示的是角色这一帧实际做出的移动，不是 AI 原本“想要”但被墙挡住的移动。

### 16.3 远距离追击链路

当 Boss 尚未进入 `AttackEnterDistance`：

```text
BossApproachState.Tick
    ↓
MoveTowardTarget
    ├── 算水平直线方向，作为 Fallback
    ├── EnemyNavigationMotor.NavigateTo(player, stopDistance, speed)
    │       └── NavMeshAgent 计算路径与 desiredVelocity
    └── MoveUsingNavigationOrFallback
            ├── 优先读取 desiredVelocity
            ├── NavMesh 不可用时才用直线方向
            ├── velocity.y = 0
            ├── CharacterController.Move(horizontalDelta)
            └── SyncToCharacter → agent.nextPosition = owner.position
```

这时 `NavMeshAgent` 是 Planner，`CharacterController` 是 Mover。直线 Fallback 主要服务于漏配 NavMesh 或自动化测试中的可诊断降级；正式场景应由 NavMesh 路径绕障碍。

### 16.4 进入战斗后的环绕链路

当距离小于等于进入阈值后，Combat Engagement 变为 true，Boss 进入 Decision：

```text
BossDecisionState.Tick
    ↓
MoveInCombat(isCasting: false)
    ↓
ResolveCombatDirection
    ├── 太远：Approach = 朝向玩家 + 少量切线
    ├── 合适：Orbit = 纯切线
    └── 太近：Retreat = 背离玩家 + 少量切线
    ↓
以 Boss 当前点前方一小段作为短程 navigationTarget
    ↓
NavMeshAgent 计算局部可走 Steering
    ↓
CharacterController.Move
    ↓
FaceTarget：身体继续朝玩家转
```

“移动方向”和“面朝方向”在这里被刻意拆开。Boss 可以沿圆周向左走，同时脸朝圆心的玩家，因此视觉上是 Strafe，而不是转身侧跑。

### 16.5 移动施法链路

当 Utility 选择出一个法术 Program：

```text
Decision 选择 Program
    ↓
EnterCast(programIndex)
    ├── 记录 Program Index
    ├── 保存玩家当时的位置作为 Locked Aim Point
    └── 切换到 BossCastState
            ↓
BossCastState.Enter
    ├── 获取 Action Lock
    └── TriggerCastAnimation → Animator.SetTrigger("cast")
            ↓
每帧 BossCastState.Tick
    ├── MoveInCombat(isCasting: true)
    ├── FacePoint(Locked Aim Point)
    ├── Telegraph 到时 → ReleaseSelectedProgram
    └── Recovery 到时 → CompleteCast
```

这里的 `Action Lock` 锁定的是“不能同时开始另一个技能或阶段动作”，不锁定移动。把动作互斥和位移能力分开，才实现了“走动时攻击”。

`Locked Aim Point` 让一发技能在前摇开始时确定目标快照，Telegraph 期间玩家可以躲开。它既提供可读性，也避免弹道在释放瞬间偷偷追踪玩家。身体朝向锁定点，而环绕方向仍按玩家当前位置计算；这是 Gameplay 设计选择，后续如果发现身体与路线夹角过大，可以再决定瞄准是否持续跟踪。

### 16.6 Animator 同步链路

完成水平移动后：

```text
CharacterController.velocity 的水平部分
    ↓
speed = velocity.magnitude
    ↓
InverseTransformDirection(worldVelocity)
    ↓
得到 localVelocity.x / localVelocity.z
    ↓
除以 BossDefinition.MoveSpeed 并 Clamp 到 [-1, 1]
    ↓
写入 moveX / moveZ
    ↓
Base Layer 的 2D Blend Tree 混合前后左右步态
```

施法开始时另一路并行发生：

```text
cast Trigger
    ↓
UpperBodyCast Layer：Any State → UpperBodyCast
    ↓
AvatarMask 只放行上半身 Bone
    ↓
Base Layer 腿部移动 + Upper Layer 上半身施法
```

这两条链路互不覆盖职责：速度参数描述“腿应该怎样走”，Trigger 描述“上半身何时做一次施法动作”。

### 16.7 垂直贴地链路

每个 Gameplay State Tick 结束后都执行：

```text
CharacterController.isGrounded ?
    ├── 是，并且没有上升：verticalVelocity = -GroundedStickSpeed
    └── 否：verticalVelocity -= GravityAcceleration × deltaTime
            ↓
CharacterController.Move(Vector3.up × verticalVelocity × deltaTime)
            ↓
NavMeshAgent.nextPosition 再次同步到最终世界位置
```

将它集中在 Controller 的总入口，而不是分别散落在 Approach、Decision、Cast 等 State 中，可以保证新 State 不会忘记 Gravity。传送完成时还要把垂直速度清零，否则传送前积累的下落速度可能被带到新位置。

---

## 17. 已经完成了什么，你还需要在 Editor 中做什么

这一章先把职责彻底分开。代码和 Animator 资产能搭好结构，但最终的骨骼选择、动画观感、Scene NavMesh 与实际地形碰撞必须在 Unity Editor 里目视验证。两边缺一不可。

### 17.1 已经由实现层完成的工作

| 已完成内容 | 位于哪里 | 运行时作用 |
|---|---|---|
| 进入/退出战斗的双距离阈值 | `BossCombatMovementMath`、`BossDefinition` | 防止 Boss 在边界频繁切换追击和战斗 |
| 过远靠近、理想距离环绕、过近后撤 | `BossCombatMovementMath` | 计算水平移动意图 |
| NavMesh 路径规划与 CharacterController 单一位移权威 | `EnemyNavigationMotor`、`WizardBossController` | 绕障碍，同时避免两个组件争抢 Transform |
| Decision 中持续战斗走位 | `BossDecisionState` | Boss 思考下一招时不会原地罚站 |
| Cast 中持续战斗走位 | `BossCastState` | 前摇、释放、后摇期间仍可移动 |
| 真实速度转本地速度 | `WizardBossController.SyncAnimator` | 为四方向 Blend Tree 提供 `moveX/moveZ` |
| Cast Trigger | `BossCastState.Enter` | 每次施法只触发一次上半身动作 |
| 统一 Gravity 与贴地 | `WizardBossController.ApplyVerticalMotion` | 修复水平碰撞修正后悬空/飞天 |
| 传送后垂直速度复位 | Boss 传送流程 | 防止把传送前的下落状态带到落点 |
| 四方向移动 Blend Tree 和上半身 Layer 结构 | `Enemy_Wizard.controller` | 把移动方向和施法姿势显示出来 |

这里的“完成”指当前工作副本已经具备对应实现。它不等于已经替你完成最终视觉验收。Animator 的 Mask 是否选对 Bone、Transition 是否响应及时、动画和速度是否匹配，都需要在真实模型和关卡中观察。

### 17.2 你在 Editor 中的任务总览

你需要完成或确认以下事项：

1. 等待 Unity 编译，先排除 Console 红色 Error；
2. 检查 Boss Prefab 的移动组件和 Animator 引用；
3. 检查四个移动 Clip 的导入和循环设置；
4. 检查 Blend Tree 的类型、参数与四个坐标；
5. 关闭 Idle ↔ Locomotion 的 `Has Exit Time`；
6. 目视确认或复制一份专属上半身 `AvatarMask`；
7. 检查 `UpperBodyCast` Layer、State 和 Transition；
8. 确认 Scene 的 NavMesh 覆盖 Boss 所在区域；
9. 用分场景 Play Mode 清单验收 Gameplay 和 Presentation；
10. 最后在 Profiler 观察每帧 `GC Alloc`。

下面逐步说明“点哪里、设什么、为什么”。

### 17.3 第一步：编译与 Console 基线

1. 退出 Play Mode。
2. 等待 Unity 右下角脚本编译图标结束。
3. 打开 `Window → General → Console`。
4. 点击 `Clear`。
5. 确认没有红色编译 Error，再进入 Play Mode。

为什么先做这一步：只要任意 C# 编译失败，Unity 就不会加载最新程序集。你可能以为自己正在测试新 Gravity 或新 Animator 参数，实际上运行的是旧 Domain 中的代码，所有现象都会失去诊断意义。

黄色 Warning 要逐条理解，但它和红色 Error 的优先级不同。典型 Animator Warning 如“Parameter does not exist”代表参数名不匹配；这类 Warning 虽不阻止编译，却会直接让动画功能失效。

### 17.4 第二步：检查 Boss Prefab 的单一移动权威

在 Project 窗口打开：

`Assets/_Project/Art/Prefabs/Boss/Boss.prefab`

依次检查：

#### Animator

- `Controller` 应引用 `Enemy_Wizard.controller`；
- `Apply Root Motion` 应关闭；
- `Update Mode` 通常保持 `Normal`；
- `Culling Mode` 调试阶段建议保证离开镜头时仍会更新，正式设置再按性能需求决定。

关闭 Apply Root Motion 的理由：Boss 的世界位置由 `CharacterController.Move` 决定。若打开，动画也会尝试贡献根位移，破坏单一权威。

#### CharacterController

当前 Prefab 数据可见：

- `Height = 3.95`；
- `Radius = 0.79`；
- `Slope Limit = 45`；
- `Step Offset = 0.3`；
- `Skin Width = 0.08`。

在 Scene 视图打开 Gizmos，观察绿色胶囊是否包住 Boss 主体：

- 底部不应明显陷入地面；
- 顶部应覆盖角色身体；
- Center 应与模型实际高度匹配；
- 胶囊过宽会更容易顶到墙和台阶；
- Step Offset 过大会允许爬上不该爬的边缘。

不要仅为了压制飞天现象就把 Step Offset 设成 0。那会牺牲正常上台阶能力，而且无法替代 Gravity。先保留当前值，在具体关卡碰撞上做验证。

#### NavMeshAgent

当前 Prefab包含 NavMeshAgent。运行时构造 `EnemyNavigationMotor` 后会把：

- `updatePosition = false`；
- `updateRotation = false`。

这样 Agent 只计算路径。Inspector 在非运行状态不一定直接表现代码运行后的值，因此应在 Play Mode 选中运行时 Boss 再观察 Debug Inspector。不要手动打开自动 Position/Rotation 更新来“帮助”它移动，否则会重新引入双重控制。

### 17.5 第三步：检查四个移动 Animation Clip

动画源目录：

`Assets/ThirdParty/ModularRPGHeroesPBR/Animations/MagicWand`

需要确认四个动作：

- `Run_MagicWand`：向前；
- `WalkBack_MagicWand`：向后；
- `WalkLeft_MagicWand`：向左侧移；
- `WalkRight_MagicWand`：向右侧移。

逐个选择包含这些 Clip 的模型/动画资产，在 Inspector 的 `Animation` 页检查：

1. `Loop Time`：移动循环应打开；
2. `Loop Pose`：若首尾姿势接近，可打开减小循环跳变；
3. 预览播放：确认动作方向和文件名一致；
4. Root Transform：确认符合项目的 In-place 移动策略；
5. `Apply`：只有确实改动 Import Settings 后才点击。

为什么一定要预览方向：资源命名不是数学证据。有些资产以模型自身坐标或动画作者视角命名，`WalkLeft` 可能与项目角色的 local X 正方向相反。如果实际表现反了，优先交换 Blend Tree 左右 Clip 的位置；不要立刻把 C# 的向量公式取反，因为公式还服务于 Gameplay 走位。

修改第三方导入设置会影响所有引用同一 Clip 的角色。若多个角色需求不同，应复制可控资产或使用独立 Animator Override/导入方案，不要无意中改变全项目表现。

### 17.6 第四步：检查 Animator Parameters

1. 双击 `Assets/_Project/Art/Animators/Enemy_Wizard.controller`。
2. 打开 Animator 窗口左侧的 `Parameters`。
3. 确认至少存在：

| 名称 | 类型 | 写入者 | 用途 |
|---|---|---|---|
| `speed` | Float | `SyncAnimator` 每帧写 | Idle 与移动切换 |
| `moveX` | Float | `SyncAnimator` 每帧写 | 左右方向混合 |
| `moveZ` | Float | `SyncAnimator` 每帧写 | 前后方向混合 |
| `cast` | Trigger | `BossCastState.Enter` 写一次 | 上半身施法 |
| `phaseTransition` | Trigger | 转阶段 State 写一次 | 转阶段动画 |

大小写必须完全相同。`moveX` 和 `MoveX` 是两个不同字符串。代码会在启动时缓存 Hash，并检查可选 Float 参数是否存在；参数缺失时 Blend Tree 得不到对应轴。

Play Mode 中选中 Boss 并打开 Animator Parameters，可直接看数值变化：

- 向角色自身右侧移动：`moveX > 0`；
- 向左：`moveX < 0`；
- 向前：`moveZ > 0`；
- 向后：`moveZ < 0`；
- 完全被墙挡住：`speed` 应接近 0，而不是继续显示 AI 期望速度。

最后一点验证了当前实现使用“真实移动结果”驱动动画，可以减少顶墙跑步。

### 17.7 第五步：检查 CombatLocomotion Blend Tree

1. 在 `Base Layer` 选择 `CombatLocomotion` State。
2. 双击进入 Blend Tree。
3. 将 `Blend Type` 设为 `2D Simple Directional`。
4. 将两个轴设为：
   - X 参数：`moveX`；
   - Y 参数：`moveZ`。
5. 检查 Motion 与 Pos X / Pos Y：

| Motion | Pos X | Pos Y |
|---|---:|---:|
| `Run_MagicWand` | 0 | 1 |
| `WalkBack_MagicWand` | 0 | -1 |
| `WalkLeft_MagicWand` | -1 | 0 |
| `WalkRight_MagicWand` | 1 | 0 |

6. 拖动 Blend Tree 预览中的红点，分别放到四个方向，确认播放正确 Clip。
7. 把红点放在斜向位置，例如 `(0.7, 0.7)`，确认前进和右移自然混合。

`2D Simple Directional` 适合“每个主要方向只有一个 Motion”的结构，参见 Unity [SimpleDirectional2D](https://docs.unity3d.com/cn/6000.0/ScriptReference/Animations.BlendTreeType.SimpleDirectional2D.html)。如果以后同一方向还要区分慢走和快跑，单个 2D Directional Tree 就不够了，可以用嵌套 Blend Tree 或 `2D Freeform Cartesian`，但复杂度也会提高。

### 17.8 第六步：修正移动 Transition

回到 `Base Layer`，分别选择两条箭头：

#### Idle_Bow → CombatLocomotion

- 关闭 `Has Exit Time`；
- Condition：`speed Greater 0.1`；
- `Transition Duration` 第一版可用 0.1～0.15 秒；
- 不需要额外 Trigger。

#### CombatLocomotion → Idle_Bow

- 关闭 `Has Exit Time`；
- Condition：`speed Less 0.1`；
- `Transition Duration` 第一版可用 0.1～0.15 秒。

为什么这里要求你亲手确认：当前磁盘序列化数据中，这两条 Transition 仍启用了 `Has Exit Time`。它与飞天根因无关，但会造成移动响应延迟。关闭后，移动参数跨过阈值即可立即开始过渡。

如果角色在临界速度附近频繁抖动，可以把进入与退出条件也做成小型 Hysteresis，例如进入 `speed > 0.12`、退出 `speed < 0.08`。第一版先用同一 0.1 验证，不要一次引入过多变量。

### 17.9 第七步：创建或确认上半身 AvatarMask

推荐做法是创建项目自己的 Mask，避免依赖第三方共享资产：

1. 在 `Assets/_Project/Art/Animators/` 右键；
2. 选择 `Create → Avatar Mask`；
3. 命名为 `BossUpperBodyCast`；
4. 在 Humanoid 身体图中：
   - 开启 Body、Head、Left Arm、Right Arm；
   - 根据施法 Clip 决定是否开启手部 IK；
   - 关闭 Root、Left Leg、Right Leg、双脚 IK；
5. 若 Humanoid 粒度造成骨盆覆盖，切换到 Transform 列表，从 Spine/Chest 往下精确包含上半身骨骼，并排除根、Hips 平移和腿链；
6. 保存资产。

如果你暂时复用当前 `ForBowRunning.mask`，也必须在 Inspector 打开检查人体图，不能只根据名字推断。名字只能说明作者意图，不能证明它正好匹配 Boss 的 Skeleton 和本项目需求。

### 17.10 第八步：检查 UpperBodyCast Layer

在 Animator 窗口左上角选择 `Layers`：

1. 确认第二层名为 `UpperBodyCast`；
2. `Blending` 设为 `Override`；
3. `Weight` 设为 1；
4. `Mask` 设为刚确认的上半身 Mask；
5. 进入这一层的 State Machine；
6. 默认 State 应为 `UpperBodyEmpty`，且 Motion 为空；
7. `UpperBodyCast` State 应绑定真正的施法 Clip；
8. 建立 `Any State → UpperBodyCast`：
   - Condition：`cast`；
   - `Has Exit Time` 关闭；
   - Duration 约 0.05 秒；
9. 建立 `UpperBodyCast → UpperBodyEmpty`：
   - `Has Exit Time` 开启；
   - 不需要 Condition；
   - Exit Time 放在动作自然结束的位置；
   - Duration 根据预览微调。

为什么入口用 `Any State`：Boss 可能在 Idle、前进、后退或侧移中的任意一种基础状态开始施法。上半身 Layer 自己的 Any State 允许统一响应 Trigger，不需要为每种移动状态各拉一条线。

为什么出口不用代码 Trigger：施法 Gameplay 的 Telegraph/Recovery 时长和动画 Clip 时长可能略有差异。当前 Presentation 让 Clip 按 Exit Time 自然回空状态，结构简单。若将来 Gameplay 需要精确取消、打断或蓄力维持，再增加专门参数。

### 17.11 第九步：确认转阶段动画链路

在 `Base Layer` 中：

1. 参数列表存在 Trigger `phaseTransition`；
2. `Any State → phase_Transition` 的 Condition 是该 Trigger；
3. 入口 Transition 关闭 `Has Exit Time`；
4. `phase_Transition` State 的 Motion 不为空；
5. `phase_Transition → Idle_Bow` 或合适 Locomotion State 的出口启用 Exit Time；
6. State 名与 Trigger 名不要混淆：State 可以叫 `phase_Transition`，Trigger 当前叫 `phaseTransition`。

代码调用 `SetTrigger` 时传的是 Parameter 名，不是 State 名。如果 Console 出现 `Parameter Hash ... does not exist`，检查的是 Parameters 列表；如果出现 `State could not be found`，才检查 State 路径或 `CrossFade` 名称。

### 17.12 第十步：检查 NavMesh Bake

Boss 有 NavMeshAgent 不代表地面已经可导航。还必须保证：

1. Arena 地面参与 NavMesh 构建；
2. 高台、墙和不可走区域的几何配置正确；
3. 烘焙后的蓝色 NavMesh 覆盖 Boss 出生点和战斗区域；
4. Boss 出生点离 NavMesh 不超过允许的附着采样范围；
5. 柱子周围留下的通道宽度大于 Agent 直径；
6. 高低落差符合 Agent 的 Step Height / Max Slope 等构建设置。

在 Navigation 或 `NavMeshSurface` 对应界面打开可视化。若 Boss 根节点已经被碰撞推到空中，Agent 之后报告不在 NavMesh 往往是后果；若 Boss 从第一帧就在 NavMesh 外，则是独立的 Scene Authoring 问题。

### 17.13 第十一步：Play Mode 分项验收

不要只打一遍 Boss 看“好像可以”。按下面的小实验逐项隔离变量。

#### A. 远距离永久追击

1. 离开安全区触发 Boss；
2. 快速跑到 35 米以外；
3. 确认 Boss 继续追击，没有回 Idle；
4. 绕到柱子后，确认它沿 NavMesh 绕行。

这里的 35 米是当前 `AttackExitDistance`，只代表退出战斗走位、回到 Approach；不代表丢失 Target。

#### B. 双阈值稳定性

1. 在 24～26 米附近往返；
2. 观察 Boss 不应每帧在 Approach/Decision 间抖动；
3. 已进入战斗后，距离增大但未超过 35 米，仍保持 Combat Engagement；
4. 超过 35 米后才回 Approach。

#### C. 三个战斗距离区间

当前首选区间是 10～16 米：

- 小于 10 米：Boss 应斜向后撤；
- 10～16 米：Boss 应主要左右环绕；
- 大于 16 米但仍在战斗状态：Boss 应斜向靠近。

“斜向”来自径向修正和 0.45 倍切线混合，因此不会像电梯轨道一样直进直退。

#### D. 移动施法

1. 让 Boss 进入 10～16 米；
2. 等待它施法；
3. 确认 Transform 仍发生水平移动；
4. 确认腿继续播放 Strafe；
5. 确认胸部和手臂播放施法；
6. 确认法术只释放一次；
7. 确认施法结束上半身回到基础姿势。

#### E. 贴地与飞天回归

1. 在平地连续战斗至少 30 秒；
2. 让 Boss 贴近墙、柱子、台阶边缘；
3. 观察主 Transform 的 Y 是否稳定；
4. 在运行时 Scene 视图查看 CharacterController 胶囊；
5. 若能暂时抬高 Boss 做测试，确认它会下落回地面；
6. 测试传送后不会带着异常垂直速度继续下坠或上升。

#### F. 阶段与死亡

1. 将生命分别打到 70% 和 35% 以下；
2. 确认每个阶段 Trigger 只发生一次；
3. 转阶段期间确认设计上是否允许移动；当前状态职责应与策划预期一致；
4. 死亡后确认 AI、导航、施法和移动全部停止。

### 17.14 第十二步：Profiler 验证

打开 `Window → Analysis → Profiler`，选择 CPU Usage 和 Memory：

1. 在 Boss 环绕和施法时录制一段；
2. 展开 `PlayerLoop → Update.ScriptRunBehaviourUpdate`；
3. 找到 `WizardBossController.Update`；
4. 查看 Timeline 或 Hierarchy 的 `GC Alloc`；
5. 目标是 Boss 每帧走位与 Animator 同步路径为 0 B。

当前实现复用预创建 State，使用值类型 `Vector3`，没有在 `Update` 中使用 LINQ、闭包或 `new` 集合。Profiler 验证仍不可省略，因为第三方 Animator、日志格式化或其他调用者也可能产生分配。

---

## 18. 常见现象的排查表

| 玩家看到的现象 | 先观察什么证据 | 常见根因 | 第一检查点 |
|---|---|---|---|
| Boss 世界位置不动，但腿在走 | Transform Position、CC velocity | 只有 Animator，没有 Gameplay Move | 当前 Gameplay State 是否调用 `MoveInCombat` / `MoveTowardTarget` |
| Boss 平移，腿完全不动 | `speed/moveX/moveZ` | 参数缺失、名字不一致、Animator Controller 错 | 运行时 Parameters 是否变化 |
| Boss 侧移却播放前跑 | local velocity 数值、Clip 坐标 | World/Local Space 混用或 Blend Tree 坐标错误 | `InverseTransformDirection` 与四个 Pos |
| 左右动画反了 | `moveX` 正负正确但视觉反 | Clip 命名方向与项目定义相反 | 交换左右 Clip，不先改 Gameplay 数学 |
| 起步/停步明显延迟 | speed 已跨阈值，State 仍未切 | 移动 Transition 开了 Has Exit Time | 关闭两条移动 Transition 的 Has Exit Time |
| 施法时全身定住 | 腿 Bone 也进入 Cast Pose | AvatarMask 勾选了腿/Root，或没有 Mask | Layer 的 Mask 与人体图 |
| 施法时只有手腕轻微动 | 胸、肩没有跟随 | Mask 漏了 Spine/Chest/Arms | 扩大上半身骨骼范围 |
| 上半身一直卡在施法最后一帧 | Upper Layer State | Cast 没有返回 Empty 的 Transition | Exit Time 出口是否存在 |
| 每帧重复开始施法动画 | Trigger 高亮反复出现 | `SetTrigger` 放进 Tick/Update，而非 Enter | Trigger 调用频率 |
| 法术发出但没动画 | Console、cast 参数 | Parameter 名错、Layer Weight 为 0、入口条件错 | `cast` Trigger 和 UpperBodyCast Layer |
| Animator 报 Parameter Hash 不存在 | Console 原文 | Trigger/Float 参数没创建或改名 | Parameters 名称与类型 |
| Animator 报 State could not be found | Console 原文 | `CrossFade` 的 State 路径/名字错 | State 名、Layer 路径、Hash 来源 |
| Boss 在 25 米边界抖动 | Gameplay State 频繁变化 | 进入/退出距离相同，或排序错误 | Enter < Exit，本项目当前 25 < 35 |
| Boss 进入攻击范围后原地罚站 | Transform 与 State | Decision/Cast 没调用战斗移动 | 两个 State 的 Tick |
| Boss 只绕圈，不修正距离 | 与玩家距离持续偏离 | Combat Direction 永远只用 tangent | Preferred Min/Max 和 MoveMode |
| Boss 环绕时背对玩家 | movement 正常，rotation 不对 | 朝向跟着移动向量，而非 Target | `FaceTarget` / `FacePoint` |
| Boss 遇柱子停住 | Agent desiredVelocity、pathStatus | NavMesh 未覆盖、入口太窄、Local Avoidance 卡住 | NavMesh 可视化、Agent 是否 OnNavMesh |
| Boss 突然飞高 | 主 Transform Y、Collider | CC 碰撞上推后缺少向下运动；或双重移动 | Gameplay Root、高度日志、Root Motion/Agent 设置 |
| 只有模型飞，Collider 留在地面 | 主 Root 与 Visual Root 对比 | Clip Root/Bone、Mask 或模型层级问题 | Skeleton/Animator，而非 NavMesh |
| 传送后立即猛坠 | verticalVelocity | 传送没有清零垂直速度 | Teleport 完成逻辑 |
| 脚步滑动 | 世界速度与 Clip 步频 | 代码速度和动画播放速度不匹配 | MoveSpeed、Clip Speed、Animator Speed |

### 18.1 正确的 Debug 顺序

当多个系统叠在一起时，建议从“最真实、最底层”的证据往上查：

1. 主 Transform 是否真的移动；
2. CharacterController velocity 是多少；
3. NavMeshAgent desiredVelocity 与 pathStatus 是什么；
4. Gameplay FSM 当前是什么 State；
5. Animator Parameters 收到了什么；
6. Base Layer 当前 State/Blend Tree 采样点；
7. Upper Layer 当前 State、Weight、Mask；
8. 最后才判断 Clip 和骨骼姿势本身。

这样可以避免看到“脚没动”就直接改 Clip，最终掩盖真正的 Gameplay Movement Bug。

### 18.2 临时日志应该记录什么

如果飞天再次出现，一条有价值的诊断记录至少包括：

```text
frame/time
Gameplay State
root position.y
CharacterController.isGrounded
verticalVelocity
CharacterController.velocity
NavMeshAgent.isOnNavMesh
NavMeshAgent.desiredVelocity
Animator speed/moveX/moveZ
```

项目中应使用 `Game.Core.GameLog`，不要直接调用 `Debug.Log`。在 `Update` 中长期开字符串插值会制造日志噪声和性能问题；临时诊断应使用采样间隔、状态变化时记录，完成排查后移除或保留在明确的 Development 条件下。

---

## 19. 这套方案的设计取舍

### 19.1 为什么不用一个“攻击距离”解决全部行为

一个阈值实现简单，但边界处的微小距离变化会让状态反复切换。双阈值保存了上一帧是否已经进入战斗的状态，因此具有记忆。代价是多一个字段，并且必须保证 `AttackExitDistance >= AttackEnterDistance`。

### 19.2 为什么环绕不是纯切线

纯切线理论上保持当前半径，但碰撞、路径绕行、玩家移动和离散帧都会让半径漂移。加入径向修正后能回到理想区间。代价是路径不再是完美圆周；这通常反而更自然。

### 19.3 为什么切线权重当前固定为 0.45

固定权重便于第一版理解和测试。过小会像直线追击/后撤，过大则距离修正太慢。以后可把它做成 `BossDefinition` 数据，让不同 Boss 有不同性格；当前先避免增加尚未验证价值的调参项。

### 19.4 为什么环绕方向每 2 秒翻转

永久只沿一个方向容易把 Boss 卡在特定障碍侧面，也让玩家过快掌握固定规律。定时翻转能增加变化并帮助脱离局部卡位。代价是翻转瞬间可能显得机械。后续可在技能结束、遇障碍或随机但可控的 Decision 点切换，而不是严格定时。

### 19.5 为什么战斗中用短程 NavMesh 目标

追击时目标是玩家的世界位置；环绕时真正想要的是一个方向，不是远处固定点。将当前方向乘一个短步长得到局部目标，NavMesh 可以在附近障碍上做可走修正。步长过短会频繁改目标、方向不稳定；过长会让 Agent 切过圆心或做大范围绕路。当前 3 米是第一版折中。

### 19.6 为什么施法速度比普通战斗移动慢

当前数据使用：

- 基础 Move Speed：10；
- Combat Move Speed Multiplier：0.7；
- Cast Move Speed Multiplier：0.6。

也就是普通战斗走位约 7 单位/秒，施法约 6 单位/秒，再乘状态效果修正。移动施法不代表速度必须完全不变；略微减速可以保留攻击承诺感，让玩家有时间读动作。过慢会重新接近“原地罚站”，过快则削弱 Telegraph。

### 19.7 为什么瞄准快照与移动并行

瞄准快照让玩家在 Telegraph 期间有躲避空间；持续移动让 Boss 不容易成为静止靶。代价是 Boss 可能一边移动一边朝旧位置转。追踪型法术可以选择持续更新目标，但那应该由 Spell 或 Program 数据明确表达，不能无差别地改变所有技能。

### 19.8 为什么使用 Animator Layer，而不是做很多组合 Clip

若不用 Layer，就要制作：

- 站立施法；
- 前进施法；
- 后退施法；
- 左移施法；
- 右移施法；
- 未来每种移动速度和每个技能的更多组合。

组合数量会快速膨胀。Layer 让四个下半身移动动作复用同一个上半身施法动作。代价是 Mask 需要精心配置，而且上下半身节奏可能不完全协调。

### 19.9 为什么当前不用 Root Motion

代码位移更适合 NavMesh、动态距离修正、状态减速与可测试的战斗 AI。Root Motion 能提供更精确的脚步接触，但需要 `OnAnimatorMove`、路径速度适配、打断和网络同步等额外设计。对当前 Boss 来说，确定性 Gameplay 控制优先。

### 19.10 为什么水平和垂直 Move 分开调用

当前实现先执行 State 的水平 `Move`，再在统一出口执行垂直 `Move`。优点是职责清晰，所有 State 自动获得 Gravity；缺点是同一帧调用两次 CharacterController 碰撞求解，边缘接触可能比合成单个 Motion 更难推理。

更进一步的架构可以让各 State 只输出水平期望速度，Controller 在帧末把水平与垂直合成一次 `Move`。这会形成更纯粹的单次移动管线，但需要重构现有 State/Navigation 接口。当前修复选择了更小、风险更低的改动。

---

## 20. 验证层级：什么证据能证明什么

“没有报错”不能证明功能正确。不同验证手段覆盖不同问题。

### 20.1 Compilation

能证明：

- C# 语法、类型、程序集引用可通过；
- 代码可被 Unity 编译。

不能证明：

- Prefab 引用正确；
- Animator 参数存在；
- NavMesh 已 Bake；
- Boss 实际贴地。

### 20.2 EditMode Test

适合验证不依赖 Scene 帧循环的纯逻辑：

- Attack Enter/Exit Hysteresis；
- 10～16 米三个 Move Mode 边界；
- 径向和切线方向；
- 左右环绕符号；
- BossDefinition 的字段排序与 Prefab 契约。

纯函数测试失败时，通常能快速定位到输入输出规则；它不需要等待真实动画播放。

### 20.3 PlayMode Test

适合验证 Unity 生命周期和组件协作：

- `Update` 是否调用 Gravity；
- CharacterController 被抬高后是否下落；
- State 切换是否保持移动；
- Trigger 是否只在 Enter 调用；
- NavMeshAgent 与角色 Transform 是否同步。

### 20.4 Scene Playthrough

能发现自动测试很难覆盖的组合问题：

- 特定墙角/台阶把胶囊抬起；
- AvatarMask 的骨骼观感；
- 施法 Telegraph 是否可读；
- 环绕速度是否让玩家眩晕或难以瞄准；
- Camera、VFX、碰撞和动画的综合体验。

### 20.5 Profiler

用来证明性能性质：

- 每帧路径是否有 `GC Alloc`；
- 多个 Boss/Enemy 时 CPU 是否异常；
- Animator 多 Layer 的成本；
- NavMesh 路径刷新是否过于频繁。

### 20.6 当前验证边界与一个已发现的测试基线问题

本文档是根据当前磁盘代码、Prefab、Animator Controller 和 Boss Definition 的静态证据撰写，并完成文档结构检查。最终骨骼观感、Scene Playthrough 与 Profiler 仍必须由你在 Unity Editor 中执行。

此外，当前 Boss Definition 已从旧路径：

`Assets/_Project/ScriptableObjects/P8/Boss/P8_ArcaneArchmageDefinition.asset`

变更为：

`Assets/_Project/ScriptableObjects/P8/Boss/P8_Boss_Velmora_Definition.asset`

但 `EnemyNavigationMathTests.P8BossAssets_UseNavMeshAndValidCombatDistanceOrdering` 仍硬编码旧路径。运行全套 EditMode Tests 时，这条测试会因为加载不到旧资产而失败，即使 Gameplay Definition 本身正确。正确处理是把测试路径同步到新资产，并确认 Prefab 引用的 GUID 与新资产一致；这属于测试基线维护，不应被误报成环绕算法失败。

---

## 21. 你应该怎样阅读源码

不要从 900 多行的 Controller 第一行一路硬读到底。按照“数据定义 → 纯规则 → 导航适配 → Gameplay State → Unity 生命周期 → Animator 消费 → Focused Test”的顺序，每次只回答一个问题。

### 21.1 第一站：数据定义和数据实例

打开 [BossDefinition.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Character/Enemy/Boss/Data/BossDefinition.cs:12>)。

责任：定义一个 Boss 可以配置哪些数据。它属于 `Game.Character` Assembly，因为这些字段直接服务于 Boss AI、移动和施法决策。

先看：

- `BossDefinition`：类型定义；
- `AttackEnterDistance` / `AttackExitDistance`：Combat Engagement 双阈值；
- `PreferredCombatMinDistance` / `PreferredCombatMaxDistance`：战斗走位理想区间；
- `CombatMoveSpeedMultiplier` / `CastMoveSpeedMultiplier`：不同状态速度；
- `GravityAcceleration` / `GroundedStickSpeed`：垂直运动参数。

然后打开 [P8_Boss_Velmora_Definition.asset](</F:/Develop Project/RPG/RPG/Assets/_Project/ScriptableObjects/P8/Boss/P8_Boss_Velmora_Definition.asset:15>)。

责任：这是上面 C# 数据结构的一个具体 `ScriptableObject` 实例。`.cs` 规定“表格有哪些列”，`.asset` 填入“Velmora 这一行的值”。

读完后的检查问题：

> 为什么 `AttackExitDistance` 必须大于 `AttackEnterDistance`？为什么首选区间 10～16 米又小于进入距离 25 米？

你应该能回答：前一对控制 Gameplay State 的稳定切换，后一对控制已经进入战斗后每帧具体向哪边移动，两组数据服务于不同层次。

### 21.2 第二站：纯数学规则

打开 [EnemyNavigationMath.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Character/Enemy/Navigation/EnemyNavigationMath.cs:17>)，先只读 `BossCombatMovementMath`。

责任：输入位置、距离和已有状态，输出布尔状态、枚举或方向向量。它不读取 Scene，不移动 GameObject，也不播放动画。

按顺序读：

1. `ResolveCombatEngagement`：上一帧 engaged 状态怎样影响本帧阈值；
2. `ResolveMoveMode`：距离怎样映射到 Approach / Orbit / Retreat；
3. `ResolveCombatDirection`：径向、切线和修正方向怎样合成。

这里的关键数据结构：

- `bool isEngaged`：一个布尔状态，调用者拥有并在每帧写回；
- `float horizontalDistance`：当前帧只读的水平距离快照；
- `BossCombatMoveMode : byte`：三个互斥结果的枚举；
- `Vector3`：值类型方向结果，返回时按值传递；
- `orbitSign`：只取正负号，决定切线朝左还是朝右。

读完后的检查问题：

> 如果 Boss 位于原点、玩家位于世界前方，`orbitSign = 1` 时切线为什么指向世界右侧或左侧？把公式手算一遍，是否与项目 Clip 的视觉方向一致？

### 21.3 第三站：NavMesh 适配器

打开 [EnemyNavigationMotor.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Character/Enemy/Navigation/EnemyNavigationMotor.cs:20>)。

责任：把“我想到达某个世界点”的请求交给 `NavMeshAgent`，维护路径刷新、失败和卡住恢复，再把 Agent 的建议速度暴露给 Controller。它不直接成为角色位置权威。

先看构造阶段：

- 为什么把 `updatePosition` 和 `updateRotation` 设为 false；
- 为什么需要 `avoidancePriority`；
- 为什么保存 `_desiredVelocity` 而不是直接移动 Owner。

再看：

- `NavigateTo`：外部提交目标；
- `DesiredVelocity`：结果出口；
- `SyncToCharacter`：把模拟代理的 `nextPosition` 拉回真实角色位置；
- `StopNavigation`：State 退出或 Encounter 结束时清理路径。

读完后的检查问题：

> 如果删掉 `SyncToCharacter`，在 updatePosition=false 时 Agent 的内部位置会怎样逐渐与 CharacterController 脱节？

### 21.4 第四站：三个最关键的 Gameplay State

依次打开：

1. [BossApproachState.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Character/Enemy/Boss/Runtime/BossApproachState.cs:6>)；
2. [BossDecisionState.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Character/Enemy/Boss/Runtime/BossDecisionState.cs:7>)；
3. [BossCastState.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Character/Enemy/Boss/Runtime/BossCastState.cs:7>)。

责任分别是：

- Approach：还没进入战斗时持续接近；
- Decision：在战斗中走位，并按间隔选择下一法术；
- Cast：锁住本次行动与瞄准快照，同时继续走位，按 Telegraph/Recovery 时间释放与结束。

先看每个 State 的 `Tick`，不要先追所有辅助方法。观察它们只是组织“什么时候调用什么”，真正的位移实现回到 Controller。

读完后的检查问题：

> `BossCastState` 为什么在 `Enter` 触发动画，却在 `Tick` 调用移动？如果把 `SetTrigger` 放进 Tick 会发生什么？

### 21.5 第五站：Unity 生命周期与移动权威

打开 [WizardBossController.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Scripts/Character/Enemy/Boss/Runtime/WizardBossController.cs:21>)。

不要一次读完整文件，只按以下定位跳转：

1. `Update`：看每帧总顺序；
2. `MoveTowardTarget`：看远距离导航；
3. `MoveInCombat`：看环绕/距离修正；
4. `MoveUsingNavigationOrFallback`：找到唯一水平 `CharacterController.Move`；
5. `ApplyVerticalMotion`：找到统一垂直 Move；
6. `SyncAnimator`：看 World Space 到 Local Space；
7. `RefreshCombatEngagement`：看纯函数结果怎样写回状态；
8. `RouteAfterAction`：看状态路由。

责任：它是 Unity Runtime Adapter。上面纯规则只返回数据，NavMesh 只给建议速度，State 只决定调用时机；Controller 拥有组件引用和 Unity 生命周期，最终调用 Unity API。

读完后的检查问题：

> 每一帧有哪两个位置调用 `CharacterController.Move`？为什么导航速度中的 Y 被清零？谁在最后同步 Animator？

### 21.6 第六站：动画结果消费者

打开 [Enemy_Wizard.controller](</F:/Develop Project/RPG/RPG/Assets/_Project/Art/Animators/Enemy_Wizard.controller>)。

责任：消费 `speed/moveX/moveZ/cast/phaseTransition`，把 Gameplay 数据转成最终骨骼姿势。

阅读顺序：

1. Parameters；
2. Base Layer 的 Idle 和 CombatLocomotion；
3. CombatLocomotion 内的 2D Blend Tree；
4. UpperBodyCast Layer；
5. Layer 的 AvatarMask；
6. Any State → Cast 与 Cast → Empty；
7. phase transition 的 Trigger 和出口。

读完后的检查问题：

> 哪些参数是连续量，哪个是一次事件？Base Layer 和 Upper Layer 最终分别控制哪些 Bone？

### 21.7 第七站：Focused Tests

打开 [EnemyNavigationMathTests.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Tests/Character/EnemyNavigationMathTests.cs:1>)。

责任：用明确输入和断言验证 Hysteresis、三个距离模式、环绕方向及资产契约。

重点看测试名，不要先陷入 NUnit 语法：

- 已进入战斗时，距离在 Enter 和 Exit 之间是否仍保持 engaged；
- 未进入时，同样距离是否仍为 false；
- 过远方向是否包含朝向玩家的分量；
- 过近方向是否包含远离玩家的分量；
- 左右 orbitSign 是否产生相反切线。

再打开 [WizardBossControllerTests.cs](</F:/Develop Project/RPG/RPG/Assets/_Project/Tests/CharacterPlayMode/WizardBossControllerTests.cs>)。

责任：用真实 `GameObject`、`CharacterController` 和帧推进验证 Unity Runtime 行为。重点寻找 `Approach_WhileUnsupported_FallsInsteadOfKeepingRaisedHeight`，理解为什么纯数学测试不能证明角色会受 Gravity 影响。

读完后的检查问题：

> 哪些 Bug 可以用 EditMode 精确证明，哪些必须进 PlayMode 或真实 Scene 才能证明？

---

## 22. 阅读这些源码前需要懂的 C# 与 Unity 基础

### 22.1 ScriptableObject 是“数据资产”，MonoBehaviour 是“场景行为”

`BossDefinition` 继承 `ScriptableObject`。它作为 Project Asset 保存，可被多个 Prefab 引用，适合策划调参。它不会自己收到 `Update`。

`WizardBossController` 继承 `MonoBehaviour`，挂在 Boss GameObject 上，拥有 `Awake/OnEnable/Update` 等 Unity 生命周期，适合连接 Scene 组件和执行运行时行为。

设计收益：同一套 Controller 代码可以搭配不同 Definition，形成不同 Boss 参数；不用复制一份几乎相同的脚本。

### 22.2 普通 C# State 为什么不继承 MonoBehaviour

`BossDecisionState`、`BossCastState` 等是普通 C# 对象。Unity 不会自动调用它们的 `Tick`，由 `WizardBossController.Update` 主动驱动。

这样做的原因：

- 状态切换顺序显式；
- State 不需要各自挂到 GameObject；
- 可以在 `Awake` 一次创建并复用；
- 避免每帧或每次切换 `new` 对象；
- `Exit` 是释放 Action Lock、导航等资源的统一边界。

### 22.3 struct Vector3 为什么适合每帧数学

`Vector3` 是 `struct` 值类型，包含三个 float。局部计算如相减、归一化、乘法会得到新的值，不需要创建托管堆对象。它非常适合逐帧位置与方向运算。

不过“值类型”不等于所有用法绝对零开销。把它装箱成 `object`、放进非泛型集合或交给某些格式化 API 仍可能产生 Allocation。Profiler 才是最终证据。

### 22.4 deltaTime 为什么必须参与速度积分

速度单位通常是“世界单位/秒”。一帧真正要移动的是：

```text
motionDelta = velocity × deltaTime
```

60 FPS 时 `deltaTime` 约 1/60 秒，30 FPS 时约 1/30 秒。乘上它后，一秒累计移动距离大致相同。若直接把 velocity 传给 Move，帧率越高就走得越快。

Gravity 的速度更新同理：

```text
verticalVelocity -= gravityAcceleration × deltaTime
```

然后再把速度乘一次 deltaTime 得到本帧位移。前一次积分把加速度变成速度，后一次积分把速度变成位移。

### 22.5 sqrMagnitude 为什么常用于“接近零”判断

`magnitude` 需要平方根；`sqrMagnitude` 只算 `x²+y²+z²`。只需判断向量是否足够小时，可以比较：

```csharp
direction.sqrMagnitude <= 1e-6f
```

这样避免不必要的平方根，也避免对零向量调用 normalized 后得到无意义方向。

### 22.6 Quaternion 在这里做什么

3D 旋转常用 `Quaternion` 表示。当前流程：

```text
水平目标方向
    ↓ Quaternion.LookRotation
目标朝向 Quaternion
    ↓ Quaternion.RotateTowards
按 TurnSpeedDegrees × deltaTime 逐步旋转
```

`LookRotation` 回答“要让 forward 指向这个方向，应得到什么旋转”；`RotateTowards` 限制每帧最多旋转多少角度，避免瞬间 Snap。

### 22.7 StringToHash 为什么在初始化时做

Animator API 可以用字符串参数名，也可以用整数 Hash。每帧反复处理字符串既容易拼错，也有额外查找成本。当前实现把固定名字预先转成整数，然后使用 `SetFloat(int, value)` / `SetTrigger(int)`。

Hash 只是高效标识，不会自动创建 Parameter。Controller 中仍必须存在同名且同类型的 Parameter。

### 22.8 Clamp 的设计含义

将 `localVelocity / baseMoveSpeed` Clamp 到 `[-1, 1]`，是把任意世界速度映射到 Blend Tree 的标准坐标域。它保证异常高速度不会把采样点推到很远。

代价是超过基础速度的差异被压平：6 m/s 和 20 m/s 都可能映射到 1。若以后需要 Sprint 动画，应该增加速度维度或嵌套 Tree，而不是单纯取消 Clamp。

### 22.9 为什么每帧先把 _lastHorizontalVelocity 清零

某些 State 这一帧可能不移动。如果保留上一帧速度，Animator 会继续认为 Boss 在走。每帧开头先设为零，只有实际执行水平 Move 后才写回真实速度，就能让“本帧无移动”自然过渡到 Idle。

---

## 23. 练习：亲手建立心智模型

### 23.1 不开 Unity 的手算练习

场景：Boss 在 `(0, 0, 0)`，玩家在 `(0, 3, 12)`。

1. `toTarget = player - boss = (0, 3, 12)`；
2. 清掉 Y 后是 `(0, 0, 12)`；
3. 距离为 12；
4. Normalized Radial 是 `(0, 0, 1)`；
5. 12 位于 10～16，模式是 Orbit；
6. 正方向 Tangent 由 `(z, 0, -x)` 得 `(1, 0, 0)`。

问题：Boss 当前若面向玩家，传给 Animator 的 local velocity 大致是什么？

答案：角色 forward 约等于世界 `(0,0,1)`，world right 约等于 `(1,0,0)`，所以 local velocity 主要是 `x > 0, z ≈ 0`，应混到 `WalkRight_MagicWand`。

### 23.2 修改距离区间的观察练习

临时把 Preferred 区间改成 5～8 米，进入 Play Mode 后观察：

- Boss 会更主动贴近玩家；
- 近距离 Camera 与 VFX 更拥挤；
- 玩家绕背和近战压力变大；
- 后撤触发也更晚。

退出 Play Mode 前记下原值。由于 ScriptableObject 在 Play Mode 中修改可能直接污染 Asset，最安全方式是先记录或使用版本控制对比。这个实验的目的不是寻找最终平衡，而是建立“字段 → 玩家可见行为”的联系。

### 23.3 禁用 UpperBody Layer 的对照实验

在 Play Mode 暂时把 `UpperBodyCast` Layer Weight 从 1 调到 0：

- Gameplay 仍会移动和发法术；
- Base Layer 移动仍会播放；
- 上半身施法动作消失。

这个实验直观证明 Animator Layer 属于 Presentation，不负责技能真实释放。

### 23.4 打开 Apply Root Motion 的危险对照实验

只在可恢复的测试副本中短暂开启，观察 Transform 和脚步变化，然后立即还原。你可能看到速度、碰撞或位置与代码计算不再一致。不要在正式 Prefab 上保存这个实验。

它帮助理解“两个移动权威”的问题，但不是修复手段。

### 23.5 注释 Cast 中移动调用的代码阅读实验

无需真的修改生产文件，只在脑中推演：若删掉 `BossCastState.Tick` 中的 `MoveInCombat`：

- Decision 时仍环绕；
- 一进入 Cast 就停止世界位移；
- 上半身 Layer 即使配置正确，也只能让站立角色施法；
- 这证明 Animator Layer 不能创造 Gameplay Movement。

### 23.6 自测问题

读完后尝试不看文档回答：

1. 为什么 Target 永久锁定不等于 Boss 永远处于 Attack State？
2. 为什么 Attack Enter 和 Exit 要用两个值？
3. 径向和切线的 Dot Product 为什么接近 0？
4. `desiredVelocity` 和 `CharacterController.velocity` 分别代表什么？
5. 为什么要将世界速度转换到 Local Space？
6. `cast` 为什么用 Trigger，`speed` 为什么用 Float？
7. AvatarMask 如何让腿继续走而手臂施法？
8. Apply Root Motion 为什么关闭？
9. CharacterController 为什么不会自动下落？
10. 如何用 Transform 与 Collider 证据区分“模型骨骼飞”和“Gameplay Root 飞”？

如果这十题能用自己的话回答，你已经建立了这套系统的主要心智模型。

---

## 24. 术语表

| 术语 | 在本文中的简单含义 |
|---|---|
| `Mesh` | 玩家最终看见的角色表面几何 |
| `Skeleton` | 角色内部的层级骨架 |
| `Bone` | 骨架中的一个 Transform 节点 |
| `Animation Clip` | Bone 属性随时间变化的数据 |
| `Avatar` | Humanoid 模型骨骼到 Unity 标准人体骨骼的映射 |
| `Animator` | Scene 中每帧计算并写入骨骼姿势的组件 |
| `Animator Controller` | State、Transition、Parameter、Layer 与 Blend Tree 的资产图 |
| `Gameplay FSM` | 决定 Boss 正在追击、思考、施法、传送或转阶段的代码状态机 |
| `Animator State Machine` | 决定当前显示哪段动画的视觉状态机 |
| `Parameter` | Gameplay 代码向 Animator 传递数据或事件的接口 |
| `Trigger` | 被设置后用于触发一次 Transition 的事件型 Parameter |
| `Blend Tree` | 根据连续参数混合多段 Animation Clip |
| `Animator Layer` | 在基础姿势上再混合一套独立动画逻辑 |
| `AvatarMask` | 限制某个 Layer 能影响哪些人体部位或 Transform |
| `Override` | 上层姿势覆盖下层对应 Bone |
| `Additive` | 上层把相对参考姿势的变化叠加到下层 |
| `Root Motion` | Animation Clip 的根位移驱动 GameObject 世界移动 |
| `World Space` | 整个 Scene 共享的坐标系 |
| `Local Space` | 以当前角色朝向为基准的坐标系 |
| `Radial` | Boss 与玩家连线方向 |
| `Tangent` | 与 Radial 垂直、沿圆周的方向 |
| `Dot Product` | 衡量两个向量方向关系的标量；垂直时为 0 |
| `Normalized` | 长度变为 1，只保留方向的向量 |
| `Hysteresis` | 用不同进入/退出阈值减少边界抖动 |
| `NavMesh` | Scene 中 AI 被允许行走的可导航表面 |
| `Steering` | 路径规划器给出的期望移动速度/方向 |
| `CharacterController` | 执行胶囊碰撞移动，但不自动模拟 Gravity 的组件 |
| `Gravity Acceleration` | 每秒改变垂直速度的加速度 |
| `Grounded Stick Speed` | 落地时保留的轻微向下速度，用来稳定贴地 |
| `Telegraph` | 技能释放前让玩家读招和反应的时间 |
| `Recovery` | 技能释放后的收招时间 |
| `Action Lock` | 阻止同一时刻启动另一行动的互斥标记 |
| `GC Alloc` | 托管堆分配；频繁逐帧分配可能引起 Garbage Collection 卡顿 |

---

## 25. 官方资料与继续学习顺序

建议按本文实际需要阅读，不必一次啃完全部 Manual：

1. [Animation 系统总览](https://docs.unity3d.com/cn/6000.0/Manual/AnimationOverview.html)：先理解 Clip、Animator、Controller 的关系；
2. [创建 Animator Controller](https://docs.unity3d.com/cn/6000.0/Manual/AnimatorControllerCreation.html)：熟悉 State 与 Transition；
3. [Animator 窗口](https://docs.unity3d.com/cn/current/Manual/AnimatorWindow.html)：学习 Parameters、Layers 和状态图界面；
4. [Animation Layers](https://docs.unity3d.com/cn/current/Manual/AnimationLayers.html)：理解多层姿势组合；
5. [Avatar Mask](https://docs.unity3d.com/cn/current/Manual/class-AvatarMask.html)：理解 Humanoid 与 Transform Mask；
6. [2D Simple Directional Blend Tree](https://docs.unity3d.com/cn/6000.0/ScriptReference/Animations.BlendTreeType.SimpleDirectional2D.html)：对应四方向移动；
7. [Animator State Transition](https://docs.unity3d.com/cn/6000.0/ScriptReference/Animations.AnimatorStateTransition.html)：理解 Exit Time、Duration 与中断；
8. [Animator API](https://docs.unity3d.com/kr/6000.0/ScriptReference/Animator.html)：查 `SetFloat`、`SetTrigger`、`StringToHash`；
9. [CharacterController.Move](https://docs.unity3d.com/ja/current/ScriptReference/CharacterController.Move.html)：特别注意它不会自动应用 Gravity；
10. [NavMeshAgent.nextPosition](https://docs.unity3d.com/ja/current/ScriptReference/AI.NavMeshAgent-nextPosition.html)：理解手动位置同步；
11. [Root Motion](https://docs.unity3d.com/cn/2023.2/Manual/RootMotion.html)：理解动画根位移与 Bake Into Pose。

阅读优先级建议：先完成 1、3、4、5、9，再回项目实操；Blend Tree 数学和 Root Motion 可以在第一次实操后复习。官方文档有些语言页面版本不同，但本文引用的是 Unity 官方概念和 API；具体 Inspector 名称以项目使用的 Unity 6.3 Editor 为准。

---

## 26. 一页复习：从玩家画面反推系统

玩家看到 Boss 一边侧移、一边抬手施法。背后实际发生了这些事：

1. Encounter 已永久保存玩家 Transform，不再用普通 Detect Radius 丢失目标；
2. 双阈值决定继续战斗还是回到远距离 Approach；
3. 理想距离判断选择靠近、环绕或后撤；
4. Radial 与 Tangent 合成期望方向；
5. NavMeshAgent 把方向修正为可绕障碍的 desiredVelocity；
6. CharacterController 执行水平世界位移；
7. Cast State 与移动并行推进，并在 Enter 设置一次 cast Trigger；
8. 统一 Gravity 在帧末执行向下位移，防止碰撞上推后悬空；
9. 实际水平速度转换到角色 Local Space；
10. Base Layer Blend Tree 用 moveX/moveZ 选择腿部步态；
11. UpperBodyCast Layer 用 AvatarMask 只覆盖上半身；
12. 两层骨骼姿势合成后，由 Renderer 显示给玩家。

这条链路中最重要的架构原则是“每一层只负责一种决定”：

- ScriptableObject 保存可调数据；
- Pure Math 决定规则；
- Gameplay FSM 决定行为阶段；
- NavMeshAgent 规划路径；
- CharacterController 修改世界位置；
- Animator 根据数据生成骨骼姿势；
- AvatarMask 限制动画影响范围。

当 Bug 出现时，先找到哪一层的输出第一次变错，再修改那一层。不要让 Animator 修补 Gameplay 位移，也不要让导航参数掩盖错误骨骼 Mask。

## 模拟面试回答

我为法术型 Boss 实现移动施法时，先把 Gameplay 行为与动画表现拆成两条可以独立验证的链路。Gameplay 侧使用进入距离和退出距离形成 Hysteresis：Boss 未进入战斗时要到较近的阈值才切入，已经进入后只有超过更远的阈值才退回追击，因此不会在边界频繁切换。进入战斗后，我根据 Boss 与玩家的水平位置计算 Radial Direction，再构造与它垂直的 Tangent Direction。距离合适时采用切线环绕，过远或过近时分别混入靠近或后撤的径向修正。这个计算放在 Pure Function 中，可以用 EditMode Test 精确验证边界和方向。

路径与位移采用单一权威设计。NavMeshAgent 关闭 updatePosition 和 updateRotation，只负责路径规划并输出 desiredVelocity；WizardBossController 读取该速度，最终由 CharacterController.Move 执行碰撞移动，然后用 nextPosition 把 Agent 的内部模拟位置同步回角色。Decision State 和 Cast State 都调用同一套战斗移动，所以 Action Lock 只阻止并发技能，不会冻结角色位移。施法进入时保存 Aim Snapshot 并设置一次 cast Trigger，Telegraph 到时释放技能，Recovery 结束后回到 Decision。

动画侧把 CharacterController 的实际水平速度转换到 Boss Local Space，得到 moveX 和 moveZ，再由 Base Layer 的 2D Simple Directional Blend Tree 混合前进、后退、左移和右移动画。施法放在独立的 UpperBodyCast Layer，通过 Override 和 AvatarMask 只覆盖胸部、头部与双臂，Root、骨盆平移和腿部继续使用 Base Layer，因此玩家看到的是腿在环绕、上半身同时施法。当前移动是 In-place 加代码位移，所以关闭 Apply Root Motion，避免 Animator 和 CharacterController 同时修改 Transform。

飞天 Bug 的诊断中，我先区分 Visual Root 与 Gameplay Root。日志表明 Boss 主 Transform 的世界 Y 持续升高，Collider 和导航同步点也一起升高，因此排除了单纯 Bone、AvatarMask 和 Clip 姿势问题。Prefab 的 Apply Root Motion 已关闭，NavMeshAgent 也不自动写位置，最终定位到 CharacterController 的水平碰撞和台阶修正可能把胶囊向上推，而 CharacterController.Move 本身不会自动施加 Gravity。修复是在 Controller 的每帧总出口统一积分垂直速度：离地时应用 Gravity，落地时保留轻微向下的 Grounded Stick Speed，并在最终垂直 Move 后再次同步 Agent。这样所有 Gameplay State，包括 Approach、Decision 和 Cast，都会自动获得一致的贴地行为，也避免未来新增 State 时漏写 Gravity。
