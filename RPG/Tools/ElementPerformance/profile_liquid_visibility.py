"""P7 live visibility/dormancy windows; never save or replace the loaded level."""
import json
import pathlib
import shutil
import sys
import time

temporary=pathlib.Path('Temp/ElementPerformance')
bridge=temporary/'grid-liquid-capture.request'
prefix=sys.argv[1] if len(sys.argv)>1 else 'liquid-visibility-live'

def send(command,path=bridge):
    if path.exists():raise RuntimeError('Existing request pending: '+str(path))
    started=time.time();path.write_text(command,encoding='utf-8')
    while path.exists():
        if time.time()-started>60:raise RuntimeError('Request timed out: '+command)
        time.sleep(.1)
    if path==bridge and command not in ('play','stop','refresh'):
        result=temporary/'grid-liquid-capture.json'
        while not result.exists() or result.stat().st_mtime<started:
            if time.time()-started>60:raise RuntimeError('Snapshot stale: '+command)
            time.sleep(.1)
        return json.loads(result.read_text(encoding='utf-8-sig'))

def window(name):
    time.sleep(.6);send('profile-window');deadline=time.time()+90
    while True:
        time.sleep(.5);report=send('capture')
        if report['Samples']>=128:break
        if time.time()>deadline:raise RuntimeError('Insufficient live samples: '+name)
    (temporary/(prefix+'-'+name+'.json')).write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    if not report['MeasurementValid'] or report['TimeScale']!=1:raise RuntimeError('Invalid live window: '+name)
    started=time.time();send('dump',temporary/'dump-profiler.request');finished=temporary/'editor-profiler-finished.txt'
    while not finished.exists() or finished.stat().st_mtime<started:
        if time.time()-started>60:raise RuntimeError('Profiler export incomplete')
        time.sleep(.1)
    for original,suffix in [('editor-profiler.tsv','.tsv'),('editor-profiler-state.txt','-state.txt')]:
        shutil.copyfile(temporary/original,temporary/(prefix+'-'+name+suffix))
    print(name,json.dumps({key:report[key] for key in ('Utc','Samples','P50Ms','P95Ms','TotalQuantity','ResidentChunks','ActiveCells','SleepingCells','ContinuousPages','ContinuousVisiblePages','ContinuousHybridPages','ContinuousCulledPages','ContinuousCpuFallback','ContinuousGcBytes','ContinuousHybridLagMs')},ensure_ascii=False),flush=True)
    return report

before=send('capture');entered=not before['Playing']
(temporary/(prefix+'-before.json')).write_text(json.dumps(before,ensure_ascii=False,indent=2),encoding='utf-8')
try:
    if entered:send('play')
    deadline=time.time()+60
    while not send('capture')['Playing']:
        if time.time()>deadline:raise RuntimeError('Play did not start')
        time.sleep(.2)
    send('background-on');send('dismiss-validation-guide');time.sleep(1)
    window('baseline')
    send('visibility-seed-distant');window('distant-120')
    for count in (5,10,15):
        send('cast-mixed-five');window('near-mixed-'+str(count))
    started=time.time();send('screenshot');picture=temporary/'grid-liquid-game.png'
    while not picture.exists() or picture.stat().st_mtime<started:
        if time.time()-started>20:raise RuntimeError('Screenshot stale')
        time.sleep(.1)
    shutil.copyfile(picture,temporary/(prefix+'-game.png'))
finally:
    send('isolate-liquid-restore');send('finish-flow-profile')
    if entered:send('stop')
    deadline=time.time()+60
    while entered and send('capture')['Playing']:
        if time.time()>deadline:raise RuntimeError('Play did not stop')
        time.sleep(.2)
    after=send('capture');(temporary/(prefix+'-restored.json')).write_text(json.dumps(after,ensure_ascii=False,indent=2),encoding='utf-8')
    print('Restored:',{key:after[key] for key in ('Scene','SceneDirty','Playing','RunInBackground','TimeScale')},flush=True)
