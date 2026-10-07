import json, pathlib, sys, time

t = pathlib.Path('Temp/ElementPerformance')
t.mkdir(parents=True, exist_ok=True)
request = t / (sys.argv[1] if len(sys.argv) > 2 else 'grid-liquid-capture.request')
command = sys.argv[-1]
start = time.time()
request.write_text(command, encoding='utf-8')
while request.exists():
    if time.time() - start > 50:
        raise RuntimeError('Editor did not consume request: ' + command)
    time.sleep(.15)
if request.name == 'grid-liquid-capture.request' and command not in ('play','stop','refresh'):
    result = t / 'grid-liquid-capture.json'
    while result.stat().st_mtime < start:
        if time.time() - start > 50:
            raise RuntimeError('Editor result is stale')
        time.sleep(.15)
    value = json.loads(result.read_text(encoding='utf-8-sig'))
    print(json.dumps({k: v for k, v in value.items() if k in ['Utc','Scene','SceneDirty','Playing','Paused','TimeScale','RunInBackground','MeasurementValid'] or k.startswith('Continuous') or k in ['TotalQuantity','ActiveCells','ResidentChunks']}, ensure_ascii=False), flush=True)
else:
    print('Consumed:', command, flush=True)
