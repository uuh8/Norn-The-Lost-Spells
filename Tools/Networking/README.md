# 联机基础工具

当前覆盖 N0 工程/传输实验和 N1 本地账户、Session、线上 Profile、MySQL 库存事务与 UGUI。正式合作副本仍待后续阶段。目录固定为同级 `RPG / Server / Shared`。

人工操作先读[双端开发基础指南](<F:/Develop Project/RPG/RPG/Assets/_Project/Docs/2026-10-21 双端开发基础指南（是什么、为什么、怎么操作）.md>)：.NET 项目使用 Visual Studio、Schema 使用已配置的 External Tool，Unity 的构建/运行/检查入口都在 `Tools → Norn → Online`。已有项目不需要重复创建。

本目录的 `.ps1` 保留给维护自动化，使用 PowerShell 7.2+，不作为学习者必须照抄的操作流程。GenerateProtocol 扫描两个 Schema，唯一输出 Runtime/Generated；Test-OnlineFoundation 只验证 .NET 与真实 HTTP，Unity/KCP/Grid 各有独立菜单和证据，不能互相替代。

SDK 安装到 `RPG/.tools/dotnet`，不会修改系统 SDK 或系统 PATH。`Server/global.json` 固定版本；业务服与协议库是两个 TargetFramework，不能将共享库改为 net10.0。

依赖版本和官方来源见 dependency-lock.json。SDK 摘要来自 Microsoft 发布元数据；Protobuf 工具/包摘要记录本次官方 HTTPS 下载内容。NuGet 另生成各项目 packages.lock.json，后续可执行 `restore --locked-mode`；包源使用 NuGet 官方服务。更新依赖需要同步锁定记录并重跑验证。

Shared 是独立本地 Git 仓库，Server 也是独立本地 Git 仓库；原 Unity 仓库没有迁移。完整备份和 Clone 必须包含三者并保持相对目录，本轮没有创建远端、提交或推送。

本地业务服使用 `http://127.0.0.1:5080`；远端必须 HTTPS。N1 的密码/连接串仅保存在忽略的本机目录，Session 只驻留 Client 内存；`/health/ready` 实际查库，`/health/live` 仅表示进程活着。N0 的开发认证仍不是 N1 账号入副本的 JoinTicket。

N1 人工操作：在 Visual Studio 选择 Norn.Business → Publish → LocalOnline，发布到 Server/.artifacts/publish/online；Unity 菜单先 Start Local Database，再 Start Local Business Server。正式主菜单直接注册/登录，通过本次业务验证后才能进入据点，单机也需登录；在副本门按F再选择单机或匹配。匹配房间服务属于N2，当前按钮明确提示暂不可用。Stop Local Business Server 后 Stop Local Database 正常关闭。数据库启动禁用 Windows monitor，确保登记 PID 就是实际 Server；停止操作必须确认 SHUTDOWN 成功和该 PID 退出。

Start 只启动业务服，不会启动副本服、MySQL 或 Unity Editor。Stop 按实例文件核对 PID、启动时间、可执行路径后仅停止对应子进程，不按进程名批量结束 dotnet / Unity。测试脚本使用独立实例名、空闲端口并在 finally 中清理。

最终证据见 `docs/superpowers/evidence/multiplayer/n0-final/`，历史失败和诊断对照单独保留。普通 Server Build 使用原项目 Rendering 输入；Rendering Comparison / Without Client Resources 只用于显式诊断，工具负责 finally 恢复。加密持续分配及两条未定位 native 启动报错保持在后续兼容/远端验收清单，不将 N0 本地结果当作公网完成。
