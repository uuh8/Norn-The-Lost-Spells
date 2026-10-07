"""归档 L4.27 的固定输入、正常游戏和冻结隔离证据；不改变 Unity 场景。"""
import csv
import datetime
import difflib
import hashlib
import json
import pathlib
import shutil
import statistics

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / 'docs/superpowers/validation/2026-10-04-liquid-capture-spikes'
BACKUP = ROOT / 'Backups/ElementPerformance/L427'
SCENE_HASH = 'bdc44a9b3529128a85843aa842eb83970faeaffe322ab4774baeb4014c860b83'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run():
    gate = read(OUT / 'l427-final-after-probe.json')
    if (gate['Passed'] != 261 or gate['Failed'] or gate['Skipped'] or gate['Aborted']
            or not gate['SceneRestored'] or gate['Completed'] != gate['Planned']):
        raise RuntimeError('Final diagnostic must be complete and passing')
    state = read(OUT / 'final-editor-state.json')
    before = read(BACKUP / 'editor-before.json')
    if (state['Playing'] or state['Paused'] or state['SceneDirty']
            or state['TimeScale'] != 1 or state['RunInBackground']
            or state['Roots'] != before['Roots']):
        raise RuntimeError('Original clean Edit Mode and root states were not preserved')
    scene = ROOT / 'Assets/_Project/Scenes/P7_DemoRun.unity'
    if sha(scene) != SCENE_HASH:
        raise RuntimeError('P7 scene bytes changed')

    sparse = read(OUT / 'l427-sparse-final.json')
    dense = read(OUT / 'l427-dense-final.json')
    half = read(OUT / 'l427-half-final.json')
    if (sparse['Signature'] != 3405202243 or sparse['Quantity'] != 46080
            or sparse['Overflow'] or sparse['AllocatedBytes']
            or dense['Signature'] != 4186262329 or dense['Quantity'] != 46080
            or not dense['Overflow'] or dense['AllocatedBytes'] or half['AllocatedBytes']):
        raise RuntimeError('Fixed input, fallback or warm allocation contract changed')
    # 文件含此前轮次的追加记录；只保留最终集中 Gate 时间内的五个真实 GPU 对照。
    gpu_all = [json.loads(line) for line in
               (OUT / 'final-gpu-equivalence.jsonl').read_text().splitlines() if line.strip()]
    gpu_final = [x for x in gpu_all if gate['StartUtc'] <= x['Utc'] <= gate['EndUtc']]
    if len(gpu_final) != 5 or any(x['MaximumPositionError'] or x['MaximumNormalError']
                                or x['MaximumSwellError'] for x in gpu_final):
        raise RuntimeError('Final GPU output must retain exact geometry')

    rendering = 'Assets/_Project/Scripts/Rendering/ElementField/'
    paths = [rendering + name for name in (
        'GridLiquidFieldFrame.cs', 'GridLiquidFieldGpuLayout.cs',
        'GridLiquidFieldSurfaceRenderer.cs', 'GridLiquidFieldSampler.cs', 'GpuFireParcelRenderer.cs')]
    paths += ['Assets/_Project/Art/Elemental/Compute/GridLiquidField.compute',
              'Assets/_Project/Tests/Rendering/GridLiquidFieldFrameTests.cs',
              'Assets/_Project/Tests/Rendering/GridLiquidFieldLayoutTests.cs',
              'Assets/_Project/Tests/Rendering/GridLiquidFieldPerformanceTests.cs',
              'Assets/_Project/Editor/ElementPerformanceGateRunner.cs',
              'Assets/_Project/Editor/GridLiquidPrototypeCapture.cs',
              'Tools/ElementPerformance/profile_mixed_liquid.py',
              'Tools/ElementPerformance/run_current_liquid_gate.py',
              'Tools/ElementPerformance/archive_liquid_capture_spikes.py']
    sources = []
    for relative in paths:
        path = ROOT / relative
        old = BACKUP / (path.name + '.before.txt')
        record = {'Path': relative, 'Sha256': sha(path)}
        if old.exists():
            record['BeforeSha256'] = sha(old)
            record['ChangedThisRound'] = sha(old) != record['Sha256']
            patch = difflib.unified_diff(old.read_text(encoding='utf-8-sig').splitlines(True),
                path.read_text(encoding='utf-8-sig').splitlines(True),
                fromfile='L4.27 before/' + path.name, tofile='L4.27 final/' + path.name)
            (OUT / (path.name + '.patch')).write_text(''.join(patch), encoding='utf-8')
        shutil.copyfile(path, OUT / (path.name + '.final.txt'))
        sources.append(record)
    # GPU 线程组实验没有通过，必须证明生产 Shader/Renderer/原有 GPU 测试已完整撤回。
    for name in ('GridLiquidField.compute', 'GridLiquidFieldSurfaceRenderer.cs',
                 'GridLiquidFieldPerformanceTests.cs', 'GridLiquidFieldSampler.cs'):
        record = next(x for x in sources if pathlib.Path(x['Path']).name == name)
        if record['Sha256'] != record['BeforeSha256']:
            raise RuntimeError('Rejected GPU experiment or unrelated sampler edit remains: ' + name)

    live, frozen = {}, {}
    fixed_names = ('full', 'frozen-compute', 'hidden-liquid', 'full-repeat',
                   'hidden-liquid-and-fire', 'hidden-liquid-fire-repeat')
    fields = ('Utc', 'Samples', 'TotalQuantity', 'ActiveCells', 'ContinuousPages',
              'ContinuousRebuiltPages', 'P50Ms', 'P95Ms', 'MaxMs', 'TimeScale',
              'MeasurementValid', 'ContinuousGcBytes', 'FireGcBytes', 'ContinuousGpuBytes')
    for prefix in ('l427-before', 'l427-after', 'l427-isolation'):
        for name in ('baseline', 'live-mixed-5', 'live-mixed-10', 'live-mixed-15', 'live-mixed-20'):
            key = prefix + '-' + name
            value = read(OUT / (key + '.json'))
            if value['TimeScale'] != 1 or not value['MeasurementValid']:
                raise RuntimeError('Normal gameplay window was paused or invalid: ' + key)
            live[key] = {k: value[k] for k in fields}
    for name in fixed_names:
        key = 'l427-isolation-' + name
        value = read(OUT / (key + '.json'))
        if value['TimeScale'] != 0 or value['TotalQuantity'] != '25430':
            raise RuntimeError('Isolated snapshot changed: ' + key)
        frozen[key] = {k: value[k] for k in fields}

    # RawFrameDataView 的原始索引比截图 UI 小一；保留全线程行，避免只读折叠父 Scope。
    with (BACKUP / 'user-all-before.tsv').open(encoding='utf-8-sig', newline='') as stream:
        selected = [row for row in csv.DictReader(stream, delimiter='\t') if row['frame'] == '5388']
    (OUT / 'user-selected-5389.json').write_text(json.dumps(selected, ensure_ascii=False, indent=2), encoding='utf-8')
    for name in ('user-all-before.tsv', 'user-all-before-state.txt', 'user-all-before.frames.json'):
        shutil.copyfile(BACKUP / name, OUT / name)
    capture = BACKUP / 'user-capture-20261004-114323.data'
    manifest = {
        'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'Kind': 'L4.27 exact Half CPU decode and sparse presentation capture; phase in progress',
        'Sources': sources, 'SceneSha256Before': SCENE_HASH, 'SceneSha256Final': sha(scene),
        'EditorState': state, 'UserCapture': {'Path': str(capture.relative_to(ROOT)),
            'Bytes': capture.stat().st_size, 'Sha256': sha(capture),
            'CachedRange': [5214, 5813], 'FramesWithMainThread': 245, 'SelectedUiFrame': 5389},
        'FinalDiagnostic': {key: gate[key] for key in
            ('Kind', 'StartUtc', 'EndUtc', 'Passed', 'Failed', 'Skipped', 'Planned', 'Completed', 'Aborted', 'SceneRestored')},
        'FixedCpuCapture': {'Record': sparse, 'FullScanMeanMs': statistics.mean(sparse['FullScanMilliseconds']),
            'SparseMeanMs': statistics.mean(sparse['CaptureMilliseconds']),
            'ReductionPercent': (1-statistics.mean(sparse['CaptureMilliseconds'])
                /statistics.mean(sparse['FullScanMilliseconds']))*100,
            'Meaning': 'Same input and exact packed output; both modes use Half cache. Not game FPS.'},
        'FullFallback': dense, 'HalfDecode': half, 'FinalGpuEquivalence': gpu_final,
        'NormalGameplayWindows': live, 'FrozenDiagnosticWindows': frozen,
        'Conclusion': 'CPU capture reduced with exact output. No reliable overall FPS improvement; GPU Compute rebuild cost remains.',
        'Limits': ['Normal windows have different reactions, distribution and timing; not strict A/B.',
            'Later normal isolation-run windows were slower (baseline 98.79ms, 20 groups 211.88ms P50); retained, cause not established.',
            'Frozen diagnostics force full Compute rebuild and have TimeScale=0; cannot certify normal FPS.',
            'GPU availability includes NotSupportedWithNativeGfxJobs; WaitForGPU is not a full GPU pass timestamp.',
            '32-thread experiment had 3 failures of 6 and was removed; no measured benefit retained.'],
        'Constraints': {'NoSceneSaveOrDiscardOrReopenThisRound': True, 'NoCommit': True,
            'NoSubAgents': True, 'GameplayAndVisualResolutionUnchanged': True}}
    (OUT / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'Passed': gate['Passed'], 'CaptureReductionPercent': manifest['FixedCpuCapture']['ReductionPercent'],
                      'ScenePreserved': True, 'GpuExperimentRemoved': True}, ensure_ascii=False))


if __name__ == '__main__':
    run()
