# N0 本地验收证据

2026-10-21 21:16，汇总 [acceptance.json](acceptance.json)：LocalN0Passed=true。它代表 Windows loopback 的选型/基础实验完成；RemoteGatePassed=false、EncryptedZeroAllocationPassed=false、LinuxValidated=false、MySqlImplemented=false。

| 文件/目录 | 内容 |
|---|---|
| editmode-results.xml、editmode-report.json | 最终 67/67：Networking 38、世界 24、设置 5 |
| business-tests.trx、foundation-report.json | 10/10 业务测试与真实 Kestrel Protobuf/版本/超长拒绝；生成时间各自保留，未伪称同时运行 |
| load-server-build.json、load-client-build.json、grid/grid-server-build.json | 最终三份真实 Build Report，Errors=0，原 Rendering/Resources 输入 |
| load/numeric、load/text | 四 Client ×30/s，数值五进程稳态零分配；文本功能和 positive allocation control 通过 |
| loss/loss-numeric | 双向每 20 个 Datagram 丢 1 个，五进程功能/稳态零分配 |
| auth/auth-numeric | 三种拒绝、唯一 ActorId、约15Hz/10-byte Snapshot，五进程功能/稳态零分配 |
| encryption/encrypted-numeric | List 指纹验证、错误指纹拒绝；功能通过，持续分配未整改 |
| grid/headless-grid | 真实 Null Graphics Grid 正反向；40 Tick/4.002676s/128GMU守恒/120帧0B |
| dependency-and-source-audit.json、rendering-references.json | 15 个 Hash/50 个保护文件；依赖及RP/Resources恢复 |
| protocol-repeatability.txt、unity-protocol-report.json、nuget-audit.json | 两次生成校验、Unity协议边界、官方NuGet源通告检查记录 |

每组 verification.json 只描述该组实验；Finished 不自动等于分配通过。生命周期清理和稳定通信分别观察；Profiler trace 只用于定位，正常验收不开完整 Allocation Callstacks。

Server 最终日志仍有两条 native Built-in Resource Error，根因未定位，不能推断 fallback 无害。加密 Server 帧峰值38,308B、四Client各11,268B，N8整改前不通过远端Gate。Windows Raw IO 只接收 IPv4 loopback，未验证 Linux/公网/IL2CPP；不是可直接公开部署的正式副本。

历史失败见相邻 n0-4 至 n0-10 目录，尤其 n0-10 的正常64B失败、完整trace、Batcher RED、文本调度失败。最终通过没有覆盖或删除这些原始证据。

人工复现操作、责任划分、版本和取舍见 Unity 项目两份联机学习文档；原可见双Client按钮验收在 n0-2-gui-startup/gui-gate.json，用户已委托完成，无需重复请求。
