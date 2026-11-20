# 显式 Native 库存与合作输入回归

## Rapid 长按低蓝后的 Busy 回归

`--scenario rapid-long-busy --dungeon p7 --input-mode keyboard --timeout-seconds 280` 是独立诊断场景，复用当前 Development Client/DS 与既有 Probe。默认并要求280秒总预算、Admission仍300秒；不新增私有配置字段，不改变 Runtime、Mana、次数、Reservation 或库存门。只有完整P7/keyboard可用，不能与Home、direct、core-only混用。原场景和断言继续保留。

新账号仍经过真实Register/Login/Profile、商店和账号库存HTTP。购买Rapid、四张Damage和一张12次Fire；为避免低蓝测试先耗尽卡片，通过正式Account move把注册原starter的30次Fire搬到Rapid.slot4，再把购买的Fire装回starter.slot0。四张Damage在0–3，入局资格照常各付一次。正式P7中使用原UI回调把同一张Fire4→5，校验新MoveOwned收据、Source/Target ProgramRevision1、Client安装Revision2及新鲜DS Revision2，保留空槽4；这不是修改DTO或恢复旧checkpoint。

随后执行两轮真实Input System Mouse/Tab顺序：先松Mouse再Tab，及持Mouse打开Tab后才松Mouse。每轮从自然恢复的Mana120开始，持续按住左键至少50秒，最多等待65秒，必须观察本轮至少3个来自DS `FactReceived` 的 `Rejected/EmptyOutput`，这些Fact首次被工具观察时同份Owner报告的Mana须低于当前Fire原20 Mana成本，且Fire次数始终大于0；同轮也必须至少有一次真实Released。报告保存低蓝样本、对应CommandId、Tick和资源版本，不声称报告采样与DS释放发生在同一瞬间。只有长按时间、低蓝、普通Rejected或卡片耗尽都不能满足门槛；当前低蓝样本也不是旧现场唯一病因的证明。真正耗尽另测。

松键必须先观察设备已processed up；随后持续观察至少8秒，DriverCastCount不得再增长，迟到的原Command终结Fact允许到达。两轮分别通过原UI把空瓶Tool0→Bag(4,4)、Bag(4,4)→Bag(6,6)，都严格要求 `MoveOwned Kind12`、本人Caller/新Command/Epoch/版本、成功收据及同实例准确落点。失败仍保存拒绝与未变资产，不能用收到新Owner作为拒绝的前置条件。这里不使用已豁免的普通F/附近拾取作为Busy恢复判据，也不声称UI回调覆盖Pointer Raycast。

每轮等待一次 `ProbeSampleTick >= MoveOwned后Client.ServerTick` 的新DS诊断，四个Owner门及Driver Timeline/Queued都必须空闲，QueueRemaining只记录。报告保存Client held/focus/Source/意图/收据、原Fire余量与DS门。Probe的64项意图环由工具按CommandId逐次累计、严格匹配本轮Epoch/原Driver/Revision2和DS Fact；终结时累计条数必须等于Driver计数增量，不允许绕环丢失后还算通过。每个Released必须恰扣一次Fire，EmptyOutput和其他Rejected不能多扣，Modifier资格也不得重复收费。

初始HTTP/入场约40–70秒；每轮至少包含50秒持键、8秒静默，并保留最多25秒冷DS采样；另留正常退出时间，实际硬上限为280秒。超时是失败，不重新开预算。若第一轮明确Busy，保存现场后不继续第二轮污染持有者，仍通过原退出意图验证receipt/Profile/持久化；退出成功不能把Busy改成Passed。其他CaseFailure也尝试在原预算内补一份新DS门证据再正常退出。已有未知退出Command时只等原结果，最终停止自有进程只是清理，不代替可靠退出。

此场景覆盖真实Mouse低蓝零输出→停手→严格库存移动恢复，不等于耗尽、区域2行走、历史Draw/Ticket精确重演、硬件输入或主观手感验收。是否通过只看本轮 `evidence.json` 和保存的收据/计费证据；源码存在不代表已经运行。

## 普通掉落移动后拾取

新参数 `--scenario ordinary-pickup-movement --dungeon p7 --input-mode keyboard --timeout-seconds 280` 仅允许完整 P7/keyboard；默认并要求总期限280秒。前置仍为私有 Test Schema 与包含本轮 Probe 的 Development Client/DS。账号只走正式 Register/Login/Profile 和 single-entry，保持注册 starterWand/Fire/背包与 P7 正式入局奖励，不购买 Rapid、额外法杖或瓶子，不调用 cast，不直接写数据库资产。

流程为原 UI helper 丢下30次 Fire → 实际 WASD 向 z=0 离开出生点超过6米 → 返回同一落地物3米内 → 原 F 的 Kind15 PickupItem 拾回 → 原 UI helper 重丢 → 真实 Mouse 从 Nearby 拖入 Backpack → 原 Normal Exit。两次丢下复用既有 UI 回调 helper，不能计入真实指针覆盖。F 核对原 WorldItemPickup caller、精确 ItemId/WorldOwner、成功收据和 Owner Snapshot 水位；Mouse 核对 Kind15、AutoPlace=false、目标背包、精确 ItemId/WorldOwner、成功收据与更新后的背包位置。两次拾回后均要求初始实例 ID 集合和数量不变、Fire 次数保持30、DriverCastCount/ProbeCastSequence 为0且没有 PlayerCastObserved。正常退出再核业务 receipt、持久 Profile、Exited 与 lease 清零，保留原退出 helper 的完整断言。

移动是显式配置 `AllowSyntheticMovement=true` 与 `AllowSyntheticKeyboard=true` 后发送 `synthetic-move`，单个 W/A/S/D、DurationSeconds 在0.05–1秒。Probe 每帧自动排真实 KeyUp，Harness 等 `SyntheticMoveCompletedSequence` 追上当前脉冲且设备已松开才发下一次。每个脉冲依据新 Client Position 重新计算，按5m/s保守估计乘0.8并夹在0.05–1秒；每方向段最多12次，连续3次进展不足0.03米即失败，不盲加预算。证据要求原 MoveActionValue 与 ProcessedMove 的对应方向大于0.5、HasFocus=true、InputSuppressed=false、OnlineInputBlocked=false、InputSequence 前进，并保留整个只读移动观察。设备收到按键本身不能算角色移动成功。

路线依据当前 P7 首席出生约(-4.95,0,-8.02)、ViewYaw=0 时 W=+Z，取中央 x≈-4.95、z=0 折返点。静态地板大致为 x[-11.43,11.91]、z[-16.56,5]；Harness 进一步限定 x[-6.4,-3.5]、z[-10,1]、y[-0.5,1.5] 且 yaw 偏离不超过3度。这里只是当前固定地图的候选安全走廊，静态资源不能证明运行时可走；遇障、掉落、输入门关闭或未知几何均真实失败。返回目标来自首次已安装 Ground proxy 的 NearbyFocusedPosition 与 WorldOwner，不能猜 Drop offset 或直接写 Transform。第二次丢下重新捕获 WorldOwner 与位置。

只在远端与返回终点分别等待一次最多25秒的 DS 新 `RecordedAtUtc/MovementTick`；脉冲暂停后对照 `ActorPositions[ActorId-1]` 与当前 Client Position，要求误差≤0.5米。远端双方都须距出生点>6米；返程双方都须距原地面物≤3米。其余阶段保存的 DS 报告是上下文，未声称每条1Hz Client 报告都有同 Tick Authority 证据。

最终拖包通过 `synthetic-pointer-at` 的实际屏幕坐标和 leftButton 状态，不调用 ui-drag/OnDrop/UiMove 代替它。使用独立 `--norn-portal-acceptance` 私有配置 `{ObserveMovementOnly:true,MovementSamples:0,Output,Command}`：旧 Portal 行为关闭，UI 只读探针以 `Command + ".ui"` 的文件存在触发并输出 `Output + ".ui.json"`。源坐标取实际 Nodes 中相同 ItemId/WorldItem 的 InventoryItem；目标取库存 Probe 的只读 `Ui.BackpackGridTarget`（实际背包格2,2的 Point/Path/OwnerId），不把装备包图标当格子。先 hover 核首 Raycast，再按下、跨24像素 Drag Threshold、移动至目标、松开；保存每步实际 UiPoint、MousePosition、首 RaycastTargets 与 PointerStates。目标无可见坐标、首命中被遮挡、未进入真实 dragging、收据失败或没有原 Owner 安装均失败，不 fallback 直接命令。

280秒包含入场、玩法与本场景失败退出尝试；失败不另开20秒玩法窗口。停止本工具自有进程只算清理，不能代替成功结算。公开证据保留每个脉冲、两次新鲜 DS 终点、UI Raycast/指针、原实例与最终退出；一次性 Ticket/配置/命令仍只在本轮私有目录并按原方式逐文件清理。

此场景只覆盖移动后自丢自捡的普通 Spell 分支；不覆盖 NPC/Enemy 掉落生成、预置 WorldItemSpawnPoint、新场景来源、区域2、Ice256、双成员移动、OS输入、渲染外观或主观手感。它通过不能单独关闭用户“预置/敌人掉落失败”的问题；新来源另由正式 UI PlayMode 验证，完整区域遭遇仍须单独验收。

这是供 Agent 集中运行的独立 Console，不加入 `Norn.Online.sln` 或默认测试发现。2026-11-16 本轮已构建并运行 `ordinary-pickup-movement`，15 项检查通过；出生点往返约 8m 后，两端点 Client/DS 位置差均为 0，原 F、真实 Mouse 拖包、同 ID/30 次守恒及正常退出通过。证据在 `docs/superpowers/evidence/ordinary-pickup-20261116/`，另有双 Client 54 项、Home 16 项通过。此结果不代表区域 2 或 NPC 新来源已完成 Native 体验验收；后续修改须重新运行，不能把本段当永久通过证明。

前置条件是已启动的私有 MySQL `127.0.0.1:13384/norn_online_test`、包含本次 Probe 与最新 Manifest 的 Development Client/DS，以及本机私有 `NORN_N1_CONFIG`。工具只读取该文件的 `Test/TestMigration`，两条连接均须通过 test Schema 隔离门；从未输出连接串、账号密码、Token 或 Ticket。`NORN_CONTENT_MANIFEST` 可指定刚导出的正式清单，缺省取仓库当前 `Resources/Online/ContentManifest.json`；Gameplay Hash 始终读取仓库当前 `GameplayManifest.json`，因此构建后不能继续改变 Runtime 源码或清单。

主 Agent 从既有 SDK 包装器调用：

```powershell
& 'F:/Develop Project/RPG/RPG/Tools/Networking/Invoke-OnlineDotNet.ps1' run --project 'F:/Develop Project/RPG/RPG/Tools/Networking/NativeInventoryRegression/NativeInventoryRegression.csproj' -- --server-exe '<本次Development DS绝对Exe路径>' --client-exe '<本次Development Client绝对Exe路径>' --output '<新的或空的证据目录>'
```

可选参数是 `--repo '<外层Git根>'`、`--timeout-seconds 240`（范围60–280）、`--core-only`、`--dungeon home`（缺省 `p7`）和 `--input-mode keyboard|direct`（缺省 `keyboard`）。Core 模式保留真实入场、30→29→28、原 UI 法术卡往返与正常退出；p7 默认还验证法杖进包/重装备，以及容器在包、道具栏、附近地面之间往返和连续投掷。默认选择通过虚拟 Keyboard 的数字1–0，投掷通过真实 G 按住/松开，地面拾回通过真实 F；它们进入原 InputAction 和 Gameplay 路径，继续核对 Caller、成功收据及完整 Owner Snapshot 安装。投掷使用实际共享 Camera 的中心射线。显式 `direct` 保留旧方法调用 fallback，报告必须写明不覆盖键盘。原 scenarios 不覆盖任意 UI 指针 Raycast；新增移动场景的最后一步单独检查真实 Raycast/Mouse 拖包。所有模式均不覆盖 OS/硬件输入和战斗主观手感。

Keyboard 模式只给本次私有 Probe 配置写 `AllowSyntheticKeyboard=true`。Probe 仍须专用命令行激活且只编译进 Development/Editor Client；`synthetic-key` 命令仅允许1–0/F/G，`Pressed` 表示按下或松开。Probe 创建自己的虚拟 Keyboard 并调用 `QueueStateEvent`，不调用 `InputSystem.Update`、OS `SendInput` 或 Driver 请求方法；正常 PlayerLoop 消费事件，正式菜单、焦点、Frozen 和 Owner 门保持原样。Harness 先等报告中的实际 deviceId/已处理按键状态，再发送 release，随后等原收据与 Owner/世界断言；`QueuedSyntheticKey` 本身不是验证通过。持键跨1Hz诊断周期保留，失败也尽力松开；Probe 停用或身份/Driver 切换只移除它自己的设备，不重置现有键盘。报告的 `SyntheticKeyboardProcessed` 检查保存实际 down/up；没有执行的 Home/Core 路径不能仅凭 `InputMode=keyboard` 声称覆盖。

本地选择意图是CLR `int` JsonValue，必须严格按`int`读取Kind/TargetSlot；`TryGetValue<ulong>`不会隐式转换，退0可能误走direct并跳过选择断言。每次实际Keyboard选择必须另有`OriginalKeyboardSelection`记录，且已核原Driver Caller、匹配成功收据、更新的Owner和真实HUD单选Frame；`InputMode=keyboard`标签或只有F/G的处理记录均不能证明数字键通过。条件分支未执行的数字键不计入覆盖。

完整 P7 用例的注册账号原本只有法杖、法术卡与保底背包，没有容器。工具经正式商店购买第二把法杖和三只瓶，沿原账号 Move 装备到法杖槽1与工具槽1–3，再经商店给三瓶各买17 GMU Water，共51 GMU。账号阶段明确断言“两杖三只有料瓶”；随后 P7 原入局奖励在空工具槽0提供第四只空瓶，Native 首份库存再断言“两杖四瓶，3 filled + 1 empty”，并确认三只购买实例的ID与数量未变。工具不改注册、入局发奖规则或资产表。

默认模式的四瓶连续通过真实数字键选择及 G 按住/松开，第四次先将第一根杖放入背包，验证法杖槽0为空时仍能回退到槽1。此前的 UI drop 后，必须通过真实 F 从附近列表拾回原瓶，核对原 WorldItemPickup caller、成功收据、版本、包内归属和容器数量不变；缺少附近输入或拾到其它物品会失败，不自动改用直接请求。完整 P7 keyboard 模式还将已消耗到28次的同一张 Spell 经原 UI DropItem 放到地面，再用 F 的 PickupItem 分支拾回；确认原实例、Caller、收据、Owner、本人 SpellStorage 或原包归属以及次数不变，最后装回原杖。Spell 与容器各自保留 Kind15/Kind8 断言，不能以容器拾取通过代替 Spell。Owner移除只证明离手；工具还只读测试库已确认的 Checkpoint Head 与退役身份索引，等待四瓶不再在途、没有 ContainerTransit 材料损耗，并确认 Client 安装 impact 后库存版本，最后验证退出 Profile 中四瓶均未复活。混合空瓶与有料瓶分别覆盖空瓶破碎、真实材料 Deposit，两者不能互相代替。

HUD 验证另读实际 `HeldItemHud` 的 Cell 缓存与 Image：每次选择须只有对应槽的真实 Frame 使用作者选中 Sprite，每次 Throw 须原槽 `ItemId` 清空、`Icon.sprite` 为空且 `Icon.enabled=false`，同时实际法杖选框正确。采样还要求唯一 HUD、当前 Source 与已渲染 Source 一致、HUD Canvas 可见；只读反射不调用 Refresh/Bind，不以 DTO 正确代替 UI 正确。报告保留每格图标、背景 Selection 和 Frame 状态；原 HUD 的选中表现由 Frame Sprite 驱动。

Home 模式使用同一真实注册/Login/Profile 和 `single-entry home`，不会执行不存在入局瓶奖励的容器测试。首轮验证真实30→29→28及Tab实际角标，再正常退出；随后保持同一Bearer/Session，另起真实Run/Ticket/Native Client，验证同scope、同卡ID仍28并再次正常退出。两轮日志分别保留。Mana/CD有真实值副本，但稀疏心跳与等待期间会自然恢复，本工具明确不据此宣称Mana/CD连续性通过。

Home的研究台Kind16/17仍保留真实空间权限控制。作者首席出生x=0，研究Box中心x=6、尺寸4，出生点在区域外；真实Kind16取得NotOwned仅作为研究区域控制例。普通紧凑Tab编辑与研究会话分开：若研究窗已打开先关闭，然后实际执行原UI storage↔program往返，必须记录`CardMovementCoverage.Covered=true`及成功收据，不再在出生点跳过移卡。工具不移动角色、不放宽研究区域，也不把NotOwned当成移卡成功。

`existing`的P7/Home/Core路径在退出前还通过`ui-organize`调用原共享Controller的整理按钮，检查Kind18、本人真实包ID、匹配成功收据与已安装Owner版本；空包或已整理包允许合法no-op，不强迫伪造一次库存变化。它不直接写布局，也不宣称覆盖整理按钮的Mouse Raycast。协议版本17需配套Client/DS/业务服及ContentManifest。2026-11-16 22:25本轮最新四组Native结果为合作54、普通移动拾取15、P7 existing80、Home existing20，合计169项，Home含两次真实入局；证据在`docs/superpowers/evidence/home-inventory-20261116/`。首F与Delta积压优化另有规则/分页安装回归，本工具未量化液体采集到屏幕变化的端到端时延。

执行链为新注册 → 登录/Profile → `single-entry p7` → DS 真实 Ready → 一次性 Ticket → Native Lab Client → 正式 p7。Host 是 `WebApplicationFactory<Program>.UseKestrel(0)` 的真实空闲 loopback 监听，不借正式5080；移除 Supervisor 以防扫描历史 test Run，使用 `TimeProvider.System` 保留真实租约与心跳。DS 由原 `RoomLauncher` 启动，没有构造第二成员或伪造 Ready。

每次施法都检查 Client 实际安装的同一卡片次数、DS 当前工作区与 `InventorySentPages` 增长；Tab 检查同时保存 `ExpectedCaption` 和真实 `InventoryItemView._spellCount.text`、缓存次数与可见状态。每次拖放先调用原 `OpenBackpack`，保留 Probe 的 `IsOpen` Gate，收到匹配的新成功收据并安装更高的 Owner SnapshotSequence 后才检查位置；选择/投掷前关闭原 UI。地面容器先通过真实附近拾取回包，再绑定道具栏；没有放宽原绑定规则。

退出发布原 ESC 的有限意图，等待实时 `Committed` 后查询业务最终 receipt，并核验数据库成员 `Exited`、租约0、持久版本和 RequestHash；默认 existing 场景最后确认 Profile 的卡片剩28次，新普通移动场景要求原30次不变。失败会保存最后的 Client/DS 状态、Host 错误类别和 Native 日志，并尝试原退出；不会注入 Snapshot、直接设置玩家位置、奖励物品、直接改资产或强行解锁。

Ticket、命令和 Probe 配置只放本轮 `RPG/.tools/native-inventory-regression/<case-id>/`，结束删除一次性控制文件。只停止本工具持有句柄的 Client、当前新账号关联且 PID/启动时间/实际 Exe 三项匹配的 DS，以及本工具创建的 Host。测试账号与失败 Run 留在隔离数据库供审计，不清理历史账号或改动迁移。

## 双 Native Client 的 Rapid Mouse 回归

`--scenario rapid-mouse-coop` 是独立的合作分支，仅允许完整 `p7` 与 `--input-mode keyboard`，不能与 `--core-only` 或 Home/direct 混用。它默认并要求 `--timeout-seconds 280`；两份 Admission 配置仍为300秒。原 `existing`、`rapid-inventory`、`rapid-mouse-inventory` 分支和原断言保持，合作分支直接复用最后一个分支的真实 Mouse/Tab、F/拖包、Busy 与 Fire 守恒检查。

主 Agent 在现有 Native 全部退出、允许复制工具 DLL 的窗口执行，例如在上述命令末尾追加：

```text
--scenario rapid-mouse-coop --dungeon p7 --input-mode keyboard --timeout-seconds 280
```

两个账号都通过正式 Register/Login/Profile HTTP。Actor1 沿原商店与账号库存 API 获得 Rapid、slots0–3的四张 Damage、空slot4和slot5的Fire；Actor2保留注册 starterWand与原Fire，不购买、不注入输入。Actor1调用 `POST /api/online/rooms` 创建私房，Actor2调用 `/{room}/members` 加入，确认Seat0/1和恰好两成员；各自用本人Profile.Version与串行更新后的ReadyRevision提交Ready，再由Leader调用Start。私房沿正式RunEntry/RoomLauncher冻结两份资产，避免 `/match` 加入历史test数据库中的Open匹配房；不声明覆盖匹配UI或随机配队。

等同一DS实际Ready后，先给Actor2签Ticket并立即启动其Native Client，再给Actor1签Ticket并立即启动。Ticket有效期60秒，不能先签两份再串行等待55秒启动；ActorId由业务Seat决定，与启动次序无关。两个Client使用同一当前Development Client构建、独立Process句柄与私有ticket/config/command/report路径、各自Bearer；仍只有一个DS，MaximumInstances保持1。Actor2配置两个Synthetic开关为false，工具只允许向它发送report/exit；不改焦点、不调用施法方法、不发送虚拟输入。Actor1原Focused/CanAdvance/菜单门均保留。

入场必须观察到两个实际认证Client、Actor1/2、同一个非零WorldEpoch，以及DS的Members=2、Clients=2、两个Authority Owner；仅业务Room有两行成员不算通过。Actor2报告必须保持DriverCastCount=0、ProbeCastSequence=0、没有自建虚拟设备、原Fire次数和全部已安装实例内容不变。每轮Actor1原Mouse顺序的Fire守恒检查完成后，复用该轮已经取得的新DS样本核对Actor2的Epoch、空闲四门与本人实例；Peer的Client报告需追上主报告的ServerTick。只在开局额外等一次DS冷采样，不为每次Move/F/拖放另加15秒等待。Actor1仍执行“松Mouse后开Tab”和“开Tab后松Mouse”两种顺序，保留真实Driver命令/Fact身份和每个Released恰扣一次Fire的断言。

正常退出保持Actor1原 `ExitAndVerify`，随后Actor2通过原有限退出事件请求DS结算。第二成员还在时，Actor1退出不能冻结Actor2的世界；Actor2也必须收到匹配的Committed、通过本人Bearer查到退出receipt与Profile，再只读验证其成员Exited、本人lease为0、持久版本和RequestHash一致。两个收据分别核对Actor、Player、Run、Instance、RoomEpoch、ConnectionEpoch、SettlementId与PayloadHash；旁观者原卡次数与原实例保留。没有通过第二份收据/持久化核验，整例不能Passed。两个人退出成功不等于整个Run终态checkpoint/Completed已验收，本场景不作该声明。

合作分支的玩法与失败退出尝试共用原280秒Token，不通过清理重开一轮预算。第一次退出响应未知时等待原Command，不另造退出ID；A失败不能跳过B的独立退出尝试。超时、死亡、断线或退出未确认都保留失败；保存双方报告/日志后停止自有进程仅为资源清理，不能代替业务退出成功。公开证据增加Peer身份、两个Owner的阶段值与独立退出证明，Ticket与控制配置仍仅在本轮ignored私有目录，并在结束时逐文件清除；不修改/删除测试账号、历史Run或资产表。

这个场景覆盖真实两Client共享DS及同场Owner隔离。它仍从出生点使用普通掉落，不移动到区域2，不证明Ice256交互、双方同时施法、OS/硬件输入、任意指针Raycast或主观手感。是否通过以本轮 `evidence.json` 为准；源码存在、工具编译成功都不能代替实际Native结果。

统一诊断报告读取使用 `FileStream(FileShare.ReadWrite | FileShare.Delete)` 与 `StreamReader.ReadToEndAsync`，读前、读后均检查32MiB上限；private配置、manifest和HTTP读取不改。原因是本轮单Mouse虽有49项检查通过，Client日志仍记录一次Probe写库存报告的 `IOException / Sharing violation`：旧工具默认共享读会短暂挡住Native覆盖写。这是工具IO噪声，不算Gameplay故障；保留原日志证据，不屏蔽错误。允许共享写后仍按原方式重试partial JSON/IO窗口，且命令、版本与新鲜Tick门全部保留；超过报告上限明确失败，不能沿用旧报告冒充新样本。
