"""逐字段对照新导出的材料组件与未修改源Scene；只写task审计结果。"""
import json,pathlib,re,collections
from audit_prefab_semantics import HEADER,LOADER,diff,parse
import yaml
ROOT=pathlib.Path('F:/Develop Project/RPG');OUT=pathlib.Path('C:/Users/zhy/Documents/Codex/2026-10-31/task/prefab-semantics-audit')
def main():
    data=json.loads((OUT/'audit-final.json').read_text(encoding='utf-8'));checks=[]
    for label,scene in [('P7','P7_DemoRun.unity'),('P8','P8_BossField.unity')]:
        added=next(p for p in data['prefabs'] if p['path'].endswith(label+'World.prefab'))['added']
        text=(ROOT/'RPG/Assets/_Project/Scenes'/scene).read_text(encoding='utf-8-sig');hs=list(HEADER.finditer(text));raw={};info={};gos={}
        for i,h in enumerate(hs):
            fid=int(h[2]);body=text[h.end():hs[i+1].start() if i+1<len(hs) else len(text)];raw[fid]=body
            kind=re.search(r'^([A-Za-z0-9_]+):',body,re.M);name=re.search(r'^  m_Name: (.*)$',body,re.M);go=re.search(r'^  m_GameObject: \{fileID: (-?\d+)\}',body,re.M);script=re.search(r'^  m_Script: \{fileID: 11500000, guid: ([0-9a-f]{32})',body,re.M)
            info[fid]={'kind':kind[1] if kind else '?','go':int(go[1]) if go else None,'script':script[1] if script else None}
            if int(h[1])==1 and name:gos[fid]=name[1]
        names={a['data']['m_Script']['guid']:a['object'].split('::')[-1].split(':')[-1] for a in added}
        def descr(fid):
            v=info.get(fid)
            if not v:return '<missing:'+str(fid)+'>'
            if v['kind']=='GameObject':return 'GameObject@'+gos.get(fid,'?')
            return (('MonoBehaviour:'+names.get(v['script'],v['script'])) if v['script'] else v['kind'])+'@'+gos.get(v['go'],'?')
        def norm_src(v):
            if isinstance(v,dict):
                if 'fileID' in v and not v.get('guid') and v['fileID']:return {**v,'fileID':descr(v['fileID'])}
                return {k:norm_src(x) for k,x in v.items()}
            if isinstance(v,list):return [norm_src(x) for x in v]
            return v
        def norm_target(v):
            if isinstance(v,dict):
                if 'fileID' in v and isinstance(v['fileID'],str):
                    s=v['fileID'];go,_,kind=s.partition('::');return {**v,'fileID':(kind if kind else 'GameObject')+'@'+go.rsplit('/',1)[-1]}
                return {k:norm_target(x) for k,x in v.items()}
            if isinstance(v,list):return [norm_target(x) for x in v]
            return v
        for a in added:
            script=a['data']['m_Script']['guid'];name=a['object'].split('::')[0].rsplit('/',1)[-1];kind=names[script]
            if kind=='OnlineMaterialWorldBinding':checks.append({'object':a['object'],'status':'new_binding_expected'});continue
            candidates=[fid for fid,v in info.items() if v['script']==script and gos.get(v['go'])==name]
            rec={'object':a['object'],'source_scene':'RPG/Assets/_Project/Scenes/'+scene,'source_candidates':candidates}
            if not candidates and kind=='ElementWorldFireRenderer':
                nested='RPG/Assets/_Project/Art/Elemental/Prefabs/PF_ElementWorldFireEmitter.prefab';nd=parse((ROOT/nested).read_bytes());matching=[(fid,d) for fid,d in nd.items() if d['data'].get('m_Script',{}).get('guid')==script]
                if len(matching)!=1:raise ValueError('nested renderer source not unique')
                fid,selected=matching[0];source=selected['data']
                for ni,doc in nd.items():
                    v=doc['data'];info[ni]={'kind':doc['kind'],'go':v.get('m_GameObject',{}).get('fileID'),'script':v.get('m_Script',{}).get('guid')}
                    if doc['kind']=='GameObject':gos[ni]=v['m_Name']
                pg=re.search(r'^guid: ([0-9a-f]{32})$',pathlib.Path(str(ROOT/nested)+'.meta').read_text(),re.M)[1]
                instances=[yaml.load(body,Loader=LOADER)['PrefabInstance'] for body in raw.values() if body.lstrip().startswith('PrefabInstance:') and re.search(r'm_SourcePrefab: .*guid: '+pg,body)]
                if len(instances)!=1:raise ValueError('nested source Scene instance not unique')
                overrides=[m for m in instances[0]['m_Modification']['m_Modifications'] if m['target']['fileID']==fid]
                for m in overrides:
                    if m['propertyPath']!='_worldRuntime':raise ValueError('unexpected nested renderer override '+str(m))
                    source[m['propertyPath']]=m['objectReference']
                rec.update(status='compared_nested_prefab',source_prefab=nested,source_fileID=fid,source_line=selected['line'],scene_renderer_overrides=overrides,differences=list(diff(norm_src(source),norm_target(a['data']))))
            elif len(candidates)!=1:rec['status']='unresolved_source_component'
            else:
                fid=candidates[0];source=next(iter(yaml.load(raw[fid],Loader=LOADER).values()));rec['status']='compared';rec['differences']=list(diff(norm_src(source),norm_target(a['data'])));pos=next(h.start() for h in hs if int(h[2])==fid);rec['source_line']=text.count('\n',0,pos)+1
            checks.append(rec)
    (OUT/'source-component-comparison.json').write_text(json.dumps(checks,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'counts':dict(collections.Counter(r['status'] for r in checks)),'with_differences':[r for r in checks if r.get('differences')],'unresolved':[r for r in checks if r['status']=='unresolved_source_component']},ensure_ascii=False,indent=2))
if __name__=='__main__':main()
