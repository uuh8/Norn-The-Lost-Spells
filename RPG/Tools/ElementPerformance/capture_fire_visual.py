"""Explicit P7 fire rendering capture; creates no saved scene/profile changes.

Uses the real Fire write path, production Parcel pool and shaders. The far-LOD
shot deliberately moves only the rendering interest point; label it as a forced
LOD diagnostic, never as a natural player-distance performance measurement.
"""
import json
import pathlib
import shutil
import sys
import time
from PIL import Image

root = pathlib.Path(__file__).resolve().parents[2]
t = root / 'Temp/ElementPerformance'
prefix = sys.argv[1] if len(sys.argv) > 1 else 'fire-current'
bridge = t / 'grid-liquid-capture.request'


def send(command, path=bridge):
    if path.exists():
        raise RuntimeError('Do not overwrite a pending Editor request')
    started = time.time()
    path.write_text(command, encoding='utf-8')
    deadline = time.monotonic() + 70
    while path.exists():
        if time.monotonic() > deadline:
            raise RuntimeError('Editor did not consume ' + command)
        time.sleep(.1)
    if path == bridge and command not in ('play', 'stop', 'refresh'):
        result = t / 'grid-liquid-capture.json'
        wait_file(result, started)
        return json.loads(result.read_text(encoding='utf-8-sig'))


def wait_file(path, started):
    deadline = time.monotonic() + 70
    while not path.exists() or path.stat().st_mtime < started:
        if time.monotonic() > deadline:
            raise RuntimeError('Missing fresh result ' + str(path))
        time.sleep(.1)


def shot(name):
    source = t / 'grid-liquid-game.png'
    for attempt in range(3):
        started = time.time()
        send('screenshot')
        wait_file(source, started)
        # ScreenCapture 在未绘制 GameView 的帧可能只返回黑图；保留失败证据，不能当作视觉验收通过。
        with Image.open(source) as picture:
            valid = any(channel[1] > 0 for channel in picture.convert('RGB').getextrema())
        if valid:
            break
        shutil.copyfile(source, t / (prefix + '-' + name + '-black-attempt-' + str(attempt) + '.png'))
        send('focus-game')
        time.sleep(.5)
    else:
        raise RuntimeError('ScreenCapture produced only black frames: ' + name)
    shutil.copyfile(source, t / (prefix + '-' + name + '.png'))
    started = time.time()
    send('fire-inspect')
    source = t / 'fire-inspect.json'
    wait_file(source, started)
    shutil.copyfile(source, t / (prefix + '-' + name + '.json'))
    r = json.loads(source.read_text(encoding='utf-8-sig'))
    print(name, {k: r[k] for k in ('Active', 'Surface', 'Gas', 'Triangles', 'Overflow', 'GcBytes', 'PeakGcBytes', 'ReadbackError')}, flush=True)
    if not r['Ready'] or r['ReadbackError'] or r['Overflow']:
        raise RuntimeError('Invalid fire capture: ' + name)


# A refresh can be consumed before Editor assembly compilation. Do not request
# Play against stale tooling or a compilation error dialog.
editor_latest = max(p.stat().st_mtime for p in (root / 'Assets/_Project/Editor').rglob('*.cs'))
editor_dll = root / 'Library/ScriptAssemblies/Game.PbfTask13.Editor.dll'
deadline = time.monotonic() + 75
while not editor_dll.exists() or editor_dll.stat().st_mtime < editor_latest:
    if time.monotonic() > deadline:
        raise RuntimeError('Editor diagnostic assembly is stale')
    time.sleep(.25)

before = send('capture')
entered = not before['Playing']
print('Before:', {k: before[k] for k in ('Scene', 'SceneDirty', 'Playing', 'RunInBackground')}, flush=True)
try:
    if entered:
        send('play')
    deadline = time.monotonic() + 75
    while not send('capture')['Playing']:
        if time.monotonic() > deadline:
            raise RuntimeError('Editor failed to enter Play')
        time.sleep(.25)
    send('background-on')
    send('dismiss-validation-guide')
    send('focus-game')
    send('seed-fire-row')
    shutil.copyfile(t / 'fire-row.json', t / (prefix + '-row-input.json'))
    send('fire-camera')
    time.sleep(2)
    for i in range(3):
        shot('near-' + str(i))
        time.sleep(.5)
    send('fire-camera-low')
    shot('near-low')
    if 'overhead' in sys.argv[2:]:
        for view in ('high', 'overhead', 'overhead-roll'):
            send('fire-camera-' + view)
            time.sleep(.3)
            shot(view)
    if len(sys.argv) > 2 and sys.argv[2] == 'diagnose-layers':
        send('fire-no-splats')
        shot('surface-only')
        send('fire-opaque-surface')
        shot('surface-opaque')
        send('fire-appearance-restore')
    send('fire-lod-far')
    time.sleep(.5)
    shot('forced-far-lod')
    send('fire-lod-near')
    send('fire-camera')
    time.sleep(.5)
    send('profile-window')
    deadline = time.monotonic() + 100
    while True:
        time.sleep(.5)
        r = send('capture')
        if r['Samples'] >= 128:
            break
        if time.monotonic() > deadline:
            raise RuntimeError('Insufficient live fire frames')
    if not r['MeasurementValid'] or r['TimeScale'] != 1:
        raise RuntimeError('World was paused during performance capture')
    (t / (prefix + '-row-profile.json')).write_text(json.dumps(r, ensure_ascii=False, indent=2), encoding='utf-8')
    started = time.time()
    send('dump', t / 'dump-profiler.request')
    wait_file(t / 'editor-profiler-finished.txt', started)
    shutil.copyfile(t / 'editor-profiler.tsv', t / (prefix + '-row-profile.tsv'))
    shutil.copyfile(t / 'editor-profiler-state.txt', t / (prefix + '-row-profile-state.txt'))
    print('Profile:', {k: r[k] for k in ('Samples', 'P50Ms', 'P95Ms', 'FireReady', 'FireGcBytes', 'FirePeakGcBytes', 'FireTotalSeeds')}, flush=True)
finally:
    if send('capture')['Playing']:
        send('fire-appearance-restore')
        send('fire-lod-near')
    send('finish-flow-profile')
    if entered:
        send('stop')
    deadline = time.monotonic() + 60
    while entered and send('capture')['Playing']:
        if time.monotonic() > deadline:
            raise RuntimeError('Play exit pending')
        time.sleep(.25)
    after = send('capture')
    (t / (prefix + '-restored.json')).write_text(json.dumps(after, ensure_ascii=False, indent=2), encoding='utf-8')
    print('After:', {k: after[k] for k in ('Scene', 'SceneDirty', 'Playing', 'RunInBackground')}, flush=True)
