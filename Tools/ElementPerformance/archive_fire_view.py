"""Archive verified camera-angle evidence without touching Task F8's archive."""
import datetime
import hashlib
import json
import pathlib
import shutil
from PIL import Image

root = pathlib.Path(__file__).resolve().parents[2]
t = root / 'Temp/ElementPerformance'
a = root / 'docs/superpowers/validation/2026-10-03-fire-view-projection'
a.mkdir(parents=True, exist_ok=True)


def read(name):
    return json.loads((t / name).read_text(encoding='utf-8-sig'))


gate = read('fire-overhead-concentrated.json')
if gate['Passed'] != 243 or gate['Failed'] or gate['Skipped'] or gate['Aborted'] or not gate['SceneRestored']:
    raise RuntimeError('Require current 243-case regression and scene restoration.')
if read('fire-overhead-red.json')['Failed'] != 2 or read('fire-overhead-green.json')['Passed'] != 5:
    raise RuntimeError('Missing angle RED/GREEN.')
views = []
for stage in ('before', 'after'):
    for view in ('near-0', 'near-1', 'near-2', 'near-low', 'high', 'overhead', 'overhead-roll', 'forced-far-lod'):
        name = 'fire-overhead-' + stage + '-' + view
        d = read(name + '.json')
        if not d['Ready'] or d['ReadbackError'] or d['HasSurfaceResources'] or d['DrawCalls'] != 2 or d['PeakGcBytes']:
            raise RuntimeError('Invalid visual state: ' + name)
        if not d['Surface'] or not d['Gas']:
            raise RuntimeError('Missing phase: ' + name)
        with Image.open(t / (name + '.png')) as picture:
            if not any(high > 0 for low, high in picture.convert('RGB').getextrema()):
                raise RuntimeError('Black screenshot: ' + name)
        views.append({'File': name + '.png', **{k: d[k] for k in (
            'Utc', 'Active', 'Surface', 'Gas', 'CameraPosition', 'CameraForward', 'PeakGcBytes')}})

windows = []
for suffix in ('baseline', 'live-mixed-5', 'live-mixed-10', 'live-mixed-15'):
    name = 'fire-overhead-stress-' + suffix
    d = read(name + '.json')
    if not d['MeasurementValid'] or d['TimeScale'] != 1 or d['Samples'] < 128:
        raise RuntimeError('Invalid live window: ' + name)
    if d['FirePeakGcBytes'] or d['ContinuousGcBytes'] or d['ContinuousOverflow'] or d['ContinuousDrops'] or d['ContinuousCpuFallback']:
        raise RuntimeError('Allocation, overflow or fallback: ' + name)
    windows.append({'File': name + '.json', **{k: d[k] for k in (
        'Utc', 'Samples', 'P50Ms', 'P95Ms', 'TotalQuantity', 'ContinuousPages')}})
final = read('grid-liquid-capture.json')
if final['Playing'] or final['SceneDirty'] or final['RunInBackground'] or final['TimeScale'] != 1:
    raise RuntimeError('Editor not restored.')
if 'Failures=0' not in (t / 'p7-grid-cutover-audit.txt').read_text(encoding='utf-8-sig').splitlines()[0]:
    raise RuntimeError('P7 wiring audit failed.')

names = {'fire-overhead-red.json', 'fire-overhead-green.json', 'fire-overhead-concentrated.json',
         'grid-liquid-capture.json', 'p7-grid-cutover-audit.txt', 'gate-saved-root-state.json'}
for pattern in ('fire-overhead-before-*', 'fire-overhead-after-*', 'fire-overhead-stress-*',
                'fire-overhead-red-fire-view-*', 'fire-overhead-green-fire-view-*'):
    names.update(p.name for p in t.glob(pattern) if p.is_file())
for name in sorted(names):
    shutil.copyfile(t / name, a / name)

previous = json.loads((root / 'docs/superpowers/validation/2026-10-03-fire-unified-soft/manifest.json').read_text(encoding='utf-8'))
sources = {e['File'] for e in previous['Sources']}
sources.update({'Tools/ElementPerformance/archive_fire_view.py',
                'docs/superpowers/validation/2026-10-03-fire-view-projection/README.md'})
scene = 'Assets/_Project/Scenes/P7_DemoRun.unity'
scene_unchanged = next(e['Sha256'] for e in previous['Sources'] if e['File'] == scene) == hashlib.sha256((root / scene).read_bytes()).hexdigest()
if not scene_unchanged:
    raise RuntimeError('Scene file changed; review before recording an unchanged-scene claim.')
manifest = {
    'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'Gate': {k: gate[k] for k in ('StartUtc','EndUtc','Passed','Failed','Skipped','SceneRestored')},
    'SceneFileUnchangedFromTask8': scene_unchanged, 'Views': views, 'Windows': windows,
    'Sources': [{'File': name, 'Sha256': hashlib.sha256((root / name).read_bytes()).hexdigest()} for name in sorted(sources)],
    'Evidence': [{'File': name, 'Sha256': hashlib.sha256((a / name).read_bytes()).hexdigest()} for name in sorted(names)],
}
(a / 'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'Directory': str(a), 'Evidence': len(names), 'Sources': len(sources), 'Passed': gate['Passed'],
                  'SceneRestored': gate['SceneRestored'], 'SceneFileUnchanged': scene_unchanged},ensure_ascii=False))
