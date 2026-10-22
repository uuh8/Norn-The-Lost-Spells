# N2 本机最终验收（已完成）

最终状态以acceptance.json及cleanup.json为准。当前N2Complete=true、CleanupPassed=true：58真实业务、91相关EditMode、6玩家路径PlayMode、最终可见双Client与故障恢复/120帧GC通过。缺少这些文件或N2Complete=false时，不能将本目录解读为阶段已完成。最终Build为17:18/17:19 UTC（北京时间2026-10-23），此前失败与部分通过的轮次仍分目录保留。

| 文件/目录 | 证明的范围 |
|---|---|
| business/ | 当前完整真实MySQL业务回归58项及新增匹配/票预算RED；测试库与开发库分离 |
| editmode-report.json / results.xml | 本阶段相关91项，包含实际消息ID冲突与旧连接槽序号污染防护；不是全工程全部测试 |
| playmode-report.json / results.xml | 6项真实登录、F选择、匹配取消/原P7/P8、DS认证重连/返回、网络未知结果取消、Client Session到期UX |
| native-portal/ | 最终可见D3D11客户端的原生输入/界面、不同Actor、消息往返、返回/重入/队长退出、业务中断与恢复、校准GC |
| native-before-layout-fix/ | 首轮已通过部分功能但GC峰值736B的原生记录；不能代替最终GC通过证据 |
| performance-before-layout-fix/ | 3470条GC.Alloc事件；重复的GUILayout.Begin/GUILayoutGroup构造，量化736B根因。原始20MBProfiler数据留Tools/Networking/.artifacts/n2-profiler，未覆盖既有Profiler缓存 |
| first-full-menu-build.json | 08:05 UTC第一份完整Client构建，零错误；后续代码/配置修复的最终Build单列unity/，不混用 |
| first-full-player-data-hashes.json | 第一份完整Player的数据文件Hash，供分析后续正常增量构建复用情况 |
| gate/unknown-outcome/old-connection/session-expiry的red/green文件 | 保留实际失败与修复路径，不删历史失败冒充首次通过 |
| source-audit.json | 原50文件除历史主菜单外保护、4项ProjectSettings/现有主菜单、5个真实单机档、版本锁、旧迁移、Shared边界及本轮源Hash |
| cleanup.json | 自己的Client/DS/业务/私有MySQL退出，端口/活动租约收敛，原系统MySQL保留，Editor非Play且干净 |

原生探针只在显式Development参数下启用，负责MemoryFiles隔离、角色定位和状态记录。实际F与UGUI由操作系统输入完成；定位不等于验证角色走路。GC窗口暂停探针JSON/IO与Observe通知，要求1KiB正向校准、有效Recorder、完整120帧、峰值0B，另记录实际Windows IPv4 Raw IO是否启用。大厅HTTP、入场、窗口操作与Server Heartbeat存在离散分配，不能以此窗口宣称整个游戏全生命周期零GC。

首次截图错误/焦点输入未触发、UTC与本地时间比较误判、准备检查点未单独取得等工具问题都保留原记录，不记为通过。最终原生结果必须来自最终Build之后；不同Run/实例的检查点不能拼成一次完整故障恢复。

N2仅完成房间/入场/实例与资产租约，当前在线场景是连接测试区。权威角色、Prediction、Interpolation、AI、战斗、共享世界与结算仍在后续阶段。两条已知原生启动错误、加密分配、Linux/public/IL2CPP/D3D12的Gate仍未通过。N2完成后须用户确认，才进入N3。
