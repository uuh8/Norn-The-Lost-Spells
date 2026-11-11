"""同机QPC区间下关联真实安装/Seed/Upload/Draw CPU；不代表GPU完成或屏幕出现。"""
from pathlib import Path
from fractions import Fraction
from collections import Counter
import json,math,statistics,datetime,hashlib
e=Path(__file__).resolve().parent;n=e/'native-2'
q={r:json.loads((n/(r+'-timing.json')).read_text(encoding='utf-8-sig')) for r in ['ds','a','b']}
checks=[]
def check(name,value):
    checks.append(dict(Name=name,Passed=bool(value)))
    if not value:raise AssertionError(name)
def version(x):return tuple(x[k] for k in ['WorldEpoch','Domain','X','Y','Z','Generation','Revision'])
def stamp(x):return tuple(x[k] for k in ['WorldEpoch','Domain','X','Y','Z','Generation','BaseRevision','Revision','SnapshotId','Checksum','Words','MaterialTick','Resident'])
clock={}
for role,data in q.items():
    check(role+' complete bounded prefix',data['State']=='Completed' and len(data['Events'])==data['EventCount'] and data['DroppedCount']==data['FrameSamplesDropped']==0)
    a,b=data['ClockStart'],data['ClockEnd']
    check(role+' calibrated both endpoints',all(x['WindowsQpcCalibrated'] and x['IsHighResolution'] for x in [a,b]))
    check(role+' stable frequencies',a['QpcFrequency']==b['QpcFrequency'] and a['StopwatchFrequency']==b['StopwatchFrequency'])
    clock[role]=dict(Qpc=a['QpcFrequency'],Sw=a['StopwatchFrequency'],Low=min(a['QpcOffsetLowerTicks'],b['QpcOffsetLowerTicks']),High=max(a['QpcOffsetUpperTicks'],b['QpcOffsetUpperTicks']))
check('same Windows QPC frequency',len({x['Qpc'] for x in clock.values()})==1)
def interval(role,x):
    c=clock[role];scaled=Fraction(x['Timestamp']*c['Qpc'],c['Sw'])
    return Fraction(scaled+c['Low'],c['Qpc']),Fraction(scaled+c['High'],c['Qpc'])
def delta(r0,x0,r1,x1):
    if r0==r1:
        ms=float(Fraction((x1['Timestamp']-x0['Timestamp'])*1000,clock[r0]['Sw']))
        return dict(LowerMs=ms,UpperMs=ms)
    a,b=interval(r0,x0);c,d=interval(r1,x1)
    return dict(LowerMs=float((c-b)*1000),UpperMs=float((d-a)*1000))
def stats(values):
    if not values:return None
    mids=sorted((x['LowerMs']+x['UpperMs'])/2 for x in values)
    return dict(Count=len(values),MinLowerMs=min(x['LowerMs'] for x in values),MaxUpperMs=max(x['UpperMs'] for x in values),
        MedianMidpointMs=statistics.median(mids),P95MidpointMs=mids[math.ceil(len(mids)*.95)-1],
        MaxIntervalWidthMs=max(x['UpperMs']-x['LowerMs'] for x in values))
owner={version(x):x for x in q['ds']['Events'] if x['Stage']=='Committed'}
rows=[];summaries={};installs={};draws={}
for role,actor in [('a',1),('b',2)]:
    events=q[role]['Events'];installs[role]={version(x):x for x in events if x['Stage']=='Installed'};draws[role]={}
    check(role+' all target authority commits installed',set(installs[role])==set(owner))
    for v,installed in installs[role].items():
        wire=stamp(installed)
        find=lambda stage:[x for x in events if x['Stage']==stage and stamp(x)==wire]
        received=find('PageReceived')
        sent=[x for x in q['ds']['Events'] if x['Stage']=='PageSent' and x['ActorId']==actor and x['PageCount']>0 and stamp(x)==wire]
        ack=[x for x in q['ds']['Events'] if x['Stage']=='ReplyAccepted' and x['ActorId']==actor and x['Reason']==3 and stamp(x)==wire]
        check(role+' exact wire/page/ACK stamp R'+str(installed['Revision']),len(received)==len(sent)==len(ack)==1)
        row=dict(Role=role,ActorId=actor,ConnectionId=sent[0]['ConnectionId'],SubscriptionEpoch=sent[0]['SubscriptionEpoch'],
            Generation=installed['Generation'],Revision=installed['Revision'],MaterialTick=installed['MaterialTick'],
            SnapshotId=installed['SnapshotId'],Checksum=installed['Checksum'],Words=installed['Words'],
            CommitToSendReturn=delta('ds',owner[v],'ds',sent[0]),SendReturnToReceive=delta('ds',sent[0],role,received[0]),
            ReceiveToInstalled=delta(role,received[0],role,installed),CommitToInstalled=delta('ds',owner[v],role,installed),
            InstalledToAckAccepted=delta(role,installed,'ds',ack[0]),
            RendererRejected=[dict(Reason=x['Reason'],Quantity=x['Quantity']) for x in find('RendererRejected')])
        read,seed,upload,draw=(find(s) for s in ['RendererRead','SeedChosen','SeedUploaded','DrawSubmitted'])
        if seed:
            check(role+' actual seed nonzero R'+str(installed['Revision']),seed[0]['Quantity']>0)
            check(role+' seed follows same-stamp read/install R'+str(installed['Revision']),len(read)==1 and installed['Timestamp']<=read[0]['Timestamp']<=seed[0]['Timestamp'])
            row.update(FirstSeedCell=[seed[0]['CellX'],seed[0]['CellY'],seed[0]['CellZ']],SeedQuantity=seed[0]['Quantity'],SeedPresentationVersion=seed[0]['PresentationVersion'],
                InstalledToRead=delta(role,installed,role,read[0]),InstalledToSeed=delta(role,installed,role,seed[0]))
        if upload:
            check(role+' upload retains chosen seed R'+str(installed['Revision']),len(seed)==1 and upload[0]['Timestamp']>=seed[0]['Timestamp'] and [upload[0][k] for k in ['CellX','CellY','CellZ','Quantity','PresentationVersion']]==[seed[0][k] for k in ['CellX','CellY','CellZ','Quantity','PresentationVersion']])
            row['SeedToUploadCpu']=delta(role,seed[0],role,upload[0])
        if draw:
            check(role+' draw after real upload R'+str(installed['Revision']),len(upload)==1 and draw[0]['Timestamp']>=upload[0]['Timestamp'])
            row.update(InstalledToDrawCpu=delta(role,installed,role,draw[0]),CommitToDrawCpu=delta('ds',owner[v],role,draw[0]),UploadToDrawCpu=delta(role,upload[0],role,draw[0]))
            draws[role][v]=draw[0]
        rows.append(row)
    counts=dict(Counter(x['Stage'] for x in events))
    check(role+' real target Seed Upload Draw present',all(counts.get(x,0)>0 for x in ['RendererRead','SeedChosen','SeedUploaded','DrawSubmitted']))
    role_rows=[x for x in rows if x['Role']==role]
    frame_intervals=[float(Fraction((b['Timestamp']-a['Timestamp'])*1000,clock[role]['Sw'])) for a,b in zip(q[role]['Frames'],q[role]['Frames'][1:])]
    summaries[role]=dict(StageCounts=counts,RejectedReasons=dict(Counter(x['Reason'] for x in events if x['Stage']=='RendererRejected')),
        Viewport=[q[role]['Width'],q[role]['Height']],FocusedFrames=sum(x['Focused'] for x in q[role]['Frames']),FrameCount=len(q[role]['Frames']),
        RawMonotonicFrameIntervalMs=dict(P50=statistics.median(frame_intervals),P99=sorted(frame_intervals)[math.ceil(len(frame_intervals)*.99)-1],Max=max(frame_intervals)),
        Timings={name:stats([x[name] for x in role_rows if name in x]) for name in ['CommitToSendReturn','SendReturnToReceive','ReceiveToInstalled','CommitToInstalled','InstalledToRead','InstalledToSeed','SeedToUploadCpu','UploadToDrawCpu','InstalledToDrawCpu','CommitToDrawCpu']},
        FirstDraw=next((x for x in role_rows if 'InstalledToDrawCpu' in x),None))
common=sorted(set(draws['a'])&set(draws['b']))
check('both peers have same network version Draw CPU correlation',bool(common))
first_draw={role:min((x for x in q[role]['Events'] if x['Stage']=='DrawSubmitted'),key=lambda x:x['Timestamp']) for role in ['a','b']}
guard=[json.loads(x) for x in (n/'memory-guard.jsonl').read_text(encoding='utf-8-sig').splitlines()]
memory=[x['AvailableBytes'] for x in guard if 'AvailableBytes' in x]
result=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Passed=sum(x['Passed'] for x in checks),Failed=sum(not x['Passed'] for x in checks),Checks=checks,
    Clock=clock,ClockModel='Conservative union of start/end QPC bracket offsets on same Windows host; raw integer/Fraction arithmetic',
    AuthorityVersions=len(owner),AuthorityStageCounts=dict(Counter(x['Stage'] for x in q['ds']['Events'])),Peers=summaries,PerVersion=rows,
    AToBInstalled=stats([delta('a',installs['a'][v],'b',installs['b'][v]) for v in owner]),
    CommonDrawVersions=len(common),AToBDrawCpu=stats([delta('a',draws['a'][v],'b',draws['b'][v]) for v in common]),
    FirstCommonDraw=dict(Version=common[0],AToB=delta('a',draws['a'][common[0]],'b',draws['b'][common[0]])),
    FirstObservedDrawCpu=dict(Versions={role:version(x) for role,x in first_draw.items()},
        AToB=delta('a',first_draw['a'],'b',first_draw['b']),DifferentVersions=version(first_draw['a'])!=version(first_draw['b'])),
    MinAvailableBytes=min(memory) if memory else None,RendererCpuChainObserved=True,GpuCompletionOrScreenAppearanceMeasured=False,N5Complete=False,
    RawSha256={role:hashlib.sha256((n/(role+'-timing.json')).read_bytes()).hexdigest() for role in q},
    Limitations=['PageSent is Mirror Send return, not wire transmission','Draw is CPU submission return, not actual GPU/monitor presentation',
                 'First Seed cell may differ between peers; compare same network version rather than claiming same particle',
                 'Single P8 shot on two local processes; does not establish root cause of prior four-client P7 observation',
                 'No Native diagnostic enabled/disabled CPU overhead A/B; no full-frame zero-GC claim'])
(n/'timing-analysis.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k not in ['Checks','PerVersion','RawSha256']},ensure_ascii=False,indent=2))
