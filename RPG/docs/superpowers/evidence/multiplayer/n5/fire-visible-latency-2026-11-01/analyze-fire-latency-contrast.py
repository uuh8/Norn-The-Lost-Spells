"""分析已结束的两端短对照，明确CPU观察时间与可见时间并不等同。"""
from pathlib import Path
import json,datetime,hashlib,shutil,psutil,subprocess,math
r=Path(r'F:\Develop Project\RPG');u=r/'RPG';t=Path(__file__).parent
e=u/'docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01';p=u/'.tools/networking-checks/n5-fire-latency-20261101'
def read(x):return json.loads(x.read_text(encoding='utf-8-sig'))
def save(x,v):x.write_text(json.dumps(v,indent=2,ensure_ascii=False),encoding='utf-8')
def dt(s):return datetime.datetime.fromisoformat(s.replace('Z','+00:00'))
assert not (e/'summary.json').exists()
final=e/'native-final-report';final.mkdir()
for role in 'ab':
    for suffix in ['portal','movement','store']:
        q=p/'p8'/f'{role}-{suffix}.json'
        if q.exists():shutil.copy2(q,final/q.name)
shutil.copy2(p/'instances/6947186f9c624778b87f6074a1c3e098/state.json',final/'ds.json')
d=read(final/'ds.json');world=d['WorldEpoch'];checks=[]
def ck(n,b):checks.append(dict(Check=n,Passed=bool(b)))
ck('two admitted actual DS clients',d['Accepted']==2 and d['WorldReady'] and not d['MaterialFaulted'])
audit=[json.loads(x) for x in (e/'p8-os-input.jsonl').read_text(encoding='utf-8').splitlines()]
shots=[x for x in audit if x['Action']=='mouse' and 'AttackSeconds' in x['Result']]
ck('one recorded OS short attack on A',len(shots)==1 and shots[0]['Client']=='a' and shots[0]['Result']['AttackSeconds']==.15)
shot=shots[0];roles={};firsts={};lastzeros={};series={k:[] for k in 'ab'};dsrows=[]
for ln,line in enumerate((e/'low-load-trace.jsonl').read_text(encoding='utf-8').splitlines(),1):
    v=json.loads(line);name=v['Source'];q=v['Data']
    if name.startswith('ds-'):dsrows.append(dict(Line=ln,RecordedUtc=q['RecordedAtUtc'],MaterialAcks=q['MaterialAcks'],Resync=q['MaterialResyncAccepted']))
    if name in ['a-store','b-store'] and q.get('WorldEpoch')==world:
        role=name[0];q=dict(q);q['Line']=ln;series[role].append(q)
        if q['Legacy']>0 and role not in firsts:
            firsts[role]=q
        elif q['Legacy']==0 and role not in firsts:lastzeros[role]=q
for i,role in enumerate('ab'):
    before=read(e/f'p8/before-shot/{role}-movement.json');after=read(e/f'p8/after-shot/{role}-movement.json');own=read(final/f'{role}-movement.json')
    ck(f'{role} same actor epoch and ready',before['Ready'] and after['Ready'] and before['ActorId']==after['ActorId']==i+1 and before['Epoch']==after['Epoch']==1)
    ck(f'{role} observed A exact 30 to29',before['ActorCharges'][0]==30 and after['ActorCharges'][0]==29)
    ck(f'{role} B own charges stayed30',before['ActorCharges'][1]==after['ActorCharges'][1]==30)
    ck(f'{role} actual Fire first appeared after OS short attack',role in firsts and dt(firsts[role]['RecordedUtc'])>dt(shot['StartedUtc']))
    ck(f'{role} zero sample immediately before first Fire exists',role in lastzeros and lastzeros[role]['Legacy']==0)
    ck(f'{role} has separate actual DS install ACKs',d['MaterialPeerAcks'][i]>0 and len(d[f'MaterialPeerAck{i}'])>0)
    f=firsts[role];z=lastzeros[role]
    roles[role]=dict(Actor=i+1,Epoch=1,BeforeCharges=before['ActorCharges'][:2],AfterCharges=after['ActorCharges'][:2],FirstFireSample=dict(Line=f['Line'],RecordedUtc=f['RecordedUtc'],EngineMonotonic=f['Monotonic'],WorldTickHighWater=f['WorldTick'],Legacy=f['Legacy'],Cells=f['Cells']),PreviousZeroSample=dict(Line=z['Line'],RecordedUtc=z['RecordedUtc'],EngineMonotonic=z['Monotonic'],WorldTickHighWater=z['WorldTick']),InputStartToFirstSampleSeconds=(dt(f['RecordedUtc'])-dt(shot['StartedUtc'])).total_seconds(),MaxSampleGapSeconds=max((dt(b['RecordedUtc'])-dt(a['RecordedUtc'])).total_seconds() for a,b in zip(series[role],series[role][1:])))
def canonical(q):return sorted((x['Domain'],x['Storage'],x['X'],x['Y'],x['Z'],x['Material'],x['Quantity']) for x in q['Cells'])
ck('both first actual CPU states match material tick/cells/quantities',firsts['a']['WorldTick']==firsts['b']['WorldTick']==804 and canonical(firsts['a'])==canonical(firsts['b']) and firsts['a']['Legacy']==firsts['b']['Legacy']==382)
guard=[json.loads(x) for x in (e/'os-guard.jsonl').read_text(encoding='utf-8').splitlines()]
ck('memory guard did not trigger in short contrast',not any(x['Triggered'] for x in guard))
ck('minimum available memory remained above3GiB',min(x['AvailableBytes'] for x in guard)>3*2**30)
ck('final fresh DS summary confirms zero accepted Resync',d['MaterialResyncAccepted']==0 and dt(d['RecordedAtUtc'])>dt(shot['FinishedUtc']))
before=read(e/'before.json');changed=[]
for n,h in before['FileHashes'].items():
    q=r/n
    if not q.exists() or hashlib.sha256(q.read_bytes()).hexdigest()!=h:changed.append(n)
ck('1365 source/settings/formal assets preserved',len(before['FileHashes'])==1365 and not changed)
state=read(p/'session.private.json');live=[]
for v in state['Processes']:
    if psutil.pid_exists(v['ProcessId']):
        proc=psutil.Process(v['ProcessId'])
        if abs(proc.create_time()-v['StartUnix'])<.1:live.append(v['ProcessId'])
ck('owned recorded Native and DS exited',not live)
assert not psutil.pid_exists(3276),'Check owned business outcome instead of assuming shutdown'
phase4=u/'docs/superpowers/evidence/multiplayer/n5/four-player-native-2026-11-01'
for name in ['fire-sync-distribution-audit.md','fire-sync-render-audit.md','analyze-fire-latency-contrast.py','Invoke-N5FireLatencyNative.ps1','native-fire-input.py','guard-fire-contrast.py','prepare-fire-latency-contrast.py']:
    shutil.copy2(t/name,e/name)
shutil.copy2(phase4/'fire-observation-bounds.json',e/'historical-four-client-cpu-observation-bounds.json')
logs=[]
for q in [*(p/'p8').glob('*.log'),*(p/'instances').glob('*/server.log')]:
    b=q.read_bytes();logs.append(dict(Path=str(q),Sha256=hashlib.sha256(b).hexdigest(),Bytes=len(b),BuiltinResourceCaseInsensitiveCount=b.decode('utf-8-sig',errors='replace').lower().count('built-in resource'),ContentsPrinted=False,RetainedPrivately=True))
save(e/'private-log-hashes.json',logs)
result=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Checks=checks,Assertions=len(checks),FunctionalCpuObservationChecksPassed=all(x['Passed'] for x in checks),VisibleSymptomReproduction='Not established: no paired renderer/seed/draw timestamps or synchronized visual recording',RootCauseConfirmed=False,NoFixApplied=True,NoRuntimeProtocolGameplayOrResourceParameterChanges=True,NoBuildOrUnityTestRerun=True,WorldEpoch=world,DsPid=d['ProcessId'],Shot=shot,Clients=roles,FirstSampleUtcDifferenceSeconds=abs((dt(firsts['a']['RecordedUtc'])-dt(firsts['b']['RecordedUtc'])).total_seconds()),FirstSampleUtcDifferenceIsExactInstallOrVisibleLatency=False,FirstCpuStateSame=True,DsFinal=dict(RecordedUtc=d['RecordedAtUtc'],Acks=d['MaterialAcks'],Pages=d['MaterialPages'],PeerAcks=d['MaterialPeerAcks'],AcceptedResync=d['MaterialResyncAccepted'],MaterialFaulted=d['MaterialFaulted'],LastSummaryAgeAfterShotSeconds=(dt(d['RecordedAtUtc'])-dt(shot['FinishedUtc'])).total_seconds()),DsSampleSeries=dsrows,MinimumAvailableGiB=min(x['AvailableBytes'] for x in guard)/2**30,MemoryGuardTriggered=False,ProtectedChanged=changed,RemainingOwnedNativeDs=live,EditorProcessPreserved=psutil.pid_exists(24836),N5Complete=False,Limitations=['Same local Windows wall clock observed; per-process Unity realtime origins differ and no QPC calibration yet','250ms Store samples only bound CPU observation, no target Key G/R/Snapshot stamp; Store.WorldTick is a max high-water mark','P7 four peers/many attacks/2560x1600 compared with P8 two peers/one shot/unequal observed viewports, so NOT a one-variable memory causal experiment','Fresh final DS summary proves zero Resync during this short run, not absence of all queueing/drop/visual latency','Renderer has independent capped visual dt/spawn/probability/frustum/seed limits; code capability is not measured root cause','Battle child impact VFX is separate from authoritative material creation and replica installation','No client first Seed/Draw, GPU completion/present timing, full socket or per-stage correlated timings recorded'])
save(e/'summary.json',result);save(e/'preservation-after.json',dict(Files=len(before['FileHashes']),Changed=changed))
print(json.dumps(dict(Assertions=len(checks),Passed=result['FunctionalCpuObservationChecksPassed'],Failed=[x['Check'] for x in checks if not x['Passed']],FirstSampleUtcDifferenceSeconds=result['FirstSampleUtcDifferenceSeconds'],IsExactLatency=False,FirstCpuStateSame=True,MinimumAvailableGiB=result['MinimumAvailableGiB'],FinalDs=result['DsFinal'],ProtectedChanged=changed,OwnedRemaining=live),ensure_ascii=False))
assert result['FunctionalCpuObservationChecksPassed'],'Preserve failed checks; no weakening'
