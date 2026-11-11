"""有界外部低内存守卫。只请求本轮身份匹配窗口WM_CLOSE，不终止进程、不修改系统。"""
from pathlib import Path
import psutil,json,time,datetime,ctypes as c,ctypes.wintypes as w
e=Path(r'F:\Develop Project\RPG\RPG\docs/superpowers/evidence/multiplayer/n5/fire-visible-latency-2026-11-01')
u=c.WinDLL('user32',use_last_error=True);u.GetWindowThreadProcessId.argtypes=[w.HWND,c.POINTER(w.DWORD)];u.PostMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM]
u.IsWindowVisible.argtypes=[w.HWND];u.IsWindowVisible.restype=w.BOOL
callback=c.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM);u.EnumWindows.argtypes=[callback,w.LPARAM];u.EnumWindows.restype=w.BOOL
end=time.monotonic()+360;triggered=False
out=e/'os-guard.jsonl';assert not out.exists()
with out.open('x',encoding='utf-8') as f:
    while time.monotonic()<end and not (e/'guard.stop').exists():
        m=psutil.virtual_memory();data=dict(Utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),AvailableBytes=m.available,CpuPercent=psutil.cpu_percent(),Triggered=False)
        ledger=e/'process-ledger.json';records=[]
        if ledger.exists():
            try:records=json.loads(ledger.read_text(encoding='utf-8-sig'))
            except json.JSONDecodeError:pass
        if isinstance(records,dict):records=[records]
        valid=[]
        for v in records:
            try:
                p=psutil.Process(v['ProcessId'])
                if v['Kind']=='NativeClient' and Path(p.exe()).resolve()==Path(v['Executable']).resolve() and abs(p.create_time()-v['StartUnix'])<.05:valid.append(p.pid)
            except (psutil.NoSuchProcess,psutil.AccessDenied):pass
        if m.available<3*2**30 and valid and not triggered:
            triggered=True;data['Triggered']=True;data['NormalCloseRequestedForOwnedPids']=valid
            @callback
            def close(hwnd,unused):
                pid=w.DWORD();u.GetWindowThreadProcessId(hwnd,c.byref(pid))
                if pid.value in valid and u.IsWindowVisible(hwnd):u.PostMessageW(hwnd,0x0010,0,0)
                return True
            u.EnumWindows(close,0);(e/'memory-stop.json').write_text(json.dumps(data,indent=2),encoding='utf-8')
        data['OwnedNativePids']=valid;f.write(json.dumps(data)+'\n');f.flush();time.sleep(.5)
print(json.dumps(dict(Trace=str(out),ThresholdGiB=3,Triggered=triggered,OnlyNormalClose=True)))
