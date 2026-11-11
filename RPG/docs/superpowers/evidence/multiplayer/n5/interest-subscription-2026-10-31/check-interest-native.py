from pathlib import Path
import json,datetime,hashlib
u=Path(r'F:\Develop Project\RPG\RPG');e=u/'docs/superpowers/evidence/multiplayer/n5/interest-subscription-2026-10-31';p=u/'.tools/networking-checks/n5-interest-subscription-20261031'
def read(x):return json.loads(Path(x).read_text(encoding='utf-8-sig'))
def save(x,v):Path(x).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def chunks(v):
    out={}
    for c in v['MaterialChunks']:
        key=(c['Domain'],c['X'],c['Y'],c['Z']);assert key not in out;out[key]=c
    return out
def signature(c):return tuple(c[k] for k in ('WorldEpoch','Generation','Revision','Checksum','Words','Resident'))
def same(a,b):return a.keys()==b.keys() and all(signature(a[k])==signature(b[k]) for k in a)
phases=[]
for phase in range(1,6):
    ds=read(e/f'checkpoint{phase}-ds.json');seed=read(e/f'checkpoint{phase}-seed.json');a=read(e/f'checkpoint{phase}-client0.json');b=read(e/f'checkpoint{phase}-client1.json')
    assert ds['Ready'] and ds['WorldReady'] and not ds['MaterialFaulted'] and not ds.get('InterestProbeError')
    sc,ac,bc=chunks(ds),chunks(a),chunks(b)
    assert any(x['Words']>11 and x['Resident'] for x in sc.values()),'Empty geometry is not material evidence'
    for client in (a,b):
        assert client['Authenticated'] and client['WorldReady'] and not client['MaterialFaulted'] and not client['Authority']
        assert client['WorldEpoch']==client['AuthenticatedWorldEpoch']==ds['WorldEpoch']
        assert all(c['SubscriptionEpoch']>0 for c in client['MaterialChunks'])
    assert same(sc,bc),'Stationary B lost authoritative material'
    if phase in (2,4):
        assert not ac and ds['InterestProbePose'].lower()=='away','A cache must be empty after actual DS departure'
    else:
        assert same(sc,ac) and ds['InterestProbePose'].lower()=='home','A current replica did not converge'
    phases.append(dict(ds=ds,seed=seed,a=a,b=b,sc=sc,ac=ac,bc=bc))
initial,outside,unchanged,changedAway,updated=phases
epoch=initial['ds']['WorldEpoch']
for v in phases:
    assert v['ds']['WorldEpoch']==epoch
    for key in ('a','b'):
        assert (v[key]['ActorId'],v[key]['ConnectionEpoch'])==(initial[key]['ActorId'],initial[key]['ConnectionEpoch'])
assert same(initial['sc'],outside['sc']) and same(initial['sc'],unchanged['sc']),'Interest leave changed DS material'
for key,old in initial['ac'].items():
    new=unchanged['ac'][key]
    assert signature(new)==signature(old) and new['SubscriptionEpoch']>old['SubscriptionEpoch'] and new['BaseRevision']==0
assert unchanged['a']['MaterialInstalled']>=initial['a']['MaterialInstalled']+len(initial['ac'])
assert unchanged['ds']['MaterialPeerAcks'][0]>=initial['ds']['MaterialPeerAcks'][0]+len(initial['ac'])
assert abs(outside['ds']['ActorPositions'][0]['x']-initial['ds']['ActorPositions'][0]['x'])>=50
assert abs(outside['ds']['ActorPositions'][1]['x']-initial['ds']['ActorPositions'][1]['x'])<1
assert abs(unchanged['ds']['ActorPositions'][0]['x']-initial['ds']['ActorPositions'][0]['x'])<1
before=read(e/'away-before-write-ds.json');beforeA=read(e/'away-before-write-client0.json');beforeSeed=read(e/'away-before-write-seed.json')
assert not chunks(beforeA) and before['InterestProbePose'].lower()=='away' and beforeSeed['Phase']==1
assert changedAway['seed']['Phase']==2 and changedAway['seed']['StartTick']>=beforeSeed['WorldTick']
assert changedAway['seed']['WorldTick']>=changedAway['seed']['StartTick']+5
changed=[k for k,v in changedAway['sc'].items() if k in initial['sc'] and signature(v)!=signature(initial['sc'][k])]
assert changed,'No actual committed change while A was outside'
assert changedAway['a']['MaterialInstalled']==beforeA['MaterialInstalled'],'Outside A must not reinstall new material'
assert changedAway['ds']['MaterialPeerAcks'][0]==before['MaterialPeerAcks'][0]
assert changedAway['ds']['MaterialPeerAcks'][1]>unchanged['ds']['MaterialPeerAcks'][1]
assert same(updated['sc'],changedAway['sc'])
for key,old in unchanged['ac'].items():
    new=updated['ac'][key];assert new['SubscriptionEpoch']>old['SubscriptionEpoch'] and new['BaseRevision']==0
assert all(c['BaseRevision']==0 for c in updated['ac'].values())
assert updated['ds']['MaterialPeerAcks'][0]>=unchanged['ds']['MaterialPeerAcks'][0]+len(updated['ac'])
assert updated['a']['MaterialInstalled']>=unchanged['a']['MaterialInstalled']+len(updated['ac'])
session=read(p/'session.private.json')
secrets=[user['Token'] for room in session['Rooms'].values() for user in room['Users']]
for path in p.glob('*client*.private.json'):
    value=read(path)
    if 'Ticket' in value:secrets.append(value['Ticket']['Ticket'])
logs=[]
for item in session['Processes']:
    path=Path(item['Log']) if item['Kind']=='NativeClient' else Path(item['StatePath']).parent/'server.log'
    text=path.read_text(encoding='utf-8-sig',errors='replace')
    for secret in secrets:
        if secret:text=text.replace(secret,'[REDACTED]')
    (e/(item['Label']+'-native.log')).write_text(text,encoding='utf-8')
    errors=[x for x in text.splitlines() if 'Exception:' in x or '[WorldReplication]' in x or '[WorldReplicationProbe]' in x or 'Gas Renderer' in x]
    assert not errors,'Unexpected runtime errors retained in sanitized log'
    logs.append(dict(Label=item['Label'],BuiltInResourceErrors=sum('Built-in Resource Error:' in x for x in text.splitlines()),OtherErrors=[]))
assemblies=[]
for kind,exe in (('Client','NornRoomClient'),('Server','NornRoomServer')):
    for name in ('Game.Core','Game.ElementField','Game.Multiplayer','Game.Networking','Game.NetworkProtocol'):
        path=u/f'Builds/OnlineRooms/{kind}/{exe}_Data/Managed/{name}.dll';assert path.exists()
        assemblies.append(dict(Build=kind,Assembly=name,Sha256=hashlib.sha256(path.read_bytes()).hexdigest().upper()))
save(e/'native-assembly-hashes.json',assemblies)
result=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),ProtocolVersion=4,Dungeon='p7',WorldEpoch=epoch,
    Scenario='Real authenticated DS Actor snapshot diagnostic movement; formal KCP subscription path; not keyboard/full player path',
    Checkpoints=[dict(Phase=i+1,Pose=v['ds']['InterestProbePose'],WorldTick=v['seed']['WorldTick'],SourceChunks=len(v['sc']),AChunks=len(v['ac']),BChunks=len(v['bc']),AInstalled=v['a']['MaterialInstalled'],BInstalled=v['b']['MaterialInstalled'],PeerAcks=v['ds']['MaterialPeerAcks'][:2]) for i,v in enumerate(phases)],
    SameRevisionReentryVerified=True,ChangedWorldReentryVerified=True,OutsideAReceivesNoNewInstallation=True,StationaryBAndDSRetainMaterial=True,PerPeerInstallAcksVerified=True,
    InitialASubscriptions=[dict(Key=list(k),Epoch=c['SubscriptionEpoch'],Revision=c['Revision']) for k,c in initial['ac'].items()],
    FinalASubscriptions=[dict(Key=list(k),Epoch=c['SubscriptionEpoch'],Revision=c['Revision'],BaseRevision=c['BaseRevision']) for k,c in updated['ac'].items()],Logs=logs,
    Limits=['DS snapshot diagnostic pose rather than keyboard player path','Two paused material checkpoints with five actual running ticks while A outside','No Native adversarial old-page/missing-revision/history-overwrite/corruption injection','No four-player/stress/HOL/performance','No same-process reconnect/consumption persistence/full Hub path','P8 Native material data not tested'])
save(e/'native-interest-consistency.json',result)
print(json.dumps(dict(Passed=True,Phases=5,SourceChunks=[len(x['sc']) for x in phases],AChunks=[len(x['ac']) for x in phases],PeerAcksFinal=updated['ds']['MaterialPeerAcks'][:2]),ensure_ascii=False))
