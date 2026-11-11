# 下一最小观测段：关联版本的默认关闭计时（原始方案与后续执行索引）

2026-11-01 07:18最新：后续已完成Replica兴趣最小修复及双端真实Seed→Upload→Draw CPU观测，详见[修复阶段](instrumented-2026-11-01/presentation-interest-fix/README.md)。下方05:49阻塞及原计划保留历史，不代表当前仍等待Editor关闭。GPU/屏幕及原四端P7完整归因尚未测。

2026-11-01 05:49执行更新：下述Core/网络/安装/Renderer计时已实现，457/0回归、9/0 PlayMode、1/0方法校准以及匹配Client/DS构建已完成。Owner之前的命中/接受前段尚无计时。首次双端在启动计时窗前触发低内存守卫；用户允许检查干净后临时退出Editor，但安全检查发现持久资源dirty，未关闭、未重测。详见[instrumented阶段README](instrumented-2026-11-01/README.md)及summary.json。下文保留原计划，不能把计划点位当作已获得Native数据。容器首段04:28已批准，尚未开始实现。

目标是定位用户观察的Fire出现差异，不先改优先级、页/字节预算、渲染概率、模拟/显示参数、协议或128Key。当前Native副本/采样、独立源码审计与两端短对照没有确立单一根因。

1. 复用现有Core/Online、Multiplayer/World、ElementField/Replication及Rendering/ElementField职责目录，不建新模块。Development/Editor显式配置才启用固定容量struct事件ring；默认关闭不创建对象/不做轮询或JSON。诊断窗口内仅写值，结束稀疏导出；ring满有丢记录数，不能覆盖后声称完整。限定一目标Key/Cell与一次火球，记录实际观测开销。
2. 时钟用raw Stopwatch.GetTimestamp/Frequency/IsHighResolution，并在Windows启动时用直接QPC前后夹住Stopwatch作兼容校准；同一机器/同一frequency/同QPC基准确认后才跨进程求区间。未校准只做同进程耗时与中央采集offset上下界。Unity realtime的各自起点不能跨减；同时保留UTC只作检索。材料10Hz WorldTick、Gameplay30Hz ServerTick分栏，不能混算。
3. 共同stamp含WorldEpoch、connectionId/ConnectionEpoch、Chunk Domain/XYZ、SubscriptionEpoch、Generation/BaseRevision/Revision、SnapshotId/CRC/Words/PageIndex/Count；Owner提交与每连接冻结Snapshot分开。命中关联优先复用已有Actor/Cast/Projectile事实ID，无法取得则明确缺失，不为诊断扩正式消息。
4. DS在真实SurfaceImpact/沉积接受、材料应用/提交、选择冻结、实际首末页Send及Kind3/4接受处记事件，附token/轮次/pending/等待ACK类别、队列深度。Client在接收、身份/订阅/Codec/inbox满拒绝、Begin/收齐/真正Current交换、每个Resync触发分支记事件，附当前已装/候选stamp，保留原校验顺序和预算。
5. WorldReplicaStore仅补只读按Key Header观测或安装旁路，不能把GetChunkVersion的本地PresentationVersion冒充网络G/R。GpuFireParcelRenderer针对该已装版本/目标Cell记录读取、剔除/预算/概率结果、首次Seed/上传Dispatch/后续Draw的QPC与frame；仍不等于GPU完成或屏幕像素出现，不以CPU Draw伪称可见时刻。火球爆炸子事实VFX另列。
6. 先测默认关闭、ring容量/溢出与版本关联的真实边界，必要代码实质改动含中文学习注释；串行相关Edit/Play与N5回归、缓存复用的匹配Client/DS构建。最多1DS+2Client，外部守卫在下一输入前及时正常收束；记录实际视口/focus/FPS与其他负载，不改全局系统或用户资源参数、不再开四图形窗。一次短按火球，完整记录首个非零版本两端发送→安装→Seed/Draw，再按实际占比选择下一单一假设。

若明确是既有合同内缺陷，真实RED→最小修复→GREEN回归。若需要改变可见同步时机、优先级、随机种子表现、可靠流或架构，提出取舍/影响等待确认，不以诊断授权直接实施。容器新合同仍待审，不做炼金/N6。
