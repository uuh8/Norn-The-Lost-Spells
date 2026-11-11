"""有界只读OS采样；仅写本轮证据，不改变优先级/画质/游戏或网络。"""
from pathlib import Path
import psutil,time,json,datetime
repo=Path(r'F:\Develop Project\RPG');u=repo/'RPG'
private=u/'.tools/networking-checks/n5-four-player-20261101'
e=u/'docs/superpowers/evidence/multiplayer/n5/four-player-native-2026-11-01'
out=e/'os-sampling.jsonl';assert not out.exists()
procs={};end=time.monotonic()+240
with out.open('x',encoding='utf-8') as f:
    while time.monotonic()<end and not (e/'os-sampling.stop').exists():
        s=json.loads((private/'session.private.json').read_text(encoding='utf-8-sig'))
        pids={r['ProcessId'] for r in s['Processes'] if not r.get('Stopped')}
        records=[]
        for pid in pids:
            try:
                if pid not in procs:procs[pid]=psutil.Process(pid);procs[pid].cpu_percent()
                p=procs[pid];m=p.memory_info();records.append(dict(Pid=pid,Name=p.name(),CpuPercent=p.cpu_percent(),Rss=m.rss,Private=getattr(m,'private',None),Threads=p.num_threads()))
            except (psutil.NoSuchProcess,psutil.AccessDenied):pass
        m=psutil.virtual_memory()
        f.write(json.dumps(dict(Utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),CpuPercent=psutil.cpu_percent(),AvailableBytes=m.available,Processes=records))+'\n');f.flush();time.sleep(1)
print(json.dumps(dict(OnlyRead=True,Trace=str(out),Bytes=out.stat().st_size)))
