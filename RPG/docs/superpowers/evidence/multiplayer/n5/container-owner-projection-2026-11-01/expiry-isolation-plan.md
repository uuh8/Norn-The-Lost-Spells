# ExpireDue 全局计数污染：安全隔离复核方案

日期：2026-11-01。此为给主 Agent 的内部交接；调查仅做源码、文档和既有 TRX 的只读检查，没有连接 MySQL、读取私有连接文件、启动服务、运行测试或修改仓库源码。

## 1. 已确认的事实

- 已读外层 `AGENTS.md` 和内层 `RPG/AGENTS.md`，已看外层 Git status；工作区有大量在途改动，未触碰。
- `OnlineFactory.cs:32–40` 从 `NORN_N1_CONFIG` 取 Test/TestMigration，runtime guard 固定为 `127.0.0.1:13384/norn_online_test`。它并没有为每个 factory 建库或隔离普通业务表，也没有跟踪所创建账户/Run/Instance 的 ownership。
- `OnlineFactory.cs:54–57` 移除后台 `RoomInstanceSupervisor`。`MutableOnlineClock` 每个 factory 从真实 UtcNow 起步；Dispose 没有清理实例的覆写。
- `DatabaseTestIsolation.cs:3–5` 只禁用 xUnit 测试类并行。它不能隔离上次失败遗留、别的 testhost 或人工验收写入同一 test Schema 的实例。
- `RoomInstanceService.cs:160–171` 的 `ExpireDueAsync` 对全库 `ActivePort IS NOT NULL` 扫描，分别处理 Launching/Ready 的 deadline/lease。返回的是本次所有成功 Fail 的数量，不是当前测试所建实例数量。
- `RoomInstanceTests.cs:77–80` 本 case 只 Allocate 一个实例，推进 121 秒后要求计数为 1；实际 2 必然包含另一条到期实例。第二条具体身份和来源尚未从数据库核实，不能仅凭 TRX断言属于哪个测试或本轮验收。
- `container-full-business.trx` 保存 145 executed、143 passed、2 failed；目标 case Expected 1 / Actual 2。另一条为旧 protocol v4 断言。它不是本轮新运行结果。
- 本失败发生在 `ExpireDueAsync` 已完成后，意味着当时计入的两条实例都已经走正常 `FailAsync` 事务；首次失败并不意味着那两条仍保持 active。它可能已经使之后的原断言复核具备干净前置条件，须用只读 SQL确认。
- N5 living explainer `2026-10-29 共享材料世界N5开发解析（更新中）.md:493` 已记过同类 99/1：正常 ExpireDue 处理额外自有 Interest 实例后，只读确认活动实例 0，再原断言重跑 100/0。可借鉴流程，不能把历史结果当作当前结果。

## 2. 首选：零清库、零生产改动的最小复核

主 Agent 集中调度，确保没有其他 testhost、测试 Schema 业务进程或 native 验收会话在此窗口写入。现有 dev 服务不应被停止或改连接；不按进程名批量终止。

### 2.1 只读预检

连接仅由主 Agent 沿现有私有配置流程建立，不把连接串/密码、SecretHash、TicketHash、Session Token、EntrySnapshot 输出到聊天或证据。连接前分别 parse Test 和 TestMigration，要求两者都是精确的 `127.0.0.1:13384/norn_online_test`；现有 OnlineFactory 只 guard runtime，因此 migration 目标也须单独核对。

在 runtime test 连接执行：

```sql
SELECT DATABASE() AS SchemaName, @@port AS Port, VERSION() AS MySqlVersion;
SELECT Version, ScriptHash FROM SchemaMigrations ORDER BY Version;
SELECT InstanceId, RunId, RoomId, RoomEpoch, State, UdpPort, ActivePort,
       ReadyDeadlineUtc, LeaseExpiresUtc, ProcessId, ProcessStartTicks, Executable
FROM ServerInstances WHERE ActivePort IS NOT NULL ORDER BY InstanceId;
```

SchemaName 必须为 `norn_online_test`，Port 必须为 13384；核对私有实例真实 PID/启动时间/规范化 exe/data 路径，不能把系统 MySQL 当目标。Version/Hash 与已保存 `migration-source-hashes.json`、当前 001–006 文件字节核对；已应用迁移不得改 Hash/脚本。已有 006 应为已应用状态，本次不需要新迁移或 DDL。保存非敏感 preflight JSON，包括 active 实例精确列表和迁移摘要。

如果 active 列表为空，直接进入 2.2。若非空，暂停这条全库扫描测试，保留列表，走第 3 节。不要先调用 ExpireDue 当“清场”，因为那会终止未知归属的历史实例。

### 2.2 原 case 连续两轮

保持 `Assert.Equal(1, ...)`、121 秒推进和其余租约/旧 epoch/票拒绝断言不变。无需改任何源文件。使用主 Agent 已有的安全环境或现有本机 runsettings；下列命令仅是待主 Agent 执行的参数样例，本子 Agent 没有执行：

```powershell
$expiryEvidenceRoot = 'F:\Develop Project\RPG\RPG\docs\superpowers\evidence\multiplayer\n5\expiry-isolation-2026-11-01'
& '.\RPG\Tools\Networking\Invoke-OnlineDotNet.ps1' -DotNetArguments @(
  'test', 'Norn.Business.Tests/Norn.Business.Tests.csproj',
  '--configuration', 'Release', '--no-restore',
  '--settings', 'F:\Develop Project\RPG\RPG\.tools\local-online\Norn.Online.runsettings',
  '--filter', 'FullyQualifiedName=Norn.Business.Tests.RoomInstanceTests.OldServerEpoch_CannotRenewOrAdmit_AndLeaseExpiryReleasesAssets',
  '--logger', 'trx;LogFileName=expiry-isolated-round-1.trx',
  '--results-directory', $expiryEvidenceRoot
)
```

若当前 Release 已对应最新源码，可按既有流程加 `--no-build`；否则由主 Agent 正常构建。不要为了使用此样例改私有配置；若现有安全流程是环境变量，沿已有方式保留 `NORN_N1_CONFIG`、`NORN_CONTENT_MANIFEST`，省略 `--settings` 即可。

每轮结束重复 active 查询，要求空列表；保存 TRX、ExitCode、pre/post JSON。第 2 轮只改独立 LogFileName 为 `expiry-isolated-round-2.trx`。预期每轮 1 passed/0 failed，且没有遗留 active 实例。不要覆盖原 143/2 或 144/0 的证据。

### 2.3 全量业务回归

两轮完成且 active 仍为 0、独占窗口仍有效时，可使用相同 settings/环境运行全 Norn.Business.Tests 或现有方案文件，保留独立 `business-full-isolated.trx`；再次保存 active=0 和 migration Hash 不变证据。全量数量以实际 TRX 为准，预期现有 145 项但不得只根据历史144/0加1宣称完整绿。

只需业务测试时不要顺带调用 Test-OnlineFoundation.ps1，因为它还启动真实 HTTP smoke 进程；若主 Agent 原范围已要求 HTTP/protocol gate，再按原授权集中执行。

## 3. active 非空时：只允许有确切 ownership 的正常业务终止

### 3.1 ownership 证据

可接受证明是本任务或明确历史测试保存的 grant/descriptor/run ledger，包含 `InstanceId + RunId + RoomId + RoomEpoch + 全部成员 CharacterId`，并能从只读 JOIN 精确对应数据库。若登记过进程，还须对应测试身份/工作目录、PID + UTC StartTicks + exe 路径；存活进程先核对，不能把真实 gameplay DS 当测试假进程。

`n1_` 用户名前缀、随机 GUID 格式、State=Ready/Launching、超时、旧创建日期、当前 PID 不存在，均不能单独证明归属。当前 OnlineFactory 没有 ledger，TRX 也不打印 grant ID，因此不能只靠现有 fixture 自动认定任何未知历史实例是自己创建的。

只读交叉检查可按已知精确 `@InstanceId` 查询：

```sql
SELECT i.InstanceId, i.RunId, i.RoomId, i.RoomEpoch, i.State, i.ActivePort,
       p.ActiveRunId, p.RoomEpoch AS PartyEpoch, p.State AS PartyState,
       r.State AS RunState, m.CharacterId, l.RoomEpoch AS LeaseEpoch
FROM ServerInstances i
JOIN Parties p ON p.RoomId=i.RoomId
JOIN Runs r ON r.RunId=i.RunId
JOIN RunMembers m ON m.RunId=i.RunId
LEFT JOIN AssetLeases l ON l.RunId=i.RunId AND l.CharacterId=m.CharacterId
WHERE i.InstanceId=@InstanceId ORDER BY m.CharacterId;
```

### 3.2 正常业务终止边界

确定该实例是本轮可终止的测试控制面会话、不是有待 N6 结算的真实运行后，主 Agent 可用已有测试 Host 的 `RoomInstanceService.FailAsync(exactInstanceId, "Cancelled", ...)`。此方法通过 `RoomMutationGate → Party → 按ID排序角色锁`，在匹配当前 Party Run/Epoch 时原子失效 epoch、恢复 Open、置 Run Failed/成员 RolledBack、只解除该 Run/Epoch 的 AssetLeases、记录生命周期事件，再清该实例 ActivePort/JoinTickets。不会删除账户、物品或历史 Run。这是已有正常业务终止，不能替代 N6 gameplay 持久结算。

有 RunReservation 但尚未 Allocate 的本测试启动预留，使用精确 RunId/Epoch 的 `RunEntryService.FailStartAsync(..., "Cancelled", ...)`。该入口只接受 Starting，不适用于 Ready/Running 实例。

保存 ownership 匹配、终止结果和 post-check；若结果不明，先查询 exact 实例状态与对应 lifecycle event，再决定下一步，不重复盲调。未知归属保留原状并报告 blocker，不能按全局 State 删除/改写历史，不直接清 AssetLeases，不重写 migration。只要仍有未知 active，就不运行会扫全库的 expiry case。

## 4. 后续 fixture 防复发（可选小修，仍仅测试代码）

若本轮目标只需安全复核，先完成第 2 节即可。若主 Agent还要修复可重复性，推荐精确 ownership scope + finally，不动业务协议或生产扫描范围：

1. 所有 Allocate 后立即把精确 grant/Run/Room/Epoch/成员记录到本测试 scope；即使后面的 Ready/Assert 失败，finally 也能用正常 FailAsync(Cancelled)回收本测试实例。新创建 run 尚未 allocate 时，也需 exact FailStart。
2. `RoomInstanceTests` 大多数终止调用目前位于方法末尾，断言失败会跳过；MakeReady 自身若 Ready 失败也应在 helper 内清刚 Allocate 的 grant。`OwnedCombatAssetsTests.FrozenSnapshot...` 同样在末尾清理，应移入 finally。`DungeonInstanceTests`、`JoinTicketBudgetTests` 已有可借鉴的 exact-grant finally。
3. scope 生命周期应覆盖整个测试，不能绑定任意 factory.Dispose：`NewBusinessHost_RecognizesSameReadyInstance_AndRenews` 故意销毁第一个 factory 后让第二个 Host接管同一实例；自动在第一个 Dispose 时取消会破坏测试目的。使用跨两Host的scope，或该测试单独在最外层 finally清 exact grant。
4. Expiry 测试开始前增加 active=0 的明确隔离前置检查，在创建当前 Run之前执行；失败输出非敏感精确 IDs并退出，不调用全局expiry清场。运行完也检查无未终止自有实例。此检查使污染变成可解释的fixture失败，不能独自清除未知历史污染。
5. 未来 native/control-plane 验收也应保存 exact ownership ledger，正常退出时只终止自己实例。仅给 xUnit加锁不能阻止另一个验收服务写同一Schema。

所需最小落盘：本轮独立证据目录 + 中文 living explainer接续段；若选择防复发小修，再改测试helper/相关测试的finally，不改Shared、production RoomInstanceService或001–006 SQL。不建议把migration测试的表前缀重写搬到普通Host：普通Dapper业务SQL固定表名，前缀方案需要广泛生产SQL改造。另建Schema也不是当前最小路径：OnlineFactory硬guard和既有runtime/migration账号权限都绑定现有test Schema，仓库启动脚本不负责创建新Schema/用户授权。

## 5. 本子任务交付与局限

- 交付此方案；仓库零修改，数据库零访问，测试/服务零运行，凭据零读取。
- 源码与TRX能证明全局额外计数；第二条具体身份、当前active是否0、001–006当前数据库应用Hash均待主 Agent只读核验。
- 普通exec未执行，因Windows restricted-token sandbox无法处理split writable roots而拒绝启动；之后require_escalated的只读源码读取获准完成。未出现auto-review拒绝，未触碰Physics操作。
- 最小下一步：主Agent只读检查test Schema active列表。如果0，原case两轮后全量业务；如果非0，先做ownership对照，不能先全局到期清理。
