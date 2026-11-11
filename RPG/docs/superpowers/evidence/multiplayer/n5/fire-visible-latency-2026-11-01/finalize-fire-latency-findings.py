"""记录诊断事实与具体下一观测方案；没有修复或策略批准的暗示。"""
from pathlib import Path
import json,datetime,shutil,subprocess,psutil
r=Path(r'F:\Develop Project\RPG');u=r/'RPG';t=Path(__file__).parent;e=u/'docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01'
s=json.loads((e/'summary.json').read_text(encoding='utf-8'));assert s['FunctionalCpuObservationChecksPassed'] and s['Assertions']==20 and not s['RootCauseConfirmed']
review=t/'fire-low-load-review.md';assert review.exists(),'Wait for independent review rather than imply it completed'
shutil.copy2(review,e/review.name)
nextplan='''# 下一最小观测段：关联版本的默认关闭计时（已授权诊断范围，尚未实施）

目标是定位用户观察的Fire出现差异，不先改优先级、页/字节预算、渲染概率、模拟/显示参数、协议或128Key。当前Native副本/采样、独立源码审计与两端短对照没有确立单一根因。

1. 复用现有Core/Online、Multiplayer/World、ElementField/Replication及Rendering/ElementField职责目录，不建新模块。Development/Editor显式配置才启用固定容量struct事件ring；默认关闭不创建对象/不做轮询或JSON。诊断窗口内仅写值，结束稀疏导出；ring满有丢记录数，不能覆盖后声称完整。限定一目标Key/Cell与一次火球，记录实际观测开销。
2. 时钟用raw Stopwatch.GetTimestamp/Frequency/IsHighResolution，并在Windows启动时用直接QPC前后夹住Stopwatch作兼容校准；同一机器/同一frequency/同QPC基准确认后才跨进程求区间。未校准只做同进程耗时与中央采集offset上下界。Unity realtime的各自起点不能跨减；同时保留UTC只作检索。材料10Hz WorldTick、Gameplay30Hz ServerTick分栏，不能混算。
3. 共同stamp含WorldEpoch、connectionId/ConnectionEpoch、Chunk Domain/XYZ、SubscriptionEpoch、Generation/BaseRevision/Revision、SnapshotId/CRC/Words/PageIndex/Count；Owner提交与每连接冻结Snapshot分开。命中关联优先复用已有Actor/Cast/Projectile事实ID，无法取得则明确缺失，不为诊断扩正式消息。
4. DS在真实SurfaceImpact/沉积接受、材料应用/提交、选择冻结、实际首末页Send及Kind3/4接受处记事件，附token/轮次/pending/等待ACK类别、队列深度。Client在接收、身份/订阅/Codec/inbox满拒绝、Begin/收齐/真正Current交换、每个Resync触发分支记事件，附当前已装/候选stamp，保留原校验顺序和预算。
5. WorldReplicaStore仅补只读按Key Header观测或安装旁路，不能把GetChunkVersion的本地PresentationVersion冒充网络G/R。GpuFireParcelRenderer针对该已装版本/目标Cell记录读取、剔除/预算/概率结果、首次Seed/上传Dispatch/后续Draw的QPC与frame；仍不等于GPU完成或屏幕像素出现，不以CPU Draw伪称可见时刻。火球爆炸子事实VFX另列。
6. 先测默认关闭、ring容量/溢出与版本关联的真实边界，必要代码实质改动含中文学习注释；串行相关Edit/Play与N5回归、缓存复用的匹配Client/DS构建。最多1DS+2Client，外部守卫在下一输入前及时正常收束；记录实际视口/focus/FPS与其他负载，不改全局系统或用户资源参数、不再开四图形窗。一次短按火球，完整记录首个非零版本两端发送→安装→Seed/Draw，再按实际占比选择下一单一假设。

若明确是既有合同内缺陷，真实RED→最小修复→GREEN回归。若需要改变可见同步时机、优先级、随机种子表现、可靠流或架构，提出取舍/影响等待确认，不以诊断授权直接实施。容器新合同仍待审，不做炼金/N6。
'''
(e/'next-measurement-plan.md').write_text(nextplan,encoding='utf-8')
body='''### 火元素时延调查第一段结果（2026-11-01 04:00短对照后）

用户03:47的四端可见差异仍是有效待查问题，尚未精确复现或定位根因。本轮先收尾原四端与容器计划，再做两端短对照；没有重开四图形窗口，没有源码/协议/资源参数修改、重新构建或Unity测试重跑。原5/427/10及构建成功仍仅为先前阶段证据。

**新实际证据：** 两个新Test Schema账号通过真实菜单登录、Hub正常步行/F、原匹配/Ready/Start进同一P8。A20432/B32540，DS34648，World16694416829375328817。A在实际近地AimPoint经一次0.15秒OS短按，当前次数30→29，B自身30且远端A29。两端第一次CPU采到Fire均为材料Tick804、9格、总强度382，单格/量完整相同；前一个样本均无Fire。源UTC相距0.005966秒只是250ms采样的落点，不是精确网络/安装/显示延迟；输入到首样本约0.6秒还包含施法/飞行/权威反应等，不能当网络耗时。后续新鲜DS04:00:40报告确认每端61安装ACK、总122、Resync0、MaterialFault=false；after-shot时复制到的旧报告仍为0/0，已保留，不能用旧零值宣称当前无排队/故障。

短对照最低可用内存7.944GiB，外部0.5s低内存正常关闭守卫未触发；04:00:42.875975 UTC已正常关闭两窗口并清理自有DS/业务/认证SHUTDOWN私有MySQL，用户Editor保留。20项原始CPU/资源/ACK/保护检查通过；1365源码、设置与正式资源Hash一致，无提交推送。与四端P7不同，本轮是P8、单次火球、2人且已观察视口A1920×1080/B800×600（四端旧记录2560×1600），不是只改变内存的因果对照，不能据此认定机器瓶颈是唯一原因。四端旧CPU格可反应消失再出现，首采35秒等极差不能当同一火球延迟；原始分析和限制保留。

**源码分层事实：** DS SurfaceImpact→接受队列→材料10Hz应用/捕获→每连接预算/Key轮转/冻结/ACK→Client四槽inbox/每轮两页/完整安装→本地GPU Fire Seed/Draw。WorldPage Header.ServerTick实际是材料WorldTick，不能和30Hz移动/战斗Tick直接相减。Store.WorldTick又是所有已装Key的max水位，并非目标Key的时刻。四端0→60→清理68的Resync是通过真实当前连接/订阅/pending的Kind4，之后RequireBaseline；旧数据没有客户端触发分支，尚不能判定是基线缺口、校验/安装失败、超时或入口丢页等。

正式P7使用GpuFireParcelRenderer，P8也有GPU组件，不能以旧renderer推断正式链。实际Profile有0.08秒Spawn、0.0333秒视觉dt cap、每轮256种子、概率0.65、8192池，另有相机/距离筛选与本地轮转；源码支持“同Store但首次Seed/Draw不同”的可能，不等于本次根因已测得。RoomClient/Mirror设runInBackground=true，不能因ProjectSettings默认0就认定后台停摆。Replica普通弹体不自行碰撞沉积；DS火球爆炸子事实可经战斗通道独立播放，而材料请求在其后接受并于材料Tick应用。爆炸VFX出现不证明地面Fire已提交/安装，也不表示用户观察有误。

**下一已授权观测段：** 同一个World/Key/G/R/Snapshot/CRC关联DS提交→每连接实际发送→Client真正安装→首次Seed/Dispatch/Draw；默认关闭、固定struct ring与溢出计数，窗口内不做JSON/IO；经Stopwatch/QPC兼容校准后才跨进程比较，材料与Gameplay钟分栏。保留每一次Resync具体原因与inbox/pending/token状态。CPU Draw不当GPU完成或屏幕显示。具体点位/文件和验证见本段`next-measurement-plan.md`；尚未实施这些计时，不能写根因已找到或已修复。若需要改同步/优先级/可见表现策略，先提取舍等待确认。

证据：`docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01/summary.json`、原始low-load trace、before/after/final原档、OS守卫、分发/渲染和独立低负载审查。N5Complete=false；四人持续/性能/完整网络故障与该视觉时延Gate均未通过。容器两来源、共享抢拾与背包丢地转交已确认，真实背包/组分/意图收据新合同待审；本问题优先，炼金不实施。
'''
main=u/'docs/superpowers/plans/2026-10-31 Norn项目总计划（更新中）.md';text=main.read_text(encoding='utf-8-sig');assert '### 火元素时延调查第一段结果' not in text
lines=text.splitlines();i=next(i for i,x in enumerate(lines) if x.startswith('**2026-11-01 最新执行：**'));lines[i]='**2026-11-01 最新执行：** 两图真实路径/重连见第22节，四端最小认证证据及低内存缺口见第23节；容器来源/共享抢拾/背包丢地转交确认见第24节。现优先调查用户观察的Fire跨端出现差异：双端P8单次火球CPU首样本一致、20项事实检查通过，根因与可见延迟仍未验证，下一关联计时见第25节；N5Complete=false。'
main.write_text('\n'.join(lines)+'\n\n'+body,encoding='utf-8')
for q in [u/'docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md',u/'Assets/_Project/Docs/2026-10-29 共享材料世界N5开发解析（更新中）.md']:
    x=q.read_text(encoding='utf-8-sig');assert '### 火元素时延调查第一段结果' not in x;q.write_text(x+'\n\n'+body,encoding='utf-8')
(e/'README.md').write_text('# Fire跨端出现差异调查：根因未确认\n\n'+body+'\n\n[原始事实摘要](summary.json)｜[下一计时方案](next-measurement-plan.md)｜[独立审查](fire-low-load-review.md)\n',encoding='utf-8')
# 补充上一段Inspector最终返回，原10秒超时不抹掉；不再发请求。
prev=u/'docs/superpowers/evidence/multiplayer/n5/four-player-native-2026-11-01'
note='\n\n补充：上一只读Editor inspect请求在初始10秒等待后于03:46:24 UTC实际返回，状态为P7_MainMenu、Playing/Compiling/Dirty均false，请求已消费；见editor-after.json。保留最初超时事实，不再算执行阻塞。\n'
with (prev/'README.md').open('a',encoding='utf-8') as f:f.write(note)
shutil.copy2(t/'finalize-fire-latency-findings.py',e/'finalize-fire-latency-findings.py')
subprocess.run(['git','status','--short'],cwd=r,stdout=(e/'git-status-after.txt').open('w',encoding='utf-8'),check=True)
print(json.dumps(dict(UpdatedPlanSection=25,Assertions=20,RootCauseConfirmed=False,RuntimeChanged=False,EditorPidStillAlive=psutil.pid_exists(24836),Summary=str(e/'summary.json')),ensure_ascii=False))
