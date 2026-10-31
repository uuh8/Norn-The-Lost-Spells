using System;
using System.Reflection;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    /// <summary>进度是Run级稀疏事实，不复用某玩家连接Epoch。身份/Hash在安装前独立核对。</summary>
    public sealed class DungeonProgressCodecTests
    {
        private const string Run = "11111111111111111111111111111111";
        private Type _type, _progress;
        private object _codec;
        [SetUp] public void RequireBoundedProgressCodec()
        {
            _type = typeof(MovementCodec).Assembly.GetType("Game.Networking.DungeonProgressCodec");
            Assert.That(_type, Is.Not.Null); _codec = Activator.CreateInstance(_type);
            _progress = Type.GetType("Game.Core.OnlineDungeonProgress, Game.Core"); Assert.That(_progress, Is.Not.Null);
        }

        [TestCase(1)][TestCase(2)][TestCase(3)] public void ProgressRoundTripsWithoutClientAuthority(int phase)
        {
            byte[] bytes = Encode(Progress(phase)); Assert.That(Decode(bytes, Run, "p7", 1234, out var read), Is.True);
            Assert.That(_progress.GetField("Revision").GetValue(read), Is.EqualTo(9u));
            Assert.That(_progress.GetField("EncounterIndexPlusOne").GetValue(read), Is.EqualTo(4u));
        }
        [Test] public void WrongRunDungeonOrGameplayCannotInstallAnyProgress()
        {
            byte[] bytes = Encode(Progress(2));
            Assert.That(Decode(bytes, new string('2',32), "p7", 1234, out _), Is.False);
            Assert.That(Decode(bytes, Run, "p8", 1234, out _), Is.False);
            Assert.That(Decode(bytes, Run, "p7", 1235, out _), Is.False);
        }
        [Test] public void OversizeTruncationUnknownAndDuplicateFieldsAreRejected()
        {
            byte[] bytes = Encode(Progress(2)); Assert.That(Decode(new byte[257], Run, "p7", 1234, out _), Is.False);
            var truncated = new byte[bytes.Length - 1]; Array.Copy(bytes,truncated,truncated.Length);
            Assert.That(Decode(truncated,Run,"p7",1234,out _),Is.False);
            Assert.That(Decode(Append(bytes,new byte[]{8,2}),Run,"p7",1234,out _),Is.False);
            Assert.That(Decode(Append(bytes,new byte[]{80,1}),Run,"p7",1234,out _),Is.False);
        }
        [TestCase("Revision",0u)][TestCase("ServerTick",0u)][TestCase("EncounterIndexPlusOne",0u)]
        [TestCase("EncounterIndexPlusOne",5u)][TestCase("RemainingEnemies",13u)]
        public void InvalidProgressCannotBeEncoded(string field,uint value)
        {
            var data=Progress(2);_progress.GetField(field).SetValue(data,value);
            var args=new object[]{data,Run,"p7",1234ul,new ArraySegment<byte>(new byte[256]),0};
            Assert.That(_type.GetMethod("TryEncode").Invoke(_codec,args),Is.False);
        }
        private object Progress(int phase)
        {
            var data=Activator.CreateInstance(_progress);
            _progress.GetField("Revision").SetValue(data,9u);_progress.GetField("ServerTick").SetValue(data,27u);
            _progress.GetField("EncounterIndexPlusOne").SetValue(data,4u);
            _progress.GetField("RemainingEnemies").SetValue(data,phase==2?2u:0u);
            var info=_progress.GetField("Phase");info.SetValue(data,Enum.ToObject(info.FieldType,phase));return data;
        }
        private byte[] Encode(object value)
        {
            var buffer=new byte[256];var args=new object[]{value,Run,"p7",1234ul,new ArraySegment<byte>(buffer),0};
            Assert.That(_type.GetMethod("TryEncode").Invoke(_codec,args),Is.True);var bytes=new byte[(int)args[5]];Array.Copy(buffer,bytes,bytes.Length);return bytes;
        }
        private bool Decode(byte[] bytes,string run,string dungeon,ulong hash,out object data)
        {var args=new object[]{new ArraySegment<byte>(bytes),run,dungeon,hash,null};bool ok=(bool)_type.GetMethod("TryDecode").Invoke(_codec,args);data=args[4];return ok;}
        private static byte[] Append(byte[] a,byte[] b){var c=new byte[a.Length+b.Length];Array.Copy(a,c,a.Length);Array.Copy(b,0,c,a.Length,b.Length);return c;}
    }
}
