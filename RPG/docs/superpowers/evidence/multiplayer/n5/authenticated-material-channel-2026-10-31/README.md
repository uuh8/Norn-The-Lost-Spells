## 2026-10-31：认证材料数据通道第一可运行段

这次把现有独立复制组件接到正式 World 生命周期，而不是重建材料世界。DS 的唯一材料步后，全局 Source 最多10Hz捕获一次提交边界；CommitOwner 对原生Domain/Chunk形成独立冻结payload，四个真实认证连接各由既有 PeerState 持有一组传输。数据走现有 WorldEnvelope、WorldPageCodec 和应用版本3，未修改Schema或Reset。

### 文件职责与为什么这样拆

- `Multiplayer/World/WorldReplicationCoordinator.cs`：无Mirror直接API调用的状态装配，复用CommitOwner、PeerState和Assembler；Authority一次捕获，Client只能调用真实Installer。冻结页、Baseline/完整replacement Delta/tombstone、安装ACK、关联当前pending的Resync与超时都沿现有合同。构造分配工作区，正常页流程复用预热消息和固定数组。
- `Multiplayer/World/WorldReplicationNetworkChannel.cs`：真实连接与Mirror的薄适配。接收先核验认证对象引用、当前connectionId/ConnectionEpoch、Run/Dungeon、WorldEpoch和GameplayHash；Client复制到4页固定收件箱，不保留借用ArraySegment。每轮最多消费2页，完成Assembler校验和实际Store原子安装才允许ACK。
- `Movement/OnlineWorldBootstrap.cs`、`AuthorityTickDriver.cs`：Coordinator跟随World，Manager变化只重绑适配器；停止时先断通道并Stop Coordinator，再Detach。发送在既有Combat/进度/运动Pump之后。首个合法输入的SimulationReady是首次材料发送屏障，防止WorldHello同批之后Replica尚未激活就收到材料页。
- 两个既有Native诊断报告增加逐Chunk值副本：Domain/XYZ/WorldEpoch/Generation/Revision/Checksum/WordCount/Resident，另记发送、安装、ACK及故障状态。JSON和诊断数组分配只用于显式稀疏QA，不作为零GC验收。
- `ElementField/Diagnostics/OnlineMaterialReplicationProbe.cs`：仅Development Dedicated Server、非Editor且指定专用环境路径时启用。沿既有ElementWriteRequest接受512GMU水，运行五个真实材料Tick后使用既有Debug暂停，形成可比较的稳定提交边界。Client不会创建该夹具；没有生产默认赠品、资产、关卡或玩家投放规则变更。

所有新增文件放入既有World或Diagnostics职责目录，没有新增模块、反向依赖、同类重复文件夹或手写.meta。正式P7/P8资源保留，已有ReplicaStore只读表现继续复用。

### 身份、版本与预算边界

真实当前连接先于ACK/Resync预算；ACK必须匹配实际pending的Key/Generation/Revision/SnapshotId/Checksum，不能用高ID或未完整发送的候选清空传输。WorldEpoch在构造固定，同世界更高ConnectionEpoch只取消旧候选，已安装Store及版本栅栏保留。完整payload的CRC、数量/Geometry、版本依赖和真实Installer都成功后才能ACK；错误、缺页或容量失败保留旧数据。

本段先有界发送全部已提交Chunk，没有宣称空间Interest或缓存释放完成。初值为每World128个Key、每Chunk16384words，每连接一份冻结传输；Protobuf体900B、每页160个uint、每轮2页、18KB/s并保守预留framing成本。Client4页收件箱每轮消费2页；装配TTL15秒，重传沿原冻结身份。ACK和Resync各有限速，实际网络/socket/HOL和压力仍须后段测量。

Source/Commit捕获失败显式锁存复制故障并停止新复制，保留旧副本，不把失败冒充权威回滚。此分段不是完整生产恢复方案。动态四人Interest、evict与再进入、容量/压力、完整故障恢复、同进程自动重连、消耗后不补满和完整玩家路径继续保留未测。

### 已观察到的失败与修复

8项真实CommitOwner/Codec/Assembler/ReplicaStore闭环：缺Coordinator时0/8 RED，实现后8/0；覆盖乱序、多于四页、未完整不发布/不ACK、冻结期间Source变化、Delta精确BaseRevision、错误身份、高Snapshot不抢占、损坏Resync和重连。

4项真实NetworkClient入口：适配器缺失RED；测试夹具一度误读Mirror internal static connectState，产生API/编译错误，均单独保存而未算作行为RED/GREEN。按实际字段纠正后门禁4/0。新增停止测试复现12/1：StopWorld后旧Coordinator仍可处理延迟页；修复先Stop/解绑后，新增共13/0。同范围26 fixtures为293/0。

独立最终静态审查显式sol/xhigh，未发现确定阻塞级缺陷；它没有替代Native执行。作者最后补充Unity已销毁对象边界：4/1 RED复现CLR引用仍在时旧Channel重新注册handler，改用Unity的`!= null`存活判定后同范围26 fixtures最终294/0。中间Client/DS构建各0 error，但该判空修复改变源码Hash，因此重新刷新Hash/匹配构建，未把中间产物冒充最终验证。Native本轮只验证非零材料稳定基线，不扩展增量/压力。真实结果及剩余错误必须以本轮实际证据补充，不能写成N5完成。


### 本轮Native基线与交付结果

最终匹配Windows x64 Development Mono Client与真正Dedicated Server均Succeeded、0 error。通过隔离test Schema的真实账户/Ready/Ticket/DS流程，使用RoomLifecycleLab诊断入口装配正式P7；两台独立Native Client经真实KCP接收非零材料payload。显式夹具经五个真实材料Tick后使用既有Debug暂停，随后逐Chunk精确比对两端与DS的Domain/XYZ/WorldEpoch/Generation/Revision/Checksum/WordCount/Resident，全部一致，实际安装计数与DS收到的安装ACK为正。该检查拒绝只有空Geometry的“通过”。具体Chunk数、非空数、页数、ACK数和时间见 `native-material-consistency.json`。

这不是持续反应、Native增量、Interest或压力测试；没有测同进程自动重连、消耗后不补满、完整Hub玩家路径及性能。P8材料数据的Native路径本轮未测。DS既有两条Built-in Resource启动错误保留；本次它们未阻止初始化、实际材料传输/安装/ACK，不能因此称启动错误已解决或发布就绪。

最终同范围26 fixtures **294/0**，正式P7/P8 PlayMode **2/0**。一次构建辅助脚本因重复Parse已被JSON解析为UTC的DateTime而错误等待，确认尚未提交Native请求后仅停止自有辅助进程修正；未停止Unity或已运行构建。所有中间构建、失败/编译阻塞、RED/GREEN和最终产物分开保存。

12个保护文件及13个正式派生资源/Profile哈希保持；测试引起的Physics设置自动格式重写仅在核对起始哈希后恢复自身副作用。仅清理本轮PID/启动时间/路径匹配的Native/业务/私有数据库进程，保留用户Editor与Recovery内容；无提交/推送。本轮已确认炼金内容另按新授权仅写入现有总计划第7节，未实现炼金，也未改变N5调度。

Ruling: 先有界全量Chunk，动态Interest/evict后续接续；成本是该部分不能当成完成。Ruling: 捕获/提交失败显式停复制、保留旧副本，不能冒充权威回滚；成本是故障恢复仍待后段验证。Ruling: 显式Development暂停检查点用于精确比较；成本是不代表连续反应或压力。没有额外未披露的玩法/资产/协议裁定。

当前没有权限或待用户决策阻塞。本段到此收束；后续按已批准N5合同分段验证空间Interest/恢复与实际压力，未确认的新语义仍单独确认。N5Complete=false。
