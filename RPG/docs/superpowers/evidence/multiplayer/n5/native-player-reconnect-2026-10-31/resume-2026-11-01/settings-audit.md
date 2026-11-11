# 2026-11-01 N5 Settings 只读恢复审计

审计对象：`F:/Develop Project/RPG`。本报告只读检查两层 AGENTS、当前三项设置、Git HEAD/diff、阶段保护与历史恢复证据；未修改仓库、未运行 Unity/测试/服务/构建/生成、未读凭据、未执行 Git 写操作。

## 结论

1. `DynamicsManager.asset`：当前字节与七份已保留的 Unity 物理设置自动格式升级副作用逐字节相同；存在明确历史恢复证明，且 HEAD 字节 Hash 等于本阶段起始保护 Hash。主 Agent 可在重新确认当前 Hash 不变、保存观察字节后，仅按条件恢复这一已证副作用。
2. `ProjectSettings.asset`：差异严格只有 `runInBackground: 0 -> 1`；只在内存改回这一处，结果逐字节等于本阶段起始状态与 HEAD。mtime 与此次 Cancelled 构建证据时间一致到亚微秒；相同完整观察 Hash 也有两次历史恢复证明。主 Agent 可按单字段与 Hash 双条件恢复。
3. `QualitySettings.asset`：改动完全符合序列化格式升级的形状，逆变换也逐字节等于起始状态；但未找到相同 Hash 的历史恢复证明。mtime 为取消后 43.84806 秒，现有 Build scope 在 Dispose 中本应写回原字节，缺少该次后续写入的直接来源证据。因此应保留当前文件、记录差异，不能仅凭“看起来像 Unity 自动升级”将它归为当前任务可清理项。

恢复前两项后，若其余九项仍匹配，保护检查预期是 **11/12**；不得声称 12/12。

## 实际核对 Hash

以下均为 SHA-256，完整读取实际字节，未通过 PowerShell 文本输出重建字节。

| 文件 | 阶段起始 Hash / HEAD Hash | 当前 Hash |
|---|---|---|
| QualitySettings.asset | `6814D6CC3EB850DD7D2A069A8B17BA9F7591DEBA8B1758A855978BD9C8627643` | `90A6246B84DF30886FB817936DB3934EDD391241D003785606137C0E27014856` |
| DynamicsManager.asset | `B7C9A9DC08B98990109D4346F68745E15E238DE400F96C583E53E1C83C743A78` | `53CF96C365B85EEFE78B3CDA76AF21F68246744277AE6CA21FFD6A67BDB72A1C` |
| ProjectSettings.asset | `4491D6D50AB523F1C8B681046E9F4BBB84F87013AEF76B9F9E2F0262451F9B33` | `83106A5F1E856A5C87B7A9AD73BCF6C30ACFF034EBADC1E5814FCD3C85F3CA45` |

三项 HEAD Hash 均实际确认与 `native-player-reconnect-2026-10-31/preservation-before.json` 完全一致。阶段 `git-status-before.txt` 未列出这三项 dirty。

## DynamicsManager 证据

- 当前 mtime：`2026-10-31T17:06:46.432335Z`。
- 当前 1553 bytes；HEAD 1254 bytes；LF 行尾。
- Diff 为 `serializedVersion: 13 -> 23`、`m_AutoSimulation: 1 -> m_SimulationMode: 0` 及新默认字段等，保留重力、碰撞矩阵、solver iterations 等既有值。
- `n5/bootstrap-lifecycle-2026-10-31/physics-reserialization-restored.json` 明确记录原因：Unity Physics simulationMode API 与 SaveAssets 重写设置格式；Before/Observed/Restored Hash 正好对应上表。
- 实际逐字节比对当前文件与以下七份 `physics-reserialization-observed.asset`，全部相同：`authenticated-material-channel-2026-10-31`、`bootstrap-lifecycle-2026-10-31`、`bootstrap-native-verification-2026-10-31`、`interest-subscription-2026-10-31`、`native-delta-2026-10-31`、`native-recovery-2026-10-31`、`native-recovery-gates-2026-10-31`。
- N5 living explainer 的 266、310、353、544 行亦记录此前相同格式副作用的精确恢复规则。
- 安全条件：再次读取时当前字节 Hash 必须仍为 `53CF96...72A1C`，候选原字节 Hash 必须仍等于起始保护值；任何不符即停止恢复、保留变动。不要使用无条件 `git restore`。

## ProjectSettings 证据

- 当前 mtime：`2026-10-31T17:36:42.028626Z`。
- `RPG/Tools/Networking/.artifacts/account-menu-client-build.json`：`Result=Cancelled`、`RecordedAtUtc=2026-10-31T17:36:42.0286258Z`、`Errors=1`、Unity `6000.3.16f1`，输出 `Builds/OnlineAccounts/Menu/NornMenuClient.exe`。不是成功构建。
- 当前 27320 bytes；HEAD 27320 bytes；差异严格只有第 86 行 `runInBackground` 的一位值。
- 实际检查当前 `b'  runInBackground: 1\n'` 出现次数为 1。仅改为 `b'  runInBackground: 0\n'` 的内存候选 Hash 为 `4491D6...1F9B33`，且候选与 `git show HEAD:RPG/ProjectSettings/ProjectSettings.asset` 完全相等。
- `n5/native-recovery-2026-10-31/projectsettings-background-restoration.json` 与 `n5/native-recovery-gates-2026-10-31/projectsettings-background-restoration.json` 两次历史记录的 Initial/Observed/Restored Hash 与本次完全相同，且当时同样与本轮自有构建落盘时间重合。
- 安全条件：再次读取时当前 Hash 不变；单字段出现次数仍为 1；候选逐字节等于实际取得的起始 HEAD 字节且 Hash 匹配保护值；当前 mtime 仍可对上此 Cancelled 构建。满足后仅写候选字节，不覆盖任何额外差异。

## QualitySettings 来源仍不足

- 当前 mtime：`2026-10-31T17:37:25.876685Z`，距取消证据 **43.84806 秒**。
- 当前 3723 bytes；HEAD 3662 bytes；LF 行尾。
- 全部差异：两条 quality entry `serializedVersion: 4 -> 5`；各新增 `meshLodThreshold: 1`；两条 customRenderPipeline 相同 GUID 的两行 YAML 合并一行；新增 `Nintendo Switch 2: 1`。实际未改变既有管线 GUID、当前质量、阴影、LOD bias 等。
- 将上述差异在内存逐条逆变换，候选逐字节等于 HEAD 且 Hash 为起始 `6814D6...627643`。这只证明可精确重建原字节，不能证明写入者归属。
- 在 `n5` 的 JSON/Markdown/diff 证据中搜索当前完整 Hash 与 Quality 序列化恢复说明，仅找到今天的 current 保护报告，未找到可比照的旧恢复证明。
- `Assets/_Project/Editor/Networking/OnlineBuildTools.cs` 普通构建也使用 `ServerRenderingBuildScope`；该 scope 的 Dispose 在 finally 写回原 Graphics/Quality/Player 设置字节。取消后 43 秒再次改变 Quality，需要核实是否是正常关闭/延后序列化、独立 Editor 操作或其他来源；当前审计没有足够直接证据。
- 因此保留并在阶段保护结论中显式记为差异；不要悄悄恢复为 HEAD，也不要把它当 N5 Gameplay 变更提交。

## 执行边界与工具状态

默认 exec 在任何进程创建前被环境拒绝：Windows restricted-token sandbox 无法处理 split writable root sets。随后所有读取均通过已批准的 `require_escalated` 只读命令执行，没有绕过拒绝。`rg` 不在 PATH，改用限定目录的 Get-ChildItem/Select-String。未执行任何设置恢复；恢复动作仍由主 Agent 集中协调。本报告写入授权 task 目录。
