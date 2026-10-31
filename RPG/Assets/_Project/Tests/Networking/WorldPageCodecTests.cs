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
                ConnectionEpoch=1,WorldEpoch=7,SnapshotId=1,Kind=1,Domain=1,ChunkX=int.MinValue,
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
            expanded[b.Length]=0xA8;expanded[b.Length+1]=1;expanded[b.Length+2]=1;
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
}
