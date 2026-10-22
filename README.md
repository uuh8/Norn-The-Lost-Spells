# Norn: The Lost Spells

Unity单机RPG与2–4人独立副本联机工程。当前N0–N2本机阶段完成：账户/线上资产、房间匹配/准备、独立Dedicated Server、一次性入场票和重连；权威角色运动/AI进入N3开发，联机战斗/共享材料/可靠结算仍在后续阶段。Windows loopback验收不能视为公网/Linux兼容。

## 仓库结构

| 路径 | 责任 |
|---|---|
| `RPG/` | Unity工程，同时构建Client与Dedicated Server；保留Assets/.meta、Packages和ProjectSettings |
| `Server/` | ASP.NET Core业务服、xUnit Tests、SQL增量迁移与.NET包版本锁 |
| `Shared/Protocol/` | netstandard2.1 Class Library及本地UPM包；Protobuf Schema/Generated与纯契约 |

三个目录属于同一个Git Commit。只打开/下载RPG目录会遗漏相对引用，恢复时应Clone整个仓库并下载Git LFS对象；Unity Hub的Add项目选择内层`RPG/`，Visual Studio打开`Server/Norn.Online.sln`。Shared是库，不是另一个Web服务器；副本服的Unity源码在RPG，而非Server目录。

## 固定环境与恢复

Unity6000.3.16f1/URP17.3.0；Mirror96.11.3；SDK10.0.402/.NET及ASP.NET Core10.0.13；MySQL8.4.13、MySqlConnector2.6.2、Dapper2.1.79；Google.Protobuf3.34.2/protoc34.2。完整来源与Hash见`RPG/Tools/Networking/dependency-lock.json`，NuGet精确依赖见各packages.lock.json。不会因Clone而自动安装SDK、MySQL或Unity Dedicated Server模块。

1. GitHub Desktop Clone整个仓库；确保Git LFS已经安装、所需模型/纹理/DLL是实际文件而非pointer文本。迁移前旧历史仍可查看，当前目录以本README为准。
2. Unity Hub添加内层RPG工程，安装对应Editor和Windows Dedicated Server Build Support，等待导入；不要同时导入第二份相同工作区。Packages中的Shared相对路径依赖兄弟目录布局。
3. 按[双端基础指南](RPG/Assets/_Project/Docs/2026-10-21%20双端开发基础指南（是什么、为什么、怎么操作）.md)与[无秘密本机配置模板](RPG/Tools/Networking/Templates/README.md)配置工具、独立MySQL开发/测试Schema、自己的凭据与VS发布目标。
4. Visual Studio Publish业务服；Unity菜单构建Dedicated Server，依次Start Local Database/Start Local Business Server；浏览器检查`http://127.0.0.1:5080/health/ready`。具体点击步骤和Tests配置见上述指南，不能直接复制旧电脑的PID或秘密文件。
5. 由主菜单注册/登录后进入据点，在副本入口按F选单机/匹配。线上资产与单机存档隔离。N2匹配目前进入连接测试区，后续阶段逐步添加权威玩法。

数据库迁移属于源码，实际MySQL数据与密码不在Git中；需要长期保存的数据库应另外私有备份。`.tools`、Library/Temp、编译/发布输出与原始Profiler大文件可重建，不上传。第三方运行依赖及其License随Assets保留，不采用全局忽略DLL的规则。

## 开发与验收记录

- [总体执行方案](RPG/docs/superpowers/plans/2026-10-20%20Norn多人联机系统总体执行方案.md)
- [联机系统开发解析](RPG/Assets/_Project/Docs/2026-10-20%20联机系统开发解析（更新中）.md)
- [N2最终本机验收](RPG/docs/superpowers/evidence/multiplayer/n2-final/README.md)

已有N2记录：58真实MySQL业务、91相关EditMode、6玩家路径PlayMode；最终可见双Client/F/UGUI入场、返回/重连/队长退出、真实业务中断后同DS恢复通过。各120帧校准连接稳态GC峰0B只覆盖该窗口；两条已知native启动错误、公网加密分配、Linux/IL2CPP/D3D12保持未通过。

2026-10-23起仓库根由Unity目录上移一层。已有Unity Commit完整保留，未重写历史；原子迁移之后的提交统一包含三组件。开发规则见[RPG/AGENTS.md](RPG/AGENTS.md)，用户已允许按需使用SubAgent。
