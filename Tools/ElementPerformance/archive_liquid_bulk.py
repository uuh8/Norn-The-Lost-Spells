"""Archive the verified bulky liquid revision without rewriting earlier evidence."""
import datetime
import hashlib
import json
import pathlib
import shutil

root = pathlib.Path(__file__).resolve().parents[2]
temporary = root / 'Temp/ElementPerformance'
archive = root / 'docs/superpowers/validation/2026-10-03-liquid-bulk-final'
archive.mkdir(parents=True, exist_ok=True)
gate_name = 'liquid-bulk-endpoint-211-green.json'
gate = json.loads((temporary / gate_name).read_text(encoding='utf-8-sig'))
if gate['Passed'] != 211 or gate['Failed'] or gate['Skipped'] or gate['Aborted'] or not gate['SceneRestored']:
    raise RuntimeError('The current 211-case gate is incomplete.')
for name in [gate_name, 'liquid-bulk-final-211-green.json', 'liquid-shoulder-red.json', 'liquid-shoulder-bulk-green.json',
             'rounded-calibrated-green.json', 'liquid-bulk-final-211.json',
             'continuous-volume-calibration.json', 'continuous-support-volume.json',
             'continuous-slope-contact.json', 'continuous-slope-temporal.json',
             'frozen-presentation-signature.json', 'p7-grid-cutover-audit.txt',
             'gate-saved-root-state.json', 'grid-liquid-capture.json']:
    shutil.copyfile(temporary / name, archive / name)

windows = []
for path in sorted(temporary.glob('liquid-bulk-production-*')):
    if not path.is_file():
        continue
    shutil.copyfile(path, archive / path.name)
    if path.suffix == '.json' and not path.name.endswith('.summary.json'):
        data = json.loads(path.read_text(encoding='utf-8-sig'))
        if not data['MeasurementValid'] or data['TimeScale'] != 1 or data['Samples'] < 128:
            raise RuntimeError('Invalid live measurement: ' + path.name)
        keys = ['Utc', 'Samples', 'P50Ms', 'P95Ms', 'TotalQuantity', 'ActiveCells',
                'ResidentChunks', 'ContinuousPages', 'ContinuousCpuFallback',
                'ContinuousGpuBytes', 'ContinuousCaptureMs', 'ContinuousDisplayLagMs',
                'ContinuousGcBytes']
        windows.append({'File': path.name, **{key: data[key] for key in keys}})
if not windows:
    raise RuntimeError('Fresh live profiling is missing.')
(archive / 'performance-windows.json').write_text(json.dumps(windows, ensure_ascii=False, indent=2), encoding='utf-8')
for path in temporary.glob('liquid-bulk-isolation-*'):
    if path.is_file():shutil.copyfile(path,archive/path.name)

visuals = []
for path in sorted(archive.glob('continuous-lab-*-tick30.json')):
    data = json.loads(path.read_text(encoding='utf-8-sig'))
    if not data['Ready'] or data['Quantity'] != data['Injected'] or data['Overflow'] or data['SmallDrops']:
        raise RuntimeError('Invalid visual capture: ' + path.name)
    keys = ['Utc', 'Case', 'Quantity', 'Injected', 'ReceivedOnFloor', 'StableQuantity',
            'Pages', 'Subdivision', 'CpuFallback', 'CaptureMs', 'GcBytes',
            'DisplayLagMs', 'CpuBuildFrames', 'CpuBuildMs', 'FieldMeshVolume',
            'MeshMinimum', 'MeshMaximum']
    visuals.append({'File': path.name, **{key: data[key] for key in keys}})
(archive / 'visual-windows.json').write_text(json.dumps(visuals, ensure_ascii=False, indent=2), encoding='utf-8')

previous = root / 'docs/superpowers/validation/2026-10-03-liquid-seam-performance/source-manifest.json'
sources = {record['File'] for record in json.loads(previous.read_text(encoding='utf-8-sig'))['Sources']}
sources.update(['Assets/_Project/Editor/GridLiquidFieldValidationLab.cs',
                'Tools/ElementPerformance/capture_current_liquid.py',
                'Tools/ElementPerformance/profile_mixed_liquid.py',
                'Tools/ElementPerformance/archive_liquid_bulk.py'])
hashes = [{'File': name, 'Sha256': hashlib.sha256((root / name).read_bytes()).hexdigest()} for name in sorted(sources)]
assemblies = []
for name in ['Game.ElementField', 'Game.ElementField.Tests', 'Game.Rendering', 'Game.Rendering.Tests']:
    path = root / 'Library/ScriptAssemblies' / (name + '.dll')
    assemblies.append({'Name': name, 'ModifiedUtc': datetime.datetime.fromtimestamp(path.stat().st_mtime, datetime.timezone.utc).isoformat()})
manifest = {'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'FinalGate': {'File': gate_name, **{key: gate[key] for key in ['StartUtc', 'EndUtc', 'Passed', 'Failed', 'Skipped', 'SceneRestored']}},
            'Sources': hashes, 'Assemblies': assemblies}
(archive / 'source-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
print('Archived', len(windows), 'valid performance windows,', len(visuals), 'visual cases and', len(hashes), 'source hashes.')
