using System;
using Game.Core;

namespace Game.Multiplayer
{
    public sealed partial class WorldReplicationCoordinator
    {
        /// <summary>撤销尚未进入可靠发送入口的消息借用，不推进页游标/Announcement，也不取消冻结payload。</summary>
        public void CancelPreparedPage(int slot)
        {
            if(_preparedSlot!=slot)return;
            _preparedSlot=_preparedIndex=-1;_preparedKind=0;_preparedEpoch=0;
        }

        /// <summary>预算已经预留而Transport发送抛错时锁存故障，禁止下次Capture按“Leave已发送”退休旧地址。</summary>
        public void ReportTransportFailure(Exception error)
        {
            if(error==null)throw new ArgumentNullException(nameof(error));
            if(Faulted)return;
            LifecycleException=error;CancelPreparedPage(_preparedSlot);
            Fail("Reliable material send failed:"+error.GetType().Name);
        }
        private bool TombstoneAcknowledged(int slot,int index,WorldChunkPayloadHeader header)
        {
            var ack=_peerAckHeaders[slot,index];
            return !header.IsResident && ack.Generation==header.Generation && ack.Revision==header.Revision;
        }

        /// <summary>
        /// NetworkChannel在本轮真实connection.Send之后调用，而不是在Mark预算预留时调用。
        /// 这不声称Client已安装：旧Leave和后续新Enter靠同一可靠通道排序，全连接水位阻止旧控制复活。
        /// Capture成功后也维护未暴露给Peer的空地址；任何真实容量/版本失败仍保持FailClosed。
        /// </summary>
        public bool CompleteSendRound()
            => !IsAuthority || _stopped || Faulted ? false : RetireReleasedAuthorityChunks();

        private bool RetireReleasedAuthorityChunks()
        {
            if(!IsAuthority || _stopped || Faulted)return false;
            // 借出的消息尚未完成预算/发送时，不搬任何共享索引。
            if(_preparedSlot>=0 || _owner.InactiveChunkCount==0)return true;
            for(int index=_owner.ChunkCount-1;index>=0;index--)
            {
                var header=_owner.GetChunkHeader(index);if(header.IsResident)continue;
                bool released=true;
                for(int peer=0;peer<4;peer++)
                {
                    if(!_active[peer])continue;
                    var sub=_subscriptions[peer,index];var state=_peers[peer];
                    if(sub.Desired || sub.Announced || (sub.Known && !sub.AnnouncementKnown)
                        || state.HasPendingFor(header.Key))
                    {released=false;break;}
                }
                if(!released)continue;
                if(!_owner.TryRetireInactiveChunk(index,out int movedFrom))return Fail("Authority metadata retirement rejected");
                // 检查点只为已确认/正在投递Head中的resident地址保留真实删除版本，
                // 不根据旧Head猜一个Revision，也不让所有历史空地址再次永久占槽。
                if(!RecordCheckpointRetirement(header))return Fail("Checkpoint retirement witness rejected");
                RetiredKeys++;
                int last=movedFrom>=0?movedFrom:index;
                for(int peer=0;peer<4;peer++)
                {
                    // 上面已逐Peer预检无匹配冻结页；此处只回收有界已释放发送状态。
                    if(!_peers[peer].TryForgetReleasedKey(header.Key))return Fail("Peer metadata retirement rejected");
                    if(movedFrom>=0)
                    {
                        _subscriptions[peer,index]=_subscriptions[peer,movedFrom];
                        _peerAckHeaders[peer,index]=_peerAckHeaders[peer,movedFrom];
                        _liquidMotionRetainUntil[peer,index]=_liquidMotionRetainUntil[peer,movedFrom];
                    }
                    _subscriptions[peer,last]=default;_peerAckHeaders[peer,last]=default;
                    _liquidMotionRetainUntil[peer,last]=0;
                    if(movedFrom>=0 && _cursor[peer]==movedFrom)_cursor[peer]=index;
                    if(_cursor[peer]>=_owner.ChunkCount)_cursor[peer]=0;
                }
            }
            return true;
        }

        private bool RetireClientSlot(int slot)
        {
            try{return RetireClientSlotCore(slot);}
            catch(Exception error)
            {
                LifecycleException=error;
                // 外部Installer扩展可在Evict或Retire中抛错；保持首个异常，
                // 无论分层清理进行到哪一步，都不能继续接受新页或用重连绕过现场。
                return Fail("Replica metadata retirement threw:"+error.GetType().Name);
            }
        }

        private bool RetireClientSlotCore(int slot)
        {
            var assembler=_assemblers[slot];var key=assembler.Key;
            if(!assembler.TryReleaseInterest())return Fail("Replica payload release rejected");
            // 首页尚未安装的Assembler可能从未在Store创建过Key，不能把未知Key当可退休数据。
            if(assembler.Installed)
            {
                if(!_interestSink.TryEvictInterest(key))return Fail("Replica interest eviction rejected");
                if(!_retirementSink.TryRetireInterest(key))return Fail("Replica store metadata retirement rejected");
            }
            if(!assembler.TryRetireReleasedKey())return Fail("Replica assembler metadata retirement rejected");
            int last=--_slots;
            _clientIndices.Remove(key);
            RetiredKeys++;
            if(slot!=last)
            {
                _assemblers[slot]=_assemblers[last];_assemblers[last]=assembler;
                _clientIndices[_assemblers[slot].Key]=slot;
                _incoming[slot]=_incoming[last];_clientSubscriptions[slot]=_clientSubscriptions[last];
                _incomingEpoch[slot]=_incomingEpoch[last];_clientActive[slot]=_clientActive[last];
                _needsBaseline[slot]=_needsBaseline[last];
            }
            _incoming[last]=default;_clientSubscriptions[last]=_incomingEpoch[last]=0;
            _clientActive[last]=false;_needsBaseline[last]=true;
            return true;
        }
    }
}
