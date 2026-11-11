# Native-3 Fire Renderer Reason1：只读接线审计

日期：2026-11-01。证据目录：`RPG/docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01/instrumented-2026-11-01/native-3`。本轮只读源码、正式资源和已归档 JSON；未改代码/资产/参数，未启动窗口、Editor、服务或测试。

## 优先结论

**当前 P8 源码和正式 Prefab 确定存在一条会把目标块排除的距离中心接线链：GPU Fire 没有显式兴趣 Transform → Runtime 也没有兴趣 Transform → 回退到材料世界自身的静态 Transform，而非本地 Actor/Camera。** 这不是已确认的“残留 Hub Transform”；序列化引用是空，实际静态回退点是世界原点。

实际 Legacy Chunk 边长为 `0.25m × 8 = 2m`。对目标 `(-26,0,28)`，当中心回退到该世界原点时，其 AABB 到中心的最近距离为：

```text
AABB 相对世界原点：x[-52,-50]，y[0,2]，z[56,58]
最近点：(-50,0,56)
distance² = 50² + 56² = 5636
允许半径 = VisibilityFadeEnd 56 + view padding 8 + extra padding 3 = 67
threshold² = 4489
5636 > 4489，所以在检查 frustum planes 之前就返回 false。
```

**该计算不需要假设绝对 Origin 恰好是零。** ConfigureReplica 保存的 Origin 和未移动世界 Transform 的回退兴趣点相同；AABB 使用 `Origin + Key * span`，距离相减时 Origin 抵消。相机视野、FOV 和已有 padding11 都不能绕过先执行的距离门。

Native-3 没有直接导出当时的实际 InterestPosition/planes，因此不能把 JSON 的合并 Reason1 伪称为已经记录到了“第26行 distance 分支”。这里的结论是：**源码＋序列化资源＋正常静态世界装配推导必拒绝，并与本次两端所有 Reason1、无 Read/Seed/Draw 的观测一致**；不是新的运行时截屏或分支采样。

## 1. 已复核的 Native 事实

重新读取 `a-timing.json`、`b-timing.json`：

| 事件 | A | B |
|---|---:|---:|
| Installed | 34 | 34 |
| RendererRejected | 32 | 32 |
| RendererRejected 的 Reason 集合 | `{1}` | `{1}` |
| RendererRead / SeedChosen / SeedUploaded / DrawSubmitted | 全部0 | 全部0 |

例如 A 的首次 RendererRejected：目标 Domain0/Key(-26,0,28)、Generation1/Revision2、MaterialTick168、PresentationVersion2、Reason1、Count0=0/Count1=256。不是 Seed 预算已占满的 Reason3，也没有证据可以把后面的上传/Draw 时间当成零延迟。

`p8/before-timing/{a,b}-movement.json` 记录：

- A：Actor约 `(-50.99750,0.27264,52.26875)`；Camera `(-50.99750,4.41720,47.59953)`；forward `(0,-0.36812,0.92978)`；FOV70；Aim约 `(-50.99765,0.38658,57.77975)`。
- B：Actor约 `(-49.80000,0.15095,52.27000)`；Camera `(-49.80000,2.41095,47.02000)`；forward `+Z`；FOV70。

这些是 Camera/Actor 位置，并非 Renderer 实际使用的距离 Center。两者不可互换。

## 2. 精确的引用与控制流

路径相对外层 `F:/Develop Project/RPG/`，行号按本次工作区。

| 位置 | 确定事实 |
|---|---|
| `RPG/Assets/_Project/Prefabs/World/Online/P8World.prefab:622488`–`:622495` | 实际启用的 `GpuFireParcelRenderer`，`_worldRuntime` 指向 fileID168096278438772510；`_interestCenter: {fileID:0}`；Profile GUID618f1c45c53b17e49a89101533bed155。 |
| 同 Prefab `:1165369`–`:1165390` | 上述 fileID 正是 `ElementWorldRuntime`，采用在线 CPU 世界 Profile GUID370ae1c7ec9d3fc44b0cbbcfba264f47，`_interestPoint: {fileID:0}`。并非绑着 Hub 玩家后未刷新。 |
| `RPG/Assets/_Project/ScriptableObjects/World/Online/Materials/P8_OnlineCPU_ElementWorldProfile.asset:15`–`:16` | 实际 `_cellSize:0.25`、`_chunkSize:8`，所以 Legacy span=2m。 |
| `RPG/Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs:124`–`:130` | Origin取 `_origin`；Replica CellSize取可信Profile；ChunkSize取Profile；InterestPosition在 `_interestPoint` 为空时取 `transform.position`。 |
| `ElementField/Runtime/ElementWorldRuntime.Replica.cs:17`–`:36` | Replica 装配明确不走 TryInitialize；`:33` 保存 `_origin=transform.position`，`:36` 据此建立 Legacy Geometry；没有本地 Actor/Camera 兴趣点绑定。 |
| `ElementWorldRuntime.cs:434`–`:440` | 普通初始化中的作者兴趣点要求，并说明正式在线导出会清除此引用；它服务于单机/Authority 差别。不能把 Replica 跳过此单机初始化误解成自动绑定了本地玩家。 |
| `Rendering/ElementField/GpuFireParcelRenderer.cs:34`、`:85` | 私有 `_interestCenter` 为空时读取 `_worldRuntime.InterestPosition`，所以本场景继续回退到世界 Transform。 |
| 同文件 `:383`–`:404` | Camera用于 `_view.Update` 的planes，但 Center 参数用上述 InterestPosition；Chunk bounds用 `origin+new Vector3(key.X+.5,...)*span`；原 `Intersects(bounds,3)` 拒绝就记录 Reason1 并continue。 |
| `Rendering/ElementField/MaterialPresentationView.cs:14`–`:19` | Camera与Center是两个独立输入：Camera提供planes，Center原样保存为距离参考。 |
| 同文件 `:23`–`:32` | 有效Camera时先合并padding，`:25` 求 bounds.ClosestPoint(Center)，`:26` 先执行半径拒绝；只有通过后才在`:28`–`:32`检查6个planes。 |
| `GpuFireParcelRenderer.cs:299`–`:303` | Shader `_VisibilityCenter` 同样来自 InterestPosition。即使仅绕过CPU frustum，也没有修正这个独立的距离参考；本轮没有作这种绕过。 |
| `Multiplayer/Movement/OnlineWorldBootstrap.cs:143`–`:153` | 正式内容要求 Bootstrap单位变换，按原Prefab局部变换实例化，再在激活前配置材料Replica，未把世界根移动到本地Actor。 |

对 `Scripts/Character`、`Scripts/Multiplayer`、`Scripts/ElementField` 的兴趣引用检索，没有找到在线流程给 `_interestPoint` 赋本地 Actor、或给 GPU `_interestCenter` 动态赋值的接线。既有 `LocalActorPresentation.Bind` 与 `LocalCameraPresentationTarget`（`OnlineWorldBootstrap.cs:376`–`:382`）不能仅凭名字视为已经把 Fire Renderer 绑定；当前 Renderer 也没有相应订阅。

## 3. 实际 Legacy Origin 的资产推导与限制

Prefab 的 Runtime 位于 `ElementWorldRoot`：

- 自身 Transform fileID5433685333057464871，始于 `P8World.prefab:1165342`，局部位置 `(1.7099996,-11.635663,5.9077883)`，带一组旋转。
- 父 Transform fileID7569906790968652757，始于 `:1550023`，位置 `(-3.91,12.45,1.71)`，带相抵的旋转。
- Prefab 根 Transform fileID6030073753864585762，始于 `:1858009`，位置零、单位旋转/缩放。

按序列化四元数和父子 TRS 离线组合，Runtime静态位置约为 `(1.3e-6,-2.0e-6,-1.0e-7)`，即浮点误差量级的世界零点。这是资产数值推导，未冒充 Native 直接读取；Unity 内部四元数规范化可能带来末位差别。结论并不依赖这些末位，因为本问题的距离中相同 Origin 会抵消，且 75.07m 与 67m 有约8.07m裕量。

正常静态世界应保持其初始化后 Transform；本次未见运行中 InterestPosition、世界Transform、六个planes原始值。若存在未在当前接线中识别到的外部Transform变更或非对应构建，仍需原始值核对；不能用离线推导替代这些未采样量。

## 4. 归因边界

- **可确定的实现问题**：在线正式世界清除单机兴趣引用后，Replica的Fire显示距离中心没有接回本地表现目标，静态回退世界原点；当前目标Key在这个距离门外。它位于材料已经完整安装之后、第一次Renderer读Cell之前。
- **本次支持的观察**：34份版本已安装，32份实际进入观测的版本均在Intersects处Reason1拒绝；没有同目标Read/Seed/Draw。因此当前Native-3不能给出安装→Draw分位或宣称Fire已经可见。
- **不应下的结论**：不能说用户看错；不能把Reason1简称为“镜头没对准”；不能因概率、seed容量、网络或内存此前有候选就仍把它们并列成这32次拒绝的已证原因。当前Reason1记录点位于这些后续seed门之前。
- 本轮没有改筛选、概率、调度、相机、Interest中心、原生端运行参数或诊断Runtime。若后续需要运行时把合并Reason1细分，仅缺的关键值是当前Origin/span、实际InterestPosition、view.Center、closest distance²/阈值²，以及planes拒绝序号；这属于后续观测/修复范围，本报告未实现。

## 5. 最小修复方向的合同核查（仅方向，未实施）

已核对 `RPG/Assets/_Project/Scripts/Core/Online/LocalActorCameraPresentation.cs`：现有 `LocalActorCameraBoundEvent` 携带 Actor与Camera；`Bind`发布绑定，`Clear(owner)`核对Owner后发布 **default同一Bound事件** 表示解绑。**没有独立名为 LocalActorCameraUnboundEvent 的事件，不能误造为已存在。** 静态 Actor/Camera快照可供订阅晚到的组件补取当前状态。ElementField.asmdef 已引用 Game.Core，无需增加对Multiplayer/Character/Rendering的反向依赖。

因此，“仅Replica Runtime监听现有Core绑定/解绑语义，将表现兴趣Transform跟随本地Actor”可以沿现有合同实现；Origin仍保留ConfigureReplica时的可信网格原点，Authority/Offline门与兴趣行为不改，也不改视锥、距离、概率或调度。后续应验证先绑定/后启用、default解绑、换Owner、Scene卸载及重连生命周期，使用Unity假null检查和对称订阅/退订。这个方向在合同与依赖层面已核查可行，**没有写实现、没有跑修复测试，也没有获得修复后的Native可见证据**。
