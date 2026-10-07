"""Archive post-recovery L4.26 evidence without changing Unity assets."""
import datetime, difflib, hashlib, json, pathlib, shutil, statistics

ROOT = pathlib.Path(__file__).resolve().parents[2]
TEMP = ROOT / 'Temp/ElementPerformance'
EVIDENCE = ROOT / 'Backups/ElementPerformance/L426'
OUT = ROOT / 'docs/superpowers/validation/2026-10-04-liquid-page-cache'
SCENE_HASH = 'bdc44a9b3529128a85843aa842eb83970faeaffe322ab4774baeb4014c860b83'

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def copy(path, name=None):
    target = OUT / (name or path.name)
    if path.resolve() != target.resolve():
        shutil.copyfile(path, target)

def means(record):
    grouped = {}
    for mode, value in zip(record['Modes'], record['Milliseconds']):
        grouped.setdefault(mode, []).append(value)
    return {key: statistics.mean(value) for key, value in grouped.items()}

def run():
    OUT.mkdir(parents=True, exist_ok=True)
    gate = read(OUT / 'l426-final-concentrated.json')
    if (gate['Passed'] != 260 or gate['Failed'] or gate['Skipped'] or gate['Aborted']
            or not gate['SceneRestored'] or gate['Completed'] != gate['Planned']):
        raise RuntimeError('Final concentrated diagnostic is not complete and passing')
    state = read(TEMP / 'grid-liquid-capture.json')
    if state['Playing'] or state['Paused'] or state['SceneDirty'] or state['TimeScale'] != 1 or state['RunInBackground']:
        raise RuntimeError('Editor was not restored to clean Edit Mode')
    recovered = read(OUT / 'recovered-editor-state.json')
    if state['Roots'] != recovered['Roots']:
        raise RuntimeError('P7 root states differ from the recovered state')
    scene = ROOT / 'Assets/_Project/Scenes/P7_DemoRun.unity'
    if sha(scene) != SCENE_HASH:
        raise RuntimeError('P7 bytes changed; do not claim scene preservation')

    cpu = read(EVIDENCE / 'l426-cpu-work.json')
    gpu = read(EVIDENCE / 'l426-gpu-work.json')
    if cpu['Quantity'] != 1536 or cpu['Signature'] != 1960475504 or cpu['AllocatedBytes']:
        raise RuntimeError('CPU reference output or allocation contract changed')
    cpu_means, gpu_means = means(cpu), means(gpu)
    comparisons = [json.loads(line) for line in (EVIDENCE / 'l426-gpu-equivalence.jsonl').read_text().splitlines() if line.strip()]
    final = [x for x in comparisons if gate['StartUtc'] <= x['Utc'] <= gate['EndUtc']]
    if len(final) != 5 or any(x['MaximumPositionError'] or x['MaximumNormalError'] or x['MaximumSwellError'] for x in final):
        raise RuntimeError('Five final GPU comparisons must be exact')
    paths = ['Assets/_Project/Scripts/Rendering/ElementField/' + name for name in (
        'GridLiquidFieldFrame.cs', 'GridLiquidFieldSampler.cs', 'GridLiquidFieldSurfaceRenderer.cs',
        'GridLiquidFieldResources.cs', 'GridLiquidFieldBudget.cs')]
    paths += ['Assets/_Project/Art/Elemental/Compute/' + name for name in (
        'GridLiquidField.compute', 'GridLiquidFieldCommon.hlsl')]
    paths += [
        'Assets/_Project/Tests/Rendering/GridLiquidFieldPerformanceTests.cs',
        'Assets/_Project/Editor/ElementLoadedSceneGate.cs',
        'Assets/_Project/Editor/GridLiquidPrototypeCapture.cs',
        'Tools/ElementPerformance/profile_mixed_liquid.py',
        'Tools/ElementPerformance/editor_request.py',
        'Tools/ElementPerformance/run_current_liquid_gate.py',
        'Tools/ElementPerformance/archive_liquid_page_cache.py']
    sources = []
    for relative in paths:
        path = ROOT / relative
        sources.append({'File': relative, 'Sha256': sha(path)})
        copy(path, path.name + '.final.txt')
    # Restart cleared early Temp backups; only this persistent L4.25 Frame is a verified earlier source.
    previous = ROOT / 'docs/superpowers/validation/2026-10-04-liquid-profiler/GridLiquidFieldFrame.after.cs.txt'
    current = ROOT / paths[0]
    copy(previous, 'GridLiquidFieldFrame.previous-final.txt')
    patch = difflib.unified_diff(previous.read_text(encoding='utf-8-sig').splitlines(True),
        current.read_text(encoding='utf-8-sig').splitlines(True), fromfile='L4.25 final Frame', tofile='L4.26 final Frame')
    (OUT / 'frame-query-cache.patch').write_text(''.join(patch), encoding='utf-8')
    for path in EVIDENCE.glob('l426-*.json*'):
        copy(path)
    for pattern in ('l426-*.json', 'l426-*.tsv', 'l426-*.summary.json', 'l426-*-state.txt', 'l426-*-game.png'):
        for path in TEMP.glob(pattern):
            copy(path)
    copy(ROOT / 'Backups/ElementPerformance/gate-saved-root-state.json')
    for name in ('p7-grid-cutover-audit.txt', 'benchmark-player-protection.txt'):
        copy(TEMP / name)
    copy(TEMP / 'grid-liquid-capture.json', 'final-editor-state.json')
    copy(OUT / 'l426-final-concentrated.json', 'final-260-concentrated.json')
    saved = read(OUT / 'saved-capture-summary.json')
    for capture in saved['Captures']:
        source = pathlib.Path(capture['Input'])
        if sha(source) != capture['InputSha256'] or source.stat().st_size != capture['InputBytes']:
            raise RuntimeError('Profiler input differs from the analyzed file')
    manifest = {
        'Utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'Kind': 'L4.26 sparse CPU queries, GPU unchanged-page reuse and empty-row skip; phase in progress',
        'Sources': sources, 'OriginalP7SceneSha256': SCENE_HASH, 'FinalP7SceneSha256': sha(scene),
        'RecoveredRoots': recovered['Roots'], 'FinalRoots': state['Roots'],
        'FinalDiagnostic': {key: gate[key] for key in ('StartUtc','EndUtc','Passed','Failed','Skipped','Planned','Completed','Aborted','SceneRestored')},
        'SavedCaptureAnalysis': {'Files':len(saved['Captures']),'UniqueMainFrames':saved['UniqueMainFrames'],'SharedMainFrameTimestamps':saved['SharedMainFrameTimestamps'],'GpuTimingValid':saved['GpuTimingValid']},
        'CpuBuild': {'Record':cpu,'ModeMeansMs':cpu_means,'ReductionPercent':(1-cpu_means['CachedWetCandidates']/cpu_means['Full175CellScan'])*100,
                    'Meaning':'Same snapshot, completed current CPU geometry build with full175 scan versus wet candidates; not frame FPS'},
        'GpuFencedWork': {'Record':gpu,'ModeMeansMs':gpu_means,'ReductionPercent':(1-gpu_means['CachedWithAirSkip']/gpu_means['FullWithoutAirSkip'])*100,
                         'Meaning':'CPU submission plus GPU readback fence completion Wall Time; not GPU timestamp or game FPS. Empty-column mask experiment was rejected and removed.'},
        'FinalGpuEquivalence':final,
        'EvidenceLoss':'Native Editor crash and authorized reopen cleared Temp. Early CPU/source backups and some intermediate reports are unavailable. Fresh post-reopen measurements replace them. Saved capture summary and main frames survived and input .data hashes were rechecked.',
        'Constraints':{'NoSceneSaveOrDiscard':True,'NoCommit':True,'NoSubAgents':True,'EditorReopenedWithExplicitPermission':True,
            'GameplayQuantityDensityReactionsUnchanged':True,'GpuBudgetLimitUnchanged':True,
            'ProtectedPlay':'Temporary player invulnerability only in fresh diagnostic Play; removed on exit'}
    }
    (OUT / 'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'Passed':gate['Passed'],'CpuBuildReductionPercent':manifest['CpuBuild']['ReductionPercent'],
        'GpuFencedWorkReductionPercent':manifest['GpuFencedWork']['ReductionPercent'],'ScenePreserved':True},ensure_ascii=False))

if __name__ == '__main__':
    run()
