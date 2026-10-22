# 弓箭手蓄力瞄准取景（角色让位）— 实现说明

> 小功能：长按左键蓄力瞄准时，玩家角色平滑滑到画面**左下角**，把屏幕中心的准心更清楚地露出来（仿原神弓箭手重击）；松手退出瞄准时，画面平滑归位。
>
> 本文讲：怎么实现的、原理是什么、你在 Editor 端要做哪三步。

---

## 一、一句话原理

我们**不移动角色**，而是**移动相机**。让相机向右上方平移一点，由于角色没动，它在屏幕上就会朝**反方向**（左下）滑出去——这就是「角色让位、露出准心」的效果。

为什么相机右移 → 角色看起来左移？想象你举着手机拍一个站着不动的人：你把手机往右挪，取景框里的人就往左跑。相机平移和画面里物体的移动**永远相反**。所以：

| 相机偏移（相机空间） | 角色在屏幕上的移动 |
|---|---|
| +X（相机右移） | 左移 |
| +Y（相机上移） | 下移 |

取 `Offset = (+X, +Y)` → 角色滑向**左下**。正是我们要的。

---

## 二、用什么实现的：`CinemachineCameraOffset`

项目相机是 Cinemachine 3.1.6 的轨道机位。Cinemachine 自带一个扩展组件 **`CinemachineCameraOffset`**，作用就是「在相机管线最后，给相机位置加一个偏移」。它有三个关键字段：

- **`Offset`（Vector3，相机空间）**：要加的偏移。这就是我们每帧去改的东西。
- **`PreserveComposition`（bool）**：是否「偏移后重新转回去对准角色」。我们要的是角色真的偏出去，所以保持**默认 `false`**——它会让相机**连同瞄准点一起平移**（既不重新对准、也不旋转），干净地把整个画面平移，角色随之滑到一边。
- **`ApplyAfter`**：在管线哪个阶段施加，保持默认（Aim 之后）即可。

我们没有手写相机移动代码去和 Cinemachine 抢方向盘（那样会被 Cinemachine 每帧覆盖），而是**通过它官方提供的偏移口子**去改，最稳。

---

## 三、脚本怎么写的：`AimFramingController.cs`

新增文件：`Assets/_Project/Scripts/Rendering/AimFramingController.cs`（属于 `Game.Rendering` 表现层）。

核心逻辑就三件事：

1. **订阅瞄准事件**（复用蓄力重击早就发布的 `AimStateChangedEvent`，和准心 UI 是同一个事件的两个订阅者）：
   ```csharp
   private void OnEnable()  => EventBus<AimStateChangedEvent>.Subscribe(OnAimStateChanged);
   private void OnDisable() => EventBus<AimStateChangedEvent>.Unsubscribe(OnAimStateChanged);
   ```

2. **事件来了只切换"目标偏移"**，不直接瞬移：
   ```csharp
   private void OnAimStateChanged(AimStateChangedEvent e) {
       _targetOffset = e.Active ? _aimOffset : Vector3.zero; // 瞄准→偏移；退出→归零
   }
   ```

3. **每帧把当前偏移平滑逼近目标**（所以进入/退出都是顺滑过渡，不是硬切）：
   ```csharp
   private void Update() {
       _cameraOffset.Offset = Vector3.Lerp(
           _cameraOffset.Offset, _targetOffset, _transitionSpeed * Time.deltaTime);
   }
   ```

两个可调旋钮（Inspector 上）：
- **`_aimOffset`**：瞄准时的偏移量，默认 `(1.0, 0.6, 0)`。X 越大角色越靠左，Y 越大越靠下。**具体数值要你在场景里试**——它和相机离角色多远、FOV 多大都有关，没有放之四海皆准的值。
- **`_transitionSpeed`**：过渡快慢，默认 8。越大越"跟手"，越小越"电影感"。

### 为什么这样设计（和项目其它部分一致的套路）
- **表现层只订阅事件、不碰 gameplay。** 这个组件在 `Game.Rendering`，只认 `Game.Core` 里的 `AimStateChangedEvent`，**完全不知道弓箭手的存在**。蓄力逻辑（`PlayerChargeAttackState`）早就在进入/退出时发布了这个事件，我们这次**一行 gameplay 代码都没改**，只是多挂了一个监听者。这就是事件解耦的威力：加表现不动玩法。
- **进入靠"设目标 + 每帧插值"，归位靠同一套。** 退出瞄准的归位走的是同一个 `Update` 插值，自然平滑，且不管蓄力是正常放完还是被打断（事件一定会发 `Active=false`），相机都会回正——不会卡在偏移姿态。

---

## 四、你在 Editor 端要做的事（3 步）

> 前提：场景里蓄力重击已经能正常进入/退出（准心已经会显隐，说明 `AimStateChangedEvent` 发布正常）。

1. **选中虚拟相机**：Hierarchy 里找到那个 Cinemachine 虚拟相机物体（场景里叫 `CinemachineCamera`）。

2. **挂上 `Aim Framing Controller` 组件**：Add Component → 搜 `Aim Framing Controller` 添加。
   - 因为脚本上写了 `[RequireComponent(typeof(CinemachineCameraOffset))]`，Unity 会**自动**把 `CinemachineCameraOffset` 扩展一并加到这个相机物体上，你**不用手动加**。
   - 顺手确认那个自动加上的 `CinemachineCameraOffset` 的 `Preserve Composition` 是**不勾**（默认就是不勾）。

3. **调参数试手感**（在 `Aim Framing Controller` 组件上）：
   - 进 Play，长按左键蓄力，看角色是否滑到左下。
   - 嫌偏移不够 → 调大 `Aim Offset` 的 X / Y；偏过头 → 调小。
   - 嫌过渡太生硬/太拖 → 调 `Transition Speed`。

就这些。脚本端零改动 gameplay，纯加一个相机表现监听者。

---

## 五、可能的小问题排查

- **没反应**：确认组件挂在**虚拟相机**（CinemachineCamera）上，不是主相机（Main Camera）；确认 `CinemachineCameraOffset` 确实被自动加上了。
- **角色滑反了方向**（比如滑到右上）：把 `Aim Offset` 对应轴改成相反符号即可（X、Y 的正负决定左右/上下）。
- **画面在偏移时"转了一下"而不是平移**：检查 `CinemachineCameraOffset` 的 `Preserve Composition` 是否被误勾上了，勾上会让它重新对准角色、产生旋转感——取消勾选。
- **偏移生效但很突兀**：`Transition Speed` 调小一点（如 5~6）。

---

## 六、如果以后想升级（留个念想）

当前是「轻量取景偏移」——不换相机、不改 FOV，改动最小。要更像原神的话，未来可以：
- 瞄准时**拉近相机 + 收窄 FOV**（更聚焦目标），可以再加个监听者去插值 vcam 的 FOV / 距离。
- 用**专用瞄准虚拟相机**（单独一台 vcam，进瞄准时提高它的 Priority 让 Brain 平滑切过去）。

这些都能继续沿用「订阅 `AimStateChangedEvent`」这条路接进来，不必动玩法代码。
