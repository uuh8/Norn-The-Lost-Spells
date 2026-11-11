from pathlib import Path
import json,hashlib,subprocess,datetime,shutil
r=Path(r'F:\Develop Project\RPG');e=r/'RPG/docs/superpowers/evidence/multiplayer/n5/native-recovery-2026-10-31';p=r/'RPG/ProjectSettings/ProjectSettings.asset'
records=json.loads((e/'preservation-before.json').read_text(encoding='utf-8-sig'))
expected=next(x['Hash'] for x in records if x['Path'].endswith('ProjectSettings.asset'))
data=p.read_bytes();initial=subprocess.check_output(['git','show','HEAD:RPG/ProjectSettings/ProjectSettings.asset'],cwd=r)
sha=lambda x:hashlib.sha256(x).hexdigest().upper()
assert sha(initial)==expected,'Initial state differs from HEAD; preserve unknown changes'
assert data.count(b'  runInBackground: 1\n')==1
candidate=data.replace(b'  runInBackground: 1\n',b'  runInBackground: 0\n')
assert sha(candidate)==expected and candidate==initial,'More than the isolated background flag changed'
observed_time=datetime.datetime.fromtimestamp(p.stat().st_mtime,datetime.timezone.utc).isoformat()
server=json.loads((e/'server-build.json').read_text(encoding='utf-8-sig'))
observed=datetime.datetime.fromisoformat(observed_time);built=datetime.datetime.fromisoformat(server['RecordedAtUtc'].replace('Z','+00:00'))
assert abs((observed-built).total_seconds())<2,'Protected setting write not tied to this owned build completion'
shutil.copyfile(e/'preservation-after.json',e/'preservation-before-background-restoration.json')
(e/'projectsettings-background-observed.diff').write_bytes(subprocess.check_output(['git','diff','--','RPG/ProjectSettings/ProjectSettings.asset'],cwd=r))
p.write_bytes(candidate)
record=dict(OnlySingleBackgroundFlagRestored=True,OriginalBackground=0,ObservedBackground=1,InitialHash=expected,ObservedHash=sha(data),RestoredHash=sha(p.read_bytes()),ObservedWriteUtc=observed_time,OwnServerBuildCompletedUtc=server['RecordedAtUtc'],Guard='Current bytes with this single flag changed exactly equal both stage initial hash and HEAD; write within two seconds of owned DS build completion',WriterNote='Observed Unity build serialization; Mirror also sets Application.runInBackground in StartServer/StartClient; no broader settings recovery applied',RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
(e/'projectsettings-background-restoration.json').write_text(json.dumps(record,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print('Restored only runInBackground 1->0; exact stage initial and HEAD hash verified.')
