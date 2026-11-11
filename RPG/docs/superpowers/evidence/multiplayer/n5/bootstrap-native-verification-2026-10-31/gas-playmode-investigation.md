# N5 首轮 Gas PlayMode 错误只读调查（2026-10-31）

结论：**首轮实际执行了未配置材料 Binding 的旧 PlayMode 测试程序集，是高置信度根因。** 后续显式源码刷新和重编译使新夹具生效，激活前配置 Authority，现有 Binding 同步剥除 Rendering。没有证据支持改生产 Gas Renderer。该结论有日志行号和完整源码链路支撑，但没有首次 DLL/PDB 的逐字节存档，因此保留历史归因边界，不写成已经独立复现或生产 Renderer Bug 已修复。

## 可证实的事实

| 保存的运行 | XML UTC 时间 | 结果 |
|---|---|---|
| `playmode-initial-results.xml` | 10:30:31–10:30:34 | 0 passed / 2 failed |
| `playmode-green-results.xml` | 10:34:25–10:34:27 | 2 passed / 0 failed |
| `playmode-repeat-results.xml` | 10:35:12–10:35:13 | 2 passed / 0 failed |

首轮两项均为 `GasFieldVolumeRenderer.Start:68` 的未预期 Error。该分支覆盖 `source == null`、`MaximumChunks == 0` 和 `_material == null`，原日志并没有证明“材质丢失”。两张保存 Prefab 均为 Gas Renderer 配置了合法 World fileID 和现存 `M_RealGasWorld.mat` 的 GUID `5dc74216241317748927d8f21fee6898`。

最强的独立证据是 **同一条 NPC 创建调用栈的源码行号**：

- `Editor.log:11212`、`:11243`：首轮 `EnsureEnemy` 的上层 `CheckMap.MoveNext` 指向测试文件 **54** 行。
- `git show HEAD:RPG/Assets/_Project/Tests/RunPlayMode/FormalDungeonWorldPlayModeTests.cs`：`EnsureEnemy` 正好是 **54** 行；这个版本直接激活世界，没有 `TryConfigure`。HEAD 原始字节 SHA-256 与保存的 before Hash 完全相同（`6886E458E9B1A88FB46C8B84CEEFD9D9F759D968B5C9636FC63202E6E8166D37`）。
- 当前源码 `EnsureEnemy` 在 **66** 行。Authority 配置和 ready 断言共加 8 行，随后 Rendering 检查再加 4 行，合计 12 行。假如首轮已执行“有 Authority 配置、仅缺 Rendering 断言”的版本，该位置应为 **62**，与实际 54 不符。
- 两轮绿色日志分别在 `Editor.log:12132/:12163`、`:12599/:12630` 指向 **66** 行。

编译顺序也一致：首轮错误 `Editor.log:11188/:11262` 后，`:11434` 才明确 `Csc Game.Run.PlayMode.Tests.dll`，`:11448/:11449` 复制 PDB/DLL，`:11450` 报成功。首轮进入 PlayMode 虽在 `:10886/:10887` 打印 assemblies reload / forced synchronous recompile，相关区间没有此程序集的 Csc/CopyFiles；**Domain Reload 的文字不能证明新测试源码已进入程序集**。整个 full EditMode 结果也不能替代该证明：其请求只列 EditMode fixtures，`ProjectTestRunner` 的 EditMode assembly 白名单不含此 PlayMode assembly。

初次只读观察到 DLL/PDB 的最后写入时间均为 UTC 10:33:47，与首轮失败后、第一轮绿色前一致。调查期间主控后来又重编译（观察到 UTC 10:45:12/10:45:11），所以 JSON 中当前 DLL/PDB 哈希只代表采集时文件，绝不作为 10:30 首轮程序集身份。

## 能解释症状的源码链路

1. `OnlineDungeonTools.cs:360` 将正式导出世界的 `_executionMode` 预置 **Replica**，`:361` 清除单客户端 `_interestPoint`。这是要求入场时显式配置的安全默认值。
2. 旧测试 `FormalDungeonWorldPlayModeTests.cs`（HEAD）`:46` 直接 `world.SetActive(true)`，没有给 `OnlineMaterialWorldBinding` 配置模式/世界身份。
3. `OnlineMaterialWorldBinding.cs:81–85` 的 `Awake` 遇未配置候选锁存故障并禁用世界；它没有运行 `TryConfigure` 的组件剥离和 Replica readmodel 建立。
4. `ElementWorldRuntime.cs:155–160` 的 `Awake` 在 Replica 模式直接返回。`:144` 的 `GasVolumes` getter 在 Replica 模式取 `_replicaGases`；没有 `ConfigureReplica` 时此值尚未建立。
5. 保存的世界仍带 Gas Renderer，`GasFieldVolumeRenderer.cs:64–68` 在 `Start` 读到缺少的只读 source，记录对应错误。这条路径无需缺材质或真实 GPU 故障。
6. 当前测试 `:46–52` 在 inactive 边界找到唯一 Binding 并 `TryConfigure(Authority,19)`；`OnlineMaterialWorldBinding.cs:52` 调 `StripOwners`，其 Authority 分支 `:114` 按精确 namespace `Game.Rendering` 选择组件并在 `:133` 调 `DestroyImmediate`。当前测试 `:53–56` 检查不存在残留 Rendering 后才在 `:57` 激活，`:58` 检查 ready/非故障。

上述链路是源码推出的因果解释。两轮绿色直接证明新夹具的合法 Authority 装配通过，不是对首次错误对象身份的现场采样。

## Hash 与其他解释

`source-hashes-before.json` 与 `source-hashes-after.json` 中以下 SHA-256 完全一致，采集时当前文件也匹配：

| 文件 | SHA-256 |
|---|---|
| `GasFieldVolumeRenderer.cs` | `FB5183049B9053489EFFCEE721DF222639E5FBA6A730E5B6FE2830012C82E0FD` |
| `OnlineMaterialWorldBinding.cs` | `C93D5613C261812AF5A955FC49E993C15A5E436EA10F4DD3A019B73E5598A5E6` |
| `OnlineDungeonTools.cs` | `0CB8810D38EC3B63E651C6FEA96DE2A878FE0D73ED69C71DC8F64EF1E7DE8310` |

测试源码由 `6886E458…` 变为 `FCE38422…`；完整值、证据 XML Hash、文件元数据及白名单日志片段见 JSON。保存 Prefab 最后写入在 10:24:27/10:24:41，均早于三轮 PlayMode，无证据表明绿色是靠两轮之间重导出或补材质取得。

- **Scene/Preview 泄漏：证据弱。** `OnlineMaterialWorldExportTests.Cleanup` 调 `ReleasePreview`；导出器 `Close:210–215` 销毁 Root、临时 Profiles 并关闭 World/Source Preview Scene。日志首轮前后加载原主菜单和测试 backup，未见确定泄漏证据。但首轮没有 Renderer 的 scene/name/instanceID 快照，不能逻辑排除另一个失效实例同时存在；Domain Reload 也可能清除临时状态。它无法解释旧 `EnsureEnemy:54` 行号，故不是优先假设。
- **初始化时序/Gas Renderer 自身问题：证据弱。** 合法 Authority 根本不应启动该 Renderer；当前同步剥除且新断言、两轮绿色都通过。旧 Replica 未装 readmodel 时 source 为空是上述未配置路径的预期后果。没有证据要求新增 `Start` 延迟、吞日志或 Rendering 生产修复。
- **Authority 缺 Interest Point：是另一个已修问题。** 当前 Runtime 在 `TryInitialize` 仅 Authority 免除该前置条件；首轮旧夹具保留 Replica，因此不会走 Authority 初始化。不能把这次 Gas 错误再次归因于该已修条件。

## 最小后续验证方案（本次未运行）

只需临时测试夹具，复用真实保存的 P7/P8 Prefab，禁止改正式 Scene/Prefab：

1. 失配控制：实例化 inactive 世界后故意不调 `TryConfigure`，直接激活并等一帧；记录 Binding Faulted、执行模式、GasVolumes、Renderer 的实例/场景和具体失败谓词。应复现未配置 Replica 的缺 source 链路。该控制验证机制，不冒充恢复历史 DLL。
2. 合法控制：同一资源先 `TryConfigure(Authority,非零Epoch)`，确认全部 `Game.Rendering` MonoBehaviour 已删除，再激活；检查 IsReady、非 Faulted、无意外日志，并保留原 NPC/入口断言。当前两轮历史绿色已覆盖这条合法路径。
3. 验证新源码：显式 refresh-source，等待脚本编译和 Domain Reload 完成，在提交 TestRunner 请求前保存测试源码 Hash、PlayMode DLL/PDB Hash、最后写时和程序集 MVID（必要时对 Portable PDB document checksum），再运行。`isCompiling=false` 与 `scriptCompilationFailed=false` 只说明当前未编译/无失败，不能证明磁盘刚拷贝的源码已被导入。不要靠重复点运行碰运气。

当前无需生产 Rendering 改动。应保留首次 RED，将结论更新为“旧测试程序集导致测试未满足激活前配置合同；显式刷新编译后使用新夹具通过”，并注明缺首轮二进制存档这一历史归因限制。

本调查只读取仓库、历史报告和有限 Editor.log，未运行 Unity/测试/构建/服务，未修改仓库或 Git；只在任务工作目录写入本报告和配套 JSON/精简日志。没有未解决权限阻塞。
