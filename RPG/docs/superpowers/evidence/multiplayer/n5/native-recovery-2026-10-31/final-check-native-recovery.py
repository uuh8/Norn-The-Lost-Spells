from pathlib import Path
import json,datetime,hashlib,subprocess,re
from urllib.parse import unquote
r=Path(r'F:\Develop Project\RPG');u=r/'RPG';e=u/'docs/superpowers/evidence/multiplayer/n5/native-recovery-2026-10-31';t=Path(__file__).parent
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def save(p,v):Path(p).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
extra='另外保护检查发现ProjectSettings.asset仅runInBackground由0变1，写入时间与本任务DS构建完成相差不足2秒；只还原该字段后字节精确匹配阶段起始Hash与HEAD，保存projectsettings-background-observed.diff和projectsettings-background-restoration.json。未回滚其他设置。'
docs=[e/'README.md',e/'progress.md',u/'Assets/_Project/Docs/2026-10-29 共享材料世界N5开发解析（更新中）.md']
for p in docs:
 s=p.read_text(encoding='utf-8-sig');marker='12保护文件、13旧正式资源及已有网络Profile/Meta共15资产Hash保持；'
 assert s.count(marker)==1 and extra not in s
 s=s.replace(marker,extra+'\n\n'+marker)
 p.write_text(s,encoding='utf-8')
total=u/'docs/superpowers/plans/2026-10-31 Norn项目总计划（更新中）.md'
s=total.read_text(encoding='utf-8-sig');marker='12保护文件和13旧资源＋已有Profile/Meta Hash保持；本任务Native/业务/MySQL清理完成'
assert marker in s
s=s.replace(marker,'保护检查仅还原与本任务DS构建同刻落盘的runInBackground单字段变化，恢复后精确匹配起始Hash/HEAD；12保护文件和13旧资源＋已有Profile/Meta Hash保持；本任务Native/业务/MySQL清理完成')
total.write_text(s,encoding='utf-8')
summary=read(e/'summary.json');summary['ProtectedSettingsRestoration']=read(e/'projectsettings-background-restoration.json')
if (e/'physics-restoration.json').exists():summary['PhysicsRestoration']=read(e/'physics-restoration.json')
for name in ('finalize-native-recovery.py','restore-recovery-background-setting.py','final-check-native-recovery.py'):
 (e/name).write_bytes((t/name).read_bytes())
for item in read(e/'preservation-after.json'):assert sha(item['Path'])==item['Hash'],item['Path']
for item in read(e/'formal-assets-after.json'):assert sha(r/item['Path'])==item['Hash'],item['Path']
missing=[];links=0
plan=u/'docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md'
for doc in (docs[2],total,plan):
 for m in re.finditer(r'\[[^\]]*\]\((<[^>]+>|[^)]+)\)',doc.read_text(encoding='utf-8-sig')):
  link=m.group(1).strip().strip('<>')
  if re.match(r'^(?:[a-zA-Z]+:|#)',link):continue
  link=unquote(link.split('#',1)[0])
  if link:
   links+=1
   if not (doc.parent/link).exists():missing.append(dict(Document=str(doc),Target=link))
assert not missing;save(e/'document-links-check.json',dict(Checked=links,Missing=missing))
check=subprocess.run(['git','-c','core.quotepath=false','diff','--check','--','*.cs','*.proto','*.ps1','*.md','*.json','*.asmdef'],cwd=r,capture_output=True)
(e/'text-diff-check.log').write_bytes(check.stdout+check.stderr);assert check.returncode==0
(e/'git-status-after.txt').write_bytes(subprocess.check_output(['git','-c','core.quotepath=false','status','--short'],cwd=r))
summary['FinalChecks']['Links']=links;summary['RecordedUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat();save(e/'summary.json',summary)
print(json.dumps(dict(Protected=12,Assets=15,Links=links,TextDiffCheck=0,ProjectSettingsExactlyRestored=True,N5Complete=False)))
