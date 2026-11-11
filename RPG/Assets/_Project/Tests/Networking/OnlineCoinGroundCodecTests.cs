using System;
using System.IO;
using System.Reflection;
using Google.Protobuf;
using NUnit.Framework;
using UnityEngine;

namespace Game.Networking.Tests
{
    /// <summary>Coin是DS公开事实，不是客户端领取命令；测试经完整正式Codec，保留long与有界列表。</summary>
    public sealed class OnlineCoinGroundCodecTests
    {
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void RoundTripKeepsTwelveIdentitiesLongAmountsAndDeathPositions()
        {
            object snapshot = Snapshot(12); byte[] bytes = Encode(snapshot);
            Assert.That(Decode(bytes, out var decoded), Is.True);
            var coins = Coins(Get(decoded, "Ground"));
            Assert.That(coins.Length, Is.EqualTo(12));
            Assert.That(Get(coins.GetValue(11), "Id"), Is.EqualTo(16ul));
            Assert.That(Get(coins.GetValue(0), "Amount"), Is.EqualTo(long.MaxValue));
            Assert.That(Get(coins.GetValue(11), "Position"), Is.EqualTo(new Vector3(11, -2, 3)));
            Assert.That(Get(Get(decoded, "Assets"), "Gold"), Is.EqualTo(977L), "地面币值不能被当作Owner余额入账。");
        }

        [TestCase(0ul)] [TestCase(4ul)] [TestCase(17ul)] [TestCase(ulong.MaxValue)]
        public void EncodeRejectsIdentityOutsideFiniteNpcDomain(ulong id)
        { object value = Snapshot(); Change(value, "Id", id); Assert.That(TryEncode(value, out _), Is.False); }

        [TestCase(0L)] [TestCase(-1L)]
        public void EncodeRejectsNonPositiveAmount(long amount)
        { object value = Snapshot(); Change(value, "Amount", amount); Assert.That(TryEncode(value, out _), Is.False); }

        [Test]
        public void EncodeRejectsDuplicateAndThirteenthCoin()
        {
            object value = Snapshot(2); Change(value, "Id", 6ul);
            Assert.That(TryEncode(value, out _), Is.False);
            Assert.That(TryEncode(Snapshot(13), out _), Is.False);
        }

        [Test]
        public void EncodeRejectsNonFinitePosition()
        { object value = Snapshot(); Change(value, "Position", new Vector3(0, float.NaN, 0)); Assert.That(TryEncode(value, out _), Is.False); }

        [Test]
        public void DecodeRejectsUnknownDuplicateAndNegativeCoinWireBeforePublishing()
        {
            byte[] valid = Encode(Snapshot());
            var wire = Game.NetworkProtocol.InventorySnapshotMessage.Parser.ParseFrom(valid);
            object ground = wire.Ground;
            object collection = ground.GetType().GetProperty("Coins").GetValue(ground);
            object first = collection.GetType().GetProperty("Item").GetValue(collection, new object[] { 0 });
            byte[] original = ((IMessage)first).ToByteArray();
            foreach (byte[] invalid in new[] { Append(original, Scalar(6, 1)), Append(original, Scalar(1, 5)), ReplaceScalar(original, 2, ulong.MaxValue) })
            {
                // Parser只用于生成恶意字节，正式接收端必须在构造未知字段/嵌套对象前拒绝。
                // 保留原始恶意字节；MergeFrom再编码会合并重复标量，不能验证接收端Wire栅栏。
                byte[] changedGround = ReplaceNested(wire.Ground.ToByteArray(), 3, invalid);
                Assert.That(Decode(ReplaceNested(valid, 13, changedGround), out var decoded), Is.False); Assert.That(decoded, Is.Null);
            }
        }

        [Test]
        public void GroundResetAlsoClearsRetainedCoinAndUnknownFields()
        {
            var message = Game.NetworkProtocol.InventorySnapshotMessage.Parser.ParseFrom(Encode(Snapshot()));
            object ground = message.Ground;
            object list = ground.GetType().GetProperty("Coins").GetValue(ground);
            object coin = list.GetType().GetProperty("Item").GetValue(list, new object[] { 0 });
            ((IMessage)coin).MergeFrom(Scalar(100, 2));
            ground.GetType().GetMethod("ResetForReuse").Invoke(ground, null);
            Assert.That(((IMessage)coin).ToByteArray(), Is.Empty, "保留子引用也不能读到上一局金额/unknown fields。");
            Assert.That((int)list.GetType().GetProperty("Count").GetValue(list), Is.Zero);
        }

        private static object Snapshot(int count = 1)
        {
            object value = New("OnlineInventoryPresentationSnapshot"), assets = New("OnlineOwnedAssetSnapshot"), ground = New("OnlineGroundInventorySnapshot");
            Set(value, "SchemaVersion", 1u); Set(value, "ConnectionEpoch", 2u); Set(value, "SnapshotSequence", 1ul);
            Set(value, "ResourceVersion", 1u); Set(value, "SelectionVersion", 1u); Set(value, "SpellUseVersion", 1u);
            Set(assets, "SchemaVersion", 3u); Set(assets, "RunId", new string('a', 32)); Set(assets, "PlayerId", new string('b', 32));
            Set(assets, "InventoryId", "inventory"); Set(assets, "GameplayConfigHash", Hash); Set(assets, "ActorId", 1ul);
            Set(assets, "InventoryVersion", 1u); Set(assets, "SelectedWandInstanceId", ""); Set(assets, "Gold", 977L); Set(assets, "CurrencyVersion", 1u);
            Set(value, "Assets", assets); Set(ground, "Version", 1u);
            var array = Array.CreateInstance(Core("OnlineCoinSnapshot"), count);
            for (int i = 0; i < count; i++) { object coin = New("OnlineCoinSnapshot"); Set(coin, "Id", (ulong)(5 + i)); Set(coin, "Amount", long.MaxValue); Set(coin, "Position", new Vector3(i, -2, 3)); array.SetValue(coin, i); }
            Set(ground, "Coins", array); Set(value, "Ground", ground); return value;
        }
        private static void Change(object value, string field, object content)
        { Array values = Coins(Get(value, "Ground")); object coin = values.GetValue(0); Set(coin, field, content); values.SetValue(coin, 0); }
        private static Array Coins(object ground) => (Array)Get(ground, "Coins");
        private static Type Core(string name) { var type = Type.GetType("Game.Core." + name + ", Game.Core"); Assert.That(type, Is.Not.Null, "缺少中立Coin值合同"); return type; }
        private static object New(string name) => Activator.CreateInstance(Core(name));
        private static object Get(object value, string name) { var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, "缺少Coin字段:" + name); return field.GetValue(value); }
        private static void Set(object value, string name, object content) { var field = value.GetType().GetField(name); Assert.That(field, Is.Not.Null, "缺少Coin字段:" + name); field.SetValue(value, content); }
        private static bool TryEncode(object value, out byte[] bytes)
        { var codec = new InventorySnapshotCodec(); var args = new[] { value, null }; bool ok = (bool)typeof(InventorySnapshotCodec).GetMethod("TryEncode", new[] { Core("OnlineInventoryPresentationSnapshot"), typeof(byte[]).MakeByRefType() }).Invoke(codec, args); bytes = (byte[])args[1]; return ok; }
        private static byte[] Encode(object value) { Assert.That(TryEncode(value, out var bytes), Is.True); return bytes; }
        private static bool Decode(byte[] bytes, out object value)
        { var args = new object[] { new ArraySegment<byte>(bytes), null }; bool ok = (bool)typeof(InventorySnapshotCodec).GetMethod("TryDecode").Invoke(new InventorySnapshotCodec(), args); value = args[1]; return ok; }
        private static byte[] Scalar(int field, ulong value) { using var stream = new MemoryStream(); Varint(stream, (ulong)(field << 3)); Varint(stream, value); return stream.ToArray(); }
        private static byte[] Append(byte[] a, byte[] b) { byte[] result = new byte[a.Length+b.Length]; Array.Copy(a,result,a.Length); Array.Copy(b,0,result,a.Length,b.Length); return result; }
        private static byte[] ReplaceScalar(byte[] a, int field, ulong value)
        {
            int offset = 0;
            while(offset < a.Length) { int start=offset; ulong tag=Read(a,ref offset); int wire=(int)(tag&7); if(wire==0) Read(a,ref offset); else if(wire==2){int size=(int)Read(a,ref offset);offset+=size;}else offset+=wire==5?4:8;
                if((int)(tag>>3)!=field) continue; using var stream=new MemoryStream(); stream.Write(a,0,start); byte[] changed=Scalar(field,value); stream.Write(changed,0,changed.Length); stream.Write(a,offset,a.Length-offset); return stream.ToArray(); }
            throw new InvalidOperationException();
        }
        private static byte[] ReplaceNested(byte[] bytes, int field, byte[] payload)
        {
            int offset=0;
            while(offset<bytes.Length)
            {
                int start=offset;ulong tag=Read(bytes,ref offset);int wire=(int)(tag&7);
                if(wire==2)
                {
                    int length=(int)Read(bytes,ref offset);int end=offset+length;
                    if((int)(tag>>3)==field)
                    {
                        using var stream=new MemoryStream();stream.Write(bytes,0,start);Varint(stream,(ulong)(field<<3|2));
                        Varint(stream,(ulong)payload.Length);stream.Write(payload,0,payload.Length);stream.Write(bytes,end,bytes.Length-end);return stream.ToArray();
                    }
                    offset=end;
                }
                else if(wire==0)Read(bytes,ref offset);else offset+=wire==5?4:8;
            }
            throw new InvalidOperationException();
        }
        private static ulong Read(byte[] bytes, ref int at) { ulong value=0;for(int bit=0;bit<64;bit+=7){byte b=bytes[at++];value|=(ulong)(b&127)<<bit;if((b&128)==0)return value;}throw new InvalidOperationException(); }
        private static void Varint(Stream stream, ulong value) { while(value>=128){stream.WriteByte((byte)(value|128));value>>=7;}stream.WriteByte((byte)value); }
    }
}
