# Mirror 96.11.3 本地补丁

上游发布包与许可仍固定在 Tools/Networking/dependency-lock.json；以下是明确维护的本地源码差异，不伪称发布包原样导入。

* `Transports/KCP/kcp2k/highlevel/KcpServer.cs`：只将 `newClientEP` 从默认 private 改为 protected，供 NornKcpTransport 的 RawReceiveFrom 派生扩展填入固定池中的 EndPoint。没有修改 KCP framing、Cookie、可靠性、连接表或 Tick 算法。
* `Transports/KCP/kcp2k/highlevel/KcpHeader.cs`：ParseReliable / ParseUnreliable 用合法枚举值比较替代 Enum.IsDefined(Type, object) 的每包装箱；256 个字节的合法性/输出值测试保持等价，原 16 次解析有 16 次分配的 RED 已保留。未知包头仍拒绝，wire format 不变。
* `Core/NetworkConnectionToClient.cs`：时间插值 SortedList 从默认空容量改为连接建立时预留 64 项，保持默认 64 项准入及 32 项插入上限；丢包实测和 Profiler 调用栈定位到运行中扩容，对应 32 项 Burst 测试先 RED 再修正。Norn Manager 同时在启动时预留公开 Client 时间缓存；此缓存不是项目的 Protobuf Snapshot。
* `Core/Batching/Batcher.cs`：待发送批次 Queue 在构造时预留 4 项，即原先第一次入队才增长的容量。多个 Timestamp 在同次 Flush 前汇合会触发这条路径；真实 trace 定位到 Queue.SetCapacity 的 64 B，三个 Timestamp 最小测试先 RED=1 事件。批次边界、顺序和超出容量时的上游增长行为不变；不承诺任意积压都无分配。
* 使用点位于第一方 `Scripts/Networking/Transport`。Windows loopback 的 Socket 创建/关闭仍由原库负责，第一方只实现同步 Raw IO。非 Windows 不启用该适配器；Linux/公网/IL2CPP 是后续独立平台验证。
* 更新 Mirror 时先核对此字段和 RawReceiveFrom/CreateConnection 的生命周期，再重跑真实 UDP 分配测试与四 Client 进程测试；不能直接覆盖补丁后沿用旧证据。
