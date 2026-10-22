# N1 本地验收交接（2026-10-22）

总入口 acceptance.json，LocalN1Passed=true。主菜单直接登录/注册，单机也必须登录；据点P7/P8/远征门F选择单机/匹配/取消。匹配Room/JoinTicket/Dedicated Server调度属于N2，当前明确暂不可用，不回落单机。

| 证据 | 范围 |
|---|---|
| n1-entry-business.trx | 26/26；真实独立MySQL test Schema，身份、唯一性、过期/注销、并发版本、收据幂等、越权/内容和真实SQL中途失败回滚 |
| editmode-results.xml / report | 83/83；原75项相关回归+Gate/主菜单8项，不代表整个工程全部测试 |
| playmode-results.xml / report | 3/3；真实业务登录，生产Menu→Hub→P7/P8→Hub、F选择/取消/匹配不出发、Session实例不变；已有Token但服务不可达仍拒绝进入 |
| ../n1-entry/gui-review.json、main-menu-signed-out/in.png | 真正Windows D3D11 Client，实际鼠标/输入注册登录；原背景、密码遮蔽/清空、进入按钮状态，旧联机/Play入口消失 |
| client/first.json、resume.json与日志 | 首轮Null Graphics与恢复轮可见Direct3D11；同一角色/物品/位置/版本2、注销204，正向校准有效，均120空闲帧峰值0B |
| mysql-graceful-restart.json | 旧私有worker2648退出，新worker3568，PID file匹配、认证Ready；不能用端口存在当重启证据 |
| 三份 *-build.json | 主菜单Client/Lab Client/Windows Dedicated Server均Succeeded、0构建错误，原Rendering/Resources输入保留；Google.Protobuf字节Hash匹配 |
| source-and-dependency-audit.json、protocol-check.txt | 原50保护文件只改计划内主菜单，其余49不变；4项设置/5个单机档不变；无重复第一方协议DLL/Shared输出污染，生成校验2文件与新包Hash通过 |
| n1-editor-handoff.json、cleanup.json | 原Editor非Play/场景干净，回到MultiplayerAuthorityLab；本任务业务/MySQL/Client退出，无5080/13384监听，原系统MySQL保留 |

PlayMode只替换单机IProfileFileSystem为MemoryFiles，不替换账号、HTTP或MySQL；N1线上资产操作另由实际原生Client和真实DB事务测试覆盖。原生菜单没有进入会写正式单机档的Hub，完整过门路径在上述隔离单机文件系统的生产场景测试中验证。VS Publish界面为人工操作说明，本次自动构建/发布使用锁定私有SDK，不声称已点击VS。

GC只针对预热后的诊断界面空闲窗口，不包含JSON/请求/场景切换分配，不代表整个游戏或业务进程Zero GC。清单61项Hash一致：af1d4ed8e2750c9aea352af338fc66620eec8af00693d58af12590c298c98fc6；三种构建的resources.assets均找到该Hash，业务实际验证同一清单。它是N1身份/构建契约，不是完整Gameplay签名或已实现副本握手。

历史失败保留n1-1-business、n1-2-client及n1-entry/gate-red.xml。首次MySQL monitor误判、一次受控私有worker强制恢复与随后正常关闭分开记录；D3D12 native Client crash不算修复。N0两条native启动错误、EncryptionTransport持续分配、Linux/公网/IL2CPP仍未通过。RemoteGatePassed=false，不能把本地N1通过当成整个联机系统完成。

下一步N2：传送门匹配意图接业务房间、四人准备、Lease/Epoch、进程/端口分配、Ready后JoinTicket和接入认证；不在主菜单增加联机入口。
