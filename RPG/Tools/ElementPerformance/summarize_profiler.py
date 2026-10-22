import csv, collections, json, pathlib, sys, math

for argument in sys.argv[1:]:
    path=pathlib.Path(argument); rows=list(csv.DictReader(path.open(encoding='utf-8-sig'),delimiter='\t'))
    values=collections.defaultdict(list); allocations=collections.Counter();frames=set();incomplete=0
    for row in rows:
        if not row.get('ms') or not row.get('startMs'):incomplete+=1;continue
        frames.add(int(row['frame']));name=row['sample'];thread=row['thread'];value=float(row['ms'])
        if name.startswith(('GridLiquid.','ElementWorld.','FireParcel.','GpuFire.','PBF.')) or name in ['RenderLoop','EditorLoop','PlayerLoop','GC.Alloc','Gfx.WaitForPresentOnGfxThread','WaitForTargetFPS']:
            values[(thread,name)].append(value)
        if name=='GC.Alloc':allocations[int(row['frame'])]+=int(row['gcBytes'])
    summary=[]
    for (thread,name),items in values.items():
        ordered=sorted(items)
        summary.append({'Thread':thread,'Marker':name,'Calls':len(items),'MeanCallMs':sum(items)/len(items),'MaxCallMs':max(items),'TotalPerFrameMs':sum(items)/max(1,len(frames))})
    summary.sort(key=lambda x:x['TotalPerFrameMs'],reverse=True)
    result={'Input':str(path),'Frames':len(frames),'IncompleteRows':incomplete,'GcMeanBytes':sum(allocations.values())/max(1,len(frames)),'GcMaxBytes':max(allocations.values(),default=0),'Markers':summary}
    path.with_suffix('.summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(path.name,'frames',result['Frames'],'GC mean/max',round(result['GcMeanBytes']),result['GcMaxBytes'])
    for marker in summary[:13]:print(marker)
