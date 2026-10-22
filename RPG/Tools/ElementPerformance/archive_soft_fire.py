"""Archive this uniform-fire preview without altering the earlier mesh evidence."""
import datetime
import hashlib
import json
import pathlib
import shutil
from PIL import Image

root = pathlib.Path(__file__).resolve().parents[2]
t = root / 'Temp/ElementPerformance'
archive = root / 'docs/superpowers/validation/2026-10-03-fire-unified-soft'
archive.mkdir(parents=True, exist_ok=True)


def read(name):
    return json.loads((t / name).read_text(encoding='utf-8-sig'))


gate = read('fire-soft-concentrated.json')
if gate['Passed'] != 241 or gate['Failed'] or gate['Skipped'] or gate['Aborted'] or not gate['SceneRestored']:
    raise RuntimeError('Require current complete 241-case regression.')
red = read('fire-soft-red.json')
green = read('fire-soft-green.json')
if red['Failed'] != 3 or green['Passed'] != 3 or not red['SceneRestored'] or not green['SceneRestored']:
    raise RuntimeError('Require actual RED/GREEN evidence and scene restoration.')

views = []
for suffix in ('near-0', 'near-1', 'near-2', 'near-low', 'forced-far-lod'):
    name = 'fire-soft-preview-' + suffix
    d = read(name + '.json')
    if not d['Ready'] or d['ReadbackError'] or d['HasSurfaceResources'] or d['UsesLocalSurface'] or d['DrawCalls'] != 2:
        raise RuntimeError('Unexpected fire path: ' + name)
    if not d['Surface'] or not d['Gas'] or d['PeakGcBytes']:
        raise RuntimeError('Missing lifecycle or allocation regression: ' + name)
    with Image.open(t / (name + '.png')) as picture:
        if not any(high > 0 for low, high in picture.convert('RGB').getextrema()):
            raise RuntimeError('Black image is invalid: ' + name)
    views.append({'File': name + '.png', **{k: d[k] for k in (
        'Utc', 'Active', 'Surface', 'Gas', 'DrawCalls', 'HasSurfaceResources', 'DrawBounds', 'PeakGcBytes')}})

windows = []
for suffix in ('baseline', 'live-mixed-5', 'live-mixed-10', 'live-mixed-15'):
    name = 'fire-soft-stress-' + suffix
    d = read(name + '.json')
    if not d['MeasurementValid'] or d['TimeScale'] != 1 or d['Samples'] < 128:
        raise RuntimeError('Invalid live performance window: ' + name)
    if d['FirePeakGcBytes'] or d['ContinuousGcBytes'] or d['ContinuousOverflow'] or d['ContinuousDrops'] or d['ContinuousCpuFallback']:
        raise RuntimeError('Unexpected allocation, overflow or fallback: ' + name)
    windows.append({'File': name + '.json', **{k: d[k] for k in (
        'Utc', 'Samples', 'P50Ms', 'P95Ms', 'TotalQuantity', 'ContinuousPages', 'FirePeakGcBytes')}})

final = read('grid-liquid-capture.json')
if final['Playing'] or final['SceneDirty'] or final['RunInBackground'] or final['TimeScale'] != 1:
    raise RuntimeError('Editor restoration is incomplete.')
if final['Scene'] != 'Assets/_Project/Scenes/P7_DemoRun.unity':
    raise RuntimeError('Unexpected loaded scene.')
if 'Failures=0' not in (t / 'p7-grid-cutover-audit.txt').read_text(encoding='utf-8-sig').splitlines()[0]:
    raise RuntimeError('Current P7 wiring audit failed.')

names = {'fire-soft-red.json', 'fire-soft-green.json', 'fire-soft-concentrated.json',
         'gate-saved-root-state.json', 'grid-liquid-capture.json', 'p7-grid-cutover-audit.txt'}
names.update(p.name for p in t.glob('fire-soft-preview-*') if p.is_file())
names.update(p.name for p in t.glob('fire-soft-stress-*') if p.is_file())
for name in sorted(names):
    shutil.copyfile(t / name, archive / name)

sources = {
    'Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderer.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderProfile.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/FireParcelSurfaceResources.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/FireParcelGpuLayout.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/FireCellEmissionSampler.cs',
    'Assets/_Project/Art/Elemental/Compute/FireParcelLifecycle.compute',
    'Assets/_Project/Art/Elemental/Shaders/ElementFireParcelSplat.shader',
    'Assets/_Project/Art/Elemental/Shaders/FireParcelAppearance.hlsl',
    'Assets/_Project/Art/Elemental/Materials/M_ElementFireGasSplat.mat',
    'Assets/_Project/ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset',
    'Assets/_Project/Editor/GridLiquidPrototypeCapture.cs',
    'Assets/_Project/Editor/ElementLoadedSceneGate.cs',
    'Assets/_Project/Scenes/P7_DemoRun.unity',
    'Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md',
    'docs/superpowers/plans/2026-08-24 GPU Fire Parcel两阶段表现实施计划.md',
    'docs/superpowers/plans/2026-10-01 液体重构新对话交接.md',
    'docs/superpowers/validation/2026-10-03-fire-unified-soft/README.md',
    'Tools/ElementPerformance/archive_soft_fire.py',
    'Tools/ElementPerformance/capture_fire_visual.py',
    'Tools/ElementPerformance/profile_mixed_liquid.py',
    'Tools/ElementPerformance/summarize_profiler.py',
    'Tools/ElementPerformance/run_current_liquid_gate.py',
}
for fixture in {case['Name'].rsplit('.', 1)[0] for case in gate['Cases']}:
    path = next((root / 'Assets/_Project/Tests').rglob(fixture.rsplit('.', 1)[-1] + '.cs'))
    sources.add(path.relative_to(root).as_posix())

assemblies = []
for name, folder in (('Game.Rendering', 'Scripts/Rendering'), ('Game.Rendering.Tests', 'Tests/Rendering'),
                     ('Game.ElementField', 'Scripts/ElementField'), ('Game.ElementField.Tests', 'Tests/ElementField'),
                     ('Game.PbfTask13.Editor', 'Editor')):
    path = root / 'Library/ScriptAssemblies' / (name + '.dll')
    if path.stat().st_mtime < max(p.stat().st_mtime for p in (root / 'Assets/_Project' / folder).rglob('*.cs')):
        raise RuntimeError('Assembly is stale: ' + name)
    assemblies.append({'Name': name, 'ModifiedUtc': datetime.datetime.fromtimestamp(
        path.stat().st_mtime, datetime.timezone.utc).isoformat()})

manifest = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'Gate': {k: gate[k] for k in ('StartUtc', 'EndUtc', 'Passed', 'Failed', 'Skipped', 'SceneRestored')},
    'Views': views, 'Windows': windows,
    'FireGpuPayloadBytes': 8192 * 64 + 256 * 32 + 16,
    'RemovedLocalPayloadBytes': 96**3 * 8 + 131072 * 96 + 24,
    'Sources': [{'File': name, 'Sha256': hashlib.sha256((root / name).read_bytes()).hexdigest()} for name in sorted(sources)],
    'Assemblies': assemblies,
    'Evidence': [{'File': name, 'Sha256': hashlib.sha256((archive / name).read_bytes()).hexdigest()} for name in sorted(names)],
}
(archive / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'Directory': str(archive), 'Evidence': len(names), 'Sources': len(sources),
                  'Passed': gate['Passed'], 'SceneRestored': gate['SceneRestored']}, ensure_ascii=False))
