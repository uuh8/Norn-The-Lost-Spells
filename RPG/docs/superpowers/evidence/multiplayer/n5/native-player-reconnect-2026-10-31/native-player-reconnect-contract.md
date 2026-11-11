# N5 正式玩家与同进程重连：只读合同审计

只读审计，2026-10-31；源码相对 `RPG/Assets/_Project/Scripts/`。

## 关键缺口与最小交付

先P7双Native：登录/新鲜Profile→Hub真实移动/F→匹配/双方Ready/队长Start→真实移动/施法扣次→A断传输保PID→原UI新Ticket重连→同世界/Actor新Epoch→同卡次数不补→再移动/施法→返回Hub；B继续。P8独立复验，不扩通关/死亡/结算。

v4 Native仍缺断线前后同卡实例消耗对照。

## 正式路径与输入

- `Multiplayer/Accounts/OnlineAccountBootstrap.cs:13–17` 建DDOL Session/房间Client；`OnlineAccountClient.cs:92–110,149–154` 登录/Profile，EnterHub要求本次Profile成功。
- `Character/Interaction/PortalInteractInput.cs:18–38` 真实F→最近门事件；`Run/Runtime/DungeonEntryChoiceGate.cs:35–58` 验Actor/距离资格后才发布匹配。
- `Multiplayer/Rooms/OnlineRoomClient.cs:82–193` 正式HTTP match→Profile/ready→start→Running→新Ticket，核验Room/Player/Run/Epoch/Dungeon再认证；`279–309` 保留Hub、加载OnlineRoom，返回先停在线World。
- `UI/Online/AccountMenuController.cs:32–40`：OnlineLobbyRequestEvent/AccountMenuCommandEvent；`DungeonEntryDialogController`：DungeonEntryChoiceSubmittedEvent/OnlineRoomRequestEvent。批准的UI事件驱动须标注，不能称鼠标点击。DDOL Dialog保留断线Reconnect菜单。
- `Character/Controllers/PlayerControllerBase.cs:66–88` 采真实InputSystem_Actions，`OnlineWorldBootstrap.cs:485–492` 发Prediction输入；Wizard输入进入CastDriver。禁止位移/直接Cast/扣费代替输入。

## 同PID重连与资源事实

`OnlineRoomClient.cs:195–223`：断线→Reconnect→presence确认原Running房间→Stop旧Manager/下一帧新Ticket与Manager。Scene/Session/PID保留，与返回Hub再入不同；429须等待5秒。

DS `OnlineWorldBootstrap.cs:425–440` 更新席位Epoch/ConnectionId、清输入；`NetworkActorRegistry.cs:55–58` 保Player。`OnlineCombatWorld.cs:87–98` 重绑同Cast；`Character/Spells/OnlineCastDriver.cs:197–202,224–231` 同Run/Player/Actor复用_workspace，只清pending/连接去重，不重新发放入局资源。

Client `OnlineWorldBootstrap.cs:262–313,331,355–371` 保World/Registry/Cast并换通道，身份失配拒绝；`OnlineCombatWorld.cs:149–170` 新Epoch发送当前真实库存。材料Store跟World保留，新可信生命周期恢复当前Baseline。

优先记录同Wand/Card InstanceId、ProgramRevision、次数N→N−k，新Epoch真实库存不得回N，再施法继续扣。Mana/CD正常推进（`Run/Spells/OnlineOwnedWorkspace.cs:141–147`），不要求前后相等。用DS `OnlineCastDriver.TryCaptureInventory` 和Client真实Source，不能用EntrySnapshot冒充当前资源。

回归入口：`Tests/Character/OnlineCastDriverTests.cs:95.ReinitializingSameRunAndNewEpoch_DoesNotRefillOwnedWorkspace`（29次/Mana保留）；`Tests/RunPlayMode/DungeonEntryFlowPlayModeTests.cs:77–78,150–215` 分P7/P8保对象并重连后输入/施法。本轮未运行。

## 可复用Native工具与限制

完整构建：`RPG/Assets/_Project/Editor/Networking/OnlineAccountTools.cs:218–226.BuildMenu` → `Builds/OnlineAccounts/Menu/NornMenuClient.exe`；含主菜单/enabled场景/OnlineRoom。RoomLifecycle构建是Lab。需匹配v4/Manifest/DS；ServiceProfile默认5080，未见菜单临时URL参数，先核实际资产/监听。

`Run/Diagnostics/PortalRoomAcceptanceProbe` 的 `--norn-portal-acceptance` 注入MemoryFiles隔离本地档；position-portal只算定位，不算真实行走。`UI/Online/OnlineInputAcceptanceProbe` 只读矩形/Raycast，可辅助OS点击。`Multiplayer/Diagnostics/OnlineMovementAcceptanceProbe.cs:94–102` 有disconnect保PID及输入/资源读数，但缺卡InstanceId，需补实例级只读证据；诊断IO不计性能。

材料采集/倾倒尚有缺口：MaterialCollectionController/ContainerUseController绑定单机Session；OnlineInventoryCommandKind仅SelectWand/PlaceCard，Multiplayer未见对应材料事件消费者。可先验正式施法生成材料；不能用单机Port/诊断直写冒充在线容器动作。

## 历史证据与本段记录

`n4/player-7h`：两图GUI/Hub/F/Ready/双Native，P7 Tab/返回后B移动跳Dash，P8 Boss栏/Burning；后期/击杀未验收。`n4/final-audit` 最新业务PlayMode仅重跑P7。`n5/bootstrap-native-verification` 两图v3 Lab新票重入，明确同PID/消耗不补满未测；最新 `n5/native-recovery-gates` 是P7 v4故障恢复，KeyboardPath=false，P8/本段仍未测。

每图存PID/启动时间/Exe、DS/Run/Dungeon/Actor/World、旧新Epoch、新票状态、真实输入/DS位移、同卡消耗、新Epoch库存、实际Store/ACK、B继续/Hub恢复；不存秘密。保留128 Key/资源错误/P3及后续Gate。未运行Editor/测试/构建/服务/生成/Git写；无权限阻塞，Native待主控。
