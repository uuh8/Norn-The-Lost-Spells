# N5 材料玩家路径合同审计

2026-10-31，只读。依据两层AGENTS、架构导览、N5-5、总计划§2/§6/§20。Scripts相对RPG/Assets/_Project/，协议相对Shared/Protocol/。未改仓库/Git，未运行Editor/测试/构建/服务/生成，未读凭据。

## 主要结论：容器链路缺实现，目标已有授权

Native材料复制成功不代表玩家容器投放/采集已接通。N5-5/总计划§6 A5及本次委派已授权数量事务、双端一致与重连；无需再问是否进入N5。但新增Wire/资产投影/跨模块语义须按总计划§2.2/§6确认差异。

## 输入→资产→World 的实际边界

- Character/Interaction/ContainerUseController.cs:15,44–47,77–78绑定SinglePlayerInventorySession；H/G/K（54–56）发布ContainerUseRequestedEvent。MaterialCollectionController.cs:12,31–40,81–82同样依赖该Session，F发布MaterialTransferRequestedEvent（161–164）。现有装配是PracticePlayerInventoryBinding.cs:18–23。
- Run/Runtime/SinglePlayerInventorySession.cs:282–287接容器/采集/附着输出事件。Run事务服务管理资产Reservation，ElementField的ContainerDepositAdapter/MaterialInventoryTransferAdapter调用DepositPort/WorldPort，保持World锁、Stage/Undo、几何预检、Commit、Ledger与Publish。
- Client Replica **正确拒写**：ElementWorldRuntime.Authority.cs:23–24及两个Port的CanAcceptWorldWrites检查拒绝Replica；没有“拒绝后自动转发DS”。不能把单机Session接回线上或本地发EventBus当成授权网络操作。
- 未找到正式材料操作意图通道：检索 Multiplayer/Networking、全部 Protos，无容器Use/采集/投放消息或消费者。world.proto:20仅Baseline/Delta/ACK/Resync/删除/Enter/Leave；inventory.proto:109只允许SelectWand/PlaceCard。已有InventoryNetworkChannel具备认证/Epoch/速率/收据框架，但不是容器操作实现。
- 在线资产也受限：Run/Spells/OnlineOwnedWorkspaceFactory.cs:33–38只导入装备杖和安装卡；OnlineOwnedWorkspace.cs:74–80拒绝其它位置。inventory.proto:41,51亦限杖/法术卡闭包，无容器组分、材料堆或背包父链。不能仅增加一个键盘转发就获得真实DS材料资产。

## 法术材料与消耗要分清

普通沉积链路：WizardController.cs:185–188真实Attack→OnlineCastDriver意图→CombatNetworkChannel.cs:91–103认证/Epoch/预算→OnlineCombatWorld.OnIntent→DS OnlineCastDriver.cs:323–335施放。非Replica ProjectileBase.cs:499–505触发SurfaceImpacted→ElementDepositOnImpact.cs:180–207正式World写入。Mana/次数走Owned事务，Client只表现；合法线上卡/Prefab是否含沉积组件须核对，不能临时赠送资产。

“从容器取料的法术附着”另有缺口：SpellMaterialOutputService.cs:44–65要求随身Backpack/Container/Composition；MaterialPayloadRuntimeAdapter.cs:62发送SpellMaterialOutputEvent。OnlineOwnedWorkspace.cs:53–55未订阅该事件，入局投影也没有容器/附着来源。普通命中生成材料不能证明容器供料、材料量扣款已经在线实现。

## 同进程重连与资源保留

OnlineRoomClient.cs:195–209已有断线后presence→Connect入口，Connect:175–188停止旧连接、POST新Ticket并建立新Manager，不需重启Client。DS NetworkActorRegistry.cs:58复用Actor；OnlineCastDriver.cs:197–202同Run/Player/Actor复用Workspace，SetConnection:224–231只重置连接命令缓存。OnlineCombatWorld.cs:159–169按新Epoch导出当前库存，而非EntrySnapshot旧余额。

这是可独立实测的既有路径，尚非本轮结果。“不补满”须核对有限次数/实例身份；Mana有自然regen，应比较DS当前值与经过时间。容器余额保留仍依赖在线资产缺口。

## 实施前需明确的新增差异

1. 容器最小交付覆盖哪些既有操作：采集/撒出，还是同时G投掷/K固体；复用现有输入与规则，不另造玩法。
2. 线上容器/材料堆/组分/背包父链的合法来源、入局/快照合同与重连复用；不读单机档，不提前扩N6。
3. 新材料意图/收据的Shared字段、兼容升级和DS归属/距离/预算/幂等约束；不能私用world/库存字段改变语义。

待答仅阻塞依赖部分；法术键鼠/消耗后重连可先验证，不能替代容器验收。

## P3与DS资源错误

RoomInstanceBootstrap.cs:121–150的home/away P3仅在显式Interest ENV路径执行；error未清空导致后续home/away失效，但不参与真实Input或Ticket流程。优先键鼠不需它，不能因它锁住所有验证。

历史gates-4/server-native.log:37,40及native-log-findings.json保留两条Built-in Resource错误；result.json为Passed=true、KeyboardPath=false。它们未阻断那次Ready/KCP/恢复，但不证明已修或本轮输入/渲染/重连通过。无证据将其认定为容器缺通道原因；新故障须按本轮日志定位。
