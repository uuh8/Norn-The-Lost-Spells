"""补充验证 Git 不保存的 Unity PackageCache 脚本引用；只写 task 报告。"""
import collections, hashlib, json, pathlib, re, subprocess

ROOT=pathlib.Path('F:/Develop Project/RPG')
OUT=pathlib.Path('C:/Users/zhy/Documents/Codex/2026-10-31/task/prefab-semantics-audit')
GUID=re.compile(r'^guid: ([0-9a-f]{32})$',re.M)
NAMES={'NavMeshSurface.cs.meta','NavMeshModifier.cs.meta','NavMeshModifierVolume.cs.meta','Volume.cs.meta','UniversalAdditionalLightData.cs.meta','Image.cs.meta','RawImage.cs.meta','Text.cs.meta','Slider.cs.meta','CanvasScaler.cs.meta','GraphicRaycaster.cs.meta'}

def main():
    data=json.loads((OUT/'audit.json').read_text(encoding='utf-8'))
    packages={}
    for package in (ROOT/'RPG/Library/PackageCache').iterdir():
        if not package.is_dir() or not package.name.startswith(('com.unity.render-pipelines.','com.unity.ai.navigation@','com.unity.ugui@')):continue
        for meta in package.rglob('*.cs.meta'):
            if meta.name in NAMES:
                m=GUID.search(meta.read_text(encoding='utf-8-sig'))
                if m:packages[m[1]]=meta.relative_to(ROOT).as_posix()[:-5]
    data['package_guid_sources']=packages
    for record in data['external_references']:
        if record['validation']=='unresolved_guid' and record['guid'] in packages:
            record['path']=packages[record['guid']];record['validation']='package_script_guid_resolved'
            local_ids={u['fileID'] for p in data['prefabs'] for r in p['work_unresolved_guids'] if r['guid']==record['guid'] for u in r['uses']}
            record['bad_local_ids']=sorted(local_ids-{11500000})
    for prefab in data['prefabs']:
        prefab['work_unresolved_guids']=[r for r in prefab['work_unresolved_guids'] if r['guid'] not in packages]
        for guid,path in packages.items():
            if guid in prefab['work_script_counts']:prefab['work_script_counts'][pathlib.PurePosixPath(path).stem]=prefab['work_script_counts'].pop(guid)
    refs=data['external_references']
    data['reference_summary']={'unique_guids':len(refs),'uses':sum(r['uses'] for r in refs),'validation_counts':dict(collections.Counter(r['validation'] for r in refs)),'errors':[r for r in refs if r['validation'] in {'unresolved_guid','missing_asset'} or r.get('bad_local_ids')]}
    data['prefab_meta_checks']=[]
    for p in data['prefabs']:
        meta=p['path']+'.meta';old=subprocess.run(['git','-C',str(ROOT),'show',data['head']+':'+meta],check=True,stdout=subprocess.PIPE).stdout;now=(ROOT/meta).read_bytes()
        data['prefab_meta_checks'].append({'path':meta,'guid':GUID.search(now.decode('utf-8-sig'))[1],'equal_after_crlf_normalization':old.replace(b'\r\n',b'\n')==now.replace(b'\r\n',b'\n')})
    material_head=subprocess.run(['git','-C',str(ROOT),'ls-tree','-r','--name-only',data['head'],'--','RPG/Assets/_Project/ScriptableObjects/World/Online/Materials'],check=True,stdout=subprocess.PIPE).stdout.decode()
    data['materials_head_entries']=material_head.splitlines()
    data['audit_snapshot_stability']=[{'path':r['path'],'unchanged_since_read':hashlib.sha256((ROOT/r['path']).read_bytes()).hexdigest().upper()==r['work_sha256']} for r in data['prefabs']]
    data['audit_snapshot_stability'] += [{'path':r['path'],'unchanged_since_read':hashlib.sha256((ROOT/r['path']).read_bytes()).hexdigest().upper()==r['sha256']} for r in data['profiles']]
    (OUT/'audit-final.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'reference_summary':data['reference_summary'],'profiles':[{'path':r['path'],'source':r['source'],'diffs':r['differences'],'source_work_matches_head':r['source_work_matches_head']} for r in data['profiles']],'protected':data['protected'],'prefabs':[{'path':p['path'],'head_documents':p['head_documents'],'work_documents':p['work_documents'],'go_count':p['work_gameobjects'],'renumbered':p['renumbered_matched_objects'],'added':[x['object'] for x in p['added']],'removed':[x['object'] for x in p['removed']],'changed_objects':p['changed_objects'],'internal_refs':p['work_reference_count'],'missing':p['work_missing_internal_refs'],'structure_errors':p['work_structure_errors']} for p in data['prefabs']]},ensure_ascii=False,indent=2))

if __name__=='__main__':main()
