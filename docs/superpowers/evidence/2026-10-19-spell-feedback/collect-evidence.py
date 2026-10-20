"""在全部原生测试结束后收集证据；不启动 Unity、不改场景、不接触玩家存档。"""
from pathlib import Path
import hashlib
import json
import re
import shutil
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[4]
OUT = Path(__file__).resolve().parent
TEMP = ROOT / 'Temp/SpellProgrammingV2'

def save(name, value):
    (OUT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')

def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest().upper()

def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)

for file in TEMP.glob('feedback-*'):
    if file.suffix in ('.xml', '.json'):
        copy(file, OUT / 'tests' / file.name)

# 按最新有效用例去重，不把多批次伪装成一次全量PlayMode。
latest = {}
sources = ['feedback-playmode-final', 'feedback-adjacent-regression',
           'feedback-corrected-final', 'feedback-ui-path-final', 'feedback-last-smoke']
for name in sources:
    for case in ET.parse(TEMP / (name + '.xml')).findall('.//test-case'):
        latest[case.get('fullname')] = {'Result': case.get('result'), 'Source': name + '.xml'}
assert all(row['Result'] == 'Passed' for row in latest.values()), latest
save('latest-playmode.json', {'UniquePassed': len(latest), 'Batches': sources, 'Cases': latest})

edit = ET.parse(TEMP / 'feedback-full-editmode-final.xml').getroot()
edit_latest = {case.get('fullname'): case for case in edit.findall('.//test-case')}
for case in ET.parse(TEMP / 'feedback-hub-authoring-final.xml').findall('.//test-case'):
    edit_latest[case.get('fullname')] = case
failures = [{'Test': case.get('fullname'), 'Message': case.findtext('./failure/message')}
            for case in edit_latest.values() if case.get('result') == 'Failed']
baseline = json.loads((ROOT / 'docs/superpowers/evidence/2026-10-18-spell-v2-final/known-existing-editmode-failures.json').read_text(encoding='utf-8-sig'))
assert {x['Test'] for x in failures} == {x['Test'] for x in baseline}, failures
save('editmode-summary.json', {'FullSnapshotPassed': int(edit.get('passed')), 'FullSnapshotFailed': int(edit.get('failed')),
                              'Batches': ['feedback-full-editmode-final.xml', 'feedback-hub-authoring-final.xml'],
                              'LatestPassed': sum(case.get('result') == 'Passed' for case in edit_latest.values()),
                              'LatestFailed': len(failures), 'Failures': failures,
                              'SameFailureNamesAsPreviousDelivery': True})

for name in ['ContinuousFastInputFor30Seconds.txt', 'ContinuousHeavyInputFor30Seconds.txt', 'native-hot-scopes.txt']:
    copy(TEMP / 'Performance' / name, OUT / 'performance' / name)
profile = TEMP / 'Performance/native-hot-scopes.data'
with zipfile.ZipFile(OUT / 'performance/native-hot-scopes.data.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    archive.write(profile, profile.name)
save('performance/profile-source.json', {'File': profile.name, 'Bytes': profile.stat().st_size, 'Sha256': digest(profile)})

with zipfile.ZipFile(OUT / 'before-scenes-and-prefab.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for file in (TEMP / 'FeedbackBackup').iterdir():
        if file.is_file():
            archive.write(file, file.name)

for name in ['home-research.png', 'research-owned.png', 'formal-hub-region-player.png']:
    copy(TEMP / 'HubEvidence' / name, OUT / 'screenshots' / name)
copy(TEMP / 'home-research-manifest.txt', OUT / 'home-research-manifest.txt')

def rects(path):
    return dict(re.findall(r'^--- !u!224 &(\d+)\r?\n(.*?)(?=^---|\Z)', path.read_text(encoding='utf-8'), re.M | re.S))

before = TEMP / 'FeedbackBackup/SinglePlayer_Home.unity'
after = ROOT / 'Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity'
a, b = rects(before), rects(after)
layout = {'OriginalRects': len(a), 'CurrentRects': len(b),
          'ChangedRects': [k for k in a if k in b and a[k] != b[k]], 'RemovedRects': [k for k in a if k not in b],
          'OriginalMeta': digest(Path(str(before) + '.meta')), 'CurrentMeta': digest(Path(str(after) + '.meta'))}
assert not layout['ChangedRects'] and not layout['RemovedRects'] and layout['OriginalMeta'] == layout['CurrentMeta'], layout
save('home-layout-preservation.json', layout)

runtime = []
for module in ['Core', 'Combat', 'Skills', 'Run', 'Character', 'UI']:
    for file in sorted((ROOT / ('Assets/_Project/Scripts/' + module)).rglob('*.cs')):
        runtime.append({'Path': file.relative_to(ROOT).as_posix(), 'Sha256': digest(file)})
save('verified-runtime-source-hashes.json', runtime)
previous = json.loads((ROOT / 'docs/superpowers/evidence/2026-10-18-spell-v2-final/verified-assembly-source-hashes.json').read_text(encoding='utf-8-sig'))
old_hashes = {x['Path']: x['Sha256'] for x in previous}
save('runtime-files-changed-since-previous-delivery.json', [x for x in runtime if old_hashes.get(x['Path']) != x['Sha256']])
missing = []
for folder in ['Scripts', 'Tests', 'Editor']:
    for file in (ROOT / ('Assets/_Project/' + folder)).rglob('*.cs'):
        if not Path(str(file) + '.meta').is_file():
            missing.append(file.relative_to(ROOT).as_posix())
assert not missing, missing
save('meta-check.json', {'MissingScriptMeta': missing})
print(json.dumps({'PlayModeUniquePassed': len(latest), 'EditModeLatestPassed': sum(case.get('result') == 'Passed' for case in edit_latest.values()),
                  'EditModeExistingFailed': len(failures), 'RuntimeSources': len(runtime),
                  'OriginalHomeRects': len(a), 'MissingMeta': len(missing)}))
