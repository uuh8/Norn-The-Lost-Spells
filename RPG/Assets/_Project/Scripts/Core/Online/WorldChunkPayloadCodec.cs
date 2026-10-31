using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>完整Chunk replacement的身份。Delta仍依赖BaseRevision，不是可以跳过缺口的任意覆盖。</summary>
    public readonly struct WorldChunkPayloadHeader : IEquatable<WorldChunkPayloadHeader>
    {
        public readonly ulong WorldEpoch, SnapshotId, Generation, BaseRevision, Revision;
        public readonly WorldChunkKey Key;
        public readonly long ServerTick;
        public readonly int WordCount;
        public readonly uint Checksum;
        public readonly bool IsResident;
        public WorldChunkPayloadHeader(ulong worldEpoch, ulong snapshotId, WorldChunkKey key, ulong generation,
            ulong baseRevision, ulong revision, long serverTick, int wordCount, uint checksum, bool isResident)
        { WorldEpoch=worldEpoch; SnapshotId=snapshotId; Key=key; Generation=generation; BaseRevision=baseRevision;
          Revision=revision; ServerTick=serverTick; WordCount=wordCount; Checksum=checksum; IsResident=isResident; }
        public bool Equals(WorldChunkPayloadHeader h) => WorldEpoch==h.WorldEpoch && SnapshotId==h.SnapshotId
            && Key==h.Key && Generation==h.Generation && BaseRevision==h.BaseRevision && Revision==h.Revision
            && ServerTick==h.ServerTick && WordCount==h.WordCount && Checksum==h.Checksum && IsResident==h.IsResident;
        public override bool Equals(object obj) => obj is WorldChunkPayloadHeader h && Equals(h);
        public override int GetHashCode() => Key.GetHashCode() ^ SnapshotId.GetHashCode();
    }

    /// <summary>
    /// 网络中立的固定uint布局。浮点按IEEE754原始bit保存，负坐标按有符号32bit重解释。
    /// Prefix=magic/cellCount/pendingCount/geometryCount；随后Geometry、Cell、Pending。
    /// Geometry=domain/gridId/originXYZ/cellSize/chunkSize；Cell=domain/storage/cellXYZ/material/quantity；
    /// Pending=domain/queueIndex/material/positionXYZ/total/radius/falloff/velocityXYZ/normalXYZ/remaining/cursor/waitingTicks。
    /// 只表达空间数量，不包含Solver内部状态，不可据此恢复权威规则执行。
    /// </summary>
    public static class WorldChunkPayloadCodec
    {
        public const uint Magic=0x4E355731;
        public const int PrefixWords=4, GeometryWords=7, CellWords=7, PendingWords=18;
        public static uint FloatWord(float v) => unchecked((uint)BitConverter.SingleToInt32Bits(v));
        public static float WordFloat(uint v) => BitConverter.Int32BitsToSingle(unchecked((int)v));
        public static uint ComputeChecksum(uint[] words,int count)
        {
            if(words==null || count<0 || count>words.Length) throw new ArgumentOutOfRangeException(nameof(count));
            uint h=2166136261;
            unchecked { for(int i=0;i<count;i++){ uint v=words[i]; for(int b=0;b<4;b++){h=(h^(v&255))*16777619;v>>=8;} } }
            return h;
        }
        public static int FloorDiv(int value,int divisor)
        { int q=value/divisor; return value%divisor<0?q-1:q; }
        public static WorldChunkKey CellKey(WorldMaterialDomain domain,Vector3Int cell,int size)
            => new WorldChunkKey(domain,FloorDiv(cell.x,size),FloorDiv(cell.y,size),FloorDiv(cell.z,size));
        public static bool TryPositionKey(in WorldGridGeometry g,Vector3 p,out WorldChunkKey key)
        {
            key=default;
            if(!Finite(p) || !ValidGeometry(g)) return false;
            // double避免大坐标/极小CellSize先在float中溢出，再被FloorToInt静默夹成错误地址。
            double x=Math.Floor(((double)p.x-g.Origin.x)/g.CellSize), y=Math.Floor(((double)p.y-g.Origin.y)/g.CellSize), z=Math.Floor(((double)p.z-g.Origin.z)/g.CellSize);
            if(x<int.MinValue || x>int.MaxValue || y<int.MinValue || y>int.MaxValue || z<int.MinValue || z>int.MaxValue) return false;
            key=CellKey(g.Domain,new Vector3Int((int)x,(int)y,(int)z),g.ChunkSize); return true;
        }
        public static bool ValidGeometry(in WorldGridGeometry g) => (byte)g.Domain<=3 && g.GridId==(byte)g.Domain
            && Finite(g.Origin) && float.IsFinite(g.CellSize) && g.CellSize>0 && g.ChunkSize>0;
        public static bool ValidPending(in WorldPendingDepositSample p) => (byte)p.Domain<=3 && p.QueueIndex>=0
            && p.MaterialId!=0 && p.TotalAmount>0 && p.Remaining<=p.TotalAmount && p.Cursor>=0
            && float.IsFinite(p.Radius) && p.Radius>=0 && Finite(p.WorldPosition) && Finite(p.InitialVelocity) && Finite(p.SurfaceNormal);
        private static bool Finite(Vector3 v) => float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);
        public static void WriteGeometry(uint[] w,int i,in WorldGridGeometry g)
        { w[i]=(uint)g.Domain;w[i+1]=g.GridId;w[i+2]=FloatWord(g.Origin.x);w[i+3]=FloatWord(g.Origin.y);w[i+4]=FloatWord(g.Origin.z);w[i+5]=FloatWord(g.CellSize);w[i+6]=(uint)g.ChunkSize; }
        public static WorldGridGeometry ReadGeometry(uint[] w,int i) => new WorldGridGeometry((WorldMaterialDomain)w[i],new Vector3(WordFloat(w[i+2]),WordFloat(w[i+3]),WordFloat(w[i+4])),WordFloat(w[i+5]),unchecked((int)w[i+6]));
        public static void WriteCell(uint[] w,int i,in WorldMaterialCellSample c)
        { w[i]=(uint)c.Domain;w[i+1]=(uint)c.Storage;w[i+2]=unchecked((uint)c.Cell.x);w[i+3]=unchecked((uint)c.Cell.y);w[i+4]=unchecked((uint)c.Cell.z);w[i+5]=c.MaterialId;w[i+6]=c.Quantity; }
        public static WorldMaterialCellSample ReadCell(uint[] w,int i) => new WorldMaterialCellSample((WorldMaterialDomain)w[i],(WorldMaterialStorage)w[i+1],new Vector3Int(unchecked((int)w[i+2]),unchecked((int)w[i+3]),unchecked((int)w[i+4])),(byte)w[i+5],w[i+6]);
        public static void WritePending(uint[] w,int i,in WorldPendingDepositSample p)
        {
            w[i]=(uint)p.Domain;w[i+1]=(uint)p.QueueIndex;w[i+2]=p.MaterialId;
            WriteVector(w,i+3,p.WorldPosition);w[i+6]=p.TotalAmount;w[i+7]=FloatWord(p.Radius);w[i+8]=p.UseLinearFalloff?1u:0;
            WriteVector(w,i+9,p.InitialVelocity);WriteVector(w,i+12,p.SurfaceNormal);w[i+15]=p.Remaining;w[i+16]=(uint)p.Cursor;w[i+17]=p.WaitingTicks;
        }
        private static void WriteVector(uint[] w,int i,Vector3 v){w[i]=FloatWord(v.x);w[i+1]=FloatWord(v.y);w[i+2]=FloatWord(v.z);}
        private static Vector3 ReadVector(uint[] w,int i)=>new Vector3(WordFloat(w[i]),WordFloat(w[i+1]),WordFloat(w[i+2]));
        public static WorldPendingDepositSample ReadPending(uint[] w,int i) => new WorldPendingDepositSample((WorldMaterialDomain)w[i],unchecked((int)w[i+1]),(byte)w[i+2],ReadVector(w,i+3),(ushort)w[i+6],WordFloat(w[i+7]),w[i+8]!=0,ReadVector(w,i+9),ReadVector(w,i+12),w[i+15],unchecked((int)w[i+16]),w[i+17]);

        /// <summary>验证整个候选；调用者先校验Checksum。无部分写入，循环上限由maxWords限制。</summary>
        public static bool Validate(uint[] w,int count,WorldChunkKey key)
        {
            if(w==null || count<11 || count>w.Length || w[0]!=Magic || w[3]!=1 || w[1]>int.MaxValue || w[2]>int.MaxValue) return false;
            long required=11L+(long)w[1]*CellWords+(long)w[2]*PendingWords;
            if(required!=count || w[4]!=(uint)key.Domain || w[5]!=(uint)key.Domain) return false;
            var g=ReadGeometry(w,4); if(!ValidGeometry(g)) return false;
            int cellCount=(int)w[1],pendingCount=(int)w[2],i=11;
            WorldMaterialCellSample previous=default;
            for(int c=0;c<cellCount;c++,i+=CellWords)
            {
                if(w[i]!=(uint)key.Domain || w[i+1]!=0 || w[i+5]==0 || w[i+5]>255 || w[i+6]==0 || (key.Domain==WorldMaterialDomain.LegacyCell && w[i+6]>255)) return false;
                var v=ReadCell(w,i); if(CellKey(v.Domain,v.Cell,g.ChunkSize)!=key || (c>0 && CompareCells(previous,v)>=0)) return false;
                previous=v;
            }
            int lastQueue=-1;
            for(int p=0;p<pendingCount;p++,i+=PendingWords)
            {
                if(w[i]!=(uint)key.Domain || w[i+1]>int.MaxValue || w[i+2]==0 || w[i+2]>255 || w[i+6]>ushort.MaxValue || w[i+8]>1 || w[i+16]>int.MaxValue) return false;
                var v=ReadPending(w,i); if(!ValidPending(v) || v.QueueIndex<=lastQueue || !TryPositionKey(g,v.WorldPosition,out var k) || k!=key) return false;
                lastQueue=v.QueueIndex;
            }
            return true;
        }
        internal static int CompareCells(in WorldMaterialCellSample a,in WorldMaterialCellSample b)
        { int c=a.Cell.x.CompareTo(b.Cell.x);if(c!=0)return c;c=a.Cell.y.CompareTo(b.Cell.y);if(c!=0)return c;c=a.Cell.z.CompareTo(b.Cell.z);return c!=0?c:a.MaterialId.CompareTo(b.MaterialId); }
        public static bool TryDecode(uint[] words,int count,WorldChunkKey key,long tick,WorldReplicaBuffer destination)
        {
            if(destination==null || count>destination.InspectionBudget || !Validate(words,count,key))return false;
            int cells=(int)words[1],pending=(int)words[2];
            if(cells>destination.Cells.Length || pending>destination.Pending.Length)return false;
            destination.Geometries[0]=ReadGeometry(words,4);int i=11;
            for(int c=0;c<cells;c++,i+=CellWords)destination.Cells[c]=ReadCell(words,i);
            for(int p=0;p<pending;p++,i+=PendingWords)destination.Pending[p]=ReadPending(words,i);
            destination.Header=new WorldReplicaHeader(tick,cells,pending,1);return true;
        }
    }
}
