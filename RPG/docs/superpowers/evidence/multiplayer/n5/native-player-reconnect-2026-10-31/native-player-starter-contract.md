# Native 玩家起始材料合同（只读）

2026-10-31；仅核源码/序列化资产，未运行 Unity、服务或测试，未读凭据/旧 session。下文 `A=RPG/Assets/_Project/`，`C=A+Scripts/`。

## 注册与有限次数

`Server/Norn.Business/Infrastructure/OnlineOptions.cs:15–16` 默认仅 `wandtest.wand.a`＋`wandtest.fire`，火球 **30 次**。`Features/Accounts/AccountService.cs:33–50`（同一业务项目）注册事务创建真实一杖一卡、卡装入杖 slot0并选中，`HasUnlimitedCharges=FALSE`；不是无限 StarterKit。`Content/ContentManifestValidator.cs:20–24` 强制一杖一卡/正有限次数。

`A/Resources/Online/ContentManifest.json:283,298` 包含这两个 ID；Manifest 另有水/冰等是内容白名单，不表示新账户持有。未检查运行时私有配置覆盖；新注册 Profile/入局快照仍须实测确认为30/false。

## GUID 链（每个资产 GUID 均见其 `.meta:2`）

- `A/ScriptableObjects/Spell/Wands/Items/Item_Wand_StarterA.asset:15,33`：ID `wandtest.wand.a`；资产 `6b4530f821875ce4f9bcc1d29035696a` → Stats `2cba41f6fb0bd254e8cbfdc2dd39ba32`，即 `Wands/Stats/Wand_StarterA.asset:15–19`：Mana100、回10/s、冷却0.8s、BaseDraws1。
- `A/ScriptableObjects/Spell/Items/Item_Spell_Emit_Fireball.asset:15,23`：ID `wandtest.fire`；资产 `e4c700487141cda448f325b3c0dfd525` → Definition `dcbeb40589989c1498003c0a39101195`。
- `A/ScriptableObjects/Spell/Definitions/Spell_Emit_Fireball.asset:20–21`：Mana20 → Projectile `ff24eb7da93035a4bb0c145ce29849fa`，即 `A/Art/Prefabs/Wizard/Magic/Fireball.prefab`。
- 该 Prefab `:14669–14678` 的启用组件 `ElementDepositOnImpact`，脚本 GUID `aeb1cc9b05ea81b4994427978e1e3f50`（`C/ElementField/Runtime/ElementDepositOnImpact.cs.meta:2`）；参数 **Fire=2，总写入预算400，半径0.4m，linear falloff=true，初速度0**。ID2见 `C/Materials/MaterialId.cs:11`。

因此默认在线可用的材料法术是原有 Fireball，无需临时赠卡、改配方或换水/冰球。

## 接触、权威与可验证预期

`C/Combat/Projectiles/ProjectileBase.cs:442–505`：真实 Collision→排除 Replica/已消费、同队穿过与反弹→ResolveImpact→SurfaceImpacted。环境地面/墙面也触发，不要求敌人受伤；朝天空超时不触发。Prefab `:14581` Collider 非 Trigger，`:14655` 寿命5s。

`C/ElementField/Runtime/ElementDepositOnImpact.cs:51,180–207` 订阅该事件，校验命中位置/方向/法线后经当前 ActiveSink 入队；没有 Sink 或写入被拒绝不保证产生材料。`C/ElementField/Runtime/ElementWorldRuntime.cs:224–234` 经正式路由；Replica 不获写权。

`A/ScriptableObjects/World/Online/Materials/P7_OnlineCPU_MaterialSimulationRoutingProfile.asset:16–17` 与对应 `P8_...:16–17` 均路由 Fire→ElementCell(1)。两图 `*_OnlineCPU_ElementWorldProfile.asset:27` 火衰减2/tick。`C/Multiplayer/Movement/OnlineWorldBootstrap.cs:518–522` DS真实材料步后捕获复制。因此已有配置支持两图施放火球产生材料；本审计不称 Native 已通过。

主控应实测：已有30次卡成功施放后29、Mana减少20；命中近处实物后DS Fire/Legacy格发生变化，并经原安装/ACK链在两端收敛。400是本次请求总预算，Fire是会衰减/反应的 Legacy 强度，不能断言稳定400 GMU。`C/Run/Spells/OnlineOwnedWorkspaceFactory.cs:64–65` 原样导入冻结次数/无限标记；同PID重连后不补满及长期可靠结算须分别实测，不能由资产静态核对推定。
