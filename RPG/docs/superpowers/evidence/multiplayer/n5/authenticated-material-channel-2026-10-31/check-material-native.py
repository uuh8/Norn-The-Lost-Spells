from pathlib import Path
import json,datetime,hashlib,re
r=Path(r'F:\Develop Project\RPG');u=r/'RPG'
private=u/'.tools/networking-checks/n5-authenticated-material-20261031'
e=u/'docs/superpowers/evidence/multiplayer/n5/authenticated-material-channel-2026-10-31'
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def save(p,v):Path(p).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
seed=read(private/'material-seed.json')
assert seed['Accepted'] and seed['PausedForComparison'] and seed['WorldTick']>=5,seed
save(e/'material-seed.json',seed)
ds=read(e/'p7-ds-latest.json')
assert ds['Ready'] and ds['WorldReady'] and not ds['MaterialFaulted'],ds.get('MaterialFailure')
assert ds['MaterialPages']>0 and ds['MaterialAcks']>0
def chunks(v):
    result={}
    for x in v['MaterialChunks']:
        key=(x['Domain'],x['X'],x['Y'],x['Z'])
        assert key not in result
        result[key]=(x['WorldEpoch'],x['Generation'],x['Revision'],x['Checksum'],x['Words'],x['Resident'])
    return result
source=chunks(ds)
assert source and any(x[4]>11 and x[5] for x in source.values()),'只传空Geometry不构成非零材料证据'
checks=[]
for actor in (0,1):
    p=read(e/f'p7-client{actor}-latest.json')
    assert p['Authenticated'] and p['WorldReady'] and not p['Authority'] and not p['MaterialFaulted']
    assert p['WorldEpoch']==p['AuthenticatedWorldEpoch']==ds['WorldEpoch']
    actual=chunks(p)
    assert actual==source,dict(actor=actor,Missing=list(source.keys()-actual.keys()),Extra=list(actual.keys()-source.keys()),Different=[k for k in actual.keys()&source.keys() if actual[k]!=source[k]])
    assert p['MaterialInstalled']>=len(actual)
    checks.append(dict(ActorId=p['ActorId'],ConnectionEpoch=p['ConnectionEpoch'],ChunksCompared=len(actual),ExactChunkFieldsMatch=True,Installed=p['MaterialInstalled'],ReportedAtUtc=p['RecordedAtUtc']))
session=read(private/'session.private.json')
secrets=[user['Token'] for room in session['Rooms'].values() for user in room['Users']]
for p in private.glob('*client*.private.json'):
    value=read(p)
    if 'Ticket' in value:secrets.append(value['Ticket']['Ticket'])
logs=[]
for item in session['Processes']:
    label=item['Label'];p=Path(item['Log']) if item['Kind']=='NativeClient' else Path(item['StatePath']).parent/'server.log'
    text=p.read_text(encoding='utf-8-sig',errors='replace')
    for secret in secrets:
        if secret:text=text.replace(secret,'[REDACTED]')
    (e/(label+'-native.log')).write_text(text,encoding='utf-8')
    lines=[x for x in text.splitlines() if 'Built-in Resource Error:' in x or 'Exception:' in x or '[WorldReplication]' in x or 'Gas Renderer' in x]
    assert not any('Exception:' in x or '[WorldReplication]' in x or 'Gas Renderer' in x for x in lines),lines
    logs.append(dict(Label=label,BuiltInResourceErrors=sum('Built-in Resource Error:' in x for x in lines),OtherErrors=[]))
result=dict(RecordedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Dungeon='p7',WorldEpoch=ds['WorldEpoch'],
    SourceChunks=len(source),NonEmptyMaterialChunks=sum(x[4]>11 and x[5] for x in source.values()),SourcePagesSent=ds['MaterialPages'],InstallAcksAccepted=ds['MaterialAcks'],
    Seed=seed,Clients=checks,LogSummary=logs,
    ComparedFields=['Domain','XYZ','WorldEpoch','Generation','Revision','Checksum','Words','Resident'],
    Limits=['Explicit Development seed and paused material checkpoint after five real ticks','No dynamic Interest/evict, stress, continuous-reaction or Native performance claim','Same-process auto reconnect and resource consumption persistence not tested'])
save(e/'native-material-consistency.json',result)
print(json.dumps(result,ensure_ascii=False))
