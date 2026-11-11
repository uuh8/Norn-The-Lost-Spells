"""只恢复已证两项自动副作用；Quality来源未明，原样保留。主控在验证收尾运行。"""
from pathlib import Path
import json,hashlib,subprocess,datetime
r=Path(r'F:\Develop Project\RPG');u=r/'RPG';stage=u/'docs/superpowers/evidence/multiplayer/n5/native-player-reconnect-2026-10-31';out=stage/'resume-2026-11-01'
def sha(b):return hashlib.sha256(b).hexdigest().upper()
before={Path(x['Path']).name:x['Hash'] for x in json.loads((stage/'preservation-before.json').read_text(encoding='utf-8-sig'))}
observed={'DynamicsManager.asset':'53CF96C365B85EEFE78B3CDA76AF21F68246744277AE6CA21FFD6A67BDB72A1C','ProjectSettings.asset':'83106A5F1E856A5C87B7A9AD73BCF6C30ACFF034EBADC1E5814FCD3C85F3CA45'}
rows=[]
for name,known in observed.items():
 p=u/'ProjectSettings'/name;data=p.read_bytes();baseline=subprocess.check_output(['git','show','HEAD:RPG/ProjectSettings/'+name],cwd=r)
 assert sha(baseline)==before[name], 'HEAD no longer equals owned stage start'
 if sha(data)==before[name]:rows.append(dict(Path=str(p),AlreadyOriginal=True,Hash=sha(data)));continue
 assert sha(data)==known,'Unknown settings bytes: do not restore '+name
 if name=='ProjectSettings.asset':
  assert data.count(b'  runInBackground: 1\n')==1
  candidate=data.replace(b'  runInBackground: 1\n',b'  runInBackground: 0\n')
  assert candidate==baseline
 else:
  historical=u/'docs/superpowers/evidence/multiplayer/n5/native-delta-2026-10-31/physics-reserialization-observed.asset'
  assert historical.exists() and historical.read_bytes()==data,'Require previously preserved exact same automatic physics bytes'
  candidate=baseline
 (out/(name+'.observed-before-restoration')).write_bytes(data)
 assert sha(p.read_bytes())==known,'Concurrent file modification; stop'
 p.write_bytes(candidate)
 rows.append(dict(Path=str(p),Observed=known,Restored=sha(candidate),Initial=before[name],KnownSideEffectOnly=True))
quality=u/'ProjectSettings/QualitySettings.asset'
rows.append(dict(Path=str(quality),PreservedWithoutRestoration=True,Hash=sha(quality.read_bytes()),WriterUnproven=True))
(out/'settings-restoration.json').write_text(json.dumps(dict(Rows=rows,RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat()),ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Exact known settings guards applied; Quality untouched.')
