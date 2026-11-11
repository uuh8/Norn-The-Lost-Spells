# N5 Assembler 生命周期最小修复（2026-10-31）

仅处理 `WorldChunkReplicaAssembler` 的 `TryBindUnusedKey`、`ResetConnectionEpoch` 两个缺失 API；没有扩展正式复制接线。授权与已批准合同的原文位置、实现取舍及边界见 [contract-review.md](contract-review.md)。

## 实际结果

| 运行 | UTC 时间 | 结果 |
|---|---|---|
| 只加八项边界，生产不变的 RED | 09:28:41–09:28:48 | 19 case，9 passed / 10 failed |
| 最小实现后的 Assembler GREEN | 09:30:46–09:30:50 | 19 passed / 0 failed |
| 原同范围完整 20 fixture | 09:31:05–09:31:21 | 196 passed / 0 failed / 0 skipped / 0 inconclusive |

完整请求和结果为 red/green/full-request.json、*-report.json、*-results.xml。196 是前一基线 188 加八项新边界，不将重复运行累加。前一两个失败和历史九项逐名均 Passed，见 [prior-eleven-comparison.json](prior-eleven-comparison.json)。原 `../baseline-2026-10-31` 的 186/2 与复现 0/2，以及 10-29 历史失败均未覆盖。

## 变更及不变量

只改现有 `Scripts/Core/Online/WorldChunkReplicaAssembler.cs` 和 `Tests/Core/WorldChunkReplicaAssemblerTests.cs`。原 11 项测试断言不改，新增八项真实边界；diff 归档为 implementation-and-tests.diff。

- 未使用且无候选/故障/Installer 回调的槽才接受合法 Chunk Key；安装过数据或 tombstone 不改 Key。
- Epoch 非零且不倒退；同代配置幂等，不丢候选；更高代取消候选但不清世界 Header/Payload/版本。最大值不回绕。
- 安装回调不能换身份，Faulted 不能靠重连恢复；不重新分配候选或已安装缓冲。
- Reset 不认证页，页 API 不含 Epoch；传输/连接层仍须先核对真实 connectionId/Epoch。当前方法通过不等于正式重连、世界共享或 N5 完成。

本地实现继续在既有 Core/Online 与 Tests/Core 分类内，没有新增源码目录或 asmdef 依赖；本目录按既有 evidence/n5 规范保留独立运行窗口，不是新增生产模块。无 Schema/Generated、版本、场景/Prefab/SO、资产规则或玩家行为变更。

## 审查、保护与复查

[review.md](review.md) 是独立只读审查：无阻塞问题。非阻塞覆盖建议保留为同 Epoch 原 TTL 边界、tombstone 与两个 API 的组合回归，本轮未实测这些组合，不无限扩测。

1,485 份相关源码中仅两目标文件改变，其他 1,483 未变；12 项正式场景/设置/真实存档保护哈希未变。源码/保护 manifests 和 editor-final.json 留证。复用原 Unity 6000.3.16f1 Editor PID 25660，非 Play/未编译/Dirty=false，最终原 P7_MainMenu；没有双开/关闭用户进程，不提交/推送。

人工复查：Window → General → Test Runner → EditMode → WorldChunkReplicaAssemblerTests。无 Inspector 或资源安装步骤；完整同范围 fixture 在 full-request.json，串行运行现有 ProjectTestRunner 入口，不覆盖旧证据。

## 未测与下一步

未跑全项目 Run All、完整材料回归集合、PlayMode、业务/MySQL、Client/DS 构建、正式材料 Channel、双/四 Native、丢包/HOL、Native 全帧/新方法实测分配或远端平台。静态无分配不等于性能通过，N5Complete=false，N4 体验 Gate 保留。

下一步正式接线先检查 OnlineWorldBootstrap 在 WorldPrefab 激活前通过既有 Core Binding 配置 Authority/Replica，再接唯一材料时钟、认证 Actor、Frozen 与 Detach 生命周期；后续串接已有提交/冻结/Codec/Replica 组件。本轮没有执行接线。已有 N5 授权支持范围内继续，新协议语义/玩家/资产/跨模块架构决定仍需先确认。
