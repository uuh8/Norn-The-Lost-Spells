# Fire分段计时：Native复制测量完成，Seed/Draw被表现绑定缺口阻止

**2026-11-01 07:18后续更新：** 上述缺口已完成最小修复并通过16/0、10/0、474/0和匹配构建，双端真实Seed/Upload/Draw CPU链已取得，247项离线检查通过。最新见[修复阶段记录](presentation-interest-fix/README.md)。下文保留修前调查，不再作为“修复未实施”的当前状态；GPU/屏幕呈现及四端P7完整归因仍未完成。

最新检查点：2026-11-01 06:14 UTC。机器同机Windows，协议v4，N5Complete=false，无提交或推送。下方05:49阻塞是保留历史，最新结果见本节。

## 最新真实结果

67条dirty完成分类：28个Shader源无磁盘变化、32个Package和3个内置路径，四项ProjectSettings磁盘Hash与前基线一致。05:58新报告四项设置不dirty后，按用户授权正常退出Editor，06:00确认；没有保存全部/丢弃/强杀。见[只读Physics与dirty核查](physics-dirty-readonly-findings.md)和`native-2/editor-normal-close.json`。被拒Physics恢复未重试，与正常退出独立。

native-2菜单报告20秒未更新，已保留失败；native-3只提前聚焦精确自有窗口、再等Update报告，恢复正常真实键鼠入场。匹配构建未重跑。DS/A/B378/203/203事件均Completed/0溢出；34个权威版本各有两端完整安装及真实逐连接ACK，0 PageRejected/Resync，155项离线关联检查通过。三进程QPC/SW10MHz并双端夹持校准，跨进程区间最大宽0.0161ms。详见`native-3/timing-analysis.json`、三个原始`*-timing.json`和可复查分析脚本。

| 分段 | A中位 / P95 ms | B中位 / P95 ms |
|---|---|---|
| 提交→Mirror Send返回 | 11.530 / 16.401 | 11.556 / 16.413 |
| Send返回→Client接收 | 11.659 / 12.326 | 13.786 / 14.541 |
| 接收→Current安装 | 0.631 / 1.102 | 0.590 / 1.192 |
| 提交→安装 | 27.745 / 40.915 | 29.710 / 42.425 |

B同版本安装比A晚约1.505–2.628ms；统计中位/P95以每个误差区间中点计算，完整上下界留JSON。各段分位不可直接相加。A1920×1080前台/B800×600后台，各480帧，原始单调Update采样间隔P50约16.672/16.692ms、P99为19.598/17.634ms、峰24.317/25.689ms；Unity unscaledDeltaTime另存，不混成GPU呈现时间。全case最低可用14.67GiB，未触发低内存。不是同画幅A/B或四人/公网性能结论。

**尚未取得目标Read/Seed/Upload/Draw**：两端各32次RendererRejected Reason1。当前Prefab两层兴趣引用均空，Replica表现距离中心回落world Transform，目标块到它的最近距离75.073m大于Profile56+8+3=67m，在frustum之前被拒绝。原点严格相消，不必假设其为0；实际Interest/planes未直接记录。这是明确静态装配缺口，与本次拒绝吻合；不能当用户四端P7所有延迟的唯一归因。见[独立只读审计](fire-native-render-rejection-audit.md)。最小修复方向是复用现有Core Actor/Camera绑定，仅Replica表现兴趣跟随本地Actor；当前未实施，需TDD/生命周期/匹配Native再验。

06:14全部自有窗口/DS/业务/守卫退出、私有13384停止、Editor按授权保持关闭，约19.78GiB可用；系统MySQL不动。DS仍2条Built-in Resource错误。当前已测到安装，没有完成GPU/屏幕呈现、命中至Owner前段或Native计时启用/关闭CPU开销A/B。容器首切片仅只读设计，未交叉编码。

## 实际交付

现有Core/Online新增有界struct前缀记录器及Stopwatch/QPC区间校准；在Owner提交、逐连接冻结/预算/Send返回、Client接收/拒绝/Resync/Current安装、GPU Fire读取/Seed/Dispatch/Draw CPU提交处观察同一World/Key/G/R。已有诊断入口显式配置才启用，一次窗口8秒/8192条；默认关闭，窗口内不做诊断JSON，卸载后导出。没有修改协议语义、发送预算、概率、筛选、视觉参数、128Key或单机玩法。`DrawSubmitted`只表示CPU调用返回，`PageSent`只表示Mirror Send返回，分别不证明GPU呈现或网卡上实际发包。

主要文件：Core/Online/WorldReplicationTimingTrace.cs、WorldReplicationCommitOwner.cs；ElementField/Replication/WorldReplicaStore.cs、Diagnostics/OnlineMaterialReplicationProbe.cs；Multiplayer/World/WorldReplicationCoordinator.cs、WorldReplicationNetworkChannel.cs、WorldReplicationPeerState.cs及Diagnostics/OnlineMovementAcceptanceProbe.cs；Rendering/ElementField/GpuFireParcelRenderer.cs。测试沿Core、ElementField、Multiplayer、Rendering、RunPlayMode已有目录。Editor/Networking/OnlineEditorRequests.cs另补只读安全检查与一次未引用内存回收；不新建Runtime模块或重复目录。

## 本轮测试与构建

| 证据 | 实际结果 |
|---|---|
| core-and-header-red / green | 0通过/13失败 → 13通过/0失败 |
| behavior-hooks-red | 5通过/13失败（包括原入口通过），失败保留 |
| probe-lifecycle-red | 0通过/4失败 |
| timing-green-report.json | 31通过/0失败 |
| n5-and-rendering-regression-report.json | 457通过/0失败，04:44:49完成 |
| review-fix-lifecycle-report.json | 9通过/0失败，04:52:50完成 |
| timing-record-allocation-report.json | 1通过/0失败；正校准后预热Record方法0 GC事件，仅方法范围 |
| client-first-hash-preflight-* | 首次构建Failed/2 error，Hash不一致；保留失败 |
| hash-command.json | 按官方流程刷新GameplayHash通过，未跳过门禁 |
| client-build.json | 完整菜单Win64 Development Mono Client成功/0 error，05:12:31 |
| server-build.json | 真Unity Dedicated Server成功/0 error，05:20:00 |

首次新测试曾编译Blocked，旧XML误拷已标明来源而不当新运行。独立代码审查唯一P2（挂载后UTC字符串分配）已移到挂载前并回归。方法GC结果不代表Native全帧零分配；业务/Shared未修改，没有以业务Release构建代替Unity Native。

## Native首次真实停测

`native-1`实际双端登录、Hub入场、准备/开始；05:22:33.442188真实可用内存3119775744B触发3GiB守卫，两端05:22:35.813328前正常退出，无强杀；最低记录约2.750GiB。计时尚未开始，A/B报告Interrupted、EventCount=0，无法给出分段时延或解释用户观察。守卫此前已用两个隐藏自有小窗验证正常关闭目标、保留非目标。

外部runner最初在Ready后才登记DS，本次提前停测遗漏DS，05:22 cleanup.Errors=[]不证明当时所有进程退出。旧DS之后Frozen并在05:27前自行退出；已核对不再存活。runner已补开始后及finally的精确身份登记。保留该失败，不将其归为材料协议bug。

## 当前具体阻塞

用户05:41允许确认无未保存场景/资源后临时正常退出Editor，发现未保存内容须停下。05:47:57 `inspect-safety`报告场景P7_MainMenu干净、非Play/编译/更新/测试/构建，但Shader、内置资源和ProjectSettings持久对象有dirty标记。不能仅凭这些标记认定用户修改，也未证明可以丢弃；未调用保存、清dirty、正常关闭或强杀。完整清单见`native-2/editor-before-normal-close.json`；`editor-close-blocked.json`明确尚未发关闭请求。

一次未引用内存回收已结束，Editor约4.9GiB，当前可用约11GiB。未反复GC、未关闭用户程序、未启动第二案窗口。`current-environment.json`记录最终核查时刻和进程；`native-2/preparation.json`仅为准备。源码、测试、场景、设置和正式资源保护检查见`protection-after-timing.json`，工作区保留见`git-status-checkpoint.txt`。

1365个原基线文件中1350个字节Hash不变、无缺失；15个差异为本轮8个既有Runtime、4个既有测试、2个官方GameplayHash生成资源及1个Physics序列化差异，新增Core脚本/测试及meta另存于工作区。Physics当前SHA256 `53cf96c3…72a1c`与既存自动重序列化留档逐字节一致；尝试添加并执行精确Hash门禁恢复步骤被自动审批拒绝（认为可能回滚用户/工具设置，当前退出许可不涵盖设置恢复）。被拒补丁未落盘，恢复未执行，未换途径重试。差异原样保留，见`settings-restoration-denied.json`；恢复该差异需明确相关授权。当前只读证据不解除Editor内存dirty阻塞。

05:53:47再次核查本轮已知业务、DS、A/B及守卫PID均不存在；系统原有MySQL 8.0.33两进程未动，私有13384监听已停止。可用内存约11.88GiB；用户Editor保留。受影响已跟踪源码`git diff --check`通过，全仓库检查仍报告先前P7/P8序列化Prefab尾空白，本段未清理它们。五份文档/证据索引检查通过。

下一步：用户处理/确认Editor资源状态后重新获取新鲜安全报告，正常退出且确认内存释放，再运行已准备的native-2，复用现有匹配Client/DS，无需重建。仍保留3GiB正常关闭守卫，仅双端短测。取得三进程QPC校准区间、同版本逐连接发送/安装/Seed/Draw数据后再定位原因；命中/入队至Owner前段、GPU完成和屏幕呈现仍未测。容器首段04:28实施已授权，本段未交叉编码；G/K/法术供料、死亡/撤离/N6及炼金未实施。
