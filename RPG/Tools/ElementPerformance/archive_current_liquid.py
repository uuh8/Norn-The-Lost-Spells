"""Keep selected raw evidence outside Unity Temp; never save scene/assets."""
import datetime
import hashlib
import json
import pathlib
import shutil

root=pathlib.Path(__file__).resolve().parents[2]
t=root/'Temp/ElementPerformance'
out=root/'docs/superpowers/validation/2026-10-02-liquid-rounded-join'
out.mkdir(parents=True,exist_ok=True)
names=['final-liquid-regression-194.json','performance-layout-red.json',
       'cpu-bounded-work-red.json','cpu-bounded-work-green.json',
       'cpu-stable-domain-red.json',
       'continuous-support-volume.json','continuous-volume-calibration.json',
       'continuous-slope-contact.json','continuous-slope-temporal.json',
       'gate-saved-root-state.json','p7-grid-cutover-audit.txt','grid-liquid-capture.json']
for name in names:
    path=t/name
    if not path.exists():raise RuntimeError('Missing required evidence: '+name)
    shutil.copyfile(path,out/name)

# Keep all window snapshots, and full 60-frame raw samples for the conclusions
# actually compared. Earlier 64-frame/truncated exports stay diagnostic only.
prefixes=['pressure-before-valid','pressure-after-valid','pressure-final-valid','pressure-cpu-sliced-valid']
for prefix in prefixes:
    for path in t.glob(prefix+'-*.json'):shutil.copyfile(path,out/path.name)
raw=['pressure-before-valid-live-mixed-15','pressure-after-valid-full',
     'pressure-after-valid-frozen-compute','pressure-after-valid-hidden-liquid',
     'pressure-final-valid-live-mixed-25','pressure-final-valid-live-mixed-30',
     'pressure-cpu-sliced-valid-live-mixed-15','pressure-cpu-sliced-valid-live-mixed-30']
for stem in raw:
    for suffix in ['.tsv','-state.txt','.summary.json']:
        path=t/(stem+suffix)
        if path.exists():shutil.copyfile(path,out/path.name)
diagnostic=t/'pressure-before-live-mixed-15.json'
if diagnostic.exists():shutil.copyfile(diagnostic,out/diagnostic.name)

windows=[]
for prefix in prefixes:
    for path in sorted(out.glob(prefix+'-*.json')):
        if path.name.endswith('.summary.json'):continue
        report=json.loads(path.read_text(encoding='utf-8-sig'))
        keys=['Utc','Samples','TimeScale','MeasurementValid','P50Ms','P95Ms','MaxMs',
              'TotalQuantity','ActiveCells','ResidentChunks','ContinuousPages',
              'ContinuousCpuFallback','ContinuousGpuBytes','ContinuousCaptureMs',
              'ContinuousDisplayLagMs','ContinuousGcBytes']
        windows.append({'File':path.name,**{key:report[key] for key in keys}})
(out/'performance-windows.json').write_text(json.dumps(windows,ensure_ascii=False,indent=2),encoding='utf-8')

sources=list((root/'Assets/_Project/Scripts/Rendering/ElementField').glob('GridLiquid*.cs'))
sources+=list((root/'Assets/_Project/Tests/Rendering').glob('GridLiquid*Tests.cs'))
sources+=list((root/'Assets/_Project/Scripts/ElementField/Liquids').glob('*.cs'))
sources+=list((root/'Assets/_Project/Art/Elemental/Compute').glob('GridLiquid*'))
sources+=[root/'Assets/_Project/Art/Elemental/Shaders/ElementLiquidSurfaceLighting.hlsl',
          root/'Assets/_Project/Art/Elemental/Shaders/ElementLiquidContinuous.shader',
          root/'Assets/_Project/Editor/GridLiquidPrototypeCapture.cs',
          root/'Assets/_Project/Editor/ElementPerformanceGateRunner.cs',
          root/'Assets/_Project/Editor/ElementLoadedSceneGate.cs',
          root/'Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset']
records=[]
for path in sorted(set(sources)):
    records.append({'File':path.relative_to(root).as_posix(),'Sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
assemblies=[]
for name in ['Game.Rendering','Game.Rendering.Tests','Game.ElementField','Game.ElementField.Tests']:
    path=root/'Library/ScriptAssemblies'/(name+'.dll')
    stamp=datetime.datetime.fromtimestamp(path.stat().st_mtime,datetime.timezone.utc).isoformat()
    assemblies.append({'Name':name,'ModifiedUtc':stamp})
gate=json.loads((out/'final-liquid-regression-194.json').read_text(encoding='utf-8-sig'))
if gate['Failed'] or gate['Skipped'] or gate['Aborted'] or not gate['SceneRestored'] or gate['Passed']!=194:
    raise RuntimeError('The final gate is incomplete or did not restore the scene.')
previous=out/'source-manifest.json'
preserved=out/'before-stable-domain-source-manifest.json'
if previous.exists() and not preserved.exists():shutil.copyfile(previous,preserved)
manifest={'Utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
          'FinalGate':{'File':'final-liquid-regression-194.json',
                       **{key:gate[key] for key in ['StartUtc','EndUtc','Passed','Failed','Skipped','SceneRestored']}},
          'Sources':records,'Assemblies':assemblies}
(out/'source-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print('Archived',len(windows),'window snapshots and',len(records),'source hashes.')
