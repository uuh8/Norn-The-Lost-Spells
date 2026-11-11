"""核验原始四端数据，不生成预期状态；保留不足、负载停止与短测量范围。"""
from pathlib import Path
import json,hashlib,datetime,math,shutil,psutil,subprocess
repo=Path(r'F:\Develop Project\RPG');u=repo/'RPG';task=Path(__file__).parent
e=u/'docs/superpowers/evidence/multiplayer/n5/four-player-native-2026-11-01';private=u/'.tools/networking-checks/n5-four-player-20261101'
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def save(p,v):p.write_text(json.dumps(v,indent=2,ensure_ascii=False),encoding='utf-8')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def date(s):return datetime.datetime.fromisoformat(s.replace('Z','+00:00'))
assert not (e/'summary.json').exists(),'Preserve previous result'
final=e/'native-final-report';final.mkdir(exist_ok=False)
for role in 'abcd':
    for suffix in ['portal','movement','store']:
        p=private/'p7'/f'{role}-{suffix}.json'
        if p.exists():shutil.copy2(p,final/p.name)
dsprivate=private/'instances/a4619d4639c94e5f92b905103cf146b1/state.json'
shutil.copy2(dsprivate,final/'ds.json')
d=read(e/'p7/load-stop/ds.json');first=read(e/'p7/world-first/ds.json')
checks=[]
def ck(name,ok):checks.append(dict(Check=name,Passed=bool(ok)))
room=read(e/'p7/load-stop/room.json');ck('four actual room members',len(room['Members'])==4)
ck('four authenticated ready DS clients',d['Accepted']==4 and d['Ready'] and d['CanSimulate'] and not d['Frozen'])
ck('same DS process/start/world in two raw checkpoints',all(d[k]==first[k] for k in ['ProcessId','ProcessStartTicks','InstanceId','WorldEpoch']))
ck('material channel did not fault',not d['MaterialFaulted'] and not d['MaterialFailure'])
native={};charges={};connections=[];store={}
for i,role in enumerate('abcd'):
    r=read(e/f'p7/load-stop/{role}-movement.json');f=read(e/f'p7/world-first/{role}-movement.json')
    ck(f'{role} same actual authenticated actor and epoch',r['Ready'] and not r['Frozen'] and r['ActorId']==i+1 and r['Epoch']==1 and f['ActorId']==i+1 and f['Epoch']==1)
    ck(f'{role} three remote players',r['RemoteCount']==3 and f['RemoteCount']==3)
    ck(f'{role} tick progressed while connected',r['ServerTick']>f['ServerTick'])
    inv=d['ActorInventories'][i];card=inv['Cards'][0];own=r['ActorInventories'][i];owncard=own['Cards'][0]
    ck(f'{role} DS actual same card/quantity agrees client',card['InstanceId']==owncard['InstanceId'] and card['RemainingCharges']==owncard['RemainingCharges']==r['ActorCharges'][i])
    ck(f'{role} no manufactured resource refill',f['ActorCharges'][i]==30 and 0<=r['ActorCharges'][i]<=30 and not card['Unlimited'])
    ack=d[f'MaterialPeerAck{i}'];bindings={(v['ConnectionId'],v['ConnectionEpoch'],v['WorldEpoch']) for v in ack}
    ck(f'{role} independent installed ACK records',d['MaterialPeerAcks'][i]>0 and len(ack)>0 and len(bindings)==1 and next(iter(bindings))[1:]==(1,d['WorldEpoch']))
    connections.append(next(iter(bindings))[0]);charges[role]=dict(Before=30,After=r['ActorCharges'][i],CardId=card['InstanceId'])
    native[role]=dict(ActorId=r['ActorId'],Epoch=r['Epoch'],Remotes=r['RemoteCount'],FirstTick=f['ServerTick'],LastTick=r['ServerTick'],RecordedUtc=r['RecordedAtUtc'],DriverDroppedSteps=r['DriverDroppedSteps'])
    if role!='d':
        rows=[]
        for line in (e/'four-player-trace.jsonl').read_text(encoding='utf-8').splitlines():
            v=json.loads(line)
            if v['Source']==role+'-store' and v['Data'].get('WorldEpoch')==d['WorldEpoch']:rows.append(v['Data'])
        ck(f'{role} actual Fire Store observed',bool(rows) and max(v['Legacy'] for v in rows)>0)
        store[role]=dict(Samples=len(rows),MaxLegacy=max(v['Legacy'] for v in rows),MaxCells=max(v['CellCount'] for v in rows),LastLegacy=rows[-1]['Legacy'])
ck('four distinct actual ACK connection IDs',len(set(connections))==4)
r=read(e/'p7/three-cast-d-profile/d-movement.json');t=r['FrameTimes'][:r['Frames']]
dt=sorted((b-a)*1000 for a,b in zip(t,t[1:]));gc=r['FrameGcBytes'][:r['Frames']]
ck('D valid positively calibrated 240-frame recorder',r['Calibrated'] and r['RecorderValid'] and r['Frames']==240 and len(dt)==239 and min(dt)>0)
percentile=lambda q:dt[math.ceil(q*len(dt))-1]
profile=dict(Frames=r['Frames'],Intervals=len(dt),Seconds=t[-1]-t[0],FrameIntervalMs=dict(P50=percentile(.5),P95=percentile(.95),P99=percentile(.99),Maximum=max(dt)),Calibrated=r['Calibrated'],RecorderValid=r['RecorderValid'],PositiveGcFrames=sum(v>0 for v in gc),PeakFrameGcBytes=max(gc),IsCpuGpuFrameTime=False,IsFullN5PerformancePass=False,StoreSamplerOnD=False,OtherThreeStoreSamplersEnabled=True)
osrows=[json.loads(l) for l in (e/'os-sampling.jsonl').read_text(encoding='utf-8').splitlines()]
low=min(osrows,key=lambda r:r['AvailableBytes']);peak={}
for row in osrows:
    for p in row['Processes']:peak[p['Pid']]=max(peak.get(p['Pid'],0),p['Rss'])
load=dict(Samples=len(osrows),FirstUtc=osrows[0]['Utc'],LastUtc=osrows[-1]['Utc'],MinimumAvailableGB=low['AvailableBytes']/2**30,MinimumAvailableAtUtc=low['Utc'],CpuPercentMaximum=max(v['CpuPercent'] for v in osrows),ProcessPeakRssBytes=peak,AfterCleanupAvailableGB=psutil.virtual_memory().available/2**30,StopReason='Below predeclared 3GB available-memory threshold; no more load or retry')
trace=[];readyfirst={};readylast={};faults=[]
for ln,line in enumerate((e/'four-player-trace.jsonl').read_text(encoding='utf-8').splitlines(),1):
    v=json.loads(line);role=v['Source'].removesuffix('-movement');r=v['Data']
    if v['Source'].endswith('-movement') and r.get('Ready') and r.get('ActorId') in range(1,5):
        readyfirst.setdefault(role,r['RecordedAtUtc']);readylast[role]=r['RecordedAtUtc']
        if role=='d' and r['Rtt']:faults.append(dict(Line=ln,RecordedUtc=r['RecordedAtUtc'],Rtt=r['Rtt'],Jitter=r['Jitter'],Loss=r['Loss'],DiagnosticQueueDropped=r['DroppedMessages']))
overlap=(min(map(date,readylast.values()))-max(map(date,readyfirst.values()))).total_seconds()
ck('all four retained Ready for more than 60s of sampled overlap',len(readyfirst)==4 and overlap>60)
pres=read(e/'before.json');changed=[];missing=[]
for name,expected in pres['FileHashes'].items():
    p=repo/name
    if not p.exists():missing.append(name)
    elif sha(p)!=expected:changed.append(name)
ck('no protected source/settings/formal asset changed',not changed and not missing)
save(e/'preservation-after.json',dict(Files=len(pres['FileHashes']),Changed=changed,Missing=missing))
state=read(private/'session.private.json')
live=[]
for p in state['Processes']:
    if psutil.pid_exists(p['ProcessId']):
        process=psutil.Process(p['ProcessId'])
        if abs(process.create_time()-p['StartUnix'])<.1:live.append(p['ProcessId'])
ck('all owned recorded Native and DS processes exited',not live)
logs=[]
for p in [*(private/'p7').glob('*.log'),*(private/'instances').glob('*/server.log')]:
    raw=p.read_bytes();logs.append(dict(Path=str(p),Sha256=hashlib.sha256(raw).hexdigest(),Bytes=len(raw),BuiltinResourceLiteralCount=raw.count(b'Built-in resource'),ContentsNotPrinted=True,RetainedInIgnoredPrivateDirectory=True))
save(e/'private-log-hashes.json',logs)
passed=all(v['Passed'] for v in checks)
result=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Stage='Four authenticated peers and bounded observational sample; stopped for low memory',FunctionalChecksPassed=passed,Assertions=len(checks),Checks=checks,WorldEpoch=d['WorldEpoch'],DsPid=d['ProcessId'],Native=native,Charges=charges,PeerAckCounts=d['MaterialPeerAcks'],PeerAckRecordCounts=[len(d[f'MaterialPeerAck{i}']) for i in range(4)],PeerConnectionIds=connections,MaterialFaulted=d['MaterialFaulted'],MaterialChunks=len(d['MaterialChunks']),MaterialResyncAccepted=d['MaterialResyncAccepted'],RawReadyOverlapSeconds=overlap,ActualStoreObservations=store,DProfile=profile,OsLoad=load,MovementFault100Observed=bool(faults),MovementFaultFirst=faults[0] if faults else None,MovementFaultLast=faults[-1] if faults else None,MovementFaultGatePassed=False,FourSimultaneousContinuousCastersPassed=False,PerformanceGatePassed=False,N5Complete=False,OwnedNativeDsRemaining=live,NoRuntimeProtocolOrGameplayChanges=True,ExtraInputOrUncontrolledAttackObservations=True,Trace=dict(Path='four-player-trace.jsonl',Sha256=sha(e/'four-player-trace.jsonl'),Bytes=(e/'four-player-trace.jsonl').stat().st_size),Limitations=['D never performed a controlled real cast or movement after fault-100; 3 actual OS cast actions on A/B/C only','A charges had decreased before root mouse window; C continued later; sparse AttackHeld is not a full input-edge audit; do not attribute all charge changes to root 2s windows','D sampler disabled: only its actual DS installation ACKs, no D per-cell Store dump','ABC asynchronous actual Fire strength is not four-phase GMU conservation or same-tick whole-world equality','Valid 240 frames ~4 seconds: frame intervals not CPU/GPU time, 5 nonzero GC frames; no >=60s calibrated full performance window','Memory pressure and cumulative client DroppedSteps prevent interpreting four-process same-machine sample as release performance','fault-100 only MovementFaultQueue; no reliable material stream UDP loss/jitter or HOL test','No four-Actor activity union separation, reconnect with large Baseline under faults, complete queues/socket metrics, N6 settlement or alchemy'])
save(e/'summary.json',result)
subprocess.run(['git','status','--short'],cwd=repo,stdout=(e/'git-status-after.txt').open('w',encoding='utf-8'),check=True)
print(json.dumps(dict(FunctionalChecksPassed=passed,Assertions=len(checks),Failed=[c['Check'] for c in checks if not c['Passed']],ReadyOverlapSeconds=overlap,Charges=charges,DProfile=profile,MinimumAvailableGB=load['MinimumAvailableGB'],AfterCleanupAvailableGB=load['AfterCleanupAvailableGB'],ProtectedChanged=changed,LiveOwned=live),ensure_ascii=False))
assert passed,'Keep failed result; do not weaken checks'
