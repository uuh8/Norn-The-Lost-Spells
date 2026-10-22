"""Archive the visibility revision, its live windows and restored level state."""
import datetime
import hashlib
import json
import pathlib
import shutil

root = pathlib.Path(__file__).resolve().parents[2]
temporary = root / 'Temp/ElementPerformance'
archive = root / 'docs/superpowers/validation/2026-10-04-liquid-visibility'
archive.mkdir(parents=True, exist_ok=True)


def read(name):
    return json.loads((temporary / name).read_text(encoding='utf-8-sig'))


gate_name = 'liquid-visibility-final-concentrated.json'
gate = read(gate_name)
if gate['Passed'] != 252 or gate['Failed'] or gate['Skipped'] or gate['Aborted'] or not gate['SceneRestored']:
    raise RuntimeError('Require the current complete 252-case diagnostic.')
if read('liquid-visibility-red.json')['Failed'] != 2 or read('liquid-dormant-capacity-red.json')['Failed'] != 1:
    raise RuntimeError('Behavior RED evidence is missing.')
final = read('liquid-visibility-final-scene.json')
if final['Playing'] or final['SceneDirty'] or final['RunInBackground'] or final['TimeScale'] != 1:
    raise RuntimeError('The original Editor state is not restored.')
audit = (temporary / 'p7-grid-cutover-audit.txt').read_text(encoding='utf-8-sig')
if 'Failures=0' not in audit.splitlines()[0]:
    raise RuntimeError('Current P7 wiring audit failed.')

names = {gate_name, 'liquid-visibility-red.json', 'liquid-activity-red.json',
         'liquid-dormant-capacity-red.json', 'liquid-visibility-focused-green.json',
         'liquid-visibility-expanded-green.json', 'liquid-visibility-priority-green.json',
         'liquid-visible-impact-gpu.json', 'liquid-visible-capture-ab.json',
         'liquid-visibility-final-scene.json', 'p7-grid-cutover-audit.txt',
         'gate-saved-root-state.json', 'visibility-distant-seeds.txt'}
names.update(path.name for path in temporary.glob('liquid-visibility-final-live-*') if path.is_file())
windows = []
for suffix in ('baseline', 'distant-120', 'near-mixed-5', 'near-mixed-10', 'near-mixed-15'):
    name = 'liquid-visibility-final-live-' + suffix
    data = read(name + '.json')
    if not data['Playing'] or not data['MeasurementValid'] or data['TimeScale'] != 1 or data['Samples'] < 128:
        raise RuntimeError('Invalid live profiling: ' + name)
    if data['ContinuousGpuBytes'] > 128 * 1048576 or data['ContinuousGcBytes'] or data['FirePeakGcBytes']:
        raise RuntimeError('GPU budget or module allocation failed: ' + name)
    if data['ContinuousCpuFallback'] or data['ContinuousOverflow'] or data['Rejected'] or data['Overflow']:
        raise RuntimeError('Unexpected fallback or Gameplay overflow: ' + name)
    summary = read(name + '.summary.json')
    if summary['Frames'] != 60 or summary['IncompleteRows']:
        raise RuntimeError('Incomplete raw Profiler export: ' + name)
    keys = ('Utc', 'Samples', 'P50Ms', 'P95Ms', 'TotalQuantity', 'ResidentChunks',
            'ActiveCells', 'SleepingCells', 'ContinuousPages', 'ContinuousVisiblePages',
            'ContinuousHybridPages', 'ContinuousCulledPages', 'ContinuousCaptureMs',
            'ContinuousDisplayLagMs', 'ContinuousHybridLagMs', 'ContinuousGpuBytes',
            'ContinuousGcBytes', 'FirePeakGcBytes')
    windows.append({'File': name + '.json', **{key: data[key] for key in keys},
                    'WholeFrameGcMeanBytes': summary['GcMeanBytes'],
                    'WholeFrameGcMaxBytes': summary['GcMaxBytes']})
if int(windows[1]['TotalQuantity']) - int(windows[0]['TotalQuantity']) != 120 * 256:
    raise RuntimeError('The distant seed quantity was not preserved.')

visuals = []
for path in archive.glob('continuous-lab-*-tick30.json'):
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    if not data['Ready'] or data['Quantity'] != data['Injected'] or data['SmallDrops'] or data['Overflow']:
        raise RuntimeError('Invalid lab capture: ' + path.name)
    visuals.append({'File': path.name, **{key: data[key] for key in
        ('Utc', 'Case', 'Quantity', 'Injected', 'StableQuantity', 'ReceivedOnFloor', 'Pages', 'GcBytes')}})

for name in sorted(names):
    shutil.copyfile(temporary / name, archive / name)

previous = json.loads((root / 'docs/superpowers/validation/2026-10-03-fire-view-projection/manifest.json').read_text(encoding='utf-8'))
sources = {entry['File'] for entry in previous['Sources']}
sources.update({
    'Assets/_Project/Scripts/Rendering/ElementField/MaterialPresentationView.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/MaterialPresentationView.cs.meta',
    'Assets/_Project/Scripts/Rendering/ElementField/StableLiquidSurfaceRenderer.cs',
    'Assets/_Project/Scripts/ElementField/Liquids/LiquidActivityScheduler.cs',
    'Assets/_Project/Scripts/ElementField/Liquids/LiquidWorldRuntime.cs',
    'Assets/_Project/Scripts/ElementField/Liquids/LiquidWorldProfile.cs',
    'Assets/_Project/Scripts/ElementField/Liquids/LiquidFluxSolver.cs',
    'Assets/_Project/Scripts/ElementField/Liquids/Stable/StableLiquidRegionRuntime.cs',
    'Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.Activity.cs',
    'Assets/_Project/Tests/Rendering/GridLiquidVisibilityTests.cs',
    'Assets/_Project/Tests/Rendering/GridLiquidVisibilityTests.cs.meta',
    'Assets/_Project/Tests/ElementField/LiquidFluxSolverDeadbandTests.cs',
    'Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset',
    'Assets/_Project/Editor/GridLiquidPrototypeCapture.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/GridLiquidFieldLabDriver.cs',
    'Tools/ElementPerformance/profile_liquid_visibility.py',
    'Tools/ElementPerformance/archive_liquid_visibility.py',
    'Assets/_Project/Docs/湖泊与地形制作指南.md',
    'Assets/_Project/Docs/2026-09-20 通用液体底层重构完整解析（更新中）.md',
    'docs/superpowers/plans/2026-10-01 液体密度分层混色与连续输运重构计划.md',
    'docs/superpowers/plans/2026-10-01 液体连续输运执行记录.md',
    'docs/superpowers/plans/2026-10-01 液体重构新对话交接.md',
    'docs/superpowers/validation/2026-10-04-liquid-visibility/README.md',
})
scene = 'Assets/_Project/Scenes/P7_DemoRun.unity'
scene_hash = hashlib.sha256((root / scene).read_bytes()).hexdigest()
previous_scene_hash = next(entry['Sha256'] for entry in previous['Sources'] if entry['File'] == scene)
scene_modified = datetime.datetime.fromtimestamp((root / scene).stat().st_mtime, datetime.timezone.utc)
if scene_modified > datetime.datetime.fromisoformat(gate['StartUtc'].replace('Z', '+00:00')):
    raise RuntimeError('Scene changed after the final diagnostic started; inspect it before archiving.')
manifest = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'Gate': {'File': gate_name, **{key: gate[key] for key in
        ('StartUtc', 'EndUtc', 'Passed', 'Failed', 'Skipped', 'SceneRestored')}},
    'SceneFileUnchangedFromTaskF9': scene_hash == previous_scene_hash,
    'SceneFileModifiedUtc': scene_modified.isoformat(),
    'PreviousSceneSha256': previous_scene_hash, 'CurrentSceneSha256': scene_hash,
    'Windows': windows, 'Visuals': visuals,
    'Sources': [{'File': name, 'Sha256': hashlib.sha256((root / name).read_bytes()).hexdigest()}
                for name in sorted(sources)],
    'Evidence': [{'File': path.name, 'Sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
                 for path in sorted(archive.iterdir()) if path.is_file() and path.name != 'manifest.json'],
}
(archive / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'Directory': str(archive), 'Passed': gate['Passed'], 'Sources': len(sources),
                  'Evidence': len(manifest['Evidence']), 'LiveWindows': len(windows),
                  'SceneFileUnchangedFromTaskF9': scene_hash == previous_scene_hash}, ensure_ascii=False))
