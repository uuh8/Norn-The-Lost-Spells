from pathlib import Path
import datetime,hashlib,json,re,shutil,subprocess,xml.etree.ElementTree as ET
from urllib.parse import unquote
r=Path(r'F:\Develop Project\RPG');u=r/'RPG';t=Path(__file__).parent
e=u/'docs/superpowers/evidence/multiplayer/n5/native-recovery-2026-10-31'
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def save(p,v):Path(p).write_text(json.dumps(v,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest().upper()
assert (read(e/'red-report.json')['Passed'],read(e/'red-report.json')['Failed'])==(0,2)
assert (read(e/'green-report.json')['Passed'],read(e/'green-report.json')['Failed'])==(2,0)
assert (read(e/'full-report.json')['Passed'],read(e/'full-report.json')['Failed'])==(423,0)
assert (read(e/'playmode-report.json')['Passed'],read(e/'playmode-report.json')['Failed'])==(2,0)
counts=ET.parse(e/'business-final.trx').find('.//{*}Counters');assert int(counts.attrib['passed'])==100 and int(counts.attrib['failed'])==0
initial=ET.parse(e/'business-initial.trx').find('.//{*}Counters');assert int(initial.attrib['passed'])==99 and int(initial.attrib['failed'])==1
foundation=read(e/'foundation-report.json');assert foundation['Status']=='passed' and foundation['ProtocolVersion']==4
native=read(e/'native-recovery-consistency.json');assert native['Passed'] and len(native['Cases'])==2
builds={k:read(e/(k+'-build.json')) for k in ('client','server')};assert all(v['Result']=='Succeeded' and v['Errors']==0 for v in builds.values())
assert builds['server']['DedicatedServer'] and not builds['client']['DedicatedServer']
assert read(e/'database-cleanup.json')['StoppedOnlyOwnedDatabase']
protected=[]
for item in read(e/'preservation-before.json'):
 p=Path(item['Path'])
 if p.name=='DynamicsManager.asset' and sha(p)!=item['Hash']:
  known=u/'docs/superpowers/evidence/multiplayer/n5/native-delta-2026-10-31/physics-reserialization-observed.asset'
  assert p.read_bytes()==known.read_bytes(),'Unknown protected Physics change; preserve and report'
  original=subprocess.check_output(['git','show','HEAD:RPG/ProjectSettings/DynamicsManager.asset'],cwd=r)
  if hashlib.sha256(original).hexdigest().upper()!=item['Hash']:original=original.replace(b'\r\n',b'\n').replace(b'\n',b'\r\n')
  assert hashlib.sha256(original).hexdigest().upper()==item['Hash']
  (e/'physics-reserialization-observed.asset').write_bytes(p.read_bytes());p.write_bytes(original)
  save(e/'physics-restoration.json',dict(OnlyKnownOwnSerializationSideEffectRestored=True,RestoredHash=sha(p)))
 protected.append(dict(Path=str(p),Hash=sha(p),Unchanged=sha(p)==item['Hash']))
save(e/'preservation-after.json',protected);assert len(protected)==12 and all(x['Unchanged'] for x in protected)
assets=[dict(Path=x['Path'],Hash=sha(r/x['Path']),Unchanged=sha(r/x['Path'])==x['Hash']) for x in read(e/'formal-assets-before.json')]
save(e/'formal-assets-after.json',assets);assert len(assets)==15 and all(x['Unchanged'] for x in assets)
editor_path=u/'Temp/ArchitectureCleanup/editor-state.json';editor=read(editor_path);editor={k:editor[k] for k in ('Playing','Compiling','Dirty','Scene')}
assert not any(editor[k] for k in ('Playing','Compiling','Dirty')) and editor['Scene']=='Assets/_Project/Scenes/P7_MainMenu.unity'
save(e/'editor-after.json',editor)
scripts=['Invoke-N5RecoveryNative.ps1','Run-N5RecoveryNative.ps1','Watch-N5RecoveryStore.ps1','check-native-recovery.py','Run-RecoveryGate.ps1','Run-RecoveryObservationGreen.ps1','Run-RecoveryRegressions.ps1','Build-N5RecoveryPair.ps1','Run-RecoveryFoundation.ps1','Inspect-RecoveryTestExpiry.ps1']
for name in scripts:shutil.copyfile(t/name,e/name)
secrets=[];private_records=[];log_findings=[]
for case in ('checksum','missing-base'):
 private=u/'.tools/networking-checks/n5-native-recovery-20261031'/case;session=read(private/'session.private.json');private_records.append((private,session))
 for room in session['Rooms'].values():
  for user in room['Users']:
   if user.get('Token'):secrets.append(user['Token'])
 for p in private.glob('*client*.private.json'):
  cfg=read(p)
  if cfg.get('Ticket',{}).get('Ticket'):secrets.append(cfg['Ticket']['Ticket'])
 logs=[('client0',private/'p7-client0.log'),('client1',private/'p7-client1.log')]+[('server',x) for x in private.glob('instances/*/server.log')]
 for label,p in logs:
  text=p.read_text(encoding='utf-8-sig',errors='replace')
  for secret in secrets:text=text.replace(secret,'[REDACTED_OWN_CREDENTIAL]')
  (e/case/(label+'-native.log')).write_text(text,encoding='utf-8')
  issues=[dict(Line=i+1,Text=line) for i,line in enumerate(text.splitlines()) if re.search(r'(?i)\b(?:error|exception|failed)\b',line)]
  builtin=[x for x in issues if 'Built-in Resource Error:' in x['Text']]
  log_findings.append(dict(Case=case,Process=label,MatchedLines=issues,KnownBuiltinResourceErrors=len(builtin)))
  if label=='server':assert len(builtin)==2,'Preserve and review changed server startup errors'
save(e/'native-log-findings.json',log_findings)
build_paths={'client':u/'Builds/OnlineRooms/Client/NornRoomClient_Data/Managed/Game.Multiplayer.dll','server':u/'Builds/OnlineRooms/Server/NornRoomServer_Data/Managed/Game.Multiplayer.dll'}
save(e/'native-build-assemblies.json',[dict(Kind=k,Path=str(p.relative_to(r)),Hash=sha(p),LastWriteUtc=datetime.datetime.fromtimestamp(p.stat().st_mtime,datetime.timezone.utc).isoformat()) for k,p in build_paths.items()])
deferred=[dict(Severity='P2',Issue='Explicit recovery probe requires Generation>=old and Revision>=old when identifying recovery ACK; higher Generation with reset Revision can be missed',Scope='Diagnostic only; current same-Generation cases pass; formal Coordinator/Assembler unaffected',Next='Existing generation-order contract audit plus independent behavioral RED/GREEN before rebuilt-chunk diagnostic use'),dict(Severity='P3',Issue='Existing explicit home/away diagnostic error is latched after invalid/no-auth request',Scope='No pose commands issued here; normal default gameplay unaffected')]
remaining=['Native history-ring overwrite (128 changed records, not simply 128 ticks)','Multi-page Native fault transfer; current Native target is one 43-word page, pure behavior test covers assembled multi-page corruption','Cross-Generation rebuilt-chunk diagnostic P2 and existing pose diagnostic P3','Sustained reactions/four players/stress/HOL/calibrated Native performance/GC','Full keyboard Hub/player path, same-process reconnect and consumed-resource preservation','P8 Native material recovery, N4/N6 and remote platform gates','Known two Built-in Resource startup errors per DS']
summary=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),Scope='N5 v4 same-Generation checksum and missing-base Native recovery',ProtocolVersion=4,SchemaChanged=False,NewSourceDirectories=0,
 Red=dict(Passed=0,Failed=2),Green=dict(Passed=2,Failed=0),Coordinator=dict(Passed=31,Failed=0),EditMode=dict(Fixtures=27,Passed=423,Failed=0),PlayMode=dict(Passed=2,Failed=0),
 Business=dict(InitialPassed=99,InitialFailed=1,FinalPassed=100,FinalFailed=0,OriginalAssertionsPreserved=True,InitialCause='Global ExpireDueAsync also expired prior owned Interest Native instance 94a4ccb6c04740569ebcf5c3447b44dd; read-only SQL confirmed two and then zero active; no manual database cleanup'),
 HttpFoundation=foundation,NativeBuilds=builds,Native=native,DeferredIssues=deferred,Remaining=remaining,
 Metadata=dict(OwnerKeyLimit=128,ConnectionLimit=4,AckObservationRows=4*128,EvictionOrUnlimitedExploration=False),
 ProtectedFilesUnchanged=12,OldFormalAssetsUnchanged=13,ExistingProfileAndMetaUnchanged=2,Editor=editor,N5Complete=False,NoCommitOrPush=True,PermissionOrDecisionBlocker=False,
 ResolvedExecutionIssues=['Auto-review rejected reverting incomplete Channel integration while repository writes were paused; explicit narrow authorization permitted one retry, restoring exact initial hash before GREEN; complete staged Channel then copied by main','Initial root option-file read-only SQL authentication failed; no query executed; successful read-only inspection used current Test credentials in an ephemeral option file, removed afterwards'])
save(e/'summary.json',summary)
rows='\n'.join(f"| {x['Mode']} | {x['RejectedBase']}→{x['RejectedRevision']} | {x['FirstFailure']} | 0→{x['RecoveredRevision']} | {x['ReplayFailure']} | {x['AcksBefore'][0]}/{x['AcksBefore'][1]}→{x['AcksAfter'][0]}/{x['AcksAfter'][1]} |" for x in native['Cases'])
notes=f'''## 2026-10-31：v4 Native故障恢复最小两场景

### 1. 本段结果与合同

依据N5计划N5-2～N5-6和2026-10-31用户接续授权：坏数据保留旧数量/旧Revision，完整校验并原子安装后才ACK；缺Base有界Resync，控制关联当前真实pending。沿用已批准v4/独立Interest，不增协议、玩法、资产、地图重建语义或跨模块架构。此次两个全新P7隔离房间均经真实账户/Ready/Ticket/KCP和正式材料路径，A单次注入、B正常。**仅同Generation、暂停检查点，不是N5完成。**

| 场景 | 故障Base→Revision | 首次拒绝 | 当前Baseline Base→Revision | 重放拒绝 | A/B累计ACK前→后 |
|---|---|---|---|---|---|
{rows}

每次初始材料Tick5、恢复后Tick10；A/B真实Store液体数量512→1024。故障候选Revision4，拒绝时仍保留已安装Revision3和真实旧格；关联Resync后取当前Baseline Revision9。恢复/旧页重放后全部当前Key的WorldEpoch/Generation/Revision/CRC/Words/Resident与DS一致，A/B实际Current格逐项相同。表中累计数仅辅助，**准确归因来自DS每连接每Key接受的pending Header**：含真实ConnectionId/Epoch、SubscriptionEpoch、Key、Generation、Snapshot/Base/Revision、Tick、Checksum/Words，与Client安装Header逐字段核对。

`preserved-fresh-*`由独立只读观察器保存，Client和实际Store采样时间均晚于关联Resync、且恢复Baseline计数尚未增加；目标真实格与注入前一致，不能用旧采样或Header假装保旧。恢复后单次旧故障页被StaleVersion拒绝，实际格与已安装版本不回灌；产生的旧Resync未增加DS接受次数或ACK，未重开恢复。

### 2. 实现、目录与生命周期

- `Multiplayer/World/WorldReplicationCoordinator.cs`只加真实事件观测：Resync产生/接受、Baseline/Delta安装、拒绝原因/候选/保留Header、每连接4×128固定ACK记录和只读当前订阅检查。原身份/pending/预算/版本裁决不改；新连接重置对应观测槽。128 Key仍硬上限，不引淘汰或无限日志。稀疏Describe才分配值副本。
- 同目录`WorldReplicationNetworkChannel.cs`复用正式TryPrepare→Codec→Mark→Mirror。仅Native Development DS且`NORN_N5_RECOVERY_PROBE`显式绝对路径时创建诊断；默认不轮询文件/分配诊断缓存。checksum只翻首页一个Word且保留原CRC；missing-base对选中快照所有实际发送页统一Base−1。借用message在finally立即恢复，Owner/Peer冻结数组不写，成功Send后才计发送。只对当前认证slot0；正式Resync被接受后有界暂停发页3秒，B/模拟继续；恢复ACK后显式单次replay还须当前同连接/世界/订阅。无自动循环注入或无限重试。
- `Multiplayer/Diagnostics/RoomAdmissionProbe.cs`及`Multiplayer/Runtime/RoomInstanceBootstrap.cs`只扩既有稀疏报告，不新增玩家入口。`ElementField/Diagnostics/OnlineMaterialReplicationProbe.cs`复用现有文件，Client仅`NORN_N5_REPLICA_PROBE`显式开启时250ms采样真实Store.Current格/总量；不把ElementField引入Multiplayer asmdef。JSON/采样分配不参加Zero-GC结论。原DS正式ElementWriteRequest两次512与各五真实Tick检查点复用。
- `Tests/Multiplayer/WorldReplicationChannelTests.cs`新增两个真实Owner/Codec/Assembler/Store闭环，原29项Coordinator案例和断言逐字保留。校验坏Delta保旧、多页完整CRC拒绝、wire合法缺Base、Owner继续提交后当前Baseline、精确Resync与逐peer ACK、过期页/控制不制造恢复债务。无新源目录/Shared Schema/meta或资源导出。

### 3. 实际验证与失败证据

新增2项先0/2 RED（缺BaselineInstalled观测属性），补最小实现后2/0 GREEN；Coordinator31/0，最终同范围27fixtures **423/0**，正式P7/P8 PlayMode **2/0**。不是整项目Run All，也不是Native完整玩家验收。

业务首次 **99/1**，`OldServerEpoch_CannotRenewOrAdmit_AndLeaseExpiryReleasesAssets`原断言Expected1/Actual2。只读Test Schema查到前段自有Interest实例`94a4ccb6c04740569ebcf5c3447b44dd`与新测试实例都被既有全局ExpireDueAsync处理为LeaseExpired；测试后活动实例0。未手动清库、未改断言/业务语义，按相同流程重跑 **100/0**，真实HTTP v4接受/v1～v3拒绝及超长拒绝，固定8个生成文件一致性通过。保存initial TRX/log、SQL结果和分析；初始root option-file认证失败没有执行SQL，随后使用已验证Test账号完成只读核查并删除本次临时凭据文件。

匹配Windows x64 Development Mono Client构建 `{builds['client']['RecordedAtUtc']}`、真正Dedicated Server构建 `{builds['server']['RecordedAtUtc']}`，各0 error；不是业务Release冒充Unity Native。Hash刷新曾等待Shader编译，读取窗口/IO确认有效后继续，未重启Editor。两Native场景目标都是43 words的**单页**Delta，多页Native故障仍未测；当前多页拒绝只来自行为测试。两个DS各有既有两条Built-in Resource Error，完整日志与行号保留；本轮未阻断认证/恢复，但未修复，不作为图形正确性背书。

### 4. 审查、保护与接续

独立sol/xhigh只读审查未发现阻塞本轮同Generation场景的问题。新增诊断P2：recovery ACK识别用Generation>=旧值且Revision>=旧值，Chunk合法重建为更高Generation、Revision重置1时可能漏认；**仅探针，正式Coordinator/Assembler未受影响**。本段不扩跨Generation场景，后续依已有字典序版本合同做独立行为RED/GREEN最小修复。既有home/away错误锁存P3保留，本轮无位移请求所以不触发。相关审查见native-recovery-review.md。

并行编写时Channel不完整hook曾先落仓；自动审批因主控已暂停仓库写入拒绝撤回。主控新增“仅撤回自有不完整patch”明确授权后唯一重试成功，Hash精确回到本阶段起始值，再跑观测GREEN；完整暂存由主控统一落仓后才编译/回归/构建。没有绕过拒绝或在不确定源码上声明通过；现无权限/待决策阻塞。

12保护文件、13旧正式资源及已有网络Profile/Meta共15资产Hash保持；仅在当前Physics字节精确等于已证自动序列化副作用且起始Hash匹配HEAD时恢复本任务格式变化。用户Editor保留并回P7_MainMenu、非Play/Compiling/Dirty。仅按PID/启动时间/路径结束本任务两组Native/业务，认证关闭本任务13384 MySQL；保留无关Recovery及工作区，未提交推送。

后续先处理诊断P2并核查128条变更记录覆盖后的实际Peer Baseline回退；Native日志ring覆盖、四人/持续反应/HOL/有效Profiler与GC、完整键盘路径、同进程自动重连/消耗不补满、P8 Native恢复和N4/N6/远端Gate继续未完。Ring按变更记录数覆盖，不能把128 Tick或128 Key等同无限探索。炼金仅已确认计划记录，未实施。

Ruling：先交付同Generation单次故障闭环 — 依赖已有v4恢复合同 — 成本是跨代重建/多页Native/日志覆盖仍需后段。Ruling：默认关闭诊断只改编码借用输入并finally恢复 — 不更改权威材料与正常路径 — 成本是诊断采样/JSON不用于性能。Ruling：固定3秒单连接暂停 — 保存拒绝后新鲜实际Store — 成本是此段为稳定检查点，不能称持续压力。Ruling：测试全局过期多计历史自有实例 — 保留99/1并只读归因后原断言重跑 — 不清空/手改数据。
'''
(e/'README.md').write_text('# N5 v4 Native故障恢复证据\n\n'+notes,encoding='utf-8')
(e/'progress.md').write_text('# 阶段账本 — plan: RPG/docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md\n\n'+notes,encoding='utf-8')
living=u/'Assets/_Project/Docs/2026-10-29 共享材料世界N5开发解析（更新中）.md';s=living.read_text(encoding='utf-8-sig');assert notes.splitlines()[0] not in s
s=re.sub(r'\*\*最新接续（2026-10-31）：\*\*[^\n]*','**最新接续（2026-10-31）：** v4 Native同Generation坏Delta/缺Base两场景已闭环，真实Store保旧、精确Resync/逐连接每Key ACK、当前Baseline和单次旧页拒绝均有证据。27fixtures423/0、PlayMode2/0、业务最终100/0；保留初始99/1归因、诊断P2/P3和DS两条资源错误。日志覆盖/多页Native/跨代诊断及持续四人/完整路径/性能未完，N5未完成。',s,count=1)
living.write_text(s.rstrip()+'\n\n'+notes,encoding='utf-8')
total=u/'docs/superpowers/plans/2026-10-31 Norn项目总计划（更新中）.md';s=total.read_text(encoding='utf-8-sig');assert '## 16. Interest两项决策确认' in s and '### 7.1 炼金已确认玩法' in s and '并集仅是建议' in s and '## 19. Native故障恢复' not in s
s=re.sub(r'\*\*2026-10-31 最新执行：\*\*[^\n]*','**2026-10-31 最新执行：** v4 Native坏Delta/缺Base最小两场景已验证，真实Store保旧/当前Baseline/逐连接每Key ACK和单次旧页拒绝有证据；27fixtures423/0、PlayMode2/0、业务最终100/0，初始99/1保留归因。诊断P2/P3及资源错误、日志覆盖/持续四人/完整路径/性能等Gate仍未完，N5未完成，详见第19节。',s,count=1)
s+='\n\n## 19. Native故障恢复最小两场景（2026-10-31）\n\n沿用已批准v4/Interest，不改Schema或玩法。同Generation真实P7两房间分别对A注入Checksum坏Delta、wire合法Base缺口，B正常；均保留已安装Revision3和新鲜采样的实际旧格，经精确关联Resync恢复Baseline Revision9，目标每连接每Key ACK可逐字段核对，A/B数量512→1024。恢复后单次旧故障页均StaleVersion拒绝，未回灌/重开恢复。Native目标43 words单页，五Tick到十Tick暂停比较，不是持续或完整玩家路径。\n\n2项0/2 RED→2/0 GREEN，Coordinator31/0、27fixtures423/0、正式P7/P8 PlayMode2/0、业务最终100/0及HTTP/8文件生成一致性通过。业务初始99/1由全局到期测试同时处理历史自有Interest实例与新实例造成，只读归因后原断言重跑，未手动清库。匹配Windows Development Mono Client/真正DS各0 error。\n\n留存诊断P2（跨Generation且Revision重置时漏认恢复ACK，正式复制不受影响）、既有home/away错误锁存P3、每DS两条Built-in Resource错误。Native历史ring覆盖/多页/跨代、四人持续/性能/完整键盘路径/同进程重连/消耗不补满/P8/N4/N6/远端Gate继续未完；128 Key仍硬上限，N5Complete=false。12保护文件和13旧资源＋已有Profile/Meta Hash保持；本任务Native/业务/MySQL清理完成、Editor保留，无提交推送。下一小段可先按已有版本合同修诊断P2并验证ring覆盖后Peer退Baseline，不需发明新协议；若出现未确认策略再提出。\n\n详见 [本轮汇总](../evidence/multiplayer/n5/native-recovery-2026-10-31/summary.json)、[证据与Debug](../evidence/multiplayer/n5/native-recovery-2026-10-31/README.md)。炼金保持计划记录授权，未开发。\n'
total.write_text(s,encoding='utf-8')
plan=u/'docs/superpowers/plans/2026-10-29 N5共享材料世界与分块恢复实施计划.md';s=plan.read_text(encoding='utf-8-sig')
plan.write_text(s.rstrip()+'\n\n**Native故障恢复最小段结果（2026-10-31）：** 同Generation v4 Checksum坏Delta/合法Base缺口两真实P7房间均保旧Store、精确关联Resync、当前Baseline与逐连接每Key安装ACK，单次旧页StaleVersion拒绝；Native为单页43 words，Tick5→10暂停检查点。2项RED0/2→GREEN2/0；27fixtures423/0、PlayMode2/0、业务初始99/1归因后原断言100/0、HTTP/生成门禁与匹配Native0 error。保留诊断P2/P3、DS两条资源错误、128Key硬界限；Native日志覆盖/多页/跨代及持续四人/完整路径/性能等Gate仍未完。证据 `../evidence/multiplayer/n5/native-recovery-2026-10-31/summary.json`；N5Complete=false。\n',encoding='utf-8')
changes=[]
for item in read(e/'source-before.json'):
 p=r/item['Path']
 if p.exists() and sha(p)!=item['Hash']:changes.append(dict(Path=item['Path'],Hash=sha(p)))
save(e/'source-changes-final.json',changes);assert len(changes)==6,changes
check=subprocess.run(['git','-c','core.quotepath=false','diff','--check','--','*.cs','*.proto','*.ps1','*.md','*.json','*.asmdef'],cwd=r,capture_output=True)
(e/'text-diff-check.log').write_bytes(check.stdout+check.stderr);assert check.returncode==0
(e/'git-status-after.txt').write_bytes(subprocess.check_output(['git','-c','core.quotepath=false','status','--short'],cwd=r))
missing=[];links=0
for doc in (living,total,plan):
 for match in re.finditer(r'\[[^\]]*\]\((<[^>]+>|[^)]+)\)',doc.read_text(encoding='utf-8-sig')):
  link=match.group(1).strip().strip('<>')
  if re.match(r'^(?:[a-zA-Z]+:|#)',link):continue
  link=unquote(link.split('#',1)[0])
  if link:
   links+=1
   if not (doc.parent/link).exists():missing.append(dict(Document=str(doc.relative_to(r)),Target=link))
assert not missing,missing;save(e/'document-links-check.json',dict(Checked=links,Missing=missing))
exposed=[str(p.relative_to(e)) for p in e.rglob('*') if p.is_file() and any(secret.encode('utf-8') in p.read_bytes() for secret in secrets)]
assert not exposed,exposed
for private,session in private_records:
 for room in session['Rooms'].values():
  for user in room['Users']:user['Token']=''
 save(private/'session.private.json',session)
 for p in private.glob('*client*.private.json'):
  cfg=read(p)
  if 'Ticket' in cfg:cfg['Ticket']['Ticket']=''
  save(p,cfg)
save(e/'credential-cleanup.json',dict(OwnTokensAndTicketsCleared=True,ExposedFiles=0,SharedDatabaseCredentialsUntouched=True))
head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=r,text=True).strip();assert head=='0d87d0da931ec2b39f7cd569661e750bafa0df84'
summary['FinalChecks']=dict(Links=links,MissingLinks=0,TextDiffCheckExitCode=0,OwnCredentialExposure=0,CommitHead=head,ModifiedSourceFiles=6)
save(e/'summary.json',summary)
print(json.dumps(dict(EditMode=423,PlayMode=2,Business=100,NativeCases=2,ProtectedFiles=12,PreservedAssets=15,SourceFiles=6,Links=links,MissingLinks=0,TextDiffCheck=0,ExposedCredentials=0),ensure_ascii=False))
