import json, pathlib, shutil, time, sys

t=pathlib.Path('Temp/ElementPerformance'); bridge=t/'grid-liquid-capture.request'
t.mkdir(parents=True,exist_ok=True)
archive=pathlib.Path('docs/superpowers/validation/2026-10-04-liquid-capture-spikes');archive.mkdir(parents=True,exist_ok=True)
prefix=sys.argv[1] if len(sys.argv)>1 else 'mixed'
bursts=int(sys.argv[2]) if len(sys.argv)>2 else 1
live_only='live-only' in sys.argv[3:]
protect_player='protect-player' in sys.argv[3:]

def send(value,path=bridge):
    path.write_text(value,encoding='utf-8'); started=time.time()
    while path.exists():
        if time.time()-started>120: raise RuntimeError('Request pending: '+value)
        time.sleep(.1)
    if path==bridge and value not in ('play','stop','refresh'):
        result=t/'grid-liquid-capture.json'
        while not result.exists() or result.stat().st_mtime<started:
            if time.time()-started>120:raise RuntimeError('Capture pending: '+value)
            time.sleep(.1)
        return json.loads(result.read_text(encoding='utf-8-sig'))

def window(name,command=None,live=True):
    if command:send(command)
    # 不把上个窗口导出的Editor分配/同步计入下个稳定窗口。
    time.sleep(.5);send('profile-window')
    deadline=time.time()+150
    while True:
        time.sleep(.5);r=send('capture')
        if r['Samples']>=128:break
        if time.time()>deadline:raise RuntimeError('Insufficient frames: '+name)
    (t/(prefix+'-'+name+'.json')).write_text(json.dumps(r,ensure_ascii=False,indent=2),encoding='utf-8')
    shutil.copyfile(t/(prefix+'-'+name+'.json'),archive/(prefix+'-'+name+'.json'))
    if live and (not r['MeasurementValid'] or r['TimeScale']!=1):
        # 玩家死亡/说明面板可能重新暂停世界。保留诊断JSON并结束本轮，
        # 不把停止模拟的帧时间加入持续输运结论，也不继续施法假装有效。
        raise RuntimeError('Invalid live window (world is paused): '+name+' Reason='+r.get('PauseReasons','unknown'))
    started=time.time();send('dump',t/'dump-profiler.request')
    finished=t/'editor-profiler-finished.txt'
    while not finished.exists() or finished.stat().st_mtime<started:
        if time.time()-started>90:raise RuntimeError('Profiler export is incomplete')
        time.sleep(.1)
    shutil.copyfile(t/'editor-profiler.tsv',t/(prefix+'-'+name+'.tsv'))
    shutil.copyfile(t/'editor-profiler-state.txt',t/(prefix+'-'+name+'-state.txt'))
    shutil.copyfile(t/(prefix+'-'+name+'.tsv'),archive/(prefix+'-'+name+'.tsv'))
    shutil.copyfile(t/(prefix+'-'+name+'-state.txt'),archive/(prefix+'-'+name+'-state.txt'))
    print(name,json.dumps({k:r[k] for k in ['Utc','TimeScale','Samples','P50Ms','P95Ms','ContinuousPages','ContinuousCpuFallback','ContinuousCaptureMs','ContinuousDisplayLagMs','ContinuousGcBytes','ActiveCells','ResidentChunks','TotalQuantity']},ensure_ascii=False),flush=True)
    return r

before=send('capture');entered=not before['Playing'];frozen=False
print('Before:',{k:before[k] for k in ['Scene','SceneDirty','Playing','RunInBackground']},flush=True)
try:
    if entered:send('play')
    deadline=time.time()+120
    while not send('capture')['Playing']:
        if time.time()>deadline:raise RuntimeError('Play pending')
        time.sleep(.2)
    send('background-on')
    # 只有本脚本新进入的临时Play才允许保护角色；不接管用户正在游玩的伤害状态。
    if protect_player:
        if not entered:raise RuntimeError('Protected benchmark requires a fresh diagnostic Play session')
        send('protect-benchmark-player')
    send('dismiss-validation-guide')
    time.sleep(1)
    window('baseline')
    for burst in range(bursts):
        send('cast-mixed-five');r=window('live-mixed' if bursts==1 else 'live-mixed-'+str((burst+1)*5))
        # 已复现容量边界就结束累积；不继续把用户Editor拖入无限增长的压力负载。
        if r['ContinuousCpuFallback']:break
    # 只在帧时间窗口结束后截图；Readback/PNG开销不能混入正常施法性能数据。
    screenshot=t/'grid-liquid-game.png';shot_started=time.time();send('screenshot')
    deadline=time.time()+15
    while not screenshot.exists() or screenshot.stat().st_mtime<shot_started:
        if time.time()>deadline:raise RuntimeError('Game screenshot is stale')
        time.sleep(.1)
    shutil.copyfile(screenshot,t/(prefix+'-game.png'))
    shutil.copyfile(screenshot,archive/(prefix+'-game.png'))
    if not live_only and not r['ContinuousCpuFallback']:
        send('isolate-freeze-clock');frozen=True
        for name,command in [('full','isolate-liquid-full'),('frozen-compute','isolate-liquid-frozen'),('hidden-liquid','isolate-liquid-hidden'),('full-repeat','isolate-liquid-full')]:
            window(name,command,live=False)
        # 液体冻结且隐藏后再隐藏火焰，区分两种Presentation的GPU成本。
        send('isolate-liquid-hidden')
        window('hidden-liquid-and-fire','isolate-fire-hidden',live=False)
        window('hidden-liquid-fire-repeat','isolate-fire-full',live=False)
finally:
    send('isolate-fire-full')
    send('isolate-liquid-restore')
    if frozen:send('isolate-restore-clock')
    send('finish-flow-profile')
    if entered:send('stop')
    deadline=time.time()+60
    while entered and send('capture')['Playing']:
        if time.time()>deadline:raise RuntimeError('Play exit pending')
        time.sleep(.2)
    after=send('capture')
    print('After:',{k:after[k] for k in ['Scene','SceneDirty','Playing','RunInBackground']},flush=True)
