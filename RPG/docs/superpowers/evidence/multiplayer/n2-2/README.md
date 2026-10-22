# N2-2A 本地控制与入场验收

本目录证明独立实例生命周期与真实账号准入通过；N2整体、正式Portal匹配和Gameplay尚未完成。没有新增.NET项目或依赖版本升级。

| 文件/目录 | 如何阅读与范围 |
|---|---|
| acceptance.json | 最终汇总：52业务、87 EditMode、3原玩家路径PlayMode及原生检查；N2Complete/RemoteGate为false |
| business/ | 真实MySQL测试TRX与日志，保留空实现及环境继承的RED，最终以n2-2-final-business为准 |
| unity/ | 最终两份Build Report、87/3测试结果与Editor交接；两端保留原Rendering/Resources输入 |
| native/ | 最终构建之后同一DS的Ready、双账号、Ticket重放拒绝、重连、队长退出、业务重启、Frozen/恢复、资产解锁 |
| native-first/ | 第一轮功能记录，早于最终Review与MessageId修复，不能代替最终native |
| native-before-envelope-fix/ | 历史消息覆盖告警/观察超时记录；旧报告可能混合历史检查点，不用于最终通过判定 |
| source-audit.json | 保护文件/设置/单机档/历史迁移/共享边界；NuGet只记录当前图，缺历史Hash时不冒称比较过 |
| quality-settings-restore.json | 最终PlayMode后Unity序列化格式变化与精确基线恢复，具体保存触发点未确认 |
| cleanup.json | 自己的进程退出、记录消失、端口无监听、原MySQL保留和Editor干净状态 |

原生Client是隐藏的Direct3D11诊断进程，凭据来自私有验收配置，没有人工可见房间UI验收。公开报告只含状态和非秘密身份，不包含密码、Session、Ticket或实例密钥。诊断报告中的Done=false检查点表示测试中途取样；工具随后主动退出进程来验证断线，不能将它描述为探针全程自行退出0。

最终RoomProtocolEnvelope为41798；旧N0 ProtobufEnvelope与Mirror.ObjectDestroyMessage同为44385，仅保留旧Lab。本轮新增实际Handler类型与全部Mirror内置类型的ID检查，最终日志无RegisterHandler覆盖告警；未更改Mirror算法或原有四处Patch。

未通过/未实施：两条Built-in Resource启动错误、加密分配与远端Gate、Linux/public/IL2CPP/D3D12、权威角色/移动/战斗、正式Portal匹配。没有新GC测量。下一步N2-2B先限制Ticket申请/历史增长，再连接F门自动组房、成员准备、取消与认证后转场。
