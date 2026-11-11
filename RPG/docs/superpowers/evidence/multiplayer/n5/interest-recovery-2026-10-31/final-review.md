独立只读审查：N5 Store 等版本重入修复（2026-10-31）

结论：在本次指定差异内，未发现需要报告的确定问题。没有 P0/P1/P2/P3 finding；这一结论仅表示静态审查未识别缺陷，不代表完整 Interest、恢复或性能验收通过。

审查范围为 `RPG/Assets/_Project/Scripts/ElementField/Replication/WorldReplicaStore.cs:127` 的等版本分支，以及 `RPG/Assets/_Project/Tests/ElementField/WorldReplicaStoreTests.cs:166` 起新增的 13 个测试用例。生产差异已与阶段起始文件 `RPG/docs/superpowers/evidence/multiplayer/n5/interest-recovery-2026-10-31/WorldReplicaStore-before.cs.txt` 逐行比较；没有把工作区其他改动归入本次修复。

- 最小性：生产差异仅将等版本的 Checksum、WordCount、IsResident 核对从 Interested 条件中移出；内容一致后，原来的“已可见或权威 tombstone 幂等返回”仍保留于 `WorldReplicaStore.cs:133`。未新增状态、修改合同或改变其他安装分支。
- 原子性与容量：冲突候选在 `WorldReplicaStore.cs:132` 返回，位置先于容量检查、Candidate 复制、版本提交和公开引用交换。合法同版本 resident 重入继续经过 `WorldReplicaStore.cs:135` 的 Cell/Pending 容量、`:137` 的域 Chunk 容量和 `:145` 的版本检查，再进入 `:149` 后的原有发布路径；静态路径未见绕过容量或提前推进可见状态的新增分支。
- 合法重入：保留 Header 的内容一致且已经 Evict 的 resident 不在 `:133` 提前返回，仍会真正恢复占据。检查未比较 SnapshotId、ServerTick 或 BaseRevision，符合 `Core/Online/WorldReplicationCommitOwner.cs:173` 为相同内容重新生成当前 Baseline 的既有行为。`Core/Online/WorldReplicaRevisionState.cs:36` 允许合法同代同 Revision Baseline，因此没有因本次改动把合法同版本重装拒绝。Evict 后非 Baseline 仍由 Store `:102` 拒绝。
- 版本栅栏与 tombstone：旧 Generation/Revision 的校验继续由原版本对象承担；Evict 保留 Header 和版本对象。权威 tombstone 的 resident 恢复仍先经过 Store `:100` 的新 Generation 要求；等版本 tombstone 的原幂等行为未改变。
- 测试行为：新增测试为 4 个域的旧 Generation/Revision 拒绝（测试 `:172`）、4 个域的等版本不同合法 Payload 拒绝（`:191`）、4 个域的读接口清空及重复 Evict 幂等（`:211`），另加 1 项权威 tombstone 不复活（`:229`）。测试调用真实 Store 并断言返回值、数量、可见 Chunk/版本与后续合法重装；新增部分没有 mock、反射或直接依赖私有实现字段。

只读核对了主控保存的实际 NUnit 结果：`store-red-results.xml` 为 22 passed / 4 failed，四项失败均为 `InterestReentryRejectsConflictingContentAtEqualVersion` 的 LegacyCell/Liquid/Solid/Gas，断言 Expected False / But was True；`store-green-results.xml` 为 26 passed / 0 failed。随后核对 `full-report.json` 与 `full-results.xml`，同范围 26 fixtures 完整回归为 307 passed / 0 failed，报告完成时间 2026-10-31 12:46:59 UTC。这里是对主控运行证据的核对，本审查没有运行 Editor、测试、构建或服务，也没有执行 Git 写入或修改仓库。

范围限制：新增用例未穷举 Pending 内容冲突、重入时容量竞争、所有 Header 组合和哈希碰撞；不能由这些测试推导完整容量/安全压力结论。正式空间大小/形状/判交与独立 Interest 退出/重入协议仍待主控确认；生产接线、Native 出入区、真实网络恢复及性能不属于本报告。审查本身无被拒绝或待审批动作。

审查快照 SHA256：

- WorldReplicaStore.cs：`01F58ECDD5CF6486E0935C83060E6A81056C47F6A230A71EB4E523B6A84174CD`
- WorldReplicaStoreTests.cs：`3EDBE4064AEE1736A0EF23BB39CB3F785678E77AF630B4D28CCDD13A00210267`
