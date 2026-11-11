from pathlib import Path
import json,math,datetime,hashlib
root=Path(r'F:\Develop Project\RPG\RPG\docs\superpowers\evidence\multiplayer\n5\native-player-reconnect-2026-10-31\resume-2026-11-01')
output=root/'native-functional-check.json';assert not output.exists(),'Preserve earlier checker result'
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
checks=[];maps={}
def check(value,label):
 checks.append(dict(Check=label,Passed=bool(value)))
 if not value:raise AssertionError(label)
def dist(a,b):return math.sqrt(sum((a[k]-b[k])**2 for k in ('x','y','z')))
def card(data,slot=0):return data['ActorInventories'][slot]['Cards'][0]
try:
 for dungeon in ('p7','p8'):
  d=root/dungeon;derived=d/'trace-derived';pre=read(derived/'pre-first-cast/a-movement.json');preb=read(derived/'pre-first-cast/b-movement.json')
  consume=read(derived/'consumed/a-movement.json');consumeb=read(derived/'consumed/b-movement.json');re=read(derived/'reconnected/a-movement.json');reb=read(derived/'reconnected/b-movement.json');post=read(derived/'post-reconnect-cast/a-movement.json');postb=read(derived/'post-reconnect-cast/b-movement.json')
  final=read(d/'native-final-report/a-portal.json');finalb=read(d/'native-final-report/b-movement.json')
  cases={label:read(d/label/'ds.json') for label in ('baseline','consumed','disconnected','reconnected','post-reconnect-cast','returned-hub-buddy-running')}
  rooms=[read(d/label/'room.json') for label in cases]
  audits=[json.loads(x) for x in (root/(dungeon+'-os-input.jsonl')).read_text(encoding='utf-8-sig').splitlines()]
  traces=[json.loads(x) for x in (d/'input-reconnect-trace.jsonl').read_text(encoding='utf-8-sig').splitlines()]
  acard=card(pre);n=card(consume)['RemainingCharges'];last=card(post)['RemainingCharges'];world=cases['baseline']['WorldEpoch'];run=rooms[0]['RunId']
  for i,m in enumerate((pre,consume,re,post)):check(m['Ready'] and m['ActorId']==1,f'{dungeon} A actual ready owner {i}')
  check(acard['RemainingCharges']==30 and not acard['Unlimited'],dungeon+' initial actual finite30')
  check(0<n<30,dungeon+' original OS cast consumed finite charges')
  for label,m in (('consumed',consume),('reconnected',re),('post',post)):
   c=card(m);check((c['InstanceId'],c['ParentWandInstanceId'],c['TemplateId'])==(acard['InstanceId'],acard['ParentWandInstanceId'],acard['TemplateId']),dungeon+' same owned card/wand '+label)
  check(pre['Epoch']==consume['Epoch']==1 and re['Epoch']==post['Epoch']==2,dungeon+' actual epoch1 to2')
  check(card(re)['RemainingCharges']==n,dungeon+' reconnect did not refill actual current source')
  check(0<=last<n,dungeon+' post reconnect real cast consumed again')
  for label,b,numbers in (('consumed',consumeb,n),('reconnected',reb,n),('post',postb,last)):
   check(b['ActorId']==2 and b['Epoch']==1 and b['Ready'],dungeon+' B remains original connection '+label)
   check(b['ActorCharges'][0]==numbers,dungeon+' B independent remote resource agrees '+label)
   check(card(b,1)['RemainingCharges']==30 and card(b,1)['InstanceId']==card(preb,1)['InstanceId'],dungeon+' B own card unaffected '+label)
  check(run and all(x['RunId']==run and x['RoomId']==rooms[0]['RoomId'] for x in rooms),dungeon+' same actual room/run')
  check(all(x['WorldEpoch']==world and x['ProcessId']==cases['baseline']['ProcessId'] and x['ProcessStartTicks']==cases['baseline']['ProcessStartTicks'] for x in cases.values()),dungeon+' same DS process/start/world')
  check(cases['baseline']['Accepted']==2 and cases['reconnected']['Accepted']==3,dungeon+' authenticated admissions2 to3')
  check(card(cases['disconnected'])['RemainingCharges']==n,dungeon+' DS current workspace after disconnect')
  check(card(cases['reconnected'])['RemainingCharges']==n and cases['reconnected']['ActorInventories'][0]['ConnectionEpoch']==2,dungeon+' DS epoch2 current workspace retained')
  check(card(cases['returned-hub-buddy-running'])['RemainingCharges']==last,dungeon+' DS actual final finite charge agrees')
  check(dist(cases['baseline']['ActorPositions'][0],consume['Position'])>.05,dungeon+' actual online position changed after OS movement')
  check(final['Scene'].endswith('SinglePlayer_Hub.unity') and final['HubActive'] and final['Phase']=='None',dungeon+' original final native report confirms return Hub')
  check(finalb['Ready'] and finalb['Epoch']==1 and finalb['ServerTick']>preb['ServerTick'],dungeon+' B simulation/input continue through A return')
  check(any(x['Source']=='a-portal' and x['Data'].get('Phase')=='Disconnected' for x in traces),dungeon+' actual disconnected phase in raw stream')
  login=[x for x in audits if x['Client']=='a' and x['Action']=='login'];reconnect=[x for x in audits if x['Client']=='a' and x['Action']=='node' and x['Result']['Node']=='Reconnect'];casts=[x for x in audits if x['Client']=='a' and x['Action']=='mouse' and x['Result'].get('AttackSeconds')]
  check(len(login)==1 and len(reconnect)==1 and len(casts)==2 and len({x['ProcessId'] for x in login+reconnect+casts})==1,dungeon+' same Native PID across login/two casts/reconnect')
  for role in ('a','b'):
   actions=[x for x in audits if x['Client']==role and x['Action']=='node'];names={x['Result']['Node'] for x in actions}
   check({'EnterHub','Matchmaking','Ready'}.issubset(names),dungeon+' actual original UI login Hub match ready '+role)
   stores=[x['Data'] for x in traces if x['Source']==role+'-store']
   check(stores and all(x['WorldEpoch']==world for x in stores),dungeon+' sampler survived menu and observed same world '+role)
   check(any(x['CellCount']>0 and x['Legacy']>0 and any(c['Material']==2 and c['Quantity']>0 for c in x['Cells']) for x in stores),dungeon+' actual Fire cells observed '+role)
   check(stores[-1]['Legacy']==0,dungeon+' transient Fire later depleted on '+role)
  check(any(any(a.get('ConnectionEpoch')==2 for a in (x['Data'].get('MaterialPeerAck0') or [])) for x in traces if x['Source'].startswith('ds-') and x['Data'].get('WorldEpoch')==world),dungeon+' DS actual epoch2 material install ACK recorded')
  maps[dungeon]=dict(NativePid=login[0]['ProcessId'],RunId=run,WorldEpoch=world,DsPid=cases['baseline']['ProcessId'],CardId=acard['InstanceId'],Charges=[30,n,n,last],Epoch=[1,2],ReturnedHub=True,BContinued=True,ActualMaterialCellsObserved=True,IndependentRawTrace=True)
 result=dict(Passed=True,Assertions=len(checks),Maps=maps,Checks=checks,Limits=['Private room allocated by existing API; empty matchmaking GUI allocation not tested','Role standalone checkpoint copies defective; actual complete native JSONL and original final reports used with provenance','Fire observations are asynchronous Legacy intensity, not complete same-tick GMU mass conservation','No container protocol/assets implemented; no N6 persistent settlement, four-player, stress, full-frame performance, boss clear or remote gate'],RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
except Exception as error:
 result=dict(Passed=False,Assertions=len(checks),Maps=maps,Checks=checks,Error=repr(error),RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print(json.dumps({k:v for k,v in result.items() if k in ('Passed','Assertions','Maps','Error')},ensure_ascii=False))
if not result['Passed']:raise SystemExit(1)
