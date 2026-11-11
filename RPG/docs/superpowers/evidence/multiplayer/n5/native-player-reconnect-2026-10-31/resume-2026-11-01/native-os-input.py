"""仅向本轮已记录 PID/启动时间/可执行路径的 Native 窗口发送真实 OS 输入。
不调用 Unity API、EventBus、位移或 Button.Invoke；登录内容只在私有会话内读取，不记录文字。
"""
from pathlib import Path
import argparse, ctypes as c, ctypes.wintypes as w, json, time, datetime, re
import psutil
u=c.WinDLL('user32',use_last_error=True)
u.SetProcessDPIAware()
u.GetForegroundWindow.restype=w.HWND
u.GetWindowThreadProcessId.argtypes=[w.HWND,c.POINTER(w.DWORD)]
u.SetForegroundWindow.argtypes=[w.HWND];u.SetForegroundWindow.restype=w.BOOL
u.SetFocus.argtypes=[w.HWND];u.SetFocus.restype=w.HWND
u.ShowWindow.argtypes=[w.HWND,c.c_int]
u.BringWindowToTop.argtypes=[w.HWND]
u.AttachThreadInput.argtypes=[w.DWORD,w.DWORD,w.BOOL];u.AttachThreadInput.restype=w.BOOL
ULONG_PTR=w.WPARAM
class MOUSE(c.Structure): _fields_=[('dx',w.LONG),('dy',w.LONG),('data',w.DWORD),('flags',w.DWORD),('time',w.DWORD),('extra',ULONG_PTR)]
class KEY(c.Structure): _fields_=[('vk',w.WORD),('scan',w.WORD),('flags',w.DWORD),('time',w.DWORD),('extra',ULONG_PTR)]
class UNION(c.Union): _fields_=[('mouse',MOUSE),('key',KEY)]
class INPUT(c.Structure): _fields_=[('kind',w.DWORD),('data',UNION)]
p=argparse.ArgumentParser();p.add_argument('--session',required=True);p.add_argument('--client',choices=['a','b'],required=True)
p.add_argument('action',choices=['ui','node','key','mouse','login','screen','navigate']);p.add_argument('--match');p.add_argument('--name');p.add_argument('--key',default='W');p.add_argument('--seconds',type=float,default=.15);p.add_argument('--dx',type=int,default=0);p.add_argument('--dy',type=int,default=0);p.add_argument('--out')
a=p.parse_args();session=json.loads(Path(a.session).read_text(encoding='utf-8-sig'));record=session['Clients'][a.client];pid=record['ProcessId']
proc=psutil.Process(pid)
assert Path(proc.exe()).resolve()==Path(record['Executable']).resolve(),'Owned executable changed'
assert abs(proc.create_time()-record['StartUnix'])<.05,'Owned PID start changed'
assert Path(proc.exe()).name=='NornMenuClient.exe','Only formal Native acceptance client allowed'
config=json.loads(Path(record['Config']).read_text(encoding='utf-8-sig'))
handles=[]
CALLBACK=c.WINFUNCTYPE(w.BOOL,w.HWND,w.LPARAM)
@CALLBACK
def enum(hwnd,unused):
    owner=w.DWORD();u.GetWindowThreadProcessId(hwnd,c.byref(owner))
    if owner.value==pid and u.IsWindowVisible(hwnd): handles.append(hwnd)
    return True
u.EnumWindows(enum,0);assert handles,'Owned visible Native window missing';hwnd=handles[0]
def owner():
    current=u.GetForegroundWindow();value=w.DWORD();u.GetWindowThreadProcessId(current,c.byref(value));return value.value
def guard(): assert owner()==pid,'Foreground changed; input stopped'
def focus():
    u.ShowWindow(hwnd,9);u.BringWindowToTop(hwnd);u.SetForegroundWindow(hwnd);time.sleep(.15)
    if owner()!=pid:
        foreground=u.GetForegroundWindow();tid=u.GetWindowThreadProcessId(foreground,None);current=c.windll.kernel32.GetCurrentThreadId()
        # CLI线程先创建消息队列，才能按Win32约束临时连接输入队列；始终只激活已验证Native HWND。
        message=c.create_string_buffer(128);u.PeekMessageW(c.byref(message),None,0,0,0)
        target=u.GetWindowThreadProcessId(hwnd,None);attached=[]
        for thread in set([tid,target]):
            if thread and thread!=current and u.AttachThreadInput(current,thread,True):attached.append(thread)
        try: u.BringWindowToTop(hwnd);u.SetForegroundWindow(hwnd);u.SetFocus(hwnd)
        finally:
            for thread in attached:u.AttachThreadInput(current,thread,False)
        time.sleep(.15)
    guard()
def send(event):
    guard();array=(INPUT*1)(event);assert u.SendInput(1,array,c.sizeof(INPUT))==1,'OS SendInput rejected'
def key(vk,up=False,scan=0,unicode=False):
    # 游戏Input System按硬件扫描码处理方向键；Unicode仅用于账号输入框，二者不能混用。
    if not unicode:scan=u.MapVirtualKeyW(vk,0);vk=0
    event=INPUT();event.kind=1;event.data.key=KEY(vk,scan,(2 if up else 0)|(4 if unicode else 8),0,0);send(event)
def hold(vk,seconds):
    assert 0<seconds<=3,'Bounded input only';key(vk)
    try:
        end=time.monotonic()+seconds
        while time.monotonic()<end: guard();time.sleep(.03)
    finally:
        # 只发送本操作自己按下的松键，不批量重置用户输入。
        event=INPUT();event.kind=1;event.data.key=KEY(0,u.MapVirtualKeyW(vk,0),10,0,0);u.SendInput(1,c.byref(event),c.sizeof(INPUT))
def mouse(flags,dx=0,dy=0):
    event=INPUT();event.kind=0;event.data.mouse=MOUSE(dx,dy,0,flags,0,0);send(event)
def rect():
    box=w.RECT();assert u.GetClientRect(hwnd,c.byref(box));origin=w.POINT(0,0);assert u.ClientToScreen(hwnd,c.byref(origin));return origin.x,origin.y,box.right,box.bottom
def report():
    command=Path(config['Command']+'.ui');out=Path(config['Output']+'.ui.json');assert not command.exists(),'Existing UI report request pending'
    old=out.stat().st_mtime_ns if out.exists() else 0;command.write_text('report',encoding='utf-8')
    end=time.monotonic()+4
    while time.monotonic()<end:
        if out.exists() and out.stat().st_mtime_ns>old:return json.loads(out.read_text(encoding='utf-8-sig'))
        time.sleep(.1)
    raise RuntimeError('Fresh UI report timeout')
def click_node(node,data):
    assert node['Interactable'],'UI node disabled';guard();x,y,width,height=rect();cx=node['Center']['x']*width/data['Width'];cy=(data['Height']-node['Center']['y'])*height/data['Height'];assert 0<=cx<width and 0<=cy<height
    assert u.SetCursorPos(round(x+cx),round(y+cy));time.sleep(.12);mouse(2);time.sleep(.08);mouse(4);time.sleep(.2)
def text(value):
    for char in value:
        for i in range(0,len(raw:=char.encode('utf-16-le')),2):
            scan=int.from_bytes(raw[i:i+2],'little');key(0,scan=scan,unicode=True);key(0,True,scan,True)
        time.sleep(.025)
def node_find(data,kind=None,name=None,label=None):
    found=[n for n in data['Nodes'] if (not kind or n['Kind']==kind) and (not name or re.search(name,n['Name'])) and (not label or re.search(label,n.get('Label','')))]
    assert len(found)==1,'UI selector must match exactly one node: '+str([(n['Kind'],n['Name'],n.get('Label','')) for n in found]);return found[0]
focus();started=datetime.datetime.now(datetime.timezone.utc).isoformat();result={}
if a.action=='ui':
    data=report();result={k:data.get(k) for k in ('RecordedAtUtc','Width','Height','CursorLock','Focused','InventoryIsOpen')};result['Nodes']=[dict(Kind=n['Kind'],Name=n['Name'],Label=n.get('Label',''),Interactable=n['Interactable']) for n in data['Nodes'] if n['Kind'] in ('Button','InputField')]
elif a.action=='node':
    for attempt in range(6):
        data=report()
        candidates=[n for n in data['Nodes'] if (not a.name or re.search(a.name,n['Name'])) and (not a.match or re.search(a.match,n.get('Label','')))]
        if len(candidates)==1 and candidates[0]['Interactable']:break
        if attempt==5:raise RuntimeError('Expected enabled original UI node not ready: '+str(a.name))
        time.sleep(.2)
    node=candidates[0];click_node(node,data);result={'Node':node['Name'],'Label':node.get('Label','')}
elif a.action=='key':
    keys={'SPACE':32,'TAB':9,'ESC':27,'ENTER':13,'SHIFT':16};vk=keys.get(a.key.upper(),ord(a.key.upper()) if len(a.key)==1 else 0);assert vk;hold(vk,a.seconds);result={'Key':a.key,'Seconds':a.seconds}
elif a.action=='mouse':
    if a.dx or a.dy: mouse(1,a.dx,a.dy);result={'RelativeMouse':True,'Dx':a.dx,'Dy':a.dy}
    else:
        mouse(2)
        try:
            end=time.monotonic()+a.seconds
            while time.monotonic()<end:guard();time.sleep(.03)
        finally:mouse(4)
        result={'AttackSeconds':a.seconds}
elif a.action=='login':
    data=report();inputs=[n for n in data['Nodes'] if n['Kind']=='InputField'];assert len(inputs)==2,'Expected original username/password fields'
    # 原始菜单对象名明确区分两输入；不从Text诊断读取任何现有文字。
    for pattern,field in (('(?i)user','Username'),('(?i)pass','Password')):
        click_node(node_find(data,kind='InputField',name=pattern),data);text(session['Users'][a.client][field])
    data=report();click_node(node_find(data,kind='Button',label='^登录$'),data);result={'CredentialsEntered':True,'CredentialTextRecorded':False}
elif a.action=='navigate':
    import math
    assert a.match in ('p7','p8');target_suffix={'p7':'P7_DemoRun.unity','p8':'P8_BossField.unity'}[a.match]
    def portal_after(previous=None,not_before=None):
        end=time.monotonic()+12
        while time.monotonic()<end:
            try:
                state=json.loads(Path(config['Output']).read_text(encoding='utf-8-sig'))
                stamp=datetime.datetime.fromisoformat(state['RecordedAtUtc'].replace('Z','+00:00'))
                if state.get('RecordedAtUtc')!=previous and state.get('Focused') and (not_before is None or stamp>=not_before) and (datetime.datetime.now(datetime.timezone.utc)-stamp).total_seconds()<2:
                    if not state.get('HubActorAvailable') or not state.get('HubActive'):
                        time.sleep(.1);continue
                    if state.get('ChoiceOpen'):return state
                    assert state['TimeScale']>0,'Existing pause captures input'
                    return state
            except (FileNotFoundError,json.JSONDecodeError):pass
            time.sleep(.1)
        raise RuntimeError('Fresh focused Hub navigation report unavailable')
    state=portal_after();steps=[];speed=3.5
    for index in range(40):
        portals=[x for x in state['HubPortals'] if x['ScenePath'].endswith(target_suffix) and x['ActiveInHierarchy']]
        assert len(portals)==1,'One authored target portal required';target=portals[0]['Position'];pos=state['HubActorPosition'];dx=target['x']-pos['x'];dz=target['z']-pos['z'];distance=math.hypot(dx,dz)
        if state.get('ChoiceOpen') or distance<1.8:break
        # Hub中路有真实站点碰撞；P8使用已观察到的通行路线，仍完全由WASD与碰撞系统推进。
        if a.match=='p8' and pos['z']<20.6:dx,dz=0,20.8-pos['z']
        elif a.match=='p8' and pos['z']<30 and pos['x']<9:dx,dz=10-pos['x'],0
        forward=state['CameraForward'];length=math.hypot(forward['x'],forward['z']);assert length>.001
        fx,fz=forward['x']/length,forward['z']/length
        choices=[('W',dx*fx+dz*fz),('S',-dx*fx-dz*fz),('D',dx*fz-dz*fx),('A',-dx*fz+dz*fx)]
        key_name,along=max(choices,key=lambda x:x[1]);duration=min(.7,max(.10,along/speed*.8));hold(ord(key_name),duration)
        newer=portal_after(state['RecordedAtUtc'],datetime.datetime.now(datetime.timezone.utc));newpos=newer['HubActorPosition'];travel=math.hypot(newpos['x']-pos['x'],newpos['z']-pos['z'])
        steps.append(dict(Key=key_name,Seconds=duration,Before=pos,After=newpos,SourceUtc=newer['RecordedAtUtc']))
        if not newer.get('ChoiceOpen'):assert travel>.03,'Actual player did not move; stop rather than teleport'
        speed=max(1,min(15,travel/duration));state=newer
    else:raise RuntimeError('Bounded real-input navigation did not reach portal')
    pressed=not state.get('ChoiceOpen')
    if pressed:hold(ord('F'),.12)
    result={'Portal':a.match,'RealOSKeyboardOnly':True,'Steps':steps,'FinalPlanarDistance':distance,'FPressed':pressed}
elif a.action=='screen':
    from PIL import ImageGrab
    assert a.out;left,top,width,height=rect();ImageGrab.grab(bbox=(left,top,left+width,top+height)).save(a.out);result={'Screenshot':a.out,'OnlyOwnedClientRect':True}
entry=dict(Client=a.client,ProcessId=pid,StartedUtc=started,FinishedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Action=a.action,Result=result)
with Path(session['InputAudit']).open('a',encoding='utf-8') as out:out.write(json.dumps(entry,ensure_ascii=False)+'\n')
print(json.dumps(entry,ensure_ascii=False))
