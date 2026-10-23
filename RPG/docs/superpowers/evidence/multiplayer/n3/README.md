# N3 本地验收（2026-10-23）

集中结果：[acceptance.json](acceptance.json)；清理：[cleanup.json](cleanup.json)；源文件/设置/存档保护：[source-audit.json](source-audit.json)。N3完成的是服务器权威运动、Prediction/Reconciliation、Replica Interpolation、多人AI和生命周期；合作战斗/材料/结算仍是N4/N5/N6。

| 实际检查 | 结果与证据 |
|---|---|
| Unity针对性EditMode | 60/0，`final-editmode`；包含Ready拒绝缺世界的RED/GREEN |
| 玩家路径PlayMode | 6/0，`final-player-flow`，真实业务准入、重连、原P7/P8单机往返与原Hub Session |
| 共同地图Physics | 3/0，`physics-green`，CC台阶/30°/55°及NavMesh绕墙 |
| 真实MySQL业务 | 58/0，`business/n3-business.trx`；Schema重生成校验4份C#一致 |
| 新构建 | `builds`，Dedicated Server与Main Menu Client均0 error，固定版本未升级 |
| 原生双端 | `native-final`：真实F/UGUI匹配、走/跳/Dash、坡面Y约2.60米在另一端一致、菜单不暂停、消息故障档位、回Hub后同Actor/Epoch1→2、队长退出后剩余者继续 |
| 真实业务中断 | `native-final/freeze-acceptance.json`：双方Frozen、按键不推进位置/Tick、同一DS恢复 |
| GC稳态 | `native-final/gc-steady.json`：Client校准240帧peak0B、120Tick/0事件；追击AI的Server有效120Tick/0事件；`server-profiler-steady`独立8秒调用栈0事件 |
| 清理/保护 | 5个精确登记身份退出；5080/13384与房间UDP无监听；开发库租约/活动端口0；系统MySQL仍Running；50保护文件/5单机档、版本锁及正式场景审计通过；Editor停在干净Hub |

保留失败与限制：`broad-regression`实际2193通过/42失败，隔离`triage`为44/5；没有用N2基线执行证明所有失败均既有。第一次最终Tick窗口仍有1个未归因事件，见`gc-first-warmup.json`，不宣称冷启动全零。`server-allocation-red`保留旧三参数Max的44B分配；`build-recovery`保留Editor Shader停滞，重启后用D3D11完成构建，未证明引擎根因。早期坡道按键路径被实体坡底阻挡/未到达的报告仍保留，最终从低端真实上坡才计为通过。普通敌人受击Animator状态名空警告仍在，留N4接伤害时处理。

以上仅为Windows IPv4 loopback/D3D11本机证据。两条原有native Built-in Resource启动错误、公网加密分配、Linux、IL2CPP、D3D12与完整干净Clone的Unity恢复都没有被本阶段清除。应用消息延迟/丢弃不等于UDP底层KCP丢包或公网RTT。截图、操作报告、单元测试和Profiler的范围分别记录，原始大型Profiler `.data`及私有凭据不上传。

具体学习原理、逐文件职责与GUI操作见Unity工程中的两份联机学习文档。下一步N4：施法请求与法杖/法术配置校验、冷却/法力、权威弹道与命中伤害、客户端表现事件。
