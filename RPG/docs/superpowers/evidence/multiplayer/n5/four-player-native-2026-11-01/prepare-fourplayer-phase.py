"""只准备本轮隔离验收与证据；不修改Runtime、协议、正式资源或已有阶段记录。"""
from pathlib import Path
import hashlib,json,datetime,subprocess,psutil
task=Path(__file__).parent
repo=Path(r'F:\Develop Project\RPG');unity=repo/'RPG'
e=unity/'docs/superpowers/evidence/multiplayer/n5/four-player-native-2026-11-01'
assert not e.exists(),'Preserve existing stage rather than repeat launch'
private=unity/'.tools/networking-checks/n5-four-player-20261101'
assert not private.exists(),'Private stage already exists; inspect first'
e.mkdir();private.mkdir()
plan='''# N5 四认证连接持续运行小段（2026-11-01，执行中）

授权：用户要求接续N5；N5原计划第19、66–81、92–96行约束四人、持续投放、分项性能与故障证据。本段沿用已成功构建的v4菜单Client和Dedicated Server，不改Runtime/协议/游戏参数/128Key，不重建或重跑整套测试。

步骤：先用当前机器基线、串行启动和内存检查限制负载；新Test Schema账号四名，保留正式初始有限火球资产，预置隔离私有房间；真实键鼠登录/Hub门/Ready/Start。四端认证后取得各端身份、远端计数、DS逐连接与材料安装读数；依次实际施法/移动，观察持续运行与材料变化。已覆盖双人完整路径，不重复两图全流程。

只使用既有MovementAcceptanceProbe的measure及fault-100/fault-200/fault-0。该故障入口仅覆盖移动应用消息，不能证明世界可靠通道丢包/HOL。Profiler正校准/240帧报告与OS进程内存分别留档，诊断IO开销和同机四窗口GPU争用必须说明；未校准不报0GC，配置预算不伪装成最终性能通过线。

持续施法限制：真实OS一次只能聚焦一端，不把依次施法说成四人同时持续投放。任何未具备的压力/全网丢包/大Baseline并发、四人Activity并集或有效Native性能门槛保留未测。若可用内存不足3GB或机器明显卡顿，停止扩充，正常关闭本任务窗口并报告实际达到规模，绝不改画质/模拟或容量凑结果。

期间默认关闭所有材料写入/恢复注入fixture，使用真实火球，不动容器/炼金/真实存档。结束只清理本任务PID/启动时间/路径匹配的窗口、DS、业务及认证SHUTDOWN私有MySQL，保留用户Editor。

容器线上投影方案仅由独立只读任务准备，来源已确认；共享竞争和队友转交仍待用户回复。N5Complete=false。
'''
(e/'stage-plan.md').write_text(plan,encoding='utf-8')
src=(task/'Invoke-N5PlayerNativeNov1.ps1').read_text(encoding='utf-8-sig')
src=src.replace("[ValidateSet('a','b')]","[ValidateSet('a','b','c','d')]")
src=src.replace("@('a','b')","@('a','b','c','d')")
src=src.replace('n5-native-player-reconnect-20261101','n5-four-player-20261101')
src=src.replace('native-player-reconnect-2026-10-31/resume-2026-11-01','four-player-native-2026-11-01')
old=" $room=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/members') @{} $map.Users.b"
assert old in src
src=src.replace(old," foreach($role in @('b','c','d')){$room=Api 'Post' ('/api/online/rooms/'+$room.RoomId+'/members') @{} $map.Users[$role]}")
(task/'Invoke-N5FourPlayerNative.ps1').write_text(src,encoding='utf-8')
src=(task/'native-os-input.py').read_text(encoding='utf-8-sig').replace("choices=['a','b']","choices=['a','b','c','d']")
(task/'native-four-input.py').write_text(src,encoding='utf-8')
files=[repo/'AGENTS.md',unity/'AGENTS.md',*sorted((unity/'ProjectSettings').glob('*.asset'))]
files += [*sorted((unity/'Assets/_Project/Scenes/SinglePlayer').glob('*.unity')),*sorted((unity/'Assets/_Project/Resources/Online').rglob('*.*'))]
files += list((unity/'Assets/_Project/Scripts').rglob('*.cs'))+list((unity/'Assets/_Project/Tests').rglob('*.cs'))
records={str(p.relative_to(repo)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in files if p.is_file()}
mem=psutil.virtual_memory();cpu=psutil.cpu_percent(interval=1)
baseline=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),CpuPercent=cpu,AvailableGB=mem.available/2**30,TotalGB=mem.total/2**30,Head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip(),FileHashes=records)
(e/'before.json').write_text(json.dumps(baseline,indent=2),encoding='utf-8')
status=subprocess.check_output(['git','status','--short'],cwd=repo,text=True,encoding='utf-8');(e/'git-status-before.txt').write_text(status,encoding='utf-8')
print(json.dumps(dict(Stage=str(e),CpuPercent=cpu,AvailableGB=baseline['AvailableGB'],ProtectedFiles=len(records),NewRuntimeChanges=False)))
