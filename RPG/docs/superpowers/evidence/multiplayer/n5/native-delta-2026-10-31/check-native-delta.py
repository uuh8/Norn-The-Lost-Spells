from pathlib import Path
import json,datetime,hashlib
u=Path(r'F:\Develop Project\RPG\RPG')
e=u/'docs/superpowers/evidence/multiplayer/n5/native-delta-2026-10-31'
p=u/'.tools/networking-checks/n5-native-delta-20261031'
def read(x):return json.loads(Path(x).read_text(encoding='utf-8-sig'))
def save(x,v):Path(x).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def chunks(v):
    out={}
    for x in v['MaterialChunks']:
        key=(x['Domain'],x['X'],x['Y'],x['Z']);assert key not in out
        out[key]=x
    return out
def equal(source,replica):
    assert source.keys()==replica.keys(),'Chunk key set mismatch'
    fields=('WorldEpoch','Generation','Revision','Checksum','Words','Resident')
    assert all(all(source[k][f]==replica[k][f] for f in fields) for k in source),'Chunk contents/version mismatch'
phases=[]
for phase in (1,2):
    ds=read(e/f'checkpoint{phase}-ds.json');seed=read(e/f'checkpoint{phase}-seed.json');sc=chunks(ds)
    assert ds['Ready'] and ds['WorldReady'] and not ds['MaterialFaulted']
    assert seed['Phase']==phase and seed['Accepted'] and seed['PausedForComparison'] and seed['WorldTick']>=seed['StartTick']+5
    assert any(x['Words']>11 and x['Resident'] for x in sc.values()),'Empty geometry is not material evidence'
    clients=[]
    for i in (0,1):
        v=read(e/f'checkpoint{phase}-client{i}.json');cc=chunks(v)
        assert v['Authenticated'] and v['WorldReady'] and not v['Authority'] and not v['MaterialFaulted']
        assert v['WorldEpoch']==v['AuthenticatedWorldEpoch']==ds['WorldEpoch'];equal(sc,cc)
        assert v['MaterialInstalled']>=len(sc)
        clients.append(v)
    phases.append((ds,seed,sc,clients))
first,last=phases
assert first[0]['WorldEpoch']==last[0]['WorldEpoch']
assert last[1]['StartTick']>=first[1]['WorldTick'] and last[1]['WorldTick']>=first[1]['WorldTick']+5
assert last[0]['MaterialAcks']>first[0]['MaterialAcks'] and last[0]['MaterialPages']>first[0]['MaterialPages']
changed=[key for key,v in last[2].items() if key in first[2] and v['Generation']==first[2][key]['Generation'] and v['Revision']>first[2][key]['Revision'] and v['Checksum']!=first[2][key]['Checksum']]
assert changed,'Second real write did not produce changed committed material'
clients=[]
for i in (0,1):
    old=first[3][i];v=last[3][i];cc=chunks(v)
    assert v['ActorId']==old['ActorId'] and v['ConnectionEpoch']==old['ConnectionEpoch']
    assert v['MaterialInstalled']>old['MaterialInstalled']
    actual=[]
    for key in changed:
        chunk=cc[key]
        if chunk['BaseRevision']>=first[2][key]['Revision'] and chunk['Revision']>chunk['BaseRevision'] and chunk['SnapshotId']>0:
            # Delta来自实际提交Header；Baseline可能刻意使用当前捕获Tick/序号，不能套此精确比较。
            for field in ('BaseRevision','SnapshotId','ServerTick'):
                assert chunk[field]==last[2][key][field],'Actual Delta header mismatch: '+field
            actual.append(chunk)
    assert actual,'Revision growth alone cannot prove actual Delta installation'
    clients.append(dict(ActorId=v['ActorId'],ConnectionEpoch=v['ConnectionEpoch'],InstalledBefore=old['MaterialInstalled'],InstalledAfter=v['MaterialInstalled'],ExactChunks=len(cc),ActualDeltaChunks=actual))
session=read(p/'session.private.json')
secrets=[user['Token'] for room in session['Rooms'].values() for user in room['Users']]
for path in p.glob('*client*.private.json'):
    v=read(path)
    if 'Ticket' in v:secrets.append(v['Ticket']['Ticket'])
logs=[]
for item in session['Processes']:
    path=Path(item['Log']) if item['Kind']=='NativeClient' else Path(item['StatePath']).parent/'server.log'
    text=path.read_text(encoding='utf-8-sig',errors='replace')
    for secret in secrets:
        if secret:text=text.replace(secret,'[REDACTED]')
    (e/(item['Label']+'-native.log')).write_text(text,encoding='utf-8')
    relevant=[line for line in text.splitlines() if 'Built-in Resource Error:' in line or 'Exception:' in line or '[WorldReplication]' in line or '[WorldReplicationProbe]' in line or 'Gas Renderer' in line]
    other=[line for line in relevant if 'Built-in Resource Error:' not in line]
    assert not other,'Unexpected runtime error; retained sanitized log'
    logs.append(dict(Label=item['Label'],BuiltInResourceErrors=len(relevant),OtherErrors=[]))
assemblies=[]
for kind,exe in (('Client','NornRoomClient'),('Server','NornRoomServer')):
    managed=u/f'Builds/OnlineRooms/{kind}/{exe}_Data/Managed'
    for name in ('Game.Core','Game.ElementField','Game.Multiplayer','Game.Networking','Game.NetworkProtocol'):
        path=managed/(name+'.dll');assert path.exists(),str(path)
        assemblies.append(dict(Build=kind,Assembly=name,Sha256=hashlib.sha256(path.read_bytes()).hexdigest().upper()))
save(e/'native-assembly-hashes.json',assemblies)
result=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Dungeon='p7',WorldEpoch=last[0]['WorldEpoch'],
    FirstTick=first[1]['WorldTick'],SecondStartTick=last[1]['StartTick'],SecondTick=last[1]['WorldTick'],
    FirstChunks=len(first[2]),FinalChunks=len(last[2]),ChangedExistingChunks=len(changed),
    PagesBefore=first[0]['MaterialPages'],PagesAfter=last[0]['MaterialPages'],AcksBefore=first[0]['MaterialAcks'],AcksAfter=last[0]['MaterialAcks'],
    Clients=clients,Logs=logs,Mode='Two paused checkpoints with at least five real material ticks running between them',
    AckEvidence='DS aggregate accepted-install ACK count increased; final ACK receipt not attributed separately to each connection',
    NotVerified=['Unbounded continuous reactions','Native missing revision/history overwrite/corrupt page recovery','Interest subscription','Stress/performance','Same-process auto reconnect','Consumption persistence','Full player path'])
save(e/'native-delta-consistency.json',result)
print(json.dumps(dict(Passed=True,WorldEpoch=result['WorldEpoch'],ChangedChunks=len(changed),ClientCount=2,AcksBefore=result['AcksBefore'],AcksAfter=result['AcksAfter']),ensure_ascii=False))
