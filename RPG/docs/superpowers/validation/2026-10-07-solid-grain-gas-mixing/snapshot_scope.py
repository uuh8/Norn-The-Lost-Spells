"""只归档本轮真实工作区差异，不覆盖其他 Task 的修改。"""
import pathlib, shutil, hashlib, json, difflib, subprocess, sys
HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[3]
stage = sys.argv[1]
assert stage in ('before', 'after')
manifest, patches, checks = [], [], []
for relative in (HERE/'files.txt').read_text(encoding='utf-8').splitlines():
    source, target = ROOT/relative, HERE/stage/relative
    if not source.is_file():
        continue
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source,target)
    if stage=='before':
        continue
    old=(HERE/'before'/relative).read_bytes()
    current=source.read_bytes()
    manifest.append(dict(path=relative, changed=old!=current,
        before_sha256=hashlib.sha256(old).hexdigest(),after_sha256=hashlib.sha256(current).hexdigest()))
    if old!=current:
        patches.extend(difflib.unified_diff(old.decode('utf-8-sig').splitlines(True),current.decode('utf-8-sig').splitlines(True),fromfile='before/'+relative,tofile='after/'+relative))
        result=subprocess.run(['git','-c','core.autocrlf=false','-c','core.safecrlf=false','diff','--no-index','--check','--',str(HERE/'before'/relative),str(target)],cwd=ROOT,text=True,encoding='utf-8',errors='replace',capture_output=True)
        checks.append(dict(path=relative,diagnostic=(result.stdout+result.stderr).strip()))
if stage=='after':
    (HERE/'manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (HERE/'task-changes.diff').write_text(''.join(patches),encoding='utf-8')
    (HERE/'scoped-whitespace-check.json').write_text(json.dumps(checks,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    problems=[c for c in checks if c['diagnostic']]
    print('Scope:',len(manifest),'Changed:',sum(m['changed'] for m in manifest),'Whitespace findings:',len(problems))
    for problem in problems: print(problem)
    raise SystemExit(bool(problems))
print('Snapshot:',stage)
