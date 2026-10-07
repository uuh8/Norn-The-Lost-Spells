"""Archive verified fire evidence; preserve earlier failures and source hashes."""
import datetime
import hashlib
import json
import pathlib
import shutil
from PIL import Image

root = pathlib.Path(__file__).resolve().parents[2]
temporary = root / 'Temp/ElementPerformance'
archive = root / 'docs/superpowers/validation/2026-10-03-fire-cohesion'
archive.mkdir(parents=True, exist_ok=True)


def read(name):
    return json.loads((temporary / name).read_text(encoding='utf-8-sig'))


def copy(name):
    shutil.copyfile(temporary / name, archive / name)


gate_name = 'fire-liquid-concentrated-verified.json'
gate = read(gate_name)
if gate['Passed'] != 238 or gate['Failed'] or gate['Skipped'] or gate['Aborted'] or not gate['SceneRestored']:
    raise RuntimeError('Current fire/liquid gate is incomplete.')

visuals = []
for name in ('near-0', 'near-1', 'near-2', 'near-low', 'forced-far-lod'):
    filename = 'fire-verified-final-' + name
    data = read(filename + '.json')
    if not data['Ready'] or data['ReadbackError'] or data['Overflow'] or not data['Gas']:
        raise RuntimeError('Invalid final fire visual: ' + name)
    with Image.open(temporary / (filename + '.png')) as picture:
        if not any(high > 0 for low, high in picture.convert('RGB').getextrema()):
            raise RuntimeError('Black image is not valid visual evidence: ' + name)
    visuals.append({'File': filename + '.json', **{k: data[k] for k in (
        'Utc', 'Active', 'Surface', 'Gas', 'Triangles', 'Overflow', 'GcBytes', 'PeakGcBytes',
        'ReadbackError', 'MaterialState', 'MaximumEdgeLength', 'SurfaceGridCenter')}})

restored = read('fire-verified-final-restored.json')
final = read('grid-liquid-capture.json')
for data in (restored, final):
    if data['Playing'] or data['SceneDirty'] or data['RunInBackground'] or data['TimeScale'] != 1:
        raise RuntimeError('P7 diagnostic settings have not been restored.')
    if data['Scene'] != 'Assets/_Project/Scenes/P7_DemoRun.unity':
        raise RuntimeError('Unexpected loaded scene.')

windows = []
for suffix in ('baseline', 'live-mixed-5', 'live-mixed-10', 'live-mixed-15'):
    path = temporary / ('fire-verified-stress-' + suffix + '.json')
    data = read(path.name)
    if not data['MeasurementValid'] or data['TimeScale'] != 1 or data['Samples'] < 128:
        raise RuntimeError('Invalid live stress window: ' + path.name)
    if data['FirePeakGcBytes'] or data['ContinuousGcBytes'] or data['ContinuousOverflow']:
        raise RuntimeError('Unexpected allocation or surface overflow: ' + path.name)
    windows.append({'File': path.name, **{k: data[k] for k in (
        'Utc', 'Samples', 'TotalQuantity', 'ResidentChunks', 'ActiveCells', 'GridFireChunks',
        'P50Ms', 'P95Ms', 'MaxMs', 'ContinuousPages', 'ContinuousGcBytes',
        'ContinuousCpuFallback', 'ContinuousGpuBytes', 'FireReady', 'FirePeakGcBytes')}})
if len(windows) != 4:
    raise RuntimeError('Require baseline and live 5/10/15-group windows.')

names = {gate_name, 'fire-surface-red.json', 'fire-surface-green2.json', 'fire-chunk-copy-red.json',
         'fire-focused-current-green.json', 'fire-liquid-concentrated-final.json',
         'gate-saved-root-state.json', 'grid-liquid-capture.json', 'p7-grid-cutover-audit.txt',
         'fire-verified-stress-gc-context.json'}
if 'Failures=0' not in (temporary / 'p7-grid-cutover-audit.txt').read_text(encoding='utf-8-sig').splitlines()[0]:
    raise RuntimeError('Current P7 wiring audit did not pass.')
for pattern in ('fire-verified-final-*', 'fire-verified-stress-*', 'fire-chunk-optimized-live-mixed-*',
                'fire-visual-before-baseline.*', 'fire-seed-diagnosis-live-mixed.*'):
    names.update(p.name for p in temporary.glob(pattern) if p.is_file())
for variant in ('surface-only', 'surface-opaque', 'near-low'):
    names.update({'fire-layer-diagnosis-' + variant + '.json', 'fire-layer-diagnosis-' + variant + '.png'})
# Explicitly retain known failed black ScreenCapture attempts as rejected history.
names.update(p.name for p in temporary.glob('fire-verified-final-*-black-attempt-*.png'))
names.update({'fire-depth-tail-near-0.png', 'fire-depth-tail-near-1.png', 'fire-visual-before-game.png'})
for name in sorted(names):
    copy(name)

sources = {
    'Assets/_Project/Scripts/ElementField/Data/IElementWorldReadOnly.cs',
    'Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderer.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/GpuFireParcelRenderProfile.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/FireParcelSurfaceResources.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/FireParcelSurfaceGridPlanner.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/FireParcelPhaseMath.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/FireParcelGpuLayout.cs',
    'Assets/_Project/Scripts/Rendering/ElementField/FireCellEmissionSampler.cs',
    'Assets/_Project/Art/Elemental/Compute/FireParcelLifecycle.compute',
    'Assets/_Project/Art/Elemental/Compute/FireParcelDensity.compute',
    'Assets/_Project/Art/Elemental/Compute/FireParcelMarchingCubes.compute',
    'Assets/_Project/Art/Elemental/Shaders/FireParcelAppearance.hlsl',
    'Assets/_Project/Art/Elemental/Shaders/ElementFireParcelSurface.shader',
    'Assets/_Project/Art/Elemental/Shaders/ElementFireParcelSplat.shader',
    'Assets/_Project/Art/Elemental/Materials/M_ElementFireParcelSurface.mat',
    'Assets/_Project/Art/Elemental/Materials/M_ElementFireGasSplat.mat',
    'Assets/_Project/ScriptableObjects/ElementField/Fire/GpuFireParcelRenderProfile_Default.asset',
    'Assets/_Project/Scenes/P7_DemoRun.unity',
    'Assets/_Project/Editor/ElementLoadedSceneGate.cs',
    'Assets/_Project/Editor/GridLiquidPrototypeCapture.cs',
    'Tools/ElementPerformance/capture_fire_visual.py',
    'Tools/ElementPerformance/run_current_liquid_gate.py',
    'Tools/ElementPerformance/profile_mixed_liquid.py',
    'Tools/ElementPerformance/summarize_profiler.py',
    'Tools/ElementPerformance/archive_current_fire.py',
    'Assets/_Project/Docs/2026-08-09 GPU PBF流体模拟完整解析.md',
    'docs/superpowers/plans/2026-08-24 GPU Fire Parcel两阶段表现实施计划.md',
    'docs/superpowers/plans/2026-10-01 液体重构新对话交接.md',
    'docs/superpowers/validation/2026-10-03-fire-cohesion/README.md'
}
for fixture in {case['Name'].rsplit('.', 1)[0] for case in gate['Cases']}:
    source = next((root / 'Assets/_Project/Tests').rglob(fixture.rsplit('.', 1)[-1] + '.cs'))
    sources.add(source.relative_to(root).as_posix())
assemblies = []
for name in ('Game.ElementField', 'Game.ElementField.Tests', 'Game.Rendering', 'Game.Rendering.Tests', 'Game.PbfTask13.Editor'):
    path = root / 'Library/ScriptAssemblies' / (name + '.dll')
    assemblies.append({'Name': name, 'ModifiedUtc': datetime.datetime.fromtimestamp(path.stat().st_mtime, datetime.timezone.utc).isoformat()})
manifest = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'FinalGate': {'File': gate_name, **{k: gate[k] for k in ('StartUtc', 'EndUtc', 'Passed', 'Failed', 'Skipped', 'SceneRestored')}},
    'Sources': [{'File': name, 'Sha256': hashlib.sha256((root / name).read_bytes()).hexdigest()} for name in sorted(sources)],
    'Assemblies': assemblies,
    'Evidence': [{'File': name, 'Sha256': hashlib.sha256((archive / name).read_bytes()).hexdigest()} for name in sorted(names)]
}
for name, data in (('source-manifest.json', manifest), ('performance-windows.json', windows), ('visual-windows.json', visuals)):
    (archive / name).write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')
print('Archived', len(names), 'evidence files,', len(sources), 'source hashes,', len(windows), 'stress windows and', len(visuals), 'valid fire views.')
