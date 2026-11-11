"""独立只读采集本轮自有DS/Native稀疏报告；不写请求或改变世界。"""
from pathlib import Path
import argparse,json,time,datetime,hashlib
p=argparse.ArgumentParser();p.add_argument('--session',required=True);p.add_argument('--dungeon',choices=['p7','p8'],required=True);p.add_argument('--out',required=True);p.add_argument('--seconds',type=int,default=120);a=p.parse_args()
assert 1<=a.seconds<=180
s=json.loads(Path(a.session).read_text(encoding='utf-8-sig'));private=Path(a.session).parent;out=Path(a.out);assert not out.exists(),'Preserve previous trace';out.parent.mkdir(exist_ok=True)
seen={};end=time.monotonic()+a.seconds
with out.open('x',encoding='utf-8') as stream:
    while time.monotonic()<end and not out.with_suffix('.stop').exists():
        paths={}
        for role,record in s['Maps'][a.dungeon]['Clients'].items():
            cfg=json.loads(Path(record['Config']).read_text(encoding='utf-8-sig'))
            paths[role+'-portal']=Path(cfg['Output']);paths[role+'-movement']=Path(cfg['MovementOutput']);paths[role+'-store']=Path(cfg['Output']).parent/(role+'-store.json')
        for state in (private/'instances').glob('*/state.json'):paths['ds-'+state.parent.name]=state
        for name,path in paths.items():
            try:
                raw=path.read_bytes();digest=hashlib.sha256(raw).hexdigest()
                if seen.get(name)==digest:continue
                data=json.loads(raw.decode('utf-8-sig'));seen[name]=digest
            except (FileNotFoundError,json.JSONDecodeError,UnicodeDecodeError,PermissionError):continue
            stream.write(json.dumps(dict(ReadUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Source=name,Data=data),ensure_ascii=False)+'\n');stream.flush()
        time.sleep(.05)
print(json.dumps(dict(Trace=str(out),DistinctFiles=len(seen),Bytes=out.stat().st_size,OnlyRead=True)))
