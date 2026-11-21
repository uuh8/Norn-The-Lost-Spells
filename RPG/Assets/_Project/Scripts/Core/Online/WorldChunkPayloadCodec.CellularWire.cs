using System;

namespace Game.Core
{
    public static partial class WorldChunkPayloadCodec
    {
        // 仅 wire 使用。权威、Checkpoint、已安装缓存及 ACK 的 checksum 始终描述规范 W2 正文。
        // 紧凑格字的低 12 位是本块三轴局部坐标，接着 4 位材料 ID、高 16 位真实 GMU。
        private const uint CellularWireMagic=0x4E354331;
        internal static bool IsPackedCellular(uint[] words,int count)=>count>0 && words[0]==CellularWireMagic;

        /// <summary>仅压缩已经完整冻结的 Cellular 正文。预检全部成功后才覆盖 Peer 的私有发送数组，无法无损编码时返回原长度。</summary>
        public static int PackCellularForWire(WorldChunkKey key,uint[] words,int count)
        {
            if(key.Domain!=WorldMaterialDomain.Cellular || words==null || count<CellularPrefixWords || count>words.Length ||
                words[0]!=CellularMagic || words[1]==0 || words[1]>(uint)((count-CellularPrefixWords)/CellWords) ||
                words[10]==0 || words[10]>16 || words[13]==0 || words[13]>ushort.MaxValue)return count;
            int cells=(int)words[1],size=(int)words[10];
            long bx=(long)key.X*size,by=(long)key.Y*size,bz=(long)key.Z*size;
            for(int i=0,at=CellularPrefixWords;i<cells;i++,at+=CellWords)
            {
                long x=(long)unchecked((int)words[at+2])-bx,y=(long)unchecked((int)words[at+3])-by,z=(long)unchecked((int)words[at+4])-bz;
                if(words[at]!=(uint)key.Domain || words[at+1]!=0 || x<0 || x>=size || y<0 || y>=size || z<0 || z>=size ||
                   words[at+5]==0 || words[at+5]>15 || words[at+6]==0 || words[at+6]>ushort.MaxValue)return count;
            }
            for(int i=0,at=CellularPrefixWords;i<cells;i++,at+=CellWords)
            {
                uint x=(uint)((long)unchecked((int)words[at+2])-bx),y=(uint)((long)unchecked((int)words[at+3])-by);
                uint z=(uint)((long)unchecked((int)words[at+4])-bz);
                words[CellularPrefixWords+i]=x|(y<<4)|(z<<8)|(words[at+5]<<12)|(words[at+6]<<16);
            }
            int tail=CellularPrefixWords+cells*CellWords;
            Array.Copy(words,tail,words,CellularPrefixWords+cells,count-tail);
            words[0]=CellularWireMagic;return count-(CellWords-1)*cells;
        }

        // 必须检查解压后的逻辑容量；较短的网络页不能借其声明分配任意大数组。
        // 扩展时按格倒序重建，保留待写队列和相对基底覆盖的原始字节；最终仍走同一 checksum、W2 验证与安装。
        internal static bool ExpandCellularWire(WorldChunkKey key,ref uint[] words,int count,int limit,out int logicalCount)
        {
            logicalCount=0;
            if(key.Domain!=WorldMaterialDomain.Cellular || count<CellularPrefixWords || count>words.Length ||
                words[0]!=CellularWireMagic || words[1]==0 || words[10]==0 || words[10]>16 || words[13]==0 || words[13]>ushort.MaxValue)return false;
            long cells=words[1],tail=(long)words[2]*CellularPendingWords+(long)words[14]*SpanWords;
            long canonical=CellularPrefixWords+cells*CellWords+tail;
            if(CellularPrefixWords+cells+tail!=count || canonical>limit)return false;
            logicalCount=(int)canonical;int n=(int)cells,size=(int)words[10];
            if(words.Length<logicalCount)Array.Resize(ref words,(int)Math.Min(limit,Math.Max(logicalCount,words.Length*2L)));
            Array.Copy(words,CellularPrefixWords+n,words,CellularPrefixWords+n*CellWords,(int)tail);
            long bx=(long)key.X*size,by=(long)key.Y*size,bz=(long)key.Z*size;
            for(int i=n-1;i>=0;i--)
            {
                uint packed=words[CellularPrefixWords+i],x=packed&15,y=packed>>4&15,z=packed>>8&15;
                long gx=bx+x,gy=by+y,gz=bz+z;
                if(x>=size || y>=size || z>=size || gx<int.MinValue || gx>int.MaxValue ||
                    gy<int.MinValue || gy>int.MaxValue || gz<int.MinValue || gz>int.MaxValue)return false;
                int at=CellularPrefixWords+i*CellWords;
                words[at]=(uint)key.Domain;words[at+1]=0;words[at+2]=unchecked((uint)gx);words[at+3]=unchecked((uint)gy);
                words[at+4]=unchecked((uint)gz);words[at+5]=packed>>12&15;words[at+6]=packed>>16;
            }
            words[0]=CellularMagic;return true;
        }
    }
}
