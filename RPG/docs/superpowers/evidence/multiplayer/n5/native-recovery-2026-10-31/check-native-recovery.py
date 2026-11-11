from pathlib import Path
import json,datetime
u=Path(r'F:\Develop Project\RPG\RPG');e=u/'docs/superpowers/evidence/multiplayer/n5/native-recovery-2026-10-31'
def read(p):return json.loads(Path(p).read_text(encoding='utf-8-sig'))
def key(x):return tuple(x[k] for k in ('Domain','X','Y','Z'))
def route(x):return tuple(x[k] for k in ('WorldEpoch','Generation','Revision','Checksum','Words','Resident'))
def stamp(x):return tuple(x[k] for k in ('WorldEpoch','Generation','BaseRevision','Revision','SnapshotId','ServerTick','SubscriptionEpoch','Checksum','Words','Resident'))
def chunks(v):return {key(x):x for x in v['MaterialChunks']}
def cells(v,target=None):
 return sorted(tuple(c[k] for k in ('Domain','Storage','X','Y','Z','Material','Quantity')) for c in v['Cells'] if target is None or (c['Domain'],c['ChunkX'],c['ChunkY'],c['ChunkZ'])==target)
def exact_peers(ds,client,slot):
 headers={key(x):x for x in ds['MaterialPeerAck'+str(slot)]}
 for k,x in chunks(client).items():
  assert k in headers and stamp(headers[k])==stamp(x),(slot,k,'exact pending ACK mismatch')
  assert headers[k]['ConnectionEpoch']==client['ConnectionEpoch']
def wire_header_matches(st,wire,base=None):
 mapping={'WorldEpoch':'worldEpoch','Generation':'generation','BaseRevision':'baseRevision','Revision':'revision','SnapshotId':'snapshotId','ServerTick':'serverTick','SubscriptionEpoch':'subscriptionEpoch','Checksum':'checksum','Words':'totalWords','Domain':'domain','X':'chunkX','Y':'chunkY','Z':'chunkZ'}
 for a,b in mapping.items():assert st[a]==(base if a=='BaseRevision' and base is not None else wire[b]),(a,st[a],wire[b])
assert read(e/'native-progress.json')['State']=='Passed'
results=[]
for case in ('checksum','missing-base'):
 ce=e/case;result=read(ce/'result.json');assert result['Passed']
 points={name:{x:read(ce/f'{name}-{x}.json') for x in ('ds','a','b','sa','sb','seed','fault')} for name in ('before','rejected','recovered','replayed')}
 before,denied,recovered,replayed=(points[x] for x in ('before','rejected','recovered','replayed'))
 fresh=read(ce/'preserved-fresh-client.json');store=read(ce/'preserved-fresh-store.json');observation=read(ce/'preserved-fresh-observation.json')
 fault=denied['fault'];original=fault['originalHeader'];target=key(fresh['MaterialRejectedCandidate'])
 assert fault['completionReason']=='AssociatedResyncAccepted' and fault['holdUntil']>fault['firstSendMonotonic']
 assert fault['targetSlot']==0 and fault['connectionEpoch']==before['a']['ConnectionEpoch']
 assert fault['sourcePageCount']==original['pageCount'] and 0<fault['uniqueSentPages']<=fault['sourcePageCount']
 wire_header_matches(fresh['MaterialRejectedCandidate'],original,base=original['baseRevision']-(case=='missing-base'))
 assert original['baseRevision']>0 and original['revision']>original['baseRevision']
 count=fault['firstPageWords'];changed=[i for i in range(count) if fault['originalFirstPageWords'][i]!=fault['injectedFirstPageWords'][i]]
 if case=='checksum':
  assert len(changed)==1 and (fault['originalFirstPageWords'][changed[0]]^fault['injectedFirstPageWords'][changed[0]])==1
  assert fault['mutatedPages']==1 and fault['uniqueSentPages']==fault['sourcePageCount']
 else:
  assert not changed and original['baseRevision']>1
  assert fault['injectedHeader']['baseRevision']==original['baseRevision']-1 and fault['mutatedPages']==fault['sentPages']
 expected='Checksum' if case=='checksum' else 'MissingRevision'
 assert fresh['MaterialLastFailure']==expected and fresh['MaterialRejectedPayloadAvailable']
 assert fresh['MaterialBaselines']==before['a']['MaterialBaselines']
 assert stamp(fresh['MaterialRejectedInstalled'])==stamp(chunks(before['a'])[target])
 assert cells(store,target)==cells(before['sa'],target),'actual installed cells changed on rejection'
 time=lambda s:datetime.datetime.fromisoformat(s.replace('Z','+00:00'))
 assert observation['Observed'] and time(store['RecordedUtc'])>=time(observation['AfterAssociatedResyncUtc'])
 assert time(fresh['RecordedAtUtc'])>=time(observation['AfterAssociatedResyncUtc'])
 assert denied['ds']['MaterialResyncActorSlot']==0
 accepted=denied['ds']['MaterialLastAcceptedResync'];wire_header_matches(accepted,original)
 assert accepted['ConnectionId']==fault['targetConnection'] and accepted['ConnectionEpoch']==fault['connectionEpoch']
 denied_ack={key(x):x for x in denied['ds']['MaterialPeerAck0']}[target]
 assert stamp(denied_ack)==stamp(chunks(before['a'])[target]),'bad candidate was ACKed'
 for name in ('before','recovered','replayed'):
  p=points[name];source=chunks(p['ds'])
  assert set(source)==set(chunks(p['a']))==set(chunks(p['b']))
  for x in ('a','b'):
   client=chunks(p[x]);assert all(route(v)==route(client[k]) for k,v in source.items())
   assert p[x]['Authenticated'] and p[x]['WorldReady'] and p[x]['WorldEpoch']==p['ds']['WorldEpoch']==p[x]['AuthenticatedWorldEpoch']
  exact_peers(p['ds'],p['a'],0);exact_peers(p['ds'],p['b'],1)
  assert cells(p['sa'])==cells(p['sb'])
 assert before['seed']['Phase']==1 and recovered['seed']['Phase']==2 and recovered['seed']['WorldTick']>=before['seed']['WorldTick']+5
 assert recovered['seed']['PausedForComparison'] and recovered['fault']['recoveryAcknowledged']
 current=chunks(recovered['a'])[target]
 assert current['BaseRevision']==0 and current['Revision']>=original['revision']
 for x in ('a','b'):
  assert recovered[x]['ConnectionEpoch']==before[x]['ConnectionEpoch']
  assert recovered[x]['WorldEpoch']==before[x]['WorldEpoch']
  for k,v in chunks(before[x]).items():assert chunks(recovered[x])[k]['SubscriptionEpoch']==v['SubscriptionEpoch']
 assert before['b']['MaterialResyncRequested']==replayed['b']['MaterialResyncRequested']==0
 assert replayed['b']['MaterialLastFailure']=='None'
 assert recovered['a']['MaterialBaselines']>before['a']['MaterialBaselines']
 replay_failure='StaleVersion' if current['Revision']>original['revision'] else 'MissingRevision'
 assert replayed['fault']['state']=='Replayed' and replayed['fault']['replayCount']==1
 assert replayed['a']['MaterialLastFailure']==replay_failure
 assert replayed['a']['MaterialResyncRequested']==recovered['a']['MaterialResyncRequested']+1
 assert replayed['ds']['MaterialResyncAccepted']==recovered['ds']['MaterialResyncAccepted']
 assert replayed['ds']['MaterialPeerAcks']==recovered['ds']['MaterialPeerAcks']
 assert chunks(replayed['a'])==chunks(recovered['a']) and cells(replayed['sa'])==cells(recovered['sa'])
 assert read(ce/'native-cleanup.json')['StoppedOnlyOwnedProcesses']
 results.append(dict(Mode=case,Target=list(target),WorldEpoch=original['worldEpoch'],ConnectionId=fault['targetConnection'],ConnectionEpoch=fault['connectionEpoch'],SubscriptionEpoch=original['subscriptionEpoch'],Generation=original['generation'],RejectedBase=original['baseRevision']-(case=='missing-base'),RejectedRevision=original['revision'],RecoveredRevision=current['Revision'],FirstFailure=expected,ReplayFailure=replay_failure,SentPages=fault['sentPages'],MutatedPages=fault['mutatedPages'],SourcePages=fault['sourcePageCount'],AcksBefore=before['ds']['MaterialPeerAcks'],AcksAfter=recovered['ds']['MaterialPeerAcks'],LiquidQuantityBefore=before['sa']['Liquid'],LiquidQuantityAfter=recovered['sa']['Liquid'],FreshActualStorePreservation=True,ExactPerConnectionPerKeyAcks=True))
out=dict(RecordedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),ProtocolVersion=4,Cases=results,Passed=True,SameGenerationOnly=True,PausedCheckpoints=True,KeyboardPlayerPath=False)
(e/'native-recovery-consistency.json').write_text(json.dumps(out,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
print(json.dumps(out,ensure_ascii=False))
