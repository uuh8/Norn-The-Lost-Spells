using System;
using Game.NetworkProtocol;
using Google.Protobuf;

namespace Game.Networking
{
    /// <summary>
    /// 材料页的有界Protobuf适配。先在原始Span检查字段/长度/数量，才交给预热Parser；
    /// 不允许未知字段、嵌套对象或ByteString诱发热路径扩容。身份与版本依赖仍由连接/领域层审核。
    /// TryDecode返回的对象只借用到下一次Decode；跨帧必须复制到调用方固定staging。
    /// </summary>
    public sealed class WorldPageCodec
    {
        public const int MaxPayloadBytes=900, PageWords=160, MaxWords=32768, MaxPages=205;
        private readonly WorldPageMessage _received=new WorldPageMessage();
        public WorldPageCodec()
        {
            for(int i=0;i<PageWords;i++)_received.Words.Add(0);
            _received.Words.Clear();
        }
        public bool TryEncode(WorldPageMessage message,ArraySegment<byte> output,out int written)
        {
            written=0;
            if(output.Array==null || !Valid(message))return false;
            int size=message.CalculateSize();
            if(size>MaxPayloadBytes || size>output.Count)return false;
            var span=new Span<byte>(output.Array,output.Offset,size);
            message.WriteTo(span);
            if(!Wire(span))return false;
            written=size;return true;
        }
        public bool TryDecode(ArraySegment<byte> payload,out WorldPageMessage message)
        {
            message=null;_received.ResetForReuse();
            if(payload.Array==null || payload.Count<1 || payload.Count>MaxPayloadBytes)return false;
            var span=new ReadOnlySpan<byte>(payload.Array,payload.Offset,payload.Count);
            if(!Wire(span))return false;
            try{_received.MergeFrom(span);}catch(InvalidProtocolBufferException){_received.ResetForReuse();return false;}
            if(!Valid(_received)){_received.ResetForReuse();return false;}
            message=_received;return true;
        }
        private static bool Valid(WorldPageMessage m)
        {
            if(m==null || !ProtocolCompatibility.IsSupported(m.ProtocolVersion) || m.GameplayHash==0
                || m.ConnectionEpoch==0 || m.WorldEpoch==0 || m.SubscriptionEpoch==0 || m.Kind<1 || m.Kind>7
                || m.Domain>3
                || m.Words.Count>PageWords)return false;
            // 订阅控制只声明“这个认证连接当前可接收这个Key的哪一代”，不描述材料状态。
            // 先单独校验零材料字段，再处理1–5：若套用旧的Snapshot/Generation必需规则，
            // 就会被迫伪造材料版本；若放任这些字段，则控制包可能偷偷推进候选/版本水位。
            // 此处只验证wire形状；可信Enter、当前订阅匹配与幂等性仍由连接/领域层裁决。
            if(m.Kind==6 || m.Kind==7)
                return m.SnapshotId==0 && m.Generation==0 && m.BaseRevision==0 && m.Revision==0
                    && m.PageIndex==0 && m.PageCount==0 && m.ServerTick==0 && m.Checksum==0
                    && m.WordOffset==0 && m.TotalWords==0 && m.Words.Count==0;
            if(m.SnapshotId==0 || m.Generation==0 || m.Revision==0 || m.Checksum>uint.MaxValue)return false;
            if(m.Kind==3 || m.Kind==4)
                return m.Words.Count==0 && m.TotalWords==0 && m.WordOffset==0 && m.PageCount==0 && m.PageIndex==0;
            if(m.Kind==5)return m.Words.Count==0 && m.TotalWords==0 && m.WordOffset==0
                && m.PageCount==1 && m.PageIndex==0 && m.Checksum==2166136261u && m.BaseRevision<m.Revision;
            if(m.TotalWords==0 || m.TotalWords>MaxWords || m.PageCount<1 || m.PageCount>MaxPages
                || m.PageIndex>=m.PageCount || m.PageCount!=(m.TotalWords+PageWords-1)/PageWords
                || m.WordOffset!=m.PageIndex*PageWords || m.WordOffset>=m.TotalWords
                || m.Words.Count!=Math.Min(PageWords,(int)(m.TotalWords-m.WordOffset)))return false;
            return m.Kind==1 ? m.BaseRevision==0 : m.BaseRevision>0 && m.Revision>m.BaseRevision;
        }
        private static bool Wire(ReadOnlySpan<byte> bytes)
        {
            int offset=0;ulong seen=0;
            while(offset<bytes.Length)
            {
                if(!Varint(bytes,ref offset,out ulong tag) || tag>uint.MaxValue)return false;
                int field=(int)(tag>>3),wire=(int)(tag&7);
                if(field<1 || field>21 || (seen&(1UL<<field))!=0)return false;
                seen|=1UL<<field;
                // 新代次使用fixed64，不能接受varint的同值替身或重复字段后写覆盖。
                // 原始wire预检在Merge前完成，保持字段白名单和固定容量，不让unknown进入热路径。
                int expected=field==20?2:field==2 || field==4 || field==5 || field==11 || field==12 || field==13 || field==17 || field==21?1:0;
                if(wire!=expected)return false;
                if(wire==1){if(bytes.Length-offset<8)return false;offset+=8;}
                else if(wire==2)
                {
                    if(!Varint(bytes,ref offset,out ulong length) || length>PageWords*4 || (length&3)!=0
                        || length>(ulong)(bytes.Length-offset))return false;
                    offset+=(int)length;
                }
                else if(!Varint(bytes,ref offset,out ulong value) || value>uint.MaxValue)return false;
            }
            return true;
        }
        private static bool Varint(ReadOnlySpan<byte> b,ref int i,out ulong value)
        {
            value=0;
            for(int shift=0;shift<=63;shift+=7)
            {
                if(i>=b.Length)return false;byte next=b[i++];
                if(shift==63 && next>1)return false;
                value|=(ulong)(next&127)<<shift;
                if((next&128)==0)return shift==0 || next!=0;
            }
            return false;
        }
    }
}
