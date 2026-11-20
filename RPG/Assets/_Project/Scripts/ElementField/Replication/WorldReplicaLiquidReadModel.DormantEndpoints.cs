using System;
using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class WorldReplicaLiquidReadModel
    {
        /// <summary>
        /// 最近完整数量 witness 已接受的 Current endpoint，只给下一份 M 在安装 Q 前独立取证。
        /// Solver 的地面状态可以在休眠后保留两个 Tick 的非零速度，但 motion 只发送活动子集；
        /// 因此某格缺一窗后重入时，不能把“已不在 active/future 数组”误当成从未见过这个 pose。
        /// 本缓存既不是数量 History，也不直接给 Renderer 采样，更不能代替 Current witness/ACK。
        /// </summary>
        private readonly struct DormantEndpoint
        {
            internal readonly ulong WorldEpoch,Sequence,ObservedRevision;
            internal readonly uint ConnectionEpoch,MaterialTick;
            internal readonly double SampleTime;
            internal readonly WorldLiquidMotionAnchor Anchor;
            internal readonly Vector3Int Cell;
            internal readonly byte Material;
            internal readonly LiquidCellPresentationSample Sample;
            internal DormantEndpoint(WorldLiquidMotionFrame frame,WorldLiquidMotionSample row,
                WorldLiquidMotionAnchor anchor,ulong observedRevision)
            {
                WorldEpoch=frame.WorldEpoch;Sequence=frame.Sequence;ObservedRevision=observedRevision;
                ConnectionEpoch=frame.ConnectionEpoch;MaterialTick=frame.MaterialTick;SampleTime=frame.SampleTime;
                Anchor=anchor;Cell=row.Cell;Material=row.MaterialId;
                Sample=new LiquidCellPresentationSample(row.CurrentQuantity,row.CurrentOffset,row.CurrentVelocity);
            }
        }

        // 只保留当前 Tick 与前两个 Tick 的最多128行并集，按(cell,material)替换最新已验证值。
        // 即使接收到异常密集的同Tick不同窗口也不扩容，满时只是放弃该行的平滑资格，不改资源。
        private readonly DormantEndpoint[] _dormantEndpoints=new DormantEndpoint[
            WorldLiquidMotionFrame.MaxRows*(LiquidTransportState.MaximumRetainedAgeTicks+1)];
        private int _dormantEndpointCount;
        private uint _dormantLiquidVersion,_dormantSolidVersion;

        private void ClearDormantEndpoints()
        {
            Array.Clear(_dormantEndpoints,0,_dormantEndpointCount);_dormantEndpointCount=0;
            _dormantLiquidVersion=_store.DomainVersion(WorldMaterialDomain.Liquid);
            _dormantSolidVersion=MotionSolidVersion();
        }
        private void RemoveDormantEndpointAt(int index)
        {
            _dormantEndpointCount--;
            _dormantEndpoints[index]=_dormantEndpoints[_dormantEndpointCount];
            _dormantEndpoints[_dormantEndpointCount]=default;
        }
        private int FindDormantEndpoint(Vector3Int cell,byte material)
        {
            for(int i=0;i<_dormantEndpointCount;i++)
                if(_dormantEndpoints[i].Cell==cell && _dormantEndpoints[i].Material==material)return i;
            return -1;
        }

        /// <summary>
        /// 一次真实 Store/Solid 版本变化最多检查384项；逐顶点/逐格 Renderer 查询走版本快路。
        /// 特别是 cached B 已不在 active A 的子集时，F 收走 B 也必须撤掉 B 的独立旧 pose，
        /// 不能等 B 再次参与运动才检查，更不能让已实际观察到的清零/重新出生复用旧速度。
        /// </summary>
        private void ValidateDormantEndpoints()
        {
            uint liquid=_store.DomainVersion(WorldMaterialDomain.Liquid),solid=MotionSolidVersion();
            if(_dormantSolidVersion!=solid){ClearDormantEndpoints();return;}
            if(_dormantLiquidVersion==liquid)return;
            for(int i=_dormantEndpointCount-1;i>=0;i--)
            {
                var old=_dormantEndpoints[i];
                if(old.WorldEpoch!=_store.WorldEpoch || old.ConnectionEpoch!=_motionConnectionEpoch
                    || !_store.TryGetPublishedChunkFact(old.Anchor.Key,out var fact,out bool interested)
                    || fact.WorldEpoch!=old.WorldEpoch || fact.Generation!=old.Anchor.Generation
                    || fact.Revision<old.ObservedRevision || !fact.IsResident || !interested
                    || !TryGetQuantity(old.Cell,(MaterialId)old.Material,out uint quantity) || quantity!=old.Sample.Quantity)
                    RemoveDormantEndpointAt(i);
            }
            _dormantLiquidVersion=liquid;_dormantSolidVersion=solid;
        }

        /// <summary>
        /// 唯一登记入口紧接 TryPublish 的完整 Current witness 成功之后，早于正常切换 active/future。
        /// 数组保存值，不借用 Assembler/调用者 frame；正常 Activate/ClearFuture 不清此缓存。
        /// 单窗曲线失效只按真实事实删坏键，重置时钟/连接、Frozen、新Solid才全清；这样不会
        /// 让无关失窗连锁擦掉其它格的独立证明，未显示但已完整验证的 future 也同样可取证。
        /// </summary>
        private void RememberValidatedEndpoints(WorldLiquidMotionFrame frame)
        {
            ValidateDormantEndpoints();
            for(int i=_dormantEndpointCount-1;i>=0;i--)
            {
                var old=_dormantEndpoints[i];
                if(old.WorldEpoch!=frame.WorldEpoch || old.ConnectionEpoch!=frame.ConnectionEpoch
                    || frame.MaterialTick<old.MaterialTick
                    || frame.MaterialTick-old.MaterialTick>LiquidTransportState.MaximumRetainedAgeTicks)
                    RemoveDormantEndpointAt(i);
            }
            for(int rowIndex=0;rowIndex<frame.RowCount;rowIndex++)
            {
                var row=frame.Rows[rowIndex];int index=FindDormantEndpoint(row.Cell,row.MaterialId);
                // 零数量不是 endpoint 权限；清掉同key旧值，不能稍后回溯到另一批同量的水。
                if(row.CurrentQuantity==0){if(index>=0)RemoveDormantEndpointAt(index);continue;}
                var anchor=frame.Anchors[row.AnchorIndex];
                if(!_store.TryGetPublishedChunkFact(anchor.Key,out var fact,out bool interested)
                    || !fact.IsResident || !interested)continue; // 完整witness通常已保证，保留局部边界。
                if(index<0)
                {
                    if(_dormantEndpointCount==_dormantEndpoints.Length)continue;
                    index=_dormantEndpointCount++;
                }
                _dormantEndpoints[index]=new DormantEndpoint(frame,row,anchor,fact.Revision);
            }
            _dormantLiquidVersion=_store.DomainVersion(WorldMaterialDomain.Liquid);_dormantSolidVersion=MotionSolidVersion();
        }

        private bool TryReadDormantEndpoint(WorldLiquidMotionFrame frame,WorldLiquidMotionSample row,
            WorldLiquidMotionAnchor anchor,WorldChunkPayloadHeader fact,out LiquidCellPresentationSample sample)
        {
            sample=default;int index=FindDormantEndpoint(row.Cell,row.MaterialId);if(index<0)return false;
            var old=_dormantEndpoints[index];
            if(old.WorldEpoch!=frame.WorldEpoch || old.ConnectionEpoch!=frame.ConnectionEpoch
                || frame.Sequence<=old.Sequence || frame.MaterialTick<old.MaterialTick
                || frame.MaterialTick-old.MaterialTick>LiquidTransportState.MaximumRetainedAgeTicks
                || frame.SampleTime<=old.SampleTime || frame.SampleTime-old.SampleTime>MaximumMotionAgeSeconds
                || old.Anchor.Key!=anchor.Key || old.Anchor.Generation!=anchor.Generation
                || old.Anchor.SubscriptionEpoch!=anchor.SubscriptionEpoch || old.ObservedRevision>fact.Revision
                || old.Sample.Quantity!=row.PreviousQuantity || !PreviousPoseMatches(row,old.Sample))return false;
            // 此处仍要求 CaptureKnownPrevious 在安装新Q前已读到严格相等的真实旧量/身份。
            // 返回值随后只是生成原KnownPrevious证明，不能直接放行candidate或提前显示Current。
            sample=old.Sample;return true;
        }
    }
}
