"""准备旧匹配构建的两端短对照。只改外部验收脚本/文档，不改游戏或协议。"""
from pathlib import Path
import json,datetime,hashlib,subprocess,psutil
t=Path(__file__).parent;r=Path(r'F:\Develop Project\RPG');u=r/'RPG'
e=u/'docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01';p=u/'.tools/networking-checks/n5-fire-latency-20261101'
assert not e.exists() and not p.exists();e.mkdir();p.mkdir()
s=(t/'Invoke-N5PlayerNativeNov1.ps1').read_text(encoding='utf-8-sig').replace('n5-native-player-reconnect-20261101','n5-fire-latency-20261101').replace('native-player-reconnect-2026-10-31/resume-2026-11-01','fire-visible-latency-2026-11-01')
needle=' $map.Clients[$Client]=$record;$s.Processes+=@($record);Save $sessionPath $s;Save $mapPath $map'
assert needle in s
s=s.replace(needle,needle+"\n Save (Join-Path $stage 'process-ledger.json') @($s.Processes | Select-Object ProcessId,StartUnix,Executable,Kind,Stopped)")
(t/'Invoke-N5FireLatencyNative.ps1').write_text(s,encoding='utf-8')
# 对照已完成P8验收中的真实绕行路线。中间点只给OS方向选择，不设置Transform或DS位置。
s=(t/'native-os-input.py').read_text(encoding='utf-8-sig')
needle="        pos=state['HubActorPosition'];dx=target['x']-pos['x'];dz=target['z']-pos['z'];distance=math.hypot(dx,dz)"
if needle not in s:
    # 保持既有helper并由主控发送已核实绕行输入；不猜测改写语句。
    pass
(t/'native-fire-input.py').write_text(s,encoding='utf-8')
files={}
before=u/'docs/superpowers/evidence/multiplayer/n5/four-player-native-2026-11-01/before.json'
for n in json.loads(before.read_text(encoding='utf-8'))['FileHashes']:
    f=r/n
    if f.exists():files[n]=hashlib.sha256(f.read_bytes()).hexdigest()
(e/'before.json').write_text(json.dumps(dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),FileHashes=files,Head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=r,text=True).strip(),AvailableGiB=psutil.virtual_memory().available/2**30),indent=2),encoding='utf-8')
plan='''# 火元素跨端出现差异：低负载两端短对照（2026-11-01，执行中）

来源：用户03:47观察，尚未证明根因。先保存四端原始证据，再优先调查命中/权威生成、每连接复制/ACK/Resync、客户端安装、GpuFireParcelRenderer种子/绘制四段；不能把低内存当唯一根因，也不直接认定协议缺陷。

本段不构建/不改运行期，只复用同一v4正式菜单Client+DS。两新Test Schema账号，P8安全出生区，真实原菜单/Hub门/Ready/Start；一个玩家对地面一次短按火球，另一端观察。使用原250ms实际Store与1Hz角色/DS采样、OS负载及已有短Profiler入口（如可行），无恢复/网络故障注入，不做四端重开。外部守卫每0.5s检查<3GiB，若触线核对PID/启动时间/路径后只请求本轮窗口正常关闭，绝不强杀或动用户Editor。

这是CPU采样粗界对照，不具备精确命中→可见时延：没有旧数据能关联每页版本/安装完成与Fire种子/Draw，也不能把不同Unity起点Monotonic相减或把重复火格首次采到差当同一次延迟。若发现现象，只按实际复现范围报告；精确分段需后续默认关闭、固定容量的QPC关联观测，不改同步/渲染语义或优先级。

GpuFireParcelRenderer本地spawn interval、dt cap、相机剔除/距离/种子预算/概率、每连接预算与恢复、后台FPS/主线程停顿均是候选，不先改参数。真实物品/容器/炼金、死亡/撤离/N6不在本段。阶段结束正常关闭自有窗口并按原流程清理。
'''
(e/'stage-plan.md').write_text(plan,encoding='utf-8')
main=u/'docs/superpowers/plans/2026-10-31 Norn项目总计划（更新中）.md'
with main.open('a',encoding='utf-8') as f:f.write('\n\n## 25. 火元素跨端出现时间差异调查（2026-11-01 03:47用户观察，进行中）\n\n用户观察到火球命中地面后，四端Fire有时出现时间不同；当前未复现/未归因，不把四人入场扩大为可见同步时延通过。先用既有原始数据逐段核查，再做最多两端低负载短对照；不重开四图形窗口，不改同步语义/优先级/游戏参数。计划与证据在`docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01/`。共享抢拾/背包丢地转交确认及待审容器方案见第24节，本问题优先，炼金不实施。\n')
print(json.dumps(dict(Stage=str(e),ProtectedFiles=len(files),AvailableGiB=psutil.virtual_memory().available/2**30,RuntimeChange=False)))
