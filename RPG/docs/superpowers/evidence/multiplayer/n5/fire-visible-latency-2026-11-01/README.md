# Fire跨端出现差异调查：根因未确认

### 火元素时延调查第一段结果（2026-11-01 04:00短对照后）

用户03:47的四端可见差异仍是有效待查问题，尚未精确复现或定位根因。本轮先收尾原四端与容器计划，再做两端短对照；没有重开四图形窗口，没有源码/协议/资源参数修改、重新构建或Unity测试重跑。原5/427/10及构建成功仍仅为先前阶段证据。

**新实际证据：** 两个新Test Schema账号通过真实菜单登录、Hub正常步行/F、原匹配/Ready/Start进同一P8。A20432/B32540，DS34648，World16694416829375328817。A在实际近地AimPoint经一次0.15秒OS短按，当前次数30→29，B自身30且远端A29。两端第一次CPU采到Fire均为材料Tick804、9格、总强度382，单格/量完整相同；前一个样本均无Fire。源UTC相距0.005966秒只是250ms采样的落点，不是精确网络/安装/显示延迟；输入到首样本约0.6秒还包含施法/飞行/权威反应等，不能当网络耗时。后续新鲜DS04:00:40报告确认每端61安装ACK、总122、Resync0、MaterialFault=false；after-shot时复制到的旧报告仍为0/0，已保留，不能用旧零值宣称当前无排队/故障。

短对照最低可用内存7.944GiB，外部0.5s低内存正常关闭守卫未触发；04:00:42.875975 UTC已正常关闭两窗口并清理自有DS/业务/认证SHUTDOWN私有MySQL，用户Editor保留。20项原始CPU/资源/ACK/保护检查通过；1365源码、设置与正式资源Hash一致，无提交推送。与四端P7不同，本轮是P8、单次火球、2人且已观察视口A1920×1080/B800×600（四端旧记录2560×1600），不是只改变内存的因果对照，不能据此认定机器瓶颈是唯一原因。四端旧CPU格可反应消失再出现，首采35秒等极差不能当同一火球延迟；原始分析和限制保留。

**源码分层事实：** DS SurfaceImpact→接受队列→材料10Hz应用/捕获→每连接预算/Key轮转/冻结/ACK→Client四槽inbox/每轮两页/完整安装→本地GPU Fire Seed/Draw。WorldPage Header.ServerTick实际是材料WorldTick，不能和30Hz移动/战斗Tick直接相减。Store.WorldTick又是所有已装Key的max水位，并非目标Key的时刻。四端0→60→清理68的Resync是通过真实当前连接/订阅/pending的Kind4，之后RequireBaseline；旧数据没有客户端触发分支，尚不能判定是基线缺口、校验/安装失败、超时或入口丢页等。

正式P7使用GpuFireParcelRenderer，P8也有GPU组件，不能以旧renderer推断正式链。实际Profile有0.08秒Spawn、0.0333秒视觉dt cap、每轮256种子、概率0.65、8192池，另有相机/距离筛选与本地轮转；源码支持“同Store但首次Seed/Draw不同”的可能，不等于本次根因已测得。RoomClient/Mirror设runInBackground=true，不能因ProjectSettings默认0就认定后台停摆。Replica普通弹体不自行碰撞沉积；DS火球爆炸子事实可经战斗通道独立播放，而材料请求在其后接受并于材料Tick应用。爆炸VFX出现不证明地面Fire已提交/安装，也不表示用户观察有误。

**下一已授权观测段：** 同一个World/Key/G/R/Snapshot/CRC关联DS提交→每连接实际发送→Client真正安装→首次Seed/Dispatch/Draw；默认关闭、固定struct ring与溢出计数，窗口内不做JSON/IO；经Stopwatch/QPC兼容校准后才跨进程比较，材料与Gameplay钟分栏。保留每一次Resync具体原因与inbox/pending/token状态。CPU Draw不当GPU完成或屏幕显示。具体点位/文件和验证见本段`next-measurement-plan.md`；尚未实施这些计时，不能写根因已找到或已修复。若需要改同步/优先级/可见表现策略，先提取舍等待确认。

证据：`docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01/summary.json`、原始low-load trace、before/after/final原档、OS守卫、分发/渲染和独立低负载审查。N5Complete=false；四人持续/性能/完整网络故障与该视觉时延Gate均未通过。容器两来源、共享抢拾与背包丢地转交已确认，真实背包/组分/意图收据新合同待审；本问题优先，炼金不实施。


[原始事实摘要](summary.json)｜[下一计时方案](next-measurement-plan.md)｜[独立审查](fire-low-load-review.md)
