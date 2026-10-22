"""Capture explicit temporary liquid labs, then restore the loaded level.

These PNGs and diagnostic readbacks are visual evidence, not FPS samples.
"""
import json
import pathlib
import shutil
import sys
import time

root=pathlib.Path(__file__).resolve().parents[2]
t=root/'Temp/ElementPerformance'
arguments=sys.argv[1:]
archive_name='2026-10-02-liquid-rounded-join'
if '--archive' in arguments:
    at=arguments.index('--archive')
    archive_name=arguments[at+1]
    del arguments[at:at+2]
    if pathlib.Path(archive_name).name!=archive_name:
        raise ValueError('Archive must be one folder name.')
archive=root/'docs/superpowers/validation'/archive_name
archive.mkdir(parents=True,exist_ok=True)
bridge=t/'grid-liquid-capture.request'
lab=t/'continuous-lab.request'

def send(command,path=bridge):
    if path.exists():raise RuntimeError('A request is already pending: '+str(path))
    start=time.time();path.write_text(command,encoding='utf-8')
    while path.exists():
        if time.time()-start>60:raise RuntimeError('Request pending: '+command)
        time.sleep(.1)
    if path==bridge and command not in ('play','stop','refresh'):
        result=t/'grid-liquid-capture.json'
        while not result.exists() or result.stat().st_mtime<start:
            if time.time()-start>60:raise RuntimeError('Snapshot stale: '+command)
            time.sleep(.1)
        return json.loads(result.read_text(encoding='utf-8-sig'))

def copy_capture(kind,suffix,start):
    stem='continuous-lab-'+kind+suffix
    report=t/(stem+'.json');deadline=time.time()+100
    while not report.exists() or report.stat().st_mtime<start:
        if time.time()>deadline:raise RuntimeError('Lab capture stale: '+stem)
        time.sleep(.2)
    image=t/(stem+'.png')
    if not image.exists() or image.stat().st_mtime<start:raise RuntimeError('Lab PNG stale: '+stem)
    for path in [report,image]:shutil.copyfile(path,archive/path.name)
    return json.loads(report.read_text(encoding='utf-8-sig'))

before=send('capture');entered=not before['Playing']
print('Before:',{k:before[k] for k in ['Scene','SceneDirty','Playing','RunInBackground']},flush=True)
try:
    if entered:send('play')
    deadline=time.time()+60
    while not send('capture')['Playing']:
        if time.time()>deadline:raise RuntimeError('Play did not start')
        time.sleep(.2)
    send('background-on');send('dismiss-validation-guide')
    cases=arguments or ['join30','join60','bulk','thin-cpu','same','layered','poison','sticky','lake-contact']
    for kind in cases:
        start=time.time();send(kind,lab);report=copy_capture(kind,'-tick30',start)
        print(kind,json.dumps({k:report[k] for k in ['Utc','Tick','Quantity','Injected','ReceivedOnFloor','Pages','CpuFallback','CaptureMs','GcBytes','SmallDrops','StableQuantity']},ensure_ascii=False),flush=True)
        if not report['Ready'] or report['Quantity']!=report['Injected'] or report['SmallDrops'] or report['Overflow']:
            raise RuntimeError('Lab failed: '+kind)
        # 保留落水的过程，终态实拍不能证明空中液舌。CPU只在完整批次可见后才拍摄。
        for tick in [5,15]:
            for extension in ['.json','.png']:
                source=t/('continuous-lab-'+kind+'-tick'+str(tick)+extension)
                if source.exists() and source.stat().st_mtime>=start:shutil.copyfile(source,archive/source.name)
        if kind=='join60':
            # Distinguish real geometry from lighting using the same frozen lab snapshot.
            for mode in ['opaque','raw-normals']:
                send(mode,lab);time.sleep(.25)
                start=time.time();send('capture',lab);copy_capture(kind,'',start)
                for extension in ['.json','.png']:
                    shutil.copyfile(archive/('continuous-lab-'+kind+extension),archive/('continuous-lab-'+kind+'-'+mode+extension))
            send('shaded',lab)
    send('close',lab)
finally:
    send('close',lab)
    if entered:send('stop')
    deadline=time.time()+60
    while entered and send('capture')['Playing']:
        if time.time()>deadline:raise RuntimeError('Play did not exit')
        time.sleep(.2)
    after=send('capture')
    (archive/'visual-scene-restored.json').write_text(json.dumps(after,ensure_ascii=False,indent=2),encoding='utf-8')
    print('After:',{k:after[k] for k in ['Scene','SceneDirty','Playing','RunInBackground']},flush=True)
