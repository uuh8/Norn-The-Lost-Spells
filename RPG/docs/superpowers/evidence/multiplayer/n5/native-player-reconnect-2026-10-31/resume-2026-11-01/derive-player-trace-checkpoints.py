from pathlib import Path
import json,datetime,hashlib
root=Path(r'F:\Develop Project\RPG\RPG\docs\superpowers\evidence\multiplayer\n5\native-player-reconnect-2026-10-31\resume-2026-11-01')
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def dt(s):return datetime.datetime.fromisoformat(s.replace('Z','+00:00'))
def save(p,x):p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
for dungeon in ('p7','p8'):
 trace=root/dungeon/'input-reconnect-trace.jsonl';records=[]
 for line_no,line in enumerate(trace.read_text(encoding='utf-8-sig').splitlines(),1):
  x=json.loads(line);x['Line']=line_no;records.append(x)
 derived=root/dungeon/'trace-derived';derived.mkdir(exist_ok=True);provenance=[]
 totals={}
 for x in records:
  d=x['Data'];v=totals.setdefault(x['Source'],dict(Count=0,MaxLegacy=0,MaxCells=0));v['Count']+=1;v['MaxLegacy']=max(v['MaxLegacy'],d.get('Legacy',0));v['MaxCells']=max(v['MaxCells'],d.get('CellCount',0))
 for label in ('baseline','consumed','disconnected','reconnected','post-reconnect-cast','returned-hub-buddy-running'):
  case=root/dungeon/label;capture=read(case/'sampling.json')['RecordedUtc'];out=derived/label;out.mkdir(exist_ok=True)
  for role in ('a','b'):
   for kind in ('portal','movement','store'):
    source=role+'-'+kind;candidates=[]
    for x in records:
     if x['Source']!=source:continue
     stamp=x['Data'].get('RecordedAtUtc') or x['Data'].get('RecordedUtc')
     if stamp and dt(stamp)<=dt(capture):candidates.append(x)
    if not candidates:
     provenance.append(dict(Case=label,Source=source,MissingBeforeCheckpoint=True));continue
    best=max(candidates,key=lambda x:dt(x['Data'].get('RecordedAtUtc') or x['Data']['RecordedUtc']))
    stamp=best['Data'].get('RecordedAtUtc') or best['Data']['RecordedUtc'];age=(dt(capture)-dt(stamp)).total_seconds()
    save(out/(source+'.json'),best['Data'])
    provenance.append(dict(Case=label,Source=source,TraceLine=best['Line'],SourceRecordedUtc=stamp,TraceReadUtc=best['ReadUtc'],CheckpointRecordedUtc=capture,SourceAgeSeconds=age,ValuesCopiedFromActualRawTrace=True))
  a=read(out/'a-movement.json') if (out/'a-movement.json').exists() else {};b=read(out/'b-movement.json') if (out/'b-movement.json').exists() else {};ds=read(case/'ds.json');p=read(out/'a-portal.json') if (out/'a-portal.json').exists() else {}
  auth=[(x['ActorId'],x['ConnectionEpoch'],x['Cards'][0]['RemainingCharges']) for x in (ds.get('ActorInventories') or []) if x and x.get('Cards')]
  print(json.dumps(dict(Map=dungeon,Case=label,AEpoch=a.get('Epoch'),ACharges=a.get('ActorCharges'),BCharges=b.get('ActorCharges'),APhase=p.get('Phase'),AScene=p.get('Scene'),DsAccepted=ds['Accepted'],DsUtc=ds['RecordedAtUtc'],DsInventory=auth,DsAck0Epochs=sorted(set(x.get('ConnectionEpoch',0) for x in (ds.get('MaterialPeerAck0') or [])))),ensure_ascii=False))
 save(derived/'provenance.json',dict(Trace=str(trace),TraceSha256=hashlib.sha256(trace.read_bytes()).hexdigest(),Entries=provenance,NotOriginalStandaloneCheckpointCopies=True))
 save(root/dungeon/'trace-statistics.json',totals)
 print(json.dumps(dict(Map=dungeon,Trace=totals),ensure_ascii=False))
