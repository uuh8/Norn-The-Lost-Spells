# N5 Assembler 两项 API 合同核查与最小修复范围

日期：2026-10-31。当前只处理 `WorldChunkReplicaAssembler` 两个缺失生命周期方法及测试，不接正式复制 Channel，不改 Schema、玩家行为、资产规则或跨模块依赖。

## 授权与既有合同

- N5 实施计划第 3 行明确推进 N5；N4 final-audit acceptance 第 5 行 `N5ExplicitlyAuthorizedByUser=true`。本轮进一步明确允许对这两个缺失 API 在现有合同内做最小修复。
- N5 计划第 23 行：“重连ConnectionEpoch由传输层另审，不重置世界”；第 63 行要求隔离 WorldEpoch/ChunkKey/Generation/SnapshotId/ConnectionEpoch；第 80 行先验实际 connectionId/认证对象/ConnectionEpoch。
- 联机总方案第 149 行：同一玩家重连后 ConnectionEpoch 递增，旧连接/输入失效。`WorldReplicaRevisionState` 已明确世界重建换 Owner，重连不能清世界版本。
- 现有两个测试要求：未安装池槽可绑定 Key，安装后不能改 Chunk；更换 ConnectionEpoch 取消候选而保留已安装 Revision。它们是尚缺的本地 API，不是改 wire 包含义的依据。

## 本轮技术裁定

1. `TryBindUnusedKey(WorldChunkKey key)` 返回 bool：仅未安装、无候选、非 Installer 回调、无锁存故障时接受合法原生 Domain；不能跨 Chunk 复用已经安装过 Baseline 或 tombstone 的槽。WorldEpoch 与所有预分配缓冲保持。
2. `ResetConnectionEpoch(ulong epoch)` 返回 bool 以便调用者观测拒绝：非零且不能倒退；同 Epoch 幂等，不丢半包或延长 TTL；更高 Epoch 取消未完成候选，但保留已安装 Header、Payload、Generation/Revision。最大值不回绕。
3. 两方法都服从现有 `_installing` 与 InstallerFaulted 栅栏；身份切换不能在外部 Installer 回调中破坏原子发布，也不能用重连解除已锁存故障。使用已有 Failure 枚举和拒绝风格，不增加 wire 字段。
4. 认证仍由连接 Owner 完成。页 API 本身不携带 Epoch，Reset 不证明拒绝了所有旧 wire 页；上层仍须先验证当前 connectionId/Epoch。当前没有正式 Channel 接线，不将这个局部修复描述成重连端到端通过。

这些是已批准行为内的本地生命周期实现选择，没有新增玩法或协议语义。若后续接线需要改变消息身份或跨模块架构，单独列影响等待确认。

## 修改与验证计划

只修改现有 `Scripts/Core/Online/WorldChunkReplicaAssembler.cs` 与 `Tests/Core/WorldChunkReplicaAssemblerTests.cs`，保留原 11 项断言不变，先新增 8 项边界并观察 RED，再补最小实现。边界覆盖无效 Domain/候选重绑、零/旧/相同/最大 Epoch、半包重新收齐、保留已安装版本栅栏、Installer 重入与 Faulted。GREEN 后运行原同范围 20 fixture（新 case 纳入原 fixture），原基线 188 case 将增加到 196，不以重复运行累加覆盖。

源码继续复用既有 Core/Online 与 Tests/Core 分类；不新建功能目录、不搬迁文件、不改 asmdef。证据按现有 n5 约定单独建本轮运行目录，为保留历史 RED/基线，不属于新增生产模块。

保护现有 12 项正式场景/设置/真实存档；复用当前 Editor 的既有请求流程，不并发其他 Editor 操作、不关闭进程、不提交/推送。阶段结束更新总计划和 N5 解析；方法静态无分配不外推 Native 性能、正式重连或 N5 完成。
