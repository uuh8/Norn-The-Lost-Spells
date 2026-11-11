# N5 Interest / 恢复接续：合同复核与独立缓存栅栏

2026-10-31。上段已完成认证材料有界全量通道，26 fixtures 294/0、稳定P7双Native非零基线。本段继续用户已批准N5，不将历史测试当本段结果。

## 已批准与待确认

N5实施计划第70–72行：四个有效Actor真实并集、Interest仅控发送、离开释放客户端缓存、重入获取当前Baseline。总联机方案第257–264行：角色操作/关键反馈/附近优先，全部环节有界预算，四人组合压力后验收。

未找到正式网络范围大小/形状/Chunk判交的批准记录。现有WorldInterestCoordinator支持半开AABB，但不生成Bounds；P7/P8原Profile为0.25m格×8=2m块，XZ10/Y3活动半径，这些源码/资源不能被自动升格为联机批准需求。WorldPage只有Baseline/Delta/ACK/Resync/权威删除，没有Interest退出/重入标识；SnapshotId来自CommitSequence，同版本重入可能重复，不能以其现有含义证明Interest轮次。正式位置驱动接线与Native出入区验证暂缓，已提出两项决策，沉默不视为同意。

## 可独立完成的小段

只在既有WorldReplicaStoreTests内新增13项实际Store行为测试：四域释放后旧代次/旧Revision仍拒绝；相同版本不同内容不能冒充合法重装；释放后读模型数量/可见Chunk清空且重复释放幂等；权威tombstone不能因Interest释放而复活。测试直接验证真实Store，不用mock或反射空方法，不选择生产范围/协议。观察RED，若暴露既有合同内缺口，仅最小修复WorldReplicaStore；定向GREEN后回归原26 fixtures。正式Native和完整玩家路径未测，不为无依赖收益启动服务/构建。

现有Store同版本幂等分支因!Interested被跳过，怀疑释放后会接受合法CRC但与旧版本冲突的内容；先行为复现再修，不以静态推测报已确认失败。预期新增13项中冲突内容四域可能RED，其他九项作为恢复边界核查。

## 目录、保护与交付

复用ElementField/Replication及Tests/ElementField，不建新源码目录、不改Shared/asmdef/场景/派生资源。主控串行协调Editor/测试；只读合同审计由sol/xhigh子Agent负责。保存初始保护Hash，结束核对12个保护文件/13个正式资源、工作区/Editor状态、文档链接。阶段账本为本证据目录progress.md，不提交/推送，不清理其他阶段证据与Recovery。维护现有N5计划、总计划及N5解析，清楚记录阻塞和实际验证。
