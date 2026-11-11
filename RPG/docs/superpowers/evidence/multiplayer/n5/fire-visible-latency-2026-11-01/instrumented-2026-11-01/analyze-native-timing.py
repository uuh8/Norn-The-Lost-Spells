"""只读原始计时，使用Fraction保留大QPC计数低位；结果不是GPU/屏幕时延。"""
from pathlib import Path
from fractions import Fraction
from collections import Counter
import json,math,hashlib,datetime,statistics
e=Path(__file__).resolve().parent;n=e/'native-3'
reports={role:json.loads((n/(role+'-timing.json')).read_text(encoding='utf-8-sig')) for role in ['ds','a','b']}
checks=[]
def check(name,value):
    checks.append(dict(Name=name,Passed=bool(value)))
    if not value:raise AssertionError(name)
def key(x):return tuple(x[k] for k in ['WorldEpoch','Domain','X','Y','Z','Generation','Revision'])
def wire(x):return tuple(x[k] for k in ['WorldEpoch','Domain','X','Y','Z','Generation','BaseRevision','Revision','SnapshotId','Checksum','Words','MaterialTick','Resident'])
def quantile(values,p):return sorted(values)[max(0,math.ceil(len(values)*p)-1)]
clock={}
for role,q in reports.items():
    check(role+' complete and bounded',q['State']=='Completed' and q['EventCount']==len(q['Events']) and q['DroppedCount']==0 and q['FrameSamplesDropped']==0)
    cs,ce=q['ClockStart'],q['ClockEnd']
    check(role+' calibrated high resolution both ends',all(c['WindowsQpcCalibrated'] and c['IsHighResolution'] for c in [cs,ce]))
    check(role+' stable frequency',cs['QpcFrequency']==ce['QpcFrequency'] and cs['StopwatchFrequency']==ce['StopwatchFrequency'])
    check(role+' raw sequence monotonic',all(b['Sequence']==a['Sequence']+1 and b['Timestamp']>=a['Timestamp'] for a,b in zip(q['Events'],q['Events'][1:])))
    clock[role]=dict(QpcFrequency=cs['QpcFrequency'],StopwatchFrequency=cs['StopwatchFrequency'],
        Lower=min(cs['QpcOffsetLowerTicks'],ce['QpcOffsetLowerTicks']),Upper=max(cs['QpcOffsetUpperTicks'],ce['QpcOffsetUpperTicks']),
        StartWidthMicroseconds=(cs['QpcOffsetUpperTicks']-cs['QpcOffsetLowerTicks'])*1e6/cs['QpcFrequency'],
        EndWidthMicroseconds=(ce['QpcOffsetUpperTicks']-ce['QpcOffsetLowerTicks'])*1e6/ce['QpcFrequency'])
check('shared Windows QPC frequency',len({c['QpcFrequency'] for c in clock.values()})==1)
def bounds(role,event):
    c=clock[role];base=Fraction(event['Timestamp']*c['QpcFrequency'],c['StopwatchFrequency'])
    return Fraction(base+c['Lower'],c['QpcFrequency']),Fraction(base+c['Upper'],c['QpcFrequency'])
def elapsed(r0,event0,r1,event1):
    if r0==r1:
        value=Fraction((event1['Timestamp']-event0['Timestamp'])*1000,clock[r0]['StopwatchFrequency'])
        return dict(LowerMs=float(value),UpperMs=float(value))
    a,b=bounds(r0,event0);c,d=bounds(r1,event1)
    return dict(LowerMs=float((c-b)*1000),UpperMs=float((d-a)*1000))
def stats(values):
    mids=[(x['LowerMs']+x['UpperMs'])/2 for x in values]
    return dict(Count=len(values),MinLowerMs=min(x['LowerMs'] for x in values),MaxUpperMs=max(x['UpperMs'] for x in values),
        MedianMidpointMs=statistics.median(mids),P95MidpointMs=quantile(mids,.95),MaxIntervalWidthMs=max(x['UpperMs']-x['LowerMs'] for x in values))
owner={key(x):x for x in reports['ds']['Events'] if x['Stage']=='Committed'}
check('unique authority commits',len(owner)==sum(x['Stage']=='Committed' for x in reports['ds']['Events']))
rows=[];role_summary={};installs={}
for role,actor in [('a',1),('b',2)]:
    events=reports[role]['Events'];installed=[x for x in events if x['Stage']=='Installed'];installs[role]={key(x):x for x in installed}
    check(role+' one install per commit',set(installs[role])==set(owner) and len(installed)==len(owner))
    check(role+' no observed rejected page or Resync',not any(x['Stage'] in ['PageRejected','ResyncRequested'] for x in events))
    for value in installed:
        stamp=wire(value);commit=owner[key(value)]
        received=[x for x in events if x['Stage']=='PageReceived' and wire(x)==stamp]
        sent=[x for x in reports['ds']['Events'] if x['Stage']=='PageSent' and x['ActorId']==actor and wire(x)==stamp and x['PageCount']>0]
        ack=[x for x in reports['ds']['Events'] if x['Stage']=='ReplyAccepted' and x['ActorId']==actor and x['Reason']==3 and wire(x)==stamp]
        check(role+' full stamp peer correlation revision '+str(value['Revision']),len(sent)==len(received)==len(ack)==1)
        check(role+' material tick matches authority '+str(value['Revision']),commit['MaterialTick']==value['MaterialTick'])
        reject=[x for x in events if x['Stage']=='RendererRejected' and wire(x)==stamp]
        row=dict(Role=role,ActorId=actor,ConnectionId=sent[0]['ConnectionId'],SubscriptionEpoch=sent[0]['SubscriptionEpoch'],
            WorldEpoch=value['WorldEpoch'],Generation=value['Generation'],Revision=value['Revision'],BaseRevision=value['BaseRevision'],SnapshotId=value['SnapshotId'],Checksum=value['Checksum'],Words=value['Words'],MaterialTick=value['MaterialTick'],
            CommitToSendReturn=elapsed('ds',commit,'ds',sent[0]),SendReturnToReceive=elapsed('ds',sent[0],role,received[0]),
            ReceiveToInstalled=elapsed(role,received[0],role,value),CommitToInstalled=elapsed('ds',commit,role,value),
            InstalledToAckAccepted=elapsed(role,value,'ds',ack[0]),
            RendererRejections=[dict(Reason=x['Reason'],InstallToRejected=elapsed(role,value,role,x)) for x in reject])
        rows.append(row)
    role_rows=[x for x in rows if x['Role']==role]
    frames=reports[role]['Frames'];dt=[x['UnscaledDeltaTime']*1000 for x in frames]
    monotonic=[float(Fraction((b['Timestamp']-a['Timestamp'])*1000,clock[role]['StopwatchFrequency'])) for a,b in zip(frames,frames[1:])]
    role_summary[role]=dict(ActorId=actor,StageCounts=dict(Counter(x['Stage'] for x in events)),
        RendererRejectedReasons=dict(Counter(x['Reason'] for x in events if x['Stage']=='RendererRejected')),
        Viewport=[reports[role]['Width'],reports[role]['Height']],TargetFps=reports[role]['TargetFrameRate'],VSync=reports[role]['VSyncCount'],
        FocusedFrames=sum(x['Focused'] for x in frames),TotalFrames=len(frames),
        FrameDtMs=dict(P50=statistics.median(dt),P95=quantile(dt,.95),P99=quantile(dt,.99),Max=max(dt)),
        MonotonicFrameIntervalMs=dict(P50=statistics.median(monotonic),P95=quantile(monotonic,.95),P99=quantile(monotonic,.99),Max=max(monotonic)),
        Timings={name:stats([x[name] for x in role_rows]) for name in ['CommitToSendReturn','SendReturnToReceive','ReceiveToInstalled','CommitToInstalled','InstalledToAckAccepted']})
check('same target version sets both peers',set(installs['a'])==set(installs['b']))
peer_diffs=[elapsed('a',installs['a'][k],'b',installs['b'][k]) for k in owner]
guard=[json.loads(line) for line in (n/'memory-guard.jsonl').read_text(encoding='utf-8-sig').splitlines()]
memory_values=[x.get('AvailableBytes',x.get('AvailablePhysicalBytes')) for x in guard]
memory_values=[x for x in memory_values if isinstance(x,(int,float))]
output=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Checks=checks,
    Passed=sum(x['Passed'] for x in checks),Failed=sum(not x['Passed'] for x in checks),Clock=clock,
    ClockAssumption='Same physical Windows host, calibrated shared QPC origin/frequency. Conservative envelope of start/end brackets; raw counter precision is not scheduler accuracy.',
    AuthorityVersions=len(owner),AuthorityStageCounts=dict(Counter(x['Stage'] for x in reports['ds']['Events'])),
    Peers=role_summary,PerVersion=rows,AToBInstalled=stats(peer_diffs),FirstVersion=rows[:1]+[next(x for x in rows if x['Role']=='b')],
    MinAvailableBytes=min(memory_values) if memory_values else None,
    RawReportsSha256={role:hashlib.sha256((n/(role+'-timing.json')).read_bytes()).hexdigest() for role in reports},
    RendererChainComplete=False,VisibleLatencyProven=False,N5Complete=False,
    Limitation='Both peers only RendererRejected reason1(view/distance); no RendererRead/Seed/Upload/Draw for target. Reason1 does not distinguish frustum from distance. Do not claim visible latency fixed.',
    TimerOverhead='Enabled Record zero-GC method test passed; no Native disabled/enabled CPU-overhead A/B. Existing probes suppressed during window; 4Hz external guard and 15s DS state remain possible observation load.')
(n/'timing-analysis.json').write_text(json.dumps(output,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps({k:v for k,v in output.items() if k not in ['Checks','PerVersion','RawReportsSha256','FirstVersion']},ensure_ascii=False,indent=2))
