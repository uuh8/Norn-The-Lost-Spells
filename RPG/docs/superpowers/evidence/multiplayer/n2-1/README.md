# N2-1 本地证据（2026-10-22）

本目录证明房间与入局资产事务基础，**不代表N2完成或匹配已经可玩**。正式房间HTTP默认关闭；Prepare/FailStart仅内部Service。没有创建新.NET项目或Unity Scene/Prefab，没有本轮原生Player构建、GC测量或远端部署。

- `acceptance.json`：42业务（原26+新16）、83相关EditMode、3原玩家路径PlayMode，以及已完成/待完成边界。
- `business/n2-1-room-red.trx` / `n2-1-room-green.trx`：首次6项房间RED→GREEN；`entry-red` / `entry-green`：8项入局RED→GREEN。
- `business/n2-1-recovery-red.trx`：创建成功但回包丢失时尚无current接口的行为失败；最终增加GET rooms/current，复合验证错误Manifest不能退出房间。
- `business/n2-1-acceptance.trx`：最终42/42、0失败/跳过，含四人Ready预留/取消。测试Host使用真实独立MySQL test Schema，非内存Repository。成功测试在断言后按匹配Epoch取消自己的预留；早期测试历史保留在test Schema。
- `unity/*-report.json` / `*-results.xml`：同一现有Editor运行的83+3回归；PlayMode使用真实新Publish业务服与开发库，覆盖登录、Hub、F门、P7/P8单机和返回，并以MemoryFiles保护单机存档。这里没有验证联机角色/战斗。
- `source-audit.json`：原49个其他保护文件、4项设置、5个单机档未变；N1已批准的主菜单变更仍在。Shared源与本Task暂存匹配、无反向依赖/重复DLL/输出污染；001与中央包版本配置有N1基线。自动生成NuGet锁图仅记录当前Hash/原修改时间，没有历史Hash对比。
- `cleanup.json` / `unity/editor-handoff.json`：本轮进程清理与Editor回到干净实验Scene。原系统MySQL不停止。

真实开发库已通过AutoMigrate到版本1/2，共15表；AssetLeases=0。实际health/ready=200，默认rooms/current=404。两次Host持久化证明状态保存在MySQL，**没有**证明重接管仍运行的Dedicated Server。

概念与人工作法见基础指南第26节，逐文件责任与trade-off见开发解析Task N2-1；主计划第25节列出下一段真实实例、Ready/Heartbeat、JoinTicket及传送门房间UI。
