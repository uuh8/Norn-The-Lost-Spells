# Assembler 生命周期修复只读审查

日期：2026-10-31。独立 reviewer 审查合同说明、两文件完整内容/diff，以及版本/Domain 定义；未修改文件、未运行 Unity/测试/服务。

结论：未发现阻塞问题，无 Critical/Important finding。

- `WorldChunkReplicaAssembler.cs` 第 61–66、78–82 行：Faulted/Installing 检查先于状态修改，不能通过换 Key/Epoch 绕过重入或故障栅栏。
- 第 63、82 行：安装后禁止重绑；更高 Epoch 仅取消候选，保留 Header、payload、Generation/Revision；tombstone 同受 Installed 保护。
- 新增生产路径未发现数组分配、LINQ、boxing、闭包或容量增长；属于静态审查，不是性能测量。

非阻塞测试建议（本轮保留，未识别对应代码缺陷，不扩大当前范围）：

1. 测试第 175 行的同 Epoch 用例已证明保留半包，但没有在原 TTL 边界单独证明不续期。
2. 测试第 202 行附近可增加 tombstone 后 Reset/重绑组合用例；当前生产 Installed 保护明确，未见绕过路径。

主 Agent 已实际核查 19 项 GREEN 和同范围 20 fixture 的 196/0；这两个测试建议不改变通过范围，也不宣称组合场景已经实测。
