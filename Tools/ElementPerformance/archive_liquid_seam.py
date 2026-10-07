"""Archive the Oct 3 seam fix without overwriting the earlier visual evidence.

No scene/asset writes. The final diagnostic JSON and source hashes identify
exactly which code was tested; live FPS and frozen images stay separate.
"""
import datetime
import hashlib
import json
import pathlib
import shutil

root = pathlib.Path(__file__).resolve().parents[2]
temporary = root / 'Temp/ElementPerformance'
archive = root / 'docs/superpowers/validation/2026-10-03-liquid-seam-performance'
archive.mkdir(parents=True, exist_ok=True)

gate_name = 'final-liquid-regression-206.json'
gate = json.loads((temporary / gate_name).read_text(encoding='utf-8-sig'))
if (gate['Failed'] or gate['Skipped'] or gate['Aborted'] or not gate['SceneRestored']
        or gate['Passed'] != gate['Planned'] or gate['Passed'] != 206):
    raise RuntimeError('The final gate is incomplete or the scene is not restored.')

names = [gate_name, 'seam-cold-support-red.json', 'seam-cold-support-green.json',
         'seam-presentation-cache-recycle-red.json', 'seam-reaction-reserved-before.json',
         'toxic-reserved-before-4.json', 'toxic-reserved-before-65536.json',
         'toxic-reserved-scan-4.json', 'toxic-reserved-scan-65536.json', 'reaction-before-source.json',
         'seam-fire-collection-before.json', 'sparse-fire-sample-sequence-before.json',
         'sparse-fire-sample-sequence.json',
         'seam-packed-before-optimization.json', 'seam-crown-equivalence-green.json',
         'seam-store-before-optimization.json', 'seam-cache-test-ray-on-surface.json',
         'slope-committed-snapshot.json', 'slope-cold-support.json',
         'frozen-presentation-signature.json', 'continuous-support-volume.json',
         'seam-fixed-ab-off.json', 'seam-fixed-ab-on.json', 'seam-fixed-ab-off-gate.json',
         'continuous-volume-calibration.json', 'continuous-slope-contact.json',
         'continuous-slope-temporal.json', 'gate-saved-root-state.json',
         'p7-grid-cutover-audit.txt', 'grid-liquid-capture.json']
for name in names:
    source = temporary / name
    if not source.exists():
        raise RuntimeError('Required evidence is missing: ' + name)
    shutil.copyfile(source, archive / name)
# This excluded hypothesis passed before the root-cause fix; don't retain its
# misleading original "red" filename in the published evidence index.
shutil.copyfile(temporary / 'seam-snapshot-red.json', archive / 'seam-snapshot-before.json')

windows = []
invalid_windows = []
for prefix in ['seam-perf-before', 'seam-perf-after', 'seam-perf-final-cache',
               'seam-perf-final-reaction', 'seam-perf-final-collector']:
    for source in sorted(temporary.glob(prefix + '-*.json')):
        if source.name.endswith('.summary.json'):
            continue
        value = json.loads(source.read_text(encoding='utf-8-sig'))
        shutil.copyfile(source, archive / source.name)
        if not value['MeasurementValid'] or value['TimeScale'] != 1 or value['Samples'] < 128:
            invalid_windows.append({'File': source.name, 'TimeScale': value['TimeScale'],
                                    'Samples': value['Samples'], 'Reason': 'Paused or invalid live measurement'})
            continue
        fields = ['Utc', 'Samples', 'MeasurementValid', 'P50Ms', 'P95Ms', 'MaxMs',
                  'TotalQuantity', 'ActiveCells', 'ResidentChunks', 'ContinuousPages',
                  'ContinuousCpuFallback', 'ContinuousGpuBytes', 'ContinuousCaptureMs',
                  'ContinuousDisplayLagMs', 'ContinuousGcBytes']
        windows.append({'File': source.name, **{k: value[k] for k in fields}})

# These are the complete 60-frame exports used in the reported comparison.
raw = ['seam-perf-before-live-mixed-30', 'seam-perf-after-live-mixed-30',
       'seam-perf-after-live-mixed-35', 'seam-perf-final-cache-live-mixed']
reaction_raw = sorted(temporary.glob('seam-perf-final-reaction-live-mixed*.summary.json'))
raw.extend(p.name.removesuffix('.summary.json') for p in reaction_raw)
collector_raw = sorted(temporary.glob('seam-perf-final-collector-live-mixed*.summary.json'))
raw.extend(p.name.removesuffix('.summary.json') for p in collector_raw)
for stem in raw:
    for suffix in ['.tsv', '-state.txt', '.summary.json']:
        shutil.copyfile(temporary / (stem + suffix), archive / (stem + suffix))
shutil.copyfile(temporary / 'seam-perf-baseline-source/manifest.json',
                archive / 'before-performance-source-manifest.json')
for folder in ['seam-perf-baseline-source', 'seam-fixed-ab']:
    destination = archive / folder
    destination.mkdir(exist_ok=True)
    for path in (temporary / folder).iterdir():
        if path.is_file():
            shutil.copyfile(path, destination / path.name)
(archive / 'performance-windows.json').write_text(
    json.dumps(windows, ensure_ascii=False, indent=2), encoding='utf-8')
(archive / 'invalid-performance-windows.json').write_text(
    json.dumps(invalid_windows, ensure_ascii=False, indent=2), encoding='utf-8')

sources = list((root / 'Assets/_Project/Scripts/ElementField/Liquids').rglob('*.cs'))
sources += list((root / 'Assets/_Project/Scripts/Rendering/ElementField').glob('GridLiquid*.cs'))
sources += list((root / 'Assets/_Project/Tests/ElementField').glob('Liquid*Tests.cs'))
sources += list((root / 'Assets/_Project/Tests/Rendering').glob('GridLiquid*Tests.cs'))
sources += [p for p in (root / 'Assets/_Project/Art/Elemental/Compute').glob('GridLiquid*')
            if p.suffix != '.meta']
sources += [root / 'Assets/_Project/Art/Elemental/Shaders/ElementLiquidContinuous.shader',
            root / 'Assets/_Project/Art/Elemental/Shaders/ElementLiquidSurfaceLighting.hlsl',
            root / 'Assets/_Project/Scripts/ElementField/Runtime/ElementFieldSolidBaker.cs',
            root / 'Assets/_Project/Editor/ElementLoadedSceneGate.cs',
            root / 'Assets/_Project/Editor/GridLiquidPrototypeCapture.cs',
            root / 'Assets/_Project/Scripts/ElementField/Fluid/Gameplay/MaterialReactionProcessTable.cs',
            root / 'Assets/_Project/Tests/ElementField/MaterialReactionProcessTableTests.cs',
            root / 'Assets/_Project/Scripts/ElementField/Runtime/ElementWorldRuntime.cs',
            root / 'Assets/_Project/Tests/ElementField/ElementWorldFluidRoutingTests.cs',
            root / 'Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset']
records = [{'File': p.relative_to(root).as_posix(),
            'Sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(set(sources))]
assemblies = []
for name in ['Game.ElementField', 'Game.ElementField.Tests', 'Game.Rendering', 'Game.Rendering.Tests']:
    path = root / 'Library/ScriptAssemblies' / (name + '.dll')
    assemblies.append({'Name': name, 'ModifiedUtc': datetime.datetime.fromtimestamp(
        path.stat().st_mtime, datetime.timezone.utc).isoformat()})
manifest = {'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'FinalGate': {'File': gate_name, **{k: gate[k] for k in
                ['StartUtc', 'EndUtc', 'Passed', 'Failed', 'Skipped', 'SceneRestored']}},
            'Sources': records, 'Assemblies': assemblies}
(archive / 'source-manifest.json').write_text(
    json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
print('Archived', len(windows), 'valid windows,',len(raw),'complete raw captures and',
      len(records), 'source hashes.')
