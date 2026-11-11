using System;
using System.Reflection;
using Game.NetworkProtocol;
using Google.Protobuf;
using NUnit.Framework;
using Unity.Profiling;

namespace Game.Networking.Tests
{
    /// <summary>先验证真实wire预算与复用，反射仅为缺生产Codec时建立明确RED。</summary>
    public sealed class WorldPageCodecTests
    {
        private object _codec; private Type _type;
        [SetUp] public void Setup()
        {
            _type = typeof(MovementCodec).Assembly.GetType("Game.Networking.WorldPageCodec");
            Assert.That(_type, Is.Not.Null, "N5缺少WorldPageCodec");
            _codec = Activator.CreateInstance(_type);
        }
        private static WorldPageMessage Page(int count=160)
        {
            var p=new WorldPageMessage{ProtocolVersion=ProtocolCompatibility.CurrentVersion,GameplayHash=9,
                ConnectionEpoch=1,WorldEpoch=7,SubscriptionEpoch=1,SnapshotId=1,Kind=1,Domain=1,ChunkX=int.MinValue,
                ChunkY=int.MaxValue,Generation=1,Revision=1,PageCount=1,TotalWords=(uint)count,ServerTick=1,Checksum=1};
            for(int i=0;i<count;i++)p.Words.Add(uint.MaxValue); return p;
        }
        private bool Encode(WorldPageMessage p,byte[] b,out int n)
        {
            object[] a={p,new ArraySegment<byte>(b),0}; bool ok=(bool)_type.GetMethod("TryEncode").Invoke(_codec,a);
            n=(int)a[2];return ok;
        }
        private bool Decode(ArraySegment<byte> b,out WorldPageMessage p)
        { object[] a={b,null};bool ok=(bool)_type.GetMethod("TryDecode").Invoke(_codec,a);p=(WorldPageMessage)a[1];return ok; }
        [Test] public void FullWorstCaseWords_RoundTripBelow900Bytes()
        {
            var b=new byte[900];Assert.True(Encode(Page(),b,out int n)); Assert.LessOrEqual(n,900);
            Assert.True(Decode(new ArraySegment<byte>(b,0,n),out var p));Assert.AreEqual(160,p.Words.Count);
            Assert.AreEqual(int.MinValue,p.ChunkX);Assert.AreEqual(uint.MaxValue,p.Words[159]);
        }
        [Test] public void OversizeAndTruncatedPackedWords_AreRejected()
        {
            var b=new byte[900];Assert.False(Encode(Page(161),b,out _));
            Assert.True(Encode(Page(),b,out int n));Assert.False(Decode(new ArraySegment<byte>(b,0,n-1),out _));
        }
        [Test] public void DecodeReset_DoesNotRetainPriorPageWords()
        {
            var b=new byte[900];Encode(Page(),b,out int n);Assert.True(Decode(new ArraySegment<byte>(b,0,n),out _));
            Encode(Page(1),b,out n);Assert.True(Decode(new ArraySegment<byte>(b,0,n),out var p));Assert.AreEqual(1,p.Words.Count);
        }
        [Test] public void UnknownField_IsRejectedBeforeMerge()
        {
            var p=Page(1);byte[] b=p.ToByteArray();byte[] expanded=new byte[b.Length+3];Array.Copy(b,expanded,b.Length);
            expanded[b.Length]=0xB0;expanded[b.Length+1]=1;expanded[b.Length+2]=1;
            Assert.False(Decode(new ArraySegment<byte>(expanded),out _));
        }
        [Test] public void EpochMissingAndDeltaGapShape_AreRejected()
        {
            var p=Page(1);var b=new byte[900];p.ConnectionEpoch=0;Assert.False(Encode(p,b,out _));
            p.ConnectionEpoch=1;p.Kind=2;p.BaseRevision=2;p.Revision=1;Assert.False(Encode(p,b,out _));
        }
        [Test] public void ControlMessage_CannotSmuggleWords()
        {
            var p=Page(1);p.Kind=3;Assert.False(Encode(p,new byte[900],out _));
            p.Words.Clear();p.TotalWords=p.PageCount=0;Assert.True(Encode(p,new byte[900],out _));
        }
        [Test] public void WarmFullPageCodec_HasZeroPositivelyCalibratedAllocationEvents()
        {
            var codec=new WorldPageCodec();var p=Page();var b=new byte[900];
            for(int i=0;i<16;i++){Assert.True(codec.TryEncode(p,new ArraySegment<byte>(b),out int n));Assert.True(codec.TryDecode(new ArraySegment<byte>(b,0,n),out _));}
            using var recorder=ProfilerRecorder.StartNew(ProfilerCategory.Internal,"GC.Alloc",128,ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            Assert.True(recorder.Valid);recorder.Stop();recorder.Reset();recorder.Start();
            var positive=new byte[1024];GC.KeepAlive(positive);recorder.Stop();int calibration=recorder.Count;
            Assert.That(calibration,Is.GreaterThan(0));Assert.That(calibration,Is.LessThan(recorder.Capacity));
            bool passed=true;recorder.Reset();recorder.Start();
            for(int i=0;i<256;i++)
            {
                passed &= codec.TryEncode(p,new ArraySegment<byte>(b),out int n);
                passed &= codec.TryDecode(new ArraySegment<byte>(b,0,n),out var read) && read.Words.Count==160;
            }
            recorder.Stop();int measured=recorder.Count;
            Assert.True(passed);Assert.That(measured,Is.Zero,"仅Codec方法范围，不是Native全帧。");
            TestContext.WriteLine("GC.Alloc calibration="+calibration+", codec="+measured);
        }
    }

    /// <summary>
    /// 订阅代次与材料版本分别承担不同的栅栏职责：退出再进入同一个Key时，旧页即使Revision更高也不能复活。
    /// 这里直接构造field21的wire字节，使Schema尚未生成时也能编译并在真实Codec上出现行为RED。
    /// 反射仅用于过渡期访问新属性，不绕开Codec的验证；主控统一生成后仍验证同一份真实合同。
    /// </summary>
    public sealed class WorldSubscriptionCodecTests
    {
        private WorldPageCodec _codec;

        [SetUp]
        public void Setup() => _codec = new WorldPageCodec();

        private static WorldPageMessage Material(uint kind)
        {
            var page = Identity(kind);
            page.SnapshotId = 13;
            page.Generation = 5;
            page.Revision = 7;
            page.Checksum = 1;
            page.ServerTick = 17;
            if (kind == 1 || kind == 2)
            {
                page.BaseRevision = kind == 2 ? 6UL : 0UL;
                page.PageCount = 1;
                page.TotalWords = 1;
                page.Words.Add(19);
            }
            else if (kind == 5)
            {
                page.BaseRevision = 6;
                page.PageCount = 1;
                page.Checksum = 2166136261u;
            }
            return page;
        }

        private static WorldPageMessage Identity(uint kind) => new WorldPageMessage
        {
            ProtocolVersion = ProtocolCompatibility.CurrentVersion,
            GameplayHash = 9,
            ConnectionEpoch = 3,
            WorldEpoch = 11,
            Kind = kind,
            Domain = 2,
            ChunkX = -3,
            ChunkY = 4,
            ChunkZ = -5
        };

        private static PropertyInfo SubscriptionProperty()
        {
            var property = typeof(WorldPageMessage).GetProperty("SubscriptionEpoch");
            Assert.That(property, Is.Not.Null, "WorldPageMessage需要独立SubscriptionEpoch，不能借用SnapshotId或材料版本。");
            return property;
        }

        private static void SetSubscription(WorldPageMessage page, ulong epoch)
            => SubscriptionProperty().SetValue(page, epoch);

        private static ulong Subscription(WorldPageMessage page)
            => (ulong)SubscriptionProperty().GetValue(page);

        // field21=(21<<3)|1=169，fixed64小端；这里不用生产序列化器推导预期，避免同错同过。
        private static byte[] EpochBytes(ulong epoch)
        {
            var bytes = new byte[10];
            bytes[0] = 0xA9;
            bytes[1] = 0x01;
            for (int i = 0; i < 8; i++) bytes[i + 2] = (byte)(epoch >> (i * 8));
            return bytes;
        }

        private static byte[] Join(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Array.Copy(first, result, first.Length);
            Array.Copy(second, 0, result, first.Length, second.Length);
            return result;
        }

        private bool Decode(byte[] bytes, out WorldPageMessage page)
            => _codec.TryDecode(new ArraySegment<byte>(bytes), out page);

        private bool Encode(WorldPageMessage page, out int written)
            => _codec.TryEncode(page, new ArraySegment<byte>(new byte[900]), out written);

        [TestCase(6u)]
        [TestCase(7u)]
        public void SubscriptionControl_WithOnlyIdentityKeyAndEpoch_RoundTrips(uint kind)
        {
            byte[] bytes = Join(Identity(kind).ToByteArray(), EpochBytes(23));
            Assert.True(Decode(bytes, out var decoded), "Enter/Leave只需要公共身份、Key和非零订阅代次。");
            Assert.That(decoded.Kind, Is.EqualTo(kind));
            Assert.That(Subscription(decoded), Is.EqualTo(23UL));
            Assert.That(decoded.Domain, Is.EqualTo(2u));
            Assert.That(decoded.ChunkX, Is.EqualTo(-3));
            Assert.That(decoded.ChunkY, Is.EqualTo(4));
            Assert.That(decoded.ChunkZ, Is.EqualTo(-5));
            AssertMaterialFieldsZero(decoded);
            var output = new byte[900];
            Assert.True(_codec.TryEncode(decoded, new ArraySegment<byte>(output), out int written));
            Assert.That(written, Is.EqualTo(bytes.Length));
            CollectionAssert.AreEqual(bytes, new ArraySegment<byte>(output, 0, written));
        }

        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(3u)]
        [TestCase(4u)]
        [TestCase(5u)]
        public void EveryMaterialKind_WithNonzeroSubscription_RoundTrips(uint kind)
        {
            byte[] bytes = Join(Material(kind).ToByteArray(), EpochBytes(ulong.MaxValue));
            Assert.True(Decode(bytes, out var decoded), "材料页、安装ACK、Resync和删除都必须携带同一订阅代次。");
            Assert.That(Subscription(decoded), Is.EqualTo(ulong.MaxValue));
            Assert.That(decoded.Kind, Is.EqualTo(kind));
            Assert.That(decoded.SnapshotId, Is.EqualTo(13UL));
            Assert.That(decoded.Generation, Is.EqualTo(5UL));
            Assert.That(decoded.Revision, Is.EqualTo(7UL));
            Assert.True(Encode(decoded, out _));
        }

        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(3u)]
        [TestCase(4u)]
        [TestCase(5u)]
        [TestCase(6u)]
        [TestCase(7u)]
        public void MissingSubscription_IsRejectedOnEncodeAndDecode(uint kind)
        {
            var page = kind <= 5 ? Material(kind) : Identity(kind);
            Assert.False(Encode(page, out _), "零代不能由本端发送，即使其他字段合法。");
            Assert.False(Decode(page.ToByteArray(), out _), "省略field21不能默认加入当前订阅。");
        }

        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(3u)]
        [TestCase(4u)]
        [TestCase(5u)]
        [TestCase(6u)]
        [TestCase(7u)]
        public void ExplicitZeroSubscription_IsRejected(uint kind)
        {
            var page = kind <= 5 ? Material(kind) : Identity(kind);
            Assert.False(Decode(Join(page.ToByteArray(), EpochBytes(0)), out _));
        }

        [TestCase(6u)]
        [TestCase(7u)]
        public void SubscriptionControl_CannotSmuggleAnyMaterialField(uint kind)
        {
            // 每次只污染一个字段，防止验证器只检查其中一个非零字段也让整组测试误过。
            string[] fields = { "SnapshotId", "Generation", "BaseRevision", "Revision", "PageIndex",
                "PageCount", "ServerTick", "Checksum", "WordOffset", "TotalWords", "Words" };
            foreach (string field in fields)
            {
                var page = Identity(kind);
                SetSubscription(page, 23);
                Assert.True(Encode(page, out _), "合法控制消息必须先通过，才能证明污染字段导致拒绝。");
                if (field == "Words") page.Words.Add(1);
                else
                {
                    var property = typeof(WorldPageMessage).GetProperty(field);
                    object value = property.PropertyType == typeof(ulong) ? (object)1UL : 1u;
                    property.SetValue(page, value);
                }
                Assert.False(Encode(page, out _), field + "不得借Enter/Leave夹带材料状态。");
                Assert.False(Decode(page.ToByteArray(), out _), field + "不得从wire进入订阅控制。");
            }
        }

        [TestCase("ProtocolVersion")]
        [TestCase("GameplayHash")]
        [TestCase("ConnectionEpoch")]
        [TestCase("WorldEpoch")]
        public void SubscriptionControl_RequiresEveryCommonIdentityField(string field)
        {
            var page = Identity(6);
            SetSubscription(page, 23);
            var property = typeof(WorldPageMessage).GetProperty(field);
            object zero = property.PropertyType == typeof(ulong) ? (object)0UL : 0u;
            property.SetValue(page, zero);
            Assert.False(Encode(page, out _));
            Assert.False(Decode(page.ToByteArray(), out _));
        }

        [TestCase(0u)]
        [TestCase(8u)]
        public void UnknownKind_IsRejected(uint kind)
        {
            var page = Identity(kind);
            Assert.False(Decode(Join(page.ToByteArray(), EpochBytes(23)), out _));
        }

        [Test]
        public void InvalidDomain_IsRejectedForSubscriptionControl()
        {
            var page = Identity(6);
            SetSubscription(page, 23);
            page.Domain = 4;
            Assert.False(Encode(page, out _));
            Assert.False(Decode(page.ToByteArray(), out _));
        }

        [Test]
        public void DuplicateSubscription_UnknownField_WrongWireAndTruncation_AreRejected()
        {
            byte[] valid = Join(Material(1).ToByteArray(), EpochBytes(23));
            Assert.True(Decode(valid, out _), "负例必须以能成功解码的真实页为基准。");
            Assert.False(Decode(Join(valid, EpochBytes(24)), out _), "重复field21不能后写覆盖订阅代次。");
            Assert.False(Decode(Join(valid, new byte[] { 0xB0, 0x01, 0x01 }), out _), "field22仍是未知字段。");
            Assert.False(Decode(Join(Material(1).ToByteArray(), new byte[] { 0xA8, 0x01, 0x17 }), out _), "field21必须是fixed64，不能接受varint。");
            Array.Resize(ref valid, valid.Length - 1);
            Assert.False(Decode(valid, out _), "fixed64不足8字节必须在Merge前拒绝。");
        }

        [TestCase(0u)]
        [TestCase(1u)]
        [TestCase(2u)]
        [TestCase(3u)]
        public void OldApplicationVersions_AreExplicitlyRejected(uint version)
        {
            Assert.False(ProtocolCompatibility.IsSupported(version), "旧端无法识别订阅生命周期，不允许忽略field21继续入场。");
            var page = Material(1);
            page.ProtocolVersion = version;
            Assert.False(Decode(Join(page.ToByteArray(), EpochBytes(23)), out _));
            SetSubscription(page, 23);
            Assert.False(Encode(page, out _));
        }

        [Test]
        public void ResetForReuse_ClearsSubscriptionAndUnknownFields()
        {
            byte[] bytes = Join(Join(Material(1).ToByteArray(), EpochBytes(23)), new byte[] { 0xB0, 0x01, 0x01 });
            var page = WorldPageMessage.Parser.ParseFrom(bytes);
            Assert.That(Subscription(page), Is.EqualTo(23UL));
            page.ResetForReuse();
            Assert.That(Subscription(page), Is.Zero);
            Assert.That(page.CalculateSize(), Is.Zero, "已知字段、预热Words有效长度和unknown fields均须清除。");
            Assert.That(page.Words.Count, Is.Zero);
        }

        [Test]
        public void DecodeReuse_FromMaterialToEnter_DoesNotRetainMaterialFields()
        {
            Assert.True(Decode(Join(Material(1).ToByteArray(), EpochBytes(23)), out var material));
            Assert.That(material.Words.Count, Is.EqualTo(1));
            Assert.True(Decode(Join(Identity(6).ToByteArray(), EpochBytes(24)), out var enter));
            Assert.That(Subscription(enter), Is.EqualTo(24UL));
            AssertMaterialFieldsZero(enter);
        }

        [Test]
        public void FullWorstCasePage_WithFixed64Subscription_StaysWithin900Bytes()
        {
            var page = Material(2);
            page.GameplayHash = page.WorldEpoch = page.SnapshotId = page.Generation = ulong.MaxValue;
            page.ConnectionEpoch = page.ServerTick = uint.MaxValue;
            page.ChunkX = page.ChunkZ = int.MinValue;
            page.ChunkY = int.MaxValue;
            page.BaseRevision = ulong.MaxValue - 1;
            page.Revision = ulong.MaxValue;
            page.Checksum = uint.MaxValue;
            page.PageIndex = 203;
            page.PageCount = 205;
            page.WordOffset = 32480;
            page.TotalWords = 32768;
            page.Words.Clear();
            for (int i = 0; i < 160; i++) page.Words.Add(uint.MaxValue);
            byte[] bytes = Join(page.ToByteArray(), EpochBytes(ulong.MaxValue));
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(900));
            Assert.True(Decode(bytes, out var decoded));
            Assert.That(Subscription(decoded), Is.EqualTo(ulong.MaxValue));
            Assert.That(decoded.Words.Count, Is.EqualTo(160));
            Assert.That(decoded.Words[159], Is.EqualTo(uint.MaxValue));
            Assert.That(decoded.ChunkX, Is.EqualTo(int.MinValue));
            Assert.That(decoded.ChunkY, Is.EqualTo(int.MaxValue));
            Assert.That(decoded.BaseRevision, Is.EqualTo(ulong.MaxValue - 1));
            var output = new byte[900];
            Assert.True(_codec.TryEncode(decoded, new ArraySegment<byte>(output), out int written));
            Assert.That(written, Is.EqualTo(bytes.Length));
            Assert.That(written, Is.LessThanOrEqualTo(900));
            Assert.False(_codec.TryEncode(decoded, new ArraySegment<byte>(new byte[written - 1]), out _));
        }

        private static void AssertMaterialFieldsZero(WorldPageMessage page)
        {
            Assert.That(page.SnapshotId, Is.Zero);
            Assert.That(page.Generation, Is.Zero);
            Assert.That(page.BaseRevision, Is.Zero);
            Assert.That(page.Revision, Is.Zero);
            Assert.That(page.PageIndex, Is.Zero);
            Assert.That(page.PageCount, Is.Zero);
            Assert.That(page.ServerTick, Is.Zero);
            Assert.That(page.Checksum, Is.Zero);
            Assert.That(page.WordOffset, Is.Zero);
            Assert.That(page.TotalWords, Is.Zero);
            Assert.That(page.Words.Count, Is.Zero);
        }
    }
}
