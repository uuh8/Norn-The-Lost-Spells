# 本机联机配置模板

这些模板补充 [双端开发基础指南](../../../Assets/_Project/Docs/2026-10-21%20双端开发基础指南（是什么、为什么、怎么操作）.md) 第 20、22、23 节。它们是公开的格式示例，全部密码和绝对路径均为占位符，不能直接登录。复制后编辑私有副本，保留本目录示例不变。

Monorepo 根目录下是同级的 `RPG / Server / Shared`；下文 `RPG/.tools` 指 Unity 工程里的私有目录。Clone 保留源码与 Unity `.meta`，不会恢复旧电脑的数据库内容、账号密码、SDK 安装或发布输出。新电脑需要安装工具并初始化自己的开发环境，不能把旧本机进程记录复制过去。

## 1. 先准备工具和独立数据库

在资源管理器打开 Unity 工程，确认 `.tools/dotnet/dotnet.exe` 已由项目工具准备，版本与 `Server/global.json` 一致。维护者可使用已有 `Initialize-OnlineTools.ps1` 恢复锁定的 SDK、protoc 和 Protobuf 依赖；它不安装 MySQL、不初始化数据库，也不生成本页的私有配置。使用安装器/资源管理器人工准备工具的说明见基础指南；锁定版本、下载来源与摘要见上级目录的 `dependency-lock.json`。

通过官方 MySQL 安装/配置界面准备 **8.4.13 的独立实例**，并完成新数据目录初始化。Unity 菜单固定使用 `RPG/.tools/mysql/mysql-8.4.13-winx64/bin/mysqld.exe` 与同目录 `mysqladmin.exe`，因此二进制需位于这个布局。实例使用 `127.0.0.1:13384`；不要修改已有系统 MySQL。确认安装/配置界面使用的数据目录与下面 `my.ini` 的 `datadir` 相同。仅创建空文件夹不等于数据库已经初始化；本模板不会执行初始化。

在 MySQL Workbench 的连接管理界面新建本机连接，Host 填 `127.0.0.1`、Port 填 `13384`。用这个新实例的管理身份完成首次配置：

1. 在 `Schemas → Create Schema` 分别创建 `norn_online_dev` 与 `norn_online_test`，使用 `utf8mb4`。它们是不同数据库，不只是在界面里起两个名字。
2. 在 `Administration → Users and Privileges` 为下面四条连接分别创建账号并设置本机密码。模板使用 `norn_app`、`norn_migrate`、`norn_test_app`、`norn_test_migrate`；若改名，同步修改私有连接串。
3. 在 `Schema Privileges` 给两个 Runtime 账号各自 Schema 的 `SELECT / INSERT / UPDATE / DELETE`；给两个 Migration 账号各自 Schema 的建表与结构修改权限。可在各自 Schema 范围赋予 Migration 账号全部 Schema 权限，不授予其他库或全局管理权限。表结构由业务服 `AutoMigrate` 创建，不手工复制另一套表定义。
4. 单独创建 `norn_control`，仅给全局 `SHUTDOWN` 权限，用于正常关闭该独立实例。开发/测试 Runtime 不需要此权限。账号的 Host 规则应允许本机 TCP 连接；不要为公网开放账号。

开发库保存你本机的联机试验数据；测试库承受自动化测试写入。测试代码会拒绝非 `127.0.0.1:13384/norn_online_test` 的目标，不能把个人数据库改名填进去。

## 2. 复制并编辑四个私有文件

在资源管理器启用 `查看 → 显示 → 文件扩展名`，在 Unity 工程下创建 `.tools/local-mysql` 和 `.tools/local-online`。逐个复制并重命名，避免保存成 `.json.txt` 或 `.ini.txt`：

| 本目录公开模板 | 本机副本位置 | 编辑内容 |
|---|---|---|
| `connections.example.json` | `RPG/.tools/local-mysql/connections.private.json` | 四个连接串中的账号和密码；开发/测试 Schema 保持分开 |
| `my.example.ini` | `RPG/.tools/local-mysql/my.ini` | `basedir`、已初始化的 `datadir`、日志与 PID 文件的绝对路径 |
| `control.example.ini` | `RPG/.tools/local-mysql/control.private.ini` | 刚创建的专用 `SHUTDOWN` 账号及密码 |
| `Norn.Online.example.runsettings` | `RPG/.tools/local-online/Norn.Online.runsettings` | 两个 `C:/REPLACE_WITH_MONOREPO_ROOT` 改为实际 Monorepo 根目录 |

使用 Visual Studio 或文本编辑器打开副本。`connections.private.json` 的字段名必须是 `Development`、`DevelopmentMigration`、`Test`、`TestMigration`：Unity 启动菜单读取前两项；业务测试读取后两项。运行账号与 Migration 账号分开，是为了让常规请求不拥有结构修改权限。若密码含 JSON 或连接串特殊字符，分别按 JSON 字符串和 ADO.NET Connection String 规则编码；不要把未转义的双引号直接粘进 JSON。示例的 `SslMode=Disabled` 仅适用于当前独立 loopback 环境，不作为远端配置。

`my.ini` 路径使用 `/`，有空格时保留模板中的引号。数据目录必须与首次初始化一致；更换路径不会搬迁或恢复数据库内容。Unity 启动工具负责附加 `--no-monitor` 并记录新进程身份，正常使用时不另外注册第二个开机服务。

`.runsettings` 只引用配置文件与 Content Manifest 的位置，不包含密码。`NORN_N1_CONFIG` 指向上面的 JSON；`NORN_CONTENT_MANIFEST` 指向正式导出的 `Assets/_Project/Resources/Online/ContentManifest.json`。XML 路径若含 `&`，写成 `&amp;`。

真正的密码、连接串和数据库数据只放在忽略的 `.tools`。不要把编辑后的副本放回本模板目录、Server 源码或 Unity Inspector；提交前确认版本控制的更改列表没有这些私有副本。

## 3. 在 IDE / Editor 检查恢复结果

1. Unity 打开 Monorepo 中的 `RPG`，等待 Package 导入与编译完成。在 `Tools → Norn → Online → Verify Shared Protocol` 检查同源协议；需要更新清单时点击 `Export Content Manifest`。
2. Visual Studio 打开同级 `Server/Norn.Online.sln`，确认支持锁定的 .NET SDK。右键 `Norn.Business → Publish`，选择现有 `LocalOnline` Profile，发布到 `Server/.artifacts/publish/online`。
3. 安装/配置界面若已启动这一个新 MySQL 实例，先在对应 GUI 正常停止它；随后由 Unity 的 `Start Local Database` 启动，保证本工具拥有正确的进程记录。再点 `Start Local Business Server`。浏览器访问 `http://127.0.0.1:5080/health/ready`，返回 `ready` 才说明实际数据库连接与迁移成功；`/health/live` 只证明 HTTP 进程活着。
4. Visual Studio 选择 `Test → Configure Run Settings → Select Solution Wide runsettings File`，选本机 `RPG/.tools/local-online/Norn.Online.runsettings`，再打开 Test Explorer 运行 `Norn.Business.Tests`。它需要真实测试库；缺配置或数据库会失败。
5. N2 匹配还需要由 Unity `Tools → Norn → Online → Build Room Dedicated Server (Windows)` 构建 `Builds/OnlineRooms/Server/NornRoomServer.exe`。源码恢复不等于该二进制已经存在，也不等于权威角色/战斗已实现。
6. 使用结束先点 `Stop Local Business Server`，再点 `Stop Local Database`；等待 Console 确认认证 `SHUTDOWN` 与实际进程退出。已有活跃 Room Dedicated Server 的清理仍按基础指南的 N2 实例生命周期操作，不按进程名批量结束其他程序。

若启动提示缺少 `connections.private.json`、`my.ini` 或发布 DLL，回到对应复制/初始化/Publish 步骤。不要通过提交本机密码、恢复旧 PID 记录或把测试库改成个人库来消除错误。此页只补配置格式与人工恢复入口，不声称干净 Clone 已通过 Unity、MySQL 或多人运行验收。
