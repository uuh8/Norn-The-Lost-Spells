# N5 Native剩余恢复Gate：日志覆盖／多页／跨Generation

2026-10-31用户明确授权接续；沿用v4、已确认Interest/Generation与Journal合同，不新增玩法、协议、容量淘汰、重连或资产规则。前段423/0、Play2/0、Native两case属于历史；本段开始无新测试结果。参考N5计划N5-2～N5-6和项目总计划§16/§19。

1. 先修隔离探针P2。纯提取既有ACK状态记录分支，保留原generation>=old且revision>=old获得真实行为RED，再改既有Generation优先字典序；不得改变正式ACK/认证裁决。测试观察实际记录/状态而非只测数学比较。既有home/away P3留档，不隐瞒DS两资源错误。
2. 多页。复用ElementField现有OnlineMaterialReplicationProbe，在显式私有开关内用正式ElementWriteRequest种植两份Solid（候选8192GMU/radius0.9，最终规模以实际Source和原生Chunk确认）；不改Profile、场景或替换Source。仅非原点目标；每Key至少90cells即641words／5页，至少两个Key，不用单Key替代范围。真实Owner正常Capture/Codec/Mirror。A在末页前有限3秒发送暂停，保存候选完整Header/实际已送页和新鲜真实Store；完整最后页校验/原子安装才ACK。B正常。
3. 日志覆盖。A全量ACK且无pending后，仅隔离诊断在TryFreeze前有限45秒暂停A发送；真实数量Owner每0.25s对两个既有目标各减1，80轮约160条真实changed记录，使旧Base条目被128项ring覆盖。显式记录Owner实际追加计数和Peer在TryCopyDelta失败处的HistoryGapBaseline原因，不能把Capture/Tick/CommitSequence冒充日志条数，不能调用RequireBaseline伪造回退。自动截止、不无限等待/重试。恢复后两个目标均当前Base0多页Baseline、精确逐连接逐Key ACK和真实Current格对照。
4. 跨代。清空一份本fixture非原点Solid Key的真实resident/pending，跨正式Capture形成tombstone并验证ACK，然后同址正常重投形成Generation+1（第一版本1，最终版本以实际Capture为准）。同World/Connection/Subscription；缓存旧高Revision Generation页仅显式replay一次，验证StaleVersion/不回灌、旧控制不能启动恢复。正常业务/场景/资产不写，Probe只操作本fixture注入材料；未知玩法不自行填充。

本轮只增有界只读Freeze/History观测、扩展既有诊断文件和真实行为测试；复用Multiplayer/World、ElementField/Diagnostics与现有Tests，暂无新源文件夹/Shared变更。Source/Codec/Assembler/Store/Owner与单机参数不重建。公开材料Owner能否在所选几何稳定产生两大块必须实际验证，失败保留并调整隔离配置，不以计划冒充实现。

主控唯一协调Unity/测试/服务/构建/生成/Git；sol/xhigh协作明确所有权，Source完整冻结后回归及匹配Native构建。业务源码/Shared未变时不无故重复全局ExpireDue测试；Native使用既有Test Schema和全新诊断账户/私有工作目录，任何只读查询先限定13384/norn_online_test，不手删无关数据。12保护文件/15资源、用户Editor与全部无关工作区保留，自有进程三元身份核对后清理，不提交推送。

验证：P2及新闭环RED/GREEN、同范围27fixture与正式P7/P8必要Play回归、固定协议生成一致性；匹配Windows DevMono Client/真正DS。实际完成哪些Native场景就报告哪些，若物理fixture限制使范围过大，先交付最小可重复段，全部未测留档。完整键盘路径／四人持续／性能／P8／N4/N6／远端仍未完，N5Complete=false。阶段结束维护原项目总计划/N5解析/N5计划及证据。
