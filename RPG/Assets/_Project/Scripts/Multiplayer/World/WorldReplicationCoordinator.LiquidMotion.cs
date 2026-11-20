using Game.Core;
using UnityEngine;

namespace Game.Multiplayer
{
    public sealed partial class WorldReplicationCoordinator
    {
        private double[,] _liquidMotionRetainUntil;
        private double _liquidMotionRoundTime;
        /// <summary>
        /// 跨Chunk搬运的源Chunk可在本Tick变空。数量tombstone仍立刻安装/ACK；只将其订阅退休延后
        /// 最多一个表现窗口，让已经完整选中的Previous尾水有时间播放。真实空间Interest退出不延后。
        /// 使用Owner固定槽容量，随元数据搬槽同步移动；不保存无界历史地址。
        /// </summary>
        public void RetainLiquidMotionAnchors(int slot,WorldLiquidMotionFrame frame,double now)
        {
            if(!IsAuthority || (uint)slot>=4 || frame==null || !double.IsFinite(now))return;
            double until=now+System.Math.Min(.2,System.Math.Max(.05,frame.Interval+.05));
            for(int i=0;i<frame.AnchorCount;i++)
            {
                int index=OwnerIndex(frame.Anchors[i].Key);
                if(index>=0 && Sendable(slot,index))_liquidMotionRetainUntil[slot,index]=
                    System.Math.Max(_liquidMotionRetainUntil[slot,index],until);
            }
        }
        private bool LiquidMotionRetains(int slot,int index)=>_liquidMotionRetainUntil!=null
            && _liquidMotionRetainUntil[slot,index]>_liquidMotionRoundTime;
        /// <summary>只借用已提交数量和已宣布订阅的身份，绝不为了表现制造Revision或再次捕获数量。</summary>
        public bool TryGetLiquidMotionAnchor(int slot, WorldChunkKey key, out WorldLiquidMotionAnchor anchor)
        {
            anchor=default;
            if(!IsAuthority || _stopped || Faulted || (uint)slot>=4 || !_active[slot]
                || key.Domain!=WorldMaterialDomain.Liquid)return false;
            int index=OwnerIndex(key);
            if(index<0 || !Sendable(slot,index))return false;
            var header=_owner.GetChunkHeader(index);
            anchor=new WorldLiquidMotionAnchor(key,header.Generation,header.Revision,
                _subscriptions[slot,index].Epoch,header.IsResident);
            return true;
        }
        public bool TryGetLiquidMotionGeometry(out WorldGridGeometry geometry)
        { geometry=_geometry[(int)WorldMaterialDomain.Liquid];return _hasGeometry[(int)WorldMaterialDomain.Liquid]; }

        /// <summary>安装事实由Sink审核；这里只核验当前连接仍有权观察所有anchor，Leave当帧即失效。</summary>
        public bool AcceptsLiquidMotionFrame(WorldLiquidMotionFrame frame)
        {
            if(IsAuthority || _stopped || Faulted || frame==null || frame.WorldEpoch!=WorldEpoch
                || frame.ConnectionEpoch!=_clientEpoch || frame.AnchorCount<1
                || frame.AnchorCount>WorldLiquidMotionFrame.MaxAnchors)return false;
            for(int i=0;i<frame.AnchorCount;i++)
            {
                if(!AcceptsLiquidMotionAnchor(frame.Anchors[i]))return false;
            }
            return true;
        }
        public bool AcceptsLiquidMotionAnchor(WorldLiquidMotionAnchor anchor)
        {
            if(IsAuthority || _stopped || Faulted)return false;
            int slot=ClientIndex(anchor.Key);
            return slot>=0 && _clientActive[slot] && _clientSubscriptions[slot]==anchor.SubscriptionEpoch;
        }
    }
}
