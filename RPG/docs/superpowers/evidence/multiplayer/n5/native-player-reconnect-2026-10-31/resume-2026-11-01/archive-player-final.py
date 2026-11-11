from pathlib import Path
import json,shutil,datetime,hashlib
u=Path(r'F:\Develop Project\RPG\RPG');private=u/'.tools/networking-checks/n5-native-player-reconnect-20261101';e=u/'docs/superpowers/evidence/multiplayer/n5/native-player-reconnect-2026-10-31/resume-2026-11-01'
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def dt(s):return datetime.datetime.fromisoformat(s.replace('Z','+00:00'))
def save(p,x):p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for dungeon in ('p7','p8'):
 out=e/dungeon/'native-final-report';out.mkdir(exist_ok=True);copies=[]
 for role in ('a','b'):
  for suffix in ('portal.json','movement.json','store.json','log'):
   name=role+('-'+suffix if suffix!='log' else '.log');p=private/dungeon/name
   if p.exists():shutil.copyfile(p,out/name);copies.append(dict(File=name,OriginalModifiedUtc=datetime.datetime.fromtimestamp(p.stat().st_mtime,datetime.timezone.utc).isoformat(),Sha256=hashlib.sha256(p.read_bytes()).hexdigest()))
 save(out/'provenance.json',dict(OriginalReportFilesRetainedAfterNormalProcessClose=True,Files=copies))
 trace=e/dungeon/'input-reconnect-trace.jsonl';rows=[dict(json.loads(line),Line=i) for i,line in enumerate(trace.read_text(encoding='utf-8-sig').splitlines(),1)]
 audit=[json.loads(line) for line in (e/(dungeon+'-os-input.jsonl')).read_text(encoding='utf-8-sig').splitlines()]
 first=next(x for x in audit if x['Action']=='mouse' and x.get('Result',{}).get('AttackSeconds'))
 base=e/dungeon/'trace-derived/pre-first-cast';base.mkdir(exist_ok=True);sources=[]
 for source in ('a-movement','b-movement','a-portal','b-portal','a-store','b-store'):
  candidates=[]
  for x in rows:
   stamp=x['Data'].get('RecordedAtUtc') or x['Data'].get('RecordedUtc')
   if x['Source']==source and stamp and dt(stamp)<dt(first['StartedUtc']):candidates.append(x)
  if not candidates:continue
  x=max(candidates,key=lambda x:dt(x['Data'].get('RecordedAtUtc') or x['Data']['RecordedUtc']));save(base/(source+'.json'),x['Data']);sources.append(dict(Source=source,TraceLine=x['Line'],ReadUtc=x['ReadUtc'],SourceRecordedUtc=x['Data'].get('RecordedAtUtc') or x['Data'].get('RecordedUtc')))
 save(base/'provenance.json',dict(Definition='Latest actual native report preceding first real OS attack',FirstActualAttack=first,NotOriginalBaselineCheckpoint=True,Sources=sources))
 for instance in (e/dungeon).glob('baseline/instance.json'):
  instance_id=read(instance)['InstanceId'];work=private/'instances'/instance_id
  for log in work.glob('*.log'):
   shutil.copyfile(log,e/dungeon/('ds-'+log.name))
 print(dungeon+' final original native reports and event-aligned pre-cast stream checkpoint archived.')
save(e/'checkpoint-copy-defect.json',dict(OriginalDefect='PowerShell comma-array operator precedence combined with unparenthesized role + suffix produced a/b destination names; later role copies overwrote earlier ones',OriginalBadDirectoriesPreserved=True,DsRoomInstanceSamplingCopiesValid=True,RoleEvidenceSource='Complete actual native raw JSONL streams + original last reports retained after normal process close',FutureHelperCorrectedOnly=True,OriginalMissingMovementFilesNotClaimedRecoveredAsStandaloneOriginals=True,RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat()))
