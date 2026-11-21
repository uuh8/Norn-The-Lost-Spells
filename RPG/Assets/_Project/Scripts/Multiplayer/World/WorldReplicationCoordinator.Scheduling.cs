using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class WorldReplicationCoordinator
    {
        /// <summary>
        /// 固定容量的槽集合。Fenwick tree 记录成员数，按秩定位下一个置位槽，不用遍历未订阅/已 ACK 的槽。
        /// 控制按原目录升序发送；数量用同一顺序从原游标轮转。成员变化原位更新，不在 Tick 中分配树节点。
        /// </summary>
        private sealed class SlotSet
        {
            private readonly int[] _tree;
            private readonly bool[] _values;
            private readonly int _top;
            internal int Count { get; private set; }
            internal SlotSet(int size)
            {
                _tree=new int[size+1];_values=new bool[size];
                int step=1;while(step<=size/2)step*=2;_top=step;
            }
            internal void Set(int slot,bool value)
            {
                if(_values[slot]==value)return;
                _values[slot]=value;int delta=value?1:-1;Count+=delta;
                for(int i=slot+1;i<_tree.Length;i+=i&-i)_tree[i]+=delta;
            }
            internal void Clear()
            {
                if(Count==0)return;
                Array.Clear(_tree,0,_tree.Length);Array.Clear(_values,0,_values.Length);Count=0;
            }
            internal void Retire(int slot,int last)
            {
                bool moved=slot!=last && _values[last];
                Set(last,false);Set(slot,moved);
            }
            internal int AtRank(int rank)
            {
                if((uint)rank>=(uint)Count)return -1;
                int index=0;
                for(int step=_top;step>0;step>>=1)
                {
                    int next=index+step;
                    if(next<_tree.Length && _tree[next]<=rank){index=next;rank-=_tree[next];}
                }
                return index;
            }
            internal int AtOrAfter(int start)
            {
                if(Count==0 || start>=_values.Length)return -1;
                int before=0;
                for(int i=start;i>0;i-=i&-i)before+=_tree[i];
                return AtRank(before);
            }
        }

        private sealed class PeerWork
        {
            internal readonly SlotSet Watched,Control,Quantity;
            // 两个稀疏候选表只含身边尚未首次安装的块；近处优先与全窗轮询交替，远处和已有图的更新也有份额。
            internal readonly SlotSet NearEnter,NearFirst;
            internal bool DataTurn,NearControlTurn=true,NearDataTurn=true;
            internal int ControlCursor;
            internal PeerWork(int chunks)
            {
                Watched=new SlotSet(chunks);Control=new SlotSet(chunks);Quantity=new SlotSet(chunks);
                NearEnter=new SlotSet(chunks);NearFirst=new SlotSet(chunks);
            }
            internal void Clear()
            {
                Watched.Clear();Control.Clear();Quantity.Clear();NearEnter.Clear();NearFirst.Clear();
                DataTurn=false;NearControlTurn=NearDataTurn=true;ControlCursor=0;
            }
            internal void Retire(int slot,int last)
            {
                Watched.Retire(slot,last);Control.Retire(slot,last);Quantity.Retire(slot,last);
                NearEnter.Retire(slot,last);NearFirst.Retire(slot,last);
                if(ControlCursor==last)ControlCursor=slot;else if(ControlCursor>last)ControlCursor=0;
            }

            internal int NextControl(bool progressive,out bool nearby)
            {
                nearby=progressive && NearControlTurn && NearEnter.Count>0;
                if(nearby)return NearEnter.AtRank(0);
                if(!progressive)return Control.AtRank(0);
                int index=Control.AtOrAfter(ControlCursor);return index>=0?index:Control.AtRank(0);
            }
        }

        private readonly PeerWork[] _work;
        private readonly SlotSet _tombstones;
        private readonly WorldReplicationInterestIndex _nearby;
        private readonly List<WorldChunkKey> _nearbyKeys;
        /// <summary>累计实际进行 AABB 或锚点资格判断的块数，不含网络页、值变更数或墙钟耗时。</summary>
        public long InterestChunkChecks {get;private set;}
        /// <summary>发送准备中实际检查的控制/数量候选，不含已 ACK 或无订阅的空闲槽。</summary>
        public long SendCandidateChecks {get;private set;}
        public long RetirementChunkChecks {get;private set;}

        private void IndexCommittedChanges()
        {
            for(int i=0;i<_owner.LastChangeCount;i++)
            {
                var change=_owner.GetLastChange(i);
                if(change.Kind==WorldChunkChangeKind.Create)_nearby.Add(change.Key);
                if(change.Kind!=WorldChunkChangeKind.Update)_tombstones.Set(OwnerIndex(change.Key),change.Kind==WorldChunkChangeKind.Remove);
            }
        }

        private bool RefreshSubscriptions(int slot,bool hasCommit=false,bool unchangedGeometry=false)
        {
            var peer=_peers[slot];
            if(!_interest.TryGetInterest(peer.ConnectionId,peer.ConnectionEpoch,out var bounds))return true;
            if(!_interestCompared[slot] || !_comparedBounds[slot].Equals(bounds))
            {
                // 旧窗口的实际订阅负责 Leave，新位置附近的候选负责 Enter。远处模型换版无需加入这两个集合。
                var watched=_work[slot].Watched;
                for(int i=watched.AtOrAfter(0);i>=0;i=watched.AtOrAfter(i+1))
                    if(!Reconcile(slot,i,bounds.center))return false;
                _nearbyKeys.Clear();
                for(int domain=0;domain<_geometry.Length;domain++)
                    if(_hasGeometry[domain])_nearby.Collect(_geometry[domain],bounds,_nearbyKeys);
                foreach(var key in _nearbyKeys)
                    if(!Reconcile(slot,OwnerIndex(key),bounds.center))return false;
                // 可信内容身份始终给所有玩家，远离原点的新连接也能先确认本地地图基底。
                int anchor=OwnerIndex(new WorldChunkKey(WorldMaterialDomain.Cellular,0,0,0));
                if(anchor>=0 && !Reconcile(slot,anchor,bounds.center))return false;
                _comparedBounds[slot]=bounds;_interestCompared[slot]=true;
            }
            else if(hasCommit)
            {
                for(int i=0;i<_owner.LastChangeCount;i++)
                {
                    var change=_owner.GetLastChange(i);int index=OwnerIndex(change.Key);
                    // 同一权威源的局部提交已由 Owner 确认网格完全不变；一个原有块仅更新数量时，与同一观察窗
                    // 的空间关系一定不变。创建、移除、冷启动和重新进入仍必须经过真实判交和生命周期调度。
                    if(unchangedGeometry && change.Kind==WorldChunkChangeKind.Update && _subscriptions[slot,index].Known)
                        SchedulePeer(slot,index,_owner.GetChunkHeader(index));
                    else if(!Reconcile(slot,index,bounds.center))return false;
                }
            }
            // 等待安装 ACK 或短暂保留旧液体表现的已删除地址仍需重新判定；实际活块不参与扫描。
            for(int i=_tombstones.AtOrAfter(0);i>=0;i=_tombstones.AtOrAfter(i+1))
                if(!Reconcile(slot,i,bounds.center))return false;
            return true;
        }

        private bool Reconcile(int slot,int index,Vector3 observerCenter)
        {
            var header=_owner.GetChunkHeader(index);var key=header.Key;int domain=(int)key.Domain;
            if(!_hasGeometry[domain])return Fail("Missing committed chunk geometry");
            var peer=_peers[slot];var geometry=_geometry[domain];float edge=geometry.CellSize*geometry.ChunkSize;
            var min=geometry.Origin+new Vector3(key.X,key.Y,key.Z)*edge;
            InterestChunkChecks++;
            var chunkBounds=new Bounds(min+Vector3.one*(edge*.5f),Vector3.one*edge);
            bool anchor=key.Domain==WorldMaterialDomain.Cellular && key.X==0 && key.Y==0 && key.Z==0;
            bool inside=anchor || _interest.Intersects(peer.ConnectionId,peer.ConnectionEpoch,chunkBounds);
            ref var subscription=ref _subscriptions[slot,index];
            // 优先半径为两个正式参考块，从已认证的观察窗中心计算。锚点可先获验证，真实 Interest 不变。
            float nearRadius=2*_referenceSize;
            subscription.Near=anchor || inside && key.Domain==WorldMaterialDomain.Cellular && _interestConfigured &&
                chunkBounds.SqrDistance(observerCenter)<=nearRadius*nearRadius;
            if(!header.IsResident && (!subscription.Known || !subscription.Desired && !subscription.Announced ||
                TombstoneAcknowledged(slot,index,header) && !LiquidMotionRetains(slot,index)))inside=false;
            if(!subscription.Known)
            {
                subscription.Known=true;subscription.Desired=inside;subscription.AnnouncementKnown=!inside;
            }
            else if(subscription.Desired!=inside)
            {
                if(inside)subscription.Epoch=0;
                subscription.Desired=inside;subscription.PendingEpoch=0;
                peer.RequireBaseline(key);
            }
            SchedulePeer(slot,index,header);
            return true;
        }

        // 所有会改变可发送性的边界共用此入口：进入、离开、提交、更换冻结/ACK、水位缺口恢复。
        // 发送预算不足时保留同一候选；成功进入 Flight 后在途对象单独持有正文，直到真实安装 ACK。
        private void SchedulePeer(int slot,int index,WorldChunkPayloadHeader header)
        {
            var work=_work[slot];ref var s=ref _subscriptions[slot,index];
            if(!s.AnnouncementKnown && !s.Desired){s.AnnouncementKnown=true;s.Announced=false;}
            bool control=s.Announced?(!s.Desired || s.AnnouncedEpoch!=s.Epoch):s.Desired;
            work.Watched.Set(index,s.Desired || s.Announced);
            work.Control.Set(index,control);
            bool data=Sendable(slot,index) && !_peers[slot].HasPendingFor(header.Key) && _peers[slot].NeedsSnapshot(header);
            work.Quantity.Set(index,data);
            work.NearEnter.Set(index,control && s.Near && s.Desired && !s.Announced);
            work.NearFirst.Set(index,data && s.Near && _peerAckHeaders[slot,index].SubscriptionEpoch!=s.Epoch);
        }
    }
}
