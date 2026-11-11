# N5 当前 fixture 基线（2026-10-31）

这是一份保留失败的当前基线，不是 N5 完成验收。Git HEAD `0d87d0da931e`，Unity 6000.3.16f1，复用当前 Editor PID 25660，没有启动第二个 Editor 或关闭用户进程。

## 授权与范围

- N5 实施计划第 3 行明确记录用户于 2026-10-29 推进 N5，第 5 行要求 Editor/测试主控串行；N4 final-audit acceptance 第 5 行 `N5ExplicitlyAuthorizedByUser=true`，第 18–20 行的 N4/体验/发布 Gate 仍为 false。
- 本次明确要求先做导入/编译与现有 fixture 基线；原文位置见 [authorization.json](authorization.json)。没有实施新行为、协议或炼金，没有修改断言、生产/测试源码或资源。
- 后续讨论记录总计划，不自动插入当前任务；讨论中的想法、确认需求和授权实施分开。协作约定本身不授予新功能实施许可。

## 本次实际结果

| 运行 | 实际时间 UTC | 结果 |
|---|---|---|
| 20 个既有 N5 EditMode fixture | 2026-10-31 09:17:55–09:18:24 | 188 case，186 passed / 2 failed / 0 skipped / 0 inconclusive |
| 仅两项失败的独立复现 | 2026-10-31 09:19:56–09:20:02 | 0 passed / 2 failed，无源码变化；重复 case，不增加覆盖 |
| 历史 integration-red 的九项逐名对照 | 主运行内 | 9 passed / 0 failed / 0 missing |

主运行保留 [editmode-report.json](editmode-report.json)、[editmode-results.xml](editmode-results.xml)；[fixture-results.json](fixture-results.json) 给出 20 个 fixture 的分项。[reproduce-report.json](reproduce-report.json)、[reproduce-results.xml](reproduce-results.xml) 是独立复现。旧 `../replication` 原始失败记录未覆盖。

当前两项失败均属于 `Game.Core.Tests.WorldChunkReplicaAssemblerTests`：

1. `UnusedPoolSlotCanBindButInstalledSlotCannotChangeChunk`：测试源码第 118 行查找公开 `TryBindUnusedKey` 返回 null。
2. `ConnectionResetCancelsCandidateWithoutResettingInstalledWorldRevision`：测试源码第 126 行查找公开 `ResetConnectionEpoch` 返回 null。

当前生产类没有这两个方法，Key/ConnectionEpoch 为构造期绑定。测试在 API 存在性断言处停止，后面的池复用/重连行为尚未验证，不将其误称为已经复现的世界版本回退。失败全文见 [failures.json](failures.json)。历史九项明细见 [historical-nine-comparison.json](historical-nine-comparison.json)。

## 导入/编译与操作流程

使用仓库现有 ProjectLayoutMaintenance.inspect 获取干净主菜单状态，再使用 OnlineEditorRequests.refresh-source 刷新源码；成功回执保存在 refresh-source.json。ProjectTestRunner 只在 isCompiling/isUpdating=false、scriptCompilationFailed=false、非 Play、全部场景干净时接受请求。本次成功进入 Runner，证明通过这些门禁；没有执行全量 clean 编译或 Client/DS 构建。

request.json 是实际提交的 20 个完整 fixture 名；reproduce-request.json 是两个精确失败方法名。人工复查在 Unity 的 Window → General → Test Runner → EditMode 按这些名称选择，保留场景干净；不关闭现有作业、不修改断言求绿。自动化继续使用现有 `Temp/SinglePlayer/run-editmode.request.json` 入口，同一时刻只运行一个测试请求，完成后归档新结果，不覆盖本目录。

editor-before.json、editor-after-main-run.json、editor-final.json 记录状态。测试完成回调的 CurrentScene 暂为空，最终 inspect 已确认官方 Runner 自动恢复 `Assets/_Project/Scenes/P7_MainMenu.unity`，非 Play/未编译/Dirty=false，无需强制切场景或保存。

editor-test-log-filtered.txt 是从提交时 Editor.log 字节位置开始，仅提取编译错误、刷新/重载、项目测试和当前失败名相关行的日志摘录；不是完整原始 Editor.log。提取范围和过滤规则见 editor-log-extraction.json，没有归档启动/认证凭据。原始 NUnit XML 是结果与堆栈的主要证据。

## 源码与资产保护

- source-hashes-before/after.json：1,485 份相关 Runtime/Editor/Tests/Schema/Generated/asmdef 源码哈希均相同。
- preservation-before/after.json：12 项正式场景、项目设置、Build Scene List 和真实单机存档哈希均相同；只读取 Hash，没有读取或改写玩家资产内容。
- 原有两份文档修改、总计划和五组 Recovery 场景保留。本轮只新增证据与维护总计划/N5 解析；未提交/推送。

## 未测与下一步

未跑全项目 Run All、完整材料相关回归集合、PlayMode、真实业务/MySQL、Client/DS 构建、运行期 N5 通道、双/四 Native、丢包/HOL/全帧 GC 与帧时间、Linux/公网/IL2CPP/D3D12 或 N6 结算。当前通过的导出/只读组件不证明正式线上材料链已贯通。

下一步先核对 Assembler 两个生命周期 API 与已有 N5 身份/重连/版本合同，再补最小边界测试及实现；若需要改 wire 语义或跨模块架构，先列差异待确认。随后推进真实提交捕获、激活前装配和认证 Channel。这里仅记录建议修复顺序，未实施这些功能。当前无权限或 Unity 会话阻塞。
