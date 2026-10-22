# Norn 业务服

ASP.NET Core业务服负责账户、线上资产、持久化和房间调度。N2包含原子匹配/四人Ready、Run快照/AssetLease、独立Unity Dedicated Server进程、Ready/Heartbeat、一次性JoinTicket和重启接管。角色/战斗与结算仍待后续阶段；副本服Unity源码属于同级RPG工程。

| 项目 | 类型 / TargetFramework | 作用 |
|---|---|---|
| Norn.Business | ASP.NET Core Web / net10.0 | HTTP、身份、资产事务、迁移与 readiness |
| ../Shared/Protocol/Game.NetworkProtocol.csproj | Class Library / netstandard2.1 | 纯协议/HTTP DTO/ContentManifest，无 Unity/数据库依赖 |
| Norn.Business.Tests | xUnit / net10.0 | 原N0/N1共26项，加N2的32项，共58项，使用独立真实MySQL test Schema |

SDK10.0.402、Runtime/ASP.NET Core10.0.13，MySQL8.4.13 LTS、MySqlConnector2.6.2、Dapper2.1.79，Protobuf3.34.2/protoc34.2。版本位于Directory.Packages.props、packages.lock.json及Unity侧dependency-lock.json。三个组件在同一Monorepo中保持RPG/Server/Shared同级；Clone整个仓库及Git LFS，Unity Hub添加内层RPG工程。原本机凭据/SDK/数据库不随Clone恢复，无秘密格式示例见`../RPG/Tools/Networking/Templates/README.md`。

人工启动：Visual Studio打开Norn.Online.sln，右键Norn.Business→Publish→LocalOnline，Target Location为Server/.artifacts/publish/online。Unity菜单先Start Local Database，再Start Local Business Server；浏览器检查http://127.0.0.1:5080/health/ready。主菜单直接注册/登录，单机也必须先登录；进据点后副本门按F选单机或匹配。N2匹配进入连接测试区，还没有权威角色/战斗。停止先业务后数据库，后者等实际SHUTDOWN确认；停止Host不会顺带结束DS。

私有配置在同级RPG/.tools/local-mysql，不把密码/连接串放到源码、Inspector或截图。Client只连业务服；MySQL127.0.0.1:13384，norn_online_dev/test分离，旧系统MySQL不更改。N1本机HTTP仅用于loopback，远端须HTTPS。N0 Mirror开发身份仍不等于正式账号的副本入场权限。

测试：VS Test→Configure Run Settings→Select Solution Wide runsettings File，选择同级RPG/.tools/local-online/Norn.Online.runsettings；先启动独立MySQL，再在Test Explorer运行。缺真实数据库会失败，不回退内存Repository。

完整操作、文件责任、失败证据与trade-off见Unity项目Assets/_Project/Docs的《双端开发基础指南》和《联机系统开发解析》。N1本地完成不代表公网/Linux/正式副本通过，N0加密分配与native启动错误继续保留。

迁移003建立实例/Ticket，004增加队列标记/Presence/取票预算；001–003历史Hash保留。每成员至多一票（含消费票），最短5秒取票，删除旧Hash不让旧票恢复有效。Open大厅90秒无Presence会清成员，Starting/Running仍受实例Epoch/Lease保护。本机普通与Diagnostic业务菜单都显式启用Rooms/Instances，Server路径为Builds/OnlineRooms/Server；部署默认开关仍false。实例密钥经环境传入并剔除业务数据库配置，Ticket/密钥仅存摘要，不提供Client裸解锁API。新Host按PID/UTC ticks/路径接管原DS，只能Windows loopback；公网/Linux/加密分配与native启动错误仍有独立Gate。
