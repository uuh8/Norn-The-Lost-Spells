# 显式 Native 单人库存回归

这是供 Agent 集中运行的独立 Console，不加入 `Norn.Online.sln` 或默认测试发现。当前文件只表示工具已实现；真实通过范围以运行产出的 `evidence.json` 为准。

前置条件是已启动的私有 MySQL `127.0.0.1:13384/norn_online_test`、包含本次 Probe 与最新 Manifest 的 Development Client/DS，以及本机私有 `NORN_N1_CONFIG`。工具只读取该文件的 `Test/TestMigration`，两条连接均须通过 test Schema 隔离门；从未输出连接串、账号密码、Token 或 Ticket。`NORN_CONTENT_MANIFEST` 可指定刚导出的正式清单，缺省取仓库当前 `Resources/Online/ContentManifest.json`；Gameplay Hash 始终读取仓库当前 `GameplayManifest.json`，因此构建后不能继续改变 Runtime 源码或清单。

主 Agent 从既有 SDK 包装器调用：

```powershell
& 'F:/Develop Project/RPG/RPG/Tools/Networking/Invoke-OnlineDotNet.ps1' run --project 'F:/Develop Project/RPG/RPG/Tools/Networking/NativeInventoryRegression/NativeInventoryRegression.csproj' -- --server-exe '<本次Development DS绝对Exe路径>' --client-exe '<本次Development Client绝对Exe路径>' --output '<新的或空的证据目录>'
```

可选参数是 `--repo '<外层Git根>'`、`--timeout-seconds 240`（范围60–280）、`--core-only` 和 `--dungeon home`（缺省 `p7`）。Core 模式保留真实入场、30→29→28、原 UI 法术卡往返与正常退出；p7 默认还验证法杖进包/重装备，以及容器在包、道具栏、附近地面之间往返和连续投掷。选择与投掷经过原 `OnlineCastDriver` 入口并核对 Caller、成功收据及完整 Owner Snapshot 安装；投掷使用实际共享 Camera 的中心射线。UI 指针 Raycast、键盘硬件输入和战斗主观手感不属于本工具覆盖范围。

完整 P7 用例的注册账号原本只有法杖、法术卡与保底背包，没有容器。工具经正式商店购买第二把法杖和三只瓶，沿原账号 Move 装备到法杖槽1与工具槽1–3，再经商店给三瓶各买17 GMU Water，共51 GMU。账号阶段明确断言“两杖三只有料瓶”；随后 P7 原入局奖励在空工具槽0提供第四只空瓶，Native 首份库存再断言“两杖四瓶，3 filled + 1 empty”，并确认三只购买实例的ID与数量未变。工具不改注册、入局发奖规则或资产表。

四瓶连续走原 Driver 选择与 Throw，第四次先将第一根杖放入背包，验证法杖槽0为空时仍能回退到槽1。Owner移除只证明离手；工具还只读测试库已确认的 Checkpoint Head 与退役身份索引，等待四瓶不再在途、没有 ContainerTransit 材料损耗，并确认 Client 安装 impact 后库存版本，最后验证退出 Profile 中四瓶均未复活。混合空瓶与有料瓶分别覆盖空瓶破碎、真实材料 Deposit，两者不能互相代替。

HUD 验证另读实际 `HeldItemHud` 的 Cell 缓存与 Image：每次选择须只有对应槽的真实 Frame 使用作者选中 Sprite，每次 Throw 须原槽 `ItemId` 清空、`Icon.sprite` 为空且 `Icon.enabled=false`，同时实际法杖选框正确。采样还要求唯一 HUD、当前 Source 与已渲染 Source 一致、HUD Canvas 可见；只读反射不调用 Refresh/Bind，不以 DTO 正确代替 UI 正确。报告保留每格图标、背景 Selection 和 Frame 状态；原 HUD 的选中表现由 Frame Sprite 驱动。

Home 模式使用同一真实注册/Login/Profile 和 `single-entry home`，不会执行不存在入局瓶奖励的容器测试。首轮验证真实30→29→28及Tab实际角标，再正常退出；随后保持同一Bearer/Session，另起真实Run/Ticket/Native Client，验证同scope、同卡ID仍28并再次正常退出。两轮日志分别保留。Mana/CD有真实值副本，但稀疏心跳与等待期间会自然恢复，本工具明确不据此宣称Mana/CD连续性通过。

Home装拆卡必须先走原Kind16/17研究会话和真实空间权限。当前作者首席出生x=0，研究Box中心x=6、尺寸4，出生点在区域外；工具发送真实Kind16取得NotOwned作为Gate控制例，并将`CardMovementCoverage.Covered=false`明确记录，继续验证次数/角标/同Session再入。它不会移动角色、放宽研究区或把拒绝记成卡片移动通过。只有真实Kind16成功时才执行原UI storage↔program，随后以Kind17关闭会话。

执行链为新注册 → 登录/Profile → `single-entry p7` → DS 真实 Ready → 一次性 Ticket → Native Lab Client → 正式 p7。Host 是 `WebApplicationFactory<Program>.UseKestrel(0)` 的真实空闲 loopback 监听，不借正式5080；移除 Supervisor 以防扫描历史 test Run，使用 `TimeProvider.System` 保留真实租约与心跳。DS 由原 `RoomLauncher` 启动，没有构造第二成员或伪造 Ready。

每次施法都检查 Client 实际安装的同一卡片次数、DS 当前工作区与 `InventorySentPages` 增长；Tab 检查同时保存 `ExpectedCaption` 和真实 `InventoryItemView._spellCount.text`、缓存次数与可见状态。每次拖放先调用原 `OpenBackpack`，保留 Probe 的 `IsOpen` Gate，收到匹配的新成功收据并安装更高的 Owner SnapshotSequence 后才检查位置；选择/投掷前关闭原 UI。地面容器先通过真实附近拾取回包，再绑定道具栏；没有放宽原绑定规则。

退出发布原 ESC 的有限意图，等待实时 `Committed` 后查询业务最终 receipt，并核验数据库成员 `Exited`、租约0、持久版本和 RequestHash，最后确认 Profile 的卡片剩28次。失败会保存最后的 Client/DS 状态、Host 错误类别和 Native 日志，并尝试原退出；不会注入 Snapshot、移动玩家、奖励物品、直接改资产或强行解锁。

Ticket、命令和 Probe 配置只放本轮 `RPG/.tools/native-inventory-regression/<case-id>/`，结束删除一次性控制文件。只停止本工具持有句柄的 Client、当前新账号关联且 PID/启动时间/实际 Exe 三项匹配的 DS，以及本工具创建的 Host。测试账号与失败 Run 留在隔离数据库供审计，不清理历史账号或改动迁移。
