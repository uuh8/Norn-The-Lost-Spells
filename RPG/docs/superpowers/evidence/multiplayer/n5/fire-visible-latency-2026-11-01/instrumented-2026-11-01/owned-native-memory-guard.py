"""本任务外部负载守卫：只给身份匹配的自有顶层窗口发送正常WM_CLOSE，无强杀/系统设置。
模拟低内存仅用于显式self-test隐藏测试窗，不允许作用于Native客户端。
"""
from pathlib import Path
import argparse,ctypes as c,ctypes.wintypes as w,json,time,datetime,psutil
p=argparse.ArgumentParser();p.add_argument('--stage',required=True);p.add_argument('--seconds',type=int,default=900)
p.add_argument('--self-test-title');a=p.parse_args();assert 5<=a.seconds<=1800
stage=Path(a.stage).resolve();roots=[Path(r'C:\Users\zhy\Documents\Codex\2026-10-31\task').resolve(),Path(r'F:\Develop Project\RPG\RPG\docs\superpowers\evidence\multiplayer\n5').resolve()]
assert any(stage==r or r in stage.parents for r in roots),'Stage outside task evidence roots'
stage.mkdir(exist_ok=True);is_test=bool(a.self_test_title)
if is_test:assert a.self_test_title.startswith('NornGuardFixture_')
u=c.WinDLL('user32',use_last_error=True);u.GetWindowThreadProcessId.argtypes=[w.HWND,c.POINTER(w.DWORD)]
u.GetWindowTextLengthW.argtypes=[w.HWND];u.GetWindowTextLengthW.restype=c.c_int
u.GetWindowTextW.argtypes=[w.HWND,w.LPWSTR,c.c_int];u.PostMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM];u.PostMessageW.restype=w.BOOL
callback=c.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM);u.EnumWindows.argtypes=[callback,w.LPARAM];u.EnumWindows.restype=w.BOOL
def save(path,value):path.write_text(json.dumps(value,indent=2),encoding='utf-8')
def read_records():
    path=stage/'process-ledger.json'
    if not path.exists():return []
    try:data=json.loads(path.read_text(encoding='utf-8-sig'))
    except json.JSONDecodeError:return []
    return data if isinstance(data,list) else [data]
def owned():
    result=[]
    for v in read_records():
        if v.get('Stopped') or v.get('Kind')!=('GuardTestWindow' if is_test else 'NativeClient'):continue
        try:
            q=psutil.Process(v['ProcessId']);exe=Path(q.exe()).resolve()
            if exe!=Path(v['Executable']).resolve() or abs(q.create_time()-v['StartUnix'])>=.05:continue
            if not is_test and exe!=Path(r'F:\Develop Project\RPG\RPG\Builds\OnlineAccounts\Menu\NornMenuClient.exe').resolve():continue
            if is_test and (exe.name.lower()!='pythonw.exe' or v.get('WindowTitle')!=a.self_test_title):continue
            result.append(q.pid)
        except (psutil.NoSuchProcess,psutil.AccessDenied):pass
    return result
def normal_close(pids):
    sent=[]
    @callback
    def visit(hwnd,unused):
        pid=w.DWORD();u.GetWindowThreadProcessId(hwnd,c.byref(pid))
        if pid.value not in pids:return True
        n=u.GetWindowTextLengthW(hwnd);buf=c.create_unicode_buffer(n+1);u.GetWindowTextW(hwnd,buf,len(buf))
        # self-test精确匹配隐藏测试窗；正式只关自有Norn顶层窗口，不向其他辅助/用户窗口广播。
        match=buf.value==a.self_test_title if is_test else buf.value.startswith('Norn')
        if match and u.PostMessageW(hwnd,0x0010,0,0):sent.append(dict(Pid=pid.value,WindowTitle=buf.value))
        return True
    u.EnumWindows(visit,0);return sent
out=stage/'memory-guard.jsonl';assert not out.exists(),'Preserve previous guard trace'
start=time.monotonic();end=start+a.seconds;trigger=None;last_close=-100;close_events=[]
with out.open('x',encoding='utf-8') as f:
    while True:
        now=time.monotonic();mem=psutil.virtual_memory();pids=owned();fake=is_test and now-start>=.5
        available=2**30 if fake else mem.available
        if trigger is None and ((available<3*2**30 and pids) or now>=end or (stage/'guard.stop').exists()):
            reason='memory-below-3GiB' if available<3*2**30 and pids else ('guard-deadline' if now>=end else 'normal-owner-stop')
            trigger=dict(Utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Reason=reason,SimulatedMemory=is_test,AvailableBytes=available,OwnedPidsAtTrigger=pids)
            if reason!='normal-owner-stop':save(stage/'memory-stop.json',trigger)
            save(stage/'guard-trigger.json',trigger);trigger_started=now
        row=dict(Utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),AvailableBytes=mem.available,EffectiveAvailableBytes=available,SimulatedMemory=is_test,OwnedPids=pids)
        if trigger and pids and now-last_close>=1:
            events=normal_close(pids);last_close=now;close_events+=events;row['NormalCloseRequests']=events
        f.write(json.dumps(row)+'\n');f.flush()
        if trigger and (not pids or now-trigger_started>=20):break
        time.sleep(.25)
remaining=owned();result=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),SelfTest=is_test,Trigger=trigger,NormalCloseRequests=close_events,RemainingOwnedPids=remaining,ForcedTerminationUsed=False)
save(stage/'guard-result.json',result);print(json.dumps(result));raise SystemExit(0 if not remaining else 2)
