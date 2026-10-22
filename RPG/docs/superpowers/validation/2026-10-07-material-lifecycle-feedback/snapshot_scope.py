"""归档本 Task 精确范围；不通过 git reset 覆盖工作区内的其他修改。"""
import difflib
import hashlib
import json
import pathlib
import shutil
import subprocess

HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[3]
original = (HERE / 'files.txt').read_text(encoding='utf-8-sig').splitlines()
new_files = [
    'Assets/_Project/Scripts/ElementField/Gases/GasDiluteDissipation.cs',
    'Assets/_Project/Scripts/ElementField/Gases/GasDiluteDissipation.cs.meta',
    'Assets/_Project/Tests/Rendering/FallingLiquidPresentationTests.cs',
    'Assets/_Project/Tests/Rendering/FallingLiquidPresentationTests.cs.meta',
]
documents = [
    'Assets/_Project/Docs/2026-10-06 元素世界固体气体与材料转换完整解析（更新中）.md',
    'docs/superpowers/plans/2026-10-06 元素世界固定固体与气体扩展实施计划.md',
]
manifest, patches, checks = [], [], []
for relative in original + new_files + documents:
    source = (ROOT / relative).resolve()
    destination = (HERE / 'after' / relative).resolve()
    assert ROOT in source.parents and HERE in destination.parents
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    before = HERE / 'before' / relative
    digest = lambda data: hashlib.sha256(data).hexdigest()
    old = before.read_bytes() if before.is_file() else None
    current = source.read_bytes()
    kind = 'unchanged_reference' if old == current else 'changed' if old is not None else 'documentation_without_preedit_snapshot' if relative in documents else 'new'
    manifest.append({'path': relative, 'kind': kind, 'before_sha256': digest(old) if old is not None else None,
                     'after_sha256': digest(current)})
    if kind in ('changed', 'new'):
        patches.extend(difflib.unified_diff(old.decode('utf-8-sig').splitlines(keepends=True) if old else [],
                                           current.decode('utf-8-sig').splitlines(keepends=True),
                                           fromfile='before/' + relative, tofile='after/' + relative))
        # Unity 自动生成的 .meta 不作为人工源码风格检查，也不改写它的 GUID。
        if source.suffix == '.meta':
            continue
        if old is not None:
            result = subprocess.run(['git', '-c', 'core.autocrlf=false', '-c', 'core.safecrlf=false',
                                     'diff', '--no-index', '--check', '--', str(before), str(destination)],
                                    cwd=ROOT, text=True, encoding='utf-8', errors='replace', capture_output=True)
            detail = (result.stdout + result.stderr).strip()
            checks.append({'path': relative, 'exit_code': result.returncode, 'diagnostic': detail})
        else:
            bad = [i + 1 for i, line in enumerate(current.decode('utf-8-sig').splitlines()) if line.rstrip(' \t') != line]
            checks.append({'path': relative, 'exit_code': 0 if not bad else 2,
                           'diagnostic': '' if not bad else 'trailing whitespace: ' + str(bad)})
(HERE / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
(HERE / 'task-changes.diff').write_text(''.join(patches), encoding='utf-8')
(HERE / 'scoped-whitespace-check.json').write_text(json.dumps(checks, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
problems = [check for check in checks if check['diagnostic']]
print('Snapshot files:', len(manifest), 'Changed:', sum(item['kind'] == 'changed' for item in manifest),
      'New including Unity meta:', sum(item['kind'] == 'new' for item in manifest), 'Whitespace findings:', len(problems))
for problem in problems:
    print(problem)
raise SystemExit(1 if problems else 0)
