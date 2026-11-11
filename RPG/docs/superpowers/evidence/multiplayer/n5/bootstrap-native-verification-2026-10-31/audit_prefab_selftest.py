"""只验证独立审计器的归一化规则，不运行项目或 Unity 测试。"""
import copy, json
from audit_prefab_semantics import canonical, diff

def doc(kind, data): return {"kind":kind,"class_id":1 if kind=="GameObject" else 4,"data":data}
base={
    1:doc("GameObject",{"m_Name":"World","m_Component":[{"component":{"fileID":2}}]}),
    2:doc("Transform",{"m_GameObject":{"fileID":1},"m_Father":{"fileID":0},"m_Children":[{"fileID":4},{"fileID":6}],"m_LocalPosition":{"x":0,"y":0,"z":0}}),
    3:doc("GameObject",{"m_Name":"A","m_Component":[{"component":{"fileID":4}}]}),
    4:doc("Transform",{"m_GameObject":{"fileID":3},"m_Father":{"fileID":2},"m_Children":[]}),
    5:doc("GameObject",{"m_Name":"B","m_Component":[{"component":{"fileID":6}}]}),
    6:doc("Transform",{"m_GameObject":{"fileID":5},"m_Father":{"fileID":2},"m_Children":[]}),
}
def remap(v):
    if isinstance(v,dict):return {k:(x+100 if k=="fileID" and x else remap(x)) for k,x in v.items()}
    if isinstance(v,list):return [remap(x) for x in v]
    return v
permuted={i+100:remap(copy.deepcopy(base[i])) for i in reversed(base)}
a=canonical(base,{})[0];b=canonical(permuted,{})[0]
assert a==b,"Document reorder and fileID renumbering must be semantic noise"
changed=copy.deepcopy(base);changed[2]["data"]["m_LocalPosition"]["x"]=1
assert list(diff(a,canonical(changed,{})[0])),"Real Transform configuration must remain visible"
reordered=copy.deepcopy(base);reordered[2]["data"]["m_Children"].reverse()
assert list(diff(a,canonical(reordered,{})[0])),"True hierarchy child order must remain visible"
dangling=copy.deepcopy(base);dangling[2]["data"]["_world"]={"fileID":999}
assert canonical(dangling,{})[2][0]["fileID"]==999,"Dangling local fileID must be rejected"
print(json.dumps({"normalization_self_checks_passed":4,"cases":["document order and ID renumbering ignored","transform value change detected","child order change detected","dangling local reference detected"]}))
