"""用两个自有隐藏小窗验证真正正常关闭，控制窗不在守卫目标范围。失败不强杀。"""
from pathlib import Path
import subprocess,sys,time,json,uuid,psutil,ctypes as c,ctypes.wintypes as w,datetime
t=Path(__file__).parent;stage=Path(r'F:\Develop Project\RPG\RPG\docs\superpowers\evidence\multiplayer\n5\fire-visible-latency-2026-11-01\instrumented-2026-11-01\guard-self-test')
assert not stage.exists();stage.mkdir();pythonw=Path(sys.executable).with_name('pythonw.exe');assert pythonw.exists()
records=[];processes=[]
si=subprocess.STARTUPINFO();si.dwFlags|=subprocess.STARTF_USESHOWWINDOW;si.wShowWindow=0
for role in ['target','control']:
    title='NornGuardFixture_'+uuid.uuid4().hex;ready=stage/(role+'-ready.json')
    proc=subprocess.Popen([str(pythonw),str(t/'guard-hidden-window-fixture.py'),'--title',title,'--ready',str(ready)],startupinfo=si,creationflags=subprocess.CREATE_NO_WINDOW);processes.append(proc)
    end=time.monotonic()+10
    while not ready.exists() and time.monotonic()<end:time.sleep(.05)
    assert ready.exists(),'Fixture startup not ready; inspect own PID rather than kill'
    q=psutil.Process(proc.pid);records.append(dict(ProcessId=q.pid,StartUnix=q.create_time(),Executable=q.exe(),Kind='GuardTestWindow' if role=='target' else 'GuardTestControl',WindowTitle=title))
(stage/'process-ledger.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
run=subprocess.run([sys.executable,str(t/'owned-native-memory-guard.py'),'--stage',str(stage),'--seconds','8','--self-test-title',records[0]['WindowTitle']],capture_output=True,text=True,encoding='utf-8',timeout=35)
target_closed=processes[0].poll() is not None;control_alive=processes[1].poll() is None
result=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),GuardExit=run.returncode,TargetActuallyExitedNormally=target_closed,UnlistedControlStillAlive=control_alive,NoActualMemoryPressureInjected=True,GuardOutput=json.loads(run.stdout) if run.stdout.strip() else None)
# 控制窗也是本测试自己创建的：验证身份后单独正常关闭，不按进程名结束。
if control_alive:
    user=c.WinDLL('user32',use_last_error=True);user.GetWindowThreadProcessId.argtypes=[w.HWND,c.POINTER(w.DWORD)];user.PostMessageW.argtypes=[w.HWND,w.UINT,w.WPARAM,w.LPARAM]
    callback=c.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM);user.EnumWindows.argtypes=[callback,w.LPARAM]
    @callback
    def close(hwnd,unused):
        pid=w.DWORD();user.GetWindowThreadProcessId(hwnd,c.byref(pid))
        if pid.value==records[1]['ProcessId']:user.PostMessageW(hwnd,0x0010,0,0)
        return True
    user.EnumWindows(close,0)
    try:processes[1].wait(timeout=8)
    except subprocess.TimeoutExpired:result['ControlNormalClosePending']=True
result['ControlAlsoExitedNormally']=processes[1].poll() is not None
(stage/'verification.json').write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps(result))
assert run.returncode==0 and target_closed and control_alive and result['ControlAlsoExitedNormally'],'Guard did not prove normal-close isolation'
