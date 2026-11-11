"""只读 Unity YAML 资源语义审计。只写 --out 指定的报告目录，不调用 Unity 或 Git mutation。

使用 GameObject 层级（重名兄弟保留顺序）和组件类型/脚本 GUID 对齐对象。
替换内部 fileID 为语义对象键，但保留组件顺序、子对象顺序、所有数据和外部引用。
因此 document 排序与 ID 改号不会产生伪变更；真实重排、引用与配置变化仍可见。
"""
from __future__ import annotations
import argparse, collections, hashlib, json, pathlib, re, subprocess, sys, time
import yaml

HEADER = re.compile(r"^--- !u!(\d+) &(-?\d+)(?: stripped)?\s*$", re.M)
GUID = re.compile(r"^guid: ([0-9a-f]{32})$", re.M)
PREFABS = [
    "RPG/Assets/_Project/Prefabs/World/Online/P7World.prefab",
    "RPG/Assets/_Project/Prefabs/World/Online/P8World.prefab",
    "RPG/Assets/_Project/Prefabs/World/Online/Npcs/P8_05.prefab",
]
PROFILE_FOLDER = "RPG/Assets/_Project/ScriptableObjects/World/Online/Materials"
LOADER = getattr(yaml, "CSafeLoader", yaml.SafeLoader)

def digest(data):
    return hashlib.sha256(data).hexdigest().upper()

def git(root, *args):
    # 白名单只允许读取对象与树；调用者不能借此函数运行 Git 写操作。
    if args[0] not in {"show", "ls-tree", "rev-parse", "diff", "grep", "ls-files"}:
        raise ValueError("read-only Git whitelist")
    return subprocess.run(["git", "-C", str(root), *args], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE).stdout

def parse(data):
    text = data.decode("utf-8-sig")
    headers = list(HEADER.finditer(text))
    docs = {}
    previous_end=0;line_number=1
    for i, h in enumerate(headers):
        line_number += text.count("\n",previous_end,h.start());previous_end=h.start()
        body = yaml.load(text[h.end():headers[i+1].start() if i+1 < len(headers) else len(text)], Loader=LOADER)
        kind, value = next(iter(body.items()))
        fid = int(h.group(2))
        if fid in docs:
            raise ValueError(f"duplicate fileID {fid}")
        docs[fid] = {"class_id":int(h.group(1)), "kind":kind, "data":value, "line":line_number}
    return docs

def refs(value, path=""):
    if isinstance(value, dict):
        if "fileID" in value:
            yield path, value
        for k, v in value.items():
            yield from refs(v, path+"/"+str(k))
    elif isinstance(value, list):
        for i, v in enumerate(value):
            yield from refs(v, path+f"[{i}]")

def diff(a, b, p=""):
    if isinstance(a,dict) and isinstance(b,dict):
        for k in sorted(set(a)|set(b)):
            if k not in a: yield {"field":p+"/"+k,"before":"<missing>","after":b[k]}
            elif k not in b: yield {"field":p+"/"+k,"before":a[k],"after":"<missing>"}
            else: yield from diff(a[k],b[k],p+"/"+k)
    elif isinstance(a,list) and isinstance(b,list):
        if len(a)!=len(b):
            yield {"field":p,"before":a,"after":b}
        else:
            for i,(x,y) in enumerate(zip(a,b)): yield from diff(x,y,p+f"[{i}]")
    elif a != b:
        yield {"field":p,"before":a,"after":b}

def canonical(docs, guid_map):
    gos={i:d for i,d in docs.items() if d["kind"]=="GameObject"}
    ts={i:d for i,d in docs.items() if d["kind"] in {"Transform","RectTransform"}}
    go_t={d["data"]["m_GameObject"]["fileID"]:i for i,d in ts.items()}
    keys={}
    def go_key(i, stack=()):
        if i in keys:return keys[i]
        if i in stack:raise ValueError("hierarchy cycle")
        t=ts[go_t[i]]["data"]; parent=t.get("m_Father",{}).get("fileID",0)
        name=gos[i]["data"]["m_Name"]
        if parent:
            siblings=[x["fileID"] for x in ts[parent]["data"].get("m_Children",[])]
            same=[s for s in siblings if gos[ts[s]["data"]["m_GameObject"]["fileID"]]["data"]["m_Name"]==name]
            suffix=f"[same-name:{same.index(go_t[i])}]" if len(same)>1 else ""
            result=go_key(ts[parent]["data"]["m_GameObject"]["fileID"],stack+(i,))+"/"+name+suffix
        else:result="/"+name
        keys[i]=result
        return result
    for i in gos:go_key(i)
    for i,g in gos.items():
        counts=collections.Counter()
        for cr in g["data"].get("m_Component",[]):
            ci=cr["component"]["fileID"]
            cd=docs[ci]; script=cd["data"].get("m_Script",{}).get("guid")
            kind=cd["kind"]
            if script:kind += ":"+pathlib.PurePosixPath(guid_map.get(script,script)).stem
            n=counts[kind];counts[kind]+=1
            keys[ci]=keys[i]+"::"+kind+(f"[{n}]" if n else "")
    for i,d in docs.items():
        if i not in keys:
            # 当前目标若含 PrefabInstance 等无层级对象，必须显式报告，不能静默当作噪声。
            keys[i]=f"<unmapped>:{d['kind']}:{i}"
    if len(set(keys.values())) != len(keys):raise ValueError("semantic key collision")
    missing=[];external=collections.Counter();external_fields=collections.defaultdict(list)
    def norm(v, owner, p=""):
        if isinstance(v,dict):
            if "fileID" in v and "guid" not in v:
                f=v["fileID"]
                if f and f not in keys:missing.append({"owner":owner,"field":p,"fileID":f})
                return {**v,"fileID":keys.get(f,f)}
            if "fileID" in v and v.get("guid"):
                external[v["guid"]]+=1
                external_fields[v["guid"]].append({"owner":owner,"field":p,"fileID":v["fileID"]})
            return {k:norm(x,owner,p+"/"+str(k)) for k,x in v.items()}
        if isinstance(v,list):return [norm(x,owner,p+f"[{i}]") for i,x in enumerate(v)]
        return v
    normed={keys[i]:{"kind":d["kind"],"class_id":d["class_id"],"data":norm(d["data"],keys[i])} for i,d in docs.items()}
    # 验证双向父子关系、GameObject组件归属以及所有组件是否恰好出现一次。
    structural=[];component_use=collections.Counter()
    for i,g in gos.items():
        for c in g["data"].get("m_Component",[]):
            ci=c["component"]["fileID"];component_use[ci]+=1
            if docs[ci]["data"].get("m_GameObject",{}).get("fileID")!=i:
                structural.append({"kind":"component owner mismatch","object":keys[i],"component":ci})
    for i,t in ts.items():
        p=t["data"].get("m_Father",{}).get("fileID",0)
        if p and i not in [x["fileID"] for x in ts[p]["data"].get("m_Children",[])]:
            structural.append({"kind":"parent missing child","object":keys[i]})
        for c in t["data"].get("m_Children",[]):
            if ts[c["fileID"]]["data"].get("m_Father",{}).get("fileID")!=i:
                structural.append({"kind":"child parent mismatch","object":keys[i]})
    for i,count in component_use.items():
        if count!=1:structural.append({"kind":"component use count","object":keys[i],"count":count})
    return normed,keys,missing,external,external_fields,structural

def main():
    ap=argparse.ArgumentParser();ap.add_argument("--root",type=pathlib.Path,required=True);ap.add_argument("--out",type=pathlib.Path,required=True);a=ap.parse_args()
    root=a.root.resolve();out=a.out.resolve();out.mkdir(parents=True,exist_ok=True)
    allowed=pathlib.Path("C:/Users/zhy/Documents/Codex/2026-10-31/task").resolve()
    if not out.is_relative_to(allowed):raise ValueError("report output must remain inside task writable root")
    started=time.time();revision=git(root,"rev-parse","HEAD").decode().strip()
    print("Indexing .meta GUIDs from HEAD plus current changed/untracked .meta",flush=True)
    meta_guids={}
    for line in git(root,"grep","-I","-e","^guid: ",revision,"--","*.meta").decode("utf-8").splitlines():
        m=re.match(r"[^:]+:(.*\.meta):guid: ([0-9a-f]{32})$",line)
        if m:meta_guids[m[1]]=m[2]
    changed=git(root,"diff","--name-only","-z",revision,"--","*.meta").decode().split("\0")
    untracked=git(root,"ls-files","--others","--exclude-standard","-z","--","*.meta").decode().split("\0")
    for rel in set(changed+untracked)-{""}:
        path=root/rel
        if path.is_file():
            m=GUID.search(path.read_text(encoding="utf-8-sig",errors="replace"))
            if m:meta_guids[rel]=m[1]
        else:meta_guids.pop(rel,None)
    guid_map={};duplicates=[]
    for meta,guid in meta_guids.items():
        rel=meta[:-5]
        if guid in guid_map and guid_map[guid]!=rel:duplicates.append({"guid":guid,"paths":[guid_map[guid],rel]})
        else:guid_map[guid]=rel
    result={"head":revision,"method":"YAML document order ignored; fileID normalized to hierarchy/component semantic keys; all data and list order retained", "repository_write":False,"unity_or_tests_invoked":False,"meta_guid_count":len(guid_map),"duplicate_guids":duplicates,"prefabs":[],"profiles":[],"protected":[],"limitations":["Static serialized data audit; no Unity import/instantiation/render/physics or player experience validation.","External GUIDs resolved via existing .meta; imported model/texture subasset local IDs require Unity importer and remain unverified.","Hierarchy keys use same-name sibling order; true sibling/component order is preserved, not normalized away."]}
    profile_pairs={};all_refs=collections.defaultdict(list)
    # 旧导出器可能完全剥除了材料Owner，因此原配置必须以源Scene实际序列化引用为准。
    for label,scene in [("P7","P7_DemoRun.unity"),("P8","P8_BossField.unity")]:
        scene_text=(root/"RPG/Assets/_Project/Scenes"/scene).read_text(encoding="utf-8-sig")
        for owner,profile in [("ElementWorldRuntime","ElementWorldProfile"),("LiquidWorldRuntime","LiquidWorldProfile"),("FixedSolidWorldRuntime","FixedSolidWorldProfile"),("GasWorldRuntime","GasWorldProfile")]:
            m=re.search(r"m_EditorClassIdentifier: Game\.ElementField::Game\.ElementField\."+owner+r"\n(?:(?!^---).)*?^  _profile: \{fileID: 11400000, guid: ([0-9a-f]{32}), type: 2\}",scene_text,re.M|re.S)
            if not m:raise ValueError("source Scene missing unique owner profile "+owner)
            newpath=f"{PROFILE_FOLDER}/{label}_OnlineCPU_{profile}.asset"
            ng=meta_guids[newpath+".meta"];profile_pairs[ng]=m[1]
        original=next(iter(parse((root/guid_map[profile_pairs[meta_guids[f'{PROFILE_FOLDER}/{label}_OnlineCPU_ElementWorldProfile.asset.meta']]]).read_bytes()).values()))["data"]
        profile_pairs[meta_guids[f"{PROFILE_FOLDER}/{label}_OnlineCPU_MaterialSimulationRoutingProfile.asset.meta"]]=original["_materialSimulationRouting"]["guid"]
    for path in PREFABS:
        print("Auditing",path,flush=True)
        before=git(root,"show",revision+":"+path);after=(root/path).read_bytes()
        bd=parse(before);ad=parse(after)
        bn,bk,bmissing,be,bef,bs=canonical(bd,guid_map);an,ak,amissing,ae,aef,ass=canonical(ad,guid_map)
        added=sorted(set(an)-set(bn));removed=sorted(set(bn)-set(an));changes=[]
        bki={key:i for i,key in bk.items()};aki={key:i for i,key in ak.items()}
        for k in sorted(set(an)&set(bn)):
            delta=list(diff(bn[k],an[k]))
            if delta:changes.append({"object":k,"head_fileID":bki[k],"work_fileID":aki[k],"fields":delta})
            for d in delta:
                if d["field"].endswith("/guid") and isinstance(d["after"],str) and guid_map.get(d["after"],"").startswith(PROFILE_FOLDER+"/"):
                    profile_pairs[d["after"]]=d["before"]
        for guid,fields in aef.items():all_refs[guid]+= [{"asset":path,**f} for f in fields]
        renumbered=sum(bk[i] in an and aki[bk[i]]!=i for i in bk)
        entry={"path":path,"head_sha256":digest(before),"work_sha256":digest(after),"head_lines":before.count(b"\n"),"work_lines":after.count(b"\n"),"head_documents":len(bd),"work_documents":len(ad),"head_gameobjects":sum(d["kind"]=="GameObject" for d in bd.values()),"work_gameobjects":sum(d["kind"]=="GameObject" for d in ad.values()),"renumbered_matched_objects":renumbered,"added": [{"object":k,**an[k]} for k in added],"removed":[{"object":k,**bn[k]} for k in removed],"changed_objects":changes,"unchanged_objects":len(set(an)&set(bn))-len(changes),"unmapped_objects":[k for k in an if k.startswith("<unmapped>")],"work_missing_internal_refs":amissing,"head_missing_internal_refs":bmissing,"work_structure_errors":ass,"head_structure_errors":bs,"work_reference_count":{"internal":sum(1 for d in ad.values() for _,r in refs(d["data"]) if r.get("fileID") and not r.get("guid")),"external":sum(ae.values()),"unique_external_guids":len(ae)},"work_unresolved_guids":[{"guid":g,"uses":aef[g]} for g in ae if g not in guid_map and not g.startswith("0000000000000000")],"work_class_counts":dict(collections.Counter(d["kind"] for d in ad.values())),"work_script_counts":dict(collections.Counter(pathlib.PurePosixPath(guid_map.get(d["data"].get("m_Script",{}).get("guid"),"<none>")).stem for d in ad.values() if d["kind"]=="MonoBehaviour"))}
        result["prefabs"].append(entry)
        # ElementWorldProfile -> routing Profile 的源/副本对应关系在该层继续展开。
        for ng,og in list(profile_pairs.items()):
            np=root/guid_map[ng];op=root/guid_map.get(og,"")
            if np.is_file() and op.is_file():
                n=next(iter(parse(np.read_bytes()).values()))["data"];o=next(iter(parse(op.read_bytes()).values()))["data"]
                for field in n:
                    if isinstance(n[field],dict) and n[field].get("guid") and isinstance(o.get(field),dict):
                        nrg=n[field]["guid"];org=o[field].get("guid")
                        if guid_map.get(nrg,"").startswith(PROFILE_FOLDER+"/") and org:profile_pairs[nrg]=org
        del bd,ad,bn,an
    for p in sorted((root/PROFILE_FOLDER).glob("*.asset")):
        path=p.relative_to(root).as_posix();ng=GUID.search(pathlib.Path(str(p)+".meta").read_text())[1]
        nd=parse(p.read_bytes());n=next(iter(nd.values()))["data"];og=profile_pairs.get(ng);source=guid_map.get(og)
        rec={"path":path,"guid":ng,"sha256":digest(p.read_bytes()),"source":source,"source_guid":og,"references":[],"differences":[],"source_work_matches_head":None}
        for field,r in refs(n):
            if r.get("guid"):
                rec["references"].append({"field":field,**r,"resolved_path":guid_map.get(r["guid"])})
                all_refs[r["guid"]].append({"asset":path,"owner":path,"field":field,"fileID":r["fileID"]})
        if source:
            oldbytes=(root/source).read_bytes();od=parse(oldbytes);o=next(iter(od.values()))["data"]
            rec["differences"]=list(diff(o,n))
            try:rec["source_work_matches_head"]=git(root,"show",revision+":"+source).replace(b"\r\n",b"\n")==oldbytes.replace(b"\r\n",b"\n")
            except subprocess.CalledProcessError:pass
        result["profiles"].append(rec)
    # 外部引用 text YAML local ID 能直接验证；导入资源只验证 GUID，不假装导入器 subasset 存在。
    external=[]
    for g,uses in sorted(all_refs.items()):
        path=guid_map.get(g);rec={"guid":g,"path":path,"uses":len(uses),"validation":None}
        if not path:
            rec["validation"]="unity_builtin" if g.startswith("0000000000000000") else "unresolved_guid"
        elif not (root/path).is_file():rec["validation"]="missing_asset"
        elif pathlib.PurePosixPath(path).suffix==".cs":rec["validation"]="script_guid_resolved";rec["bad_local_ids"]=sorted({u["fileID"] for u in uses if u["fileID"]!=11500000})
        else:
            with (root/path).open("rb") as f:head=f.read(100)
            if head.lstrip(b"\xef\xbb\xbf").startswith(b"%YAML"):
                ids={int(m[2]) for m in HEADER.finditer((root/path).read_text(encoding="utf-8-sig"))}
                rec["validation"]="text_asset_local_ids_checked";rec["bad_local_ids"]=sorted({u["fileID"] for u in uses if u["fileID"] and u["fileID"] not in ids})
            else:rec["validation"]="imported_asset_guid_only"
        external.append(rec)
    result["external_references"]=external
    result["reference_summary"]={"unique_guids":len(external),"uses":sum(r["uses"] for r in external),"validation_counts":dict(collections.Counter(r["validation"] for r in external)),"errors":[r for r in external if r["validation"] in {"unresolved_guid","missing_asset"} or r.get("bad_local_ids")]}
    protected=["RPG/Assets/_Project/Scenes/P7_DemoRun.unity","RPG/Assets/_Project/Scenes/P8_BossField.unity","RPG/ProjectSettings/EditorBuildSettings.asset","RPG/ProjectSettings/DynamicsManager.asset","RPG/ProjectSettings/GraphicsSettings.asset","RPG/ProjectSettings/QualitySettings.asset","RPG/ProjectSettings/ProjectSettings.asset"]
    for path in protected:
        b=git(root,"show",revision+":"+path);w=(root/path).read_bytes()
        result["protected"].append({"path":path,"head_sha256":digest(b),"work_sha256":digest(w),"bytes_equal":b==w,"equal_after_crlf_normalization":b.replace(b"\r\n",b"\n")==w.replace(b"\r\n",b"\n")})
    result["elapsed_seconds"]=round(time.time()-started,2)
    (out/"audit.json").write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8")
    lines=["# N5 Prefab 静态语义审计", "",f"HEAD: `{revision}`", "", "## 方法",result["method"],"", "## Prefab", "|资源|文档 HEAD→工作区|改号的匹配对象|新增/移除|真实变化对象|未变化对象|", "|---|---:|---:|---:|---:|---:|"]
    for p in result["prefabs"]:
        lines.append(f"|{pathlib.PurePosixPath(p['path']).name}|{p['head_documents']}→{p['work_documents']}|{p['renumbered_matched_objects']}|{len(p['added'])}/{len(p['removed'])}|{len(p['changed_objects'])}|{p['unchanged_objects']}|")
    lines += ["", "## 配置差异"]
    for r in result["profiles"]:
        lines += ["",f"### {pathlib.PurePosixPath(r['path']).name}",f"原配置：`{r['source']}`；原配置与 HEAD 一致：{r['source_work_matches_head']}","```json",json.dumps(r["differences"],ensure_ascii=False,indent=2),"```"]
    lines += ["", "## Prefab 真实变化"]
    for p in result["prefabs"]:
        lines += ["", f"### {pathlib.PurePosixPath(p['path']).name}","```json",json.dumps({"added":p["added"],"removed":p["removed"],"changed":p["changed_objects"]},ensure_ascii=False,indent=2),"```"]
    lines += ["", "## 引用检查", "```json",json.dumps(result["reference_summary"],ensure_ascii=False,indent=2),"```", "", "## 保护资源", "```json",json.dumps(result["protected"],ensure_ascii=False,indent=2),"```", "", "## 限制"]+ ["- "+x for x in result["limitations"]]
    (out/"audit.md").write_text("\n".join(lines)+"\n",encoding="utf-8")
    print(json.dumps({"report":str(out/"audit.json"),"elapsed":result["elapsed_seconds"],"prefabs":[{k:p[k] for k in ["path","head_documents","work_documents","renumbered_matched_objects","unchanged_objects"]}|{"added":len(p["added"]),"removed":len(p["removed"]),"changed":len(p["changed_objects"]),"missing_internal":len(p["work_missing_internal_refs"]),"unresolved_guids":len(p["work_unresolved_guids"]),"structure_errors":len(p["work_structure_errors"])} for p in result["prefabs"]],"profiles":len(result["profiles"]),"references":result["reference_summary"]},ensure_ascii=False),flush=True)

if __name__=="__main__":main()
