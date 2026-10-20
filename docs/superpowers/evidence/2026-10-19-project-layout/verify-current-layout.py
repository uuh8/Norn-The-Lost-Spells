"""只读核验整理后的资源身份、稳定 ID、配置字段与正式场景布局。"""
import hashlib
import json
import pathlib
import re
import zipfile

root = pathlib.Path.cwd()
e = root / 'docs/superpowers/evidence/2026-10-19-project-layout'
load = lambda name: json.loads((e/name).read_text(encoding='utf-8-sig'))
sha = lambda data: hashlib.sha256(data).hexdigest()
master = load('moves.json')
retired = load('hub-retirement.json')['Retired']
dead = {x['Guid'] for x in retired}
rows = {x['Guid']: dict(x) for x in master['Moves'] if x['Guid'] not in dead}
for manifest in ['resource-name-moves.json','hub-final-name-moves.json','case-rename-final.json','domain-name-finalization.json']:
    for move in load(manifest)['Moves']:
        if move['Guid'] in dead:
            continue
        if move['Guid'] in rows:
            rows[move['Guid']]['To'] = move['To']
        else:
            rows[move['Guid']] = dict(move)
            if manifest == 'case-rename-final.json':
                raise RuntimeError('Case-only rename must already have a source row')

# 保留首次记录的源路径；后续改名只更新最终目的地。
master['Moves'] = sorted(rows.values(), key=lambda x: x['From'])
master['Retired'] = [x for x in master.get('Retired',[]) if x['Guid'] not in dead] + [dict(x, Archive='docs/superpowers/evidence/2026-10-19-project-layout/before-hub-consolidation.zip') for x in retired]
(e/'moves.json').write_text(json.dumps(master,ensure_ascii=False,indent=2),encoding='utf-8')
errors=[]
for move in master['Moves']:
    p=root/move['To'];meta=pathlib.Path(str(p)+'.meta')
    if not p.exists() or not meta.exists() or ('guid: '+move['Guid']) not in meta.read_text(encoding='utf-8-sig'):
        errors.append('Moved identity missing: '+move['To'])
for row in retired:
    if (root/row['Path']).exists() or (root/(row['Path']+'.meta')).exists():errors.append('Retired source survives: '+row['Path'])

identity={}
current_assets={}
asset_paths=list((root/'Assets/_Project/ScriptableObjects').rglob('*.asset'))
production_count=len(asset_paths)
asset_paths+=list((root/'Assets/_Project/Tests/Fixtures').rglob('*.asset'))
for p in asset_paths:
    raw=p.read_bytes();guid=re.search(r'guid: (\w+)',pathlib.Path(str(p)+'.meta').read_text()).group(1)
    try:text=raw.decode('utf-8-sig')
    except UnicodeDecodeError:text=''
    current_assets[guid]=(p.relative_to(root).as_posix(),raw)
    m=re.search(r'^  _contentId: (.+)$',text,re.MULTILINE)
    if m:identity[guid]=m.group(1).strip()
for old in load('before-item-identities.json'):
    if identity.get(old['Guid']) != old['ContentId']:errors.append('ContentId changed: '+str(old))

def normalized_asset(data):
    try:text=data.decode('utf-8-sig').replace('\r\n','\n') if isinstance(data,bytes) else data.replace('\r\n','\n')
    except UnicodeDecodeError:return data
    return re.sub(r'^  m_Name:.*$', '  m_Name: <name>', text, flags=re.MULTILINE)

asset_changes=[];runtime_changes=[];runtime_removed=[];layout=[]
with zipfile.ZipFile(e/'before-hub-consolidation.zip') as z:
    names=set(z.namelist())
    for old in sorted(names):
        if old.startswith('Assets/_Project/ScriptableObjects/') and old.endswith('.asset'):
            meta=old+'.meta';g=re.search(r'guid: (\w+)',z.read(meta).decode('utf-8-sig')).group(1)
            if g in dead:continue
            if g not in current_assets:errors.append('Unexplained removed configuration: '+old);continue
            current,text=current_assets[g]
            if normalized_asset(z.read(old)) != normalized_asset(text):asset_changes.append(dict(From=old,To=current))
    source_by_guid={}
    for p in (root/'Assets/_Project/Scripts').rglob('*.cs'):
        g=re.search(r'guid: (\w+)',pathlib.Path(str(p)+'.meta').read_text()).group(1);source_by_guid[g]=p
    for old in sorted(names):
        if not old.startswith('Assets/_Project/Scripts/') or not old.endswith('.cs'):continue
        g=re.search(r'guid: (\w+)',z.read(old+'.meta').decode('utf-8-sig')).group(1)
        if g in dead:runtime_removed.append(old);continue
        p=source_by_guid.get(g)
        if p is None:errors.append('Missing runtime script: '+old)
        elif z.read(old) != p.read_bytes():runtime_changes.append(p.relative_to(root).as_posix())
    # 对正式地图逐个比较 Transform / RectTransform，能区分移除废弃组件与改变关卡布局。
    for scene in ['Scenes/P7_MainMenu.unity','Scenes/P7_DemoRun.unity','Scenes/P8_BossField.unity','Scenes/SinglePlayer/SinglePlayer_Hub.unity','Scenes/SinglePlayer/SinglePlayer_Home.unity']:
        old='Assets/_Project/'+scene;p=root/old;before=z.read(old);after=p.read_bytes()
        def transforms(data):
            s=data.decode('utf-8-sig').replace('\r\n','\n');blocks=re.split(r'(?=^--- !u!)',s,flags=re.MULTILINE)
            return {re.match(r'--- !u!(?:4|224) &(\d+)',b).group(1):b for b in blocks if re.match(r'--- !u!(?:4|224) &(\d+)',b)}
        a,b=transforms(before),transforms(after);changed=[key for key in a if key not in b or a[key]!=b[key]]
        layout.append(dict(Path=old,FileUnchanged=before==after,TransformsBefore=len(a),TransformsAfter=len(b),ChangedTransforms=changed))
        if changed:errors.append('Formal layout changed: '+old)

if asset_changes:errors.append('Unexpected configuration fields changed (beyond m_Name)')
report=dict(MovedAssets=len(master['Moves']),RetiredThisPhase=len(retired),RuntimeScriptsRetired=len(runtime_removed),RuntimeScriptsChanged=runtime_changes,
    ConfigurationCount=production_count,FixturesIncluded=len(current_assets)-production_count,ContentIdsChecked=len(load('before-item-identities.json')),UnexpectedConfigurationChanges=asset_changes,FormalLayout=layout,Errors=errors)
(e/'hub-identity-validation.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(report,ensure_ascii=False,indent=2))
if errors:raise SystemExit(1)
