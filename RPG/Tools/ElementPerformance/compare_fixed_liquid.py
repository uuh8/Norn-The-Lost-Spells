"""Bounded A/B probe: same terrain fix and inputs, CPU optimizations off/on.

Run only in Edit Mode. Always restore the exact production bytes, then refresh
and run the final 206-method gate. Never save or replace the loaded level.
"""
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys

root = pathlib.Path(__file__).resolve().parents[2]
temporary = root / 'Temp/ElementPerformance'
records = temporary / 'seam-fixed-ab'
frame = root / 'Assets/_Project/Scripts/Rendering/ElementField/GridLiquidFieldFrame.cs'
store = root / 'Assets/_Project/Scripts/ElementField/Liquids/LiquidVolumeStore.cs'
paths = [frame, store]

def run(*arguments):
    subprocess.run([sys.executable, *arguments], cwd=root, check=True)

def method_span(source, declaration):
    start = source.index(declaration)
    opened = source.index('{', start)
    level = 1
    end = opened + 1
    while level:
        level += (source[end] == '{') - (source[end] == '}')
        end += 1
    return start, end

def replace_method(source, declaration, replacement):
    start, end = method_span(source, declaration)
    return source[:start] + replacement + source[end:]

run('Tools/ElementPerformance/editor_request.py', 'capture')
state = json.loads((temporary / 'grid-liquid-capture.json').read_text(encoding='utf-8-sig'))
if state['Playing'] or state['Paused']:
    raise RuntimeError('The fixed-input comparison requires Edit Mode.')
backups = {path: path.read_bytes() for path in paths}
records.mkdir(exist_ok=True)
original = (temporary / 'seam-perf-baseline-source/GridLiquidFieldFrame.cs').read_text(encoding='utf-8-sig')
try:
    variant = frame.read_text(encoding='utf-8-sig')
    for declaration in ['private void ShapeBodies(', 'private void RebuildShapedBounds(']:
        start, end = method_span(original, declaration)
        variant = replace_method(variant, declaration, original[start:end])
    frame.write_text(variant, encoding='utf-8')
    variant = store.read_text(encoding='utf-8-sig')
    declaration = 'private bool TryLocate('
    # Original address calculation; no value/result caching. Residency fields
    # remain inert so the timed variant differs only in the lookup fast path.
    variant = replace_method(variant, declaration, '''private bool TryLocate(Vector3Int cell, out Chunk chunk, out int index)
        {
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _chunkSize, out var key, out var local);
            index = local.x + _chunkSize * (local.y + _chunkSize * local.z);
            if (_indices.TryGetValue(key, out int slot)) { chunk = _pool[slot]; return true; }
            chunk = null;
            return false;
        }''')
    store.write_text(variant, encoding='utf-8')
    for path in paths:
        shutil.copyfile(path, records / (path.name + '.optimizations-off'))
    (records / 'optimizations-off-sources.json').write_text(json.dumps([
        {'File': p.relative_to(root).as_posix(), 'Sha256': hashlib.sha256(p.read_bytes()).hexdigest()}
        for p in paths], indent=2), encoding='utf-8')
    run('Tools/ElementPerformance/editor_request.py', 'refresh')
    run('Tools/ElementPerformance/run_current_liquid_gate.py',
        'seam-fixed-ab-off-gate.json', 'Game.Rendering.Tests.GridLiquidFieldFrameTests')
    shutil.copyfile(temporary / 'frozen-presentation-signature.json', temporary / 'seam-fixed-ab-off.json')
finally:
    for path, data in backups.items():
        path.write_bytes(data)
        if hashlib.sha256(path.read_bytes()).digest() != hashlib.sha256(data).digest():
            raise RuntimeError('Failed to restore production source: ' + str(path))
    run('Tools/ElementPerformance/editor_request.py', 'refresh')

run('Tools/ElementPerformance/run_current_liquid_gate.py', 'final-liquid-regression-206.json')
shutil.copyfile(temporary / 'frozen-presentation-signature.json', temporary / 'seam-fixed-ab-on.json')
for name in ['off', 'on']:
    value = json.loads((temporary / ('seam-fixed-ab-' + name + '.json')).read_text(encoding='utf-8-sig'))
    print(name, {k: value[k] for k in ['CaptureSamples', 'CaptureMeanMs', 'QuantityReads',
                                      'QuantityReadMeanNs', 'Signature', 'QuantityChecksum', 'GcBytes']}, flush=True)
