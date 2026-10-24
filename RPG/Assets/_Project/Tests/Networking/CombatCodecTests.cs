using System;
using System.Reflection;
using Game.NetworkProtocol;
using Google.Protobuf;
using NUnit.Framework;

namespace Game.Networking.Tests
{
    public sealed class CombatCodecTests
    {
        private static object Create()
        {
            var type = typeof(MovementCodec).Assembly.GetType("Game.Networking.CombatCodec");
            Assert.That(type, Is.Not.Null, "缺少有界战斗协议解码器。");
            return Activator.CreateInstance(type);
        }
        private static bool Decode(object codec, byte[] bytes, string suffix, out object value)
        {
            var args = new object[] { new ArraySegment<byte>(bytes), null };
            bool result = (bool)codec.GetType().GetMethod("TryDecode" + suffix).Invoke(codec, args);
            value = args[1]; return result;
        }
        private static CombatCastIntentMessage Intent() => new CombatCastIntentMessage
        {
            ProtocolVersion = 1, ConnectionEpoch = 1, CommandId = 1, GameplayHash = 123,
            WandInstanceId = new string('a', 32), ProgramRevision = 1, AimZ = 20
        };
        [Test] public void ValidIntent_RoundTripsWithoutTrustingActorOrDamageFields()
        {
            var codec = Create(); Assert.That(Decode(codec, Intent().ToByteArray(), "Intent", out var value), Is.True);
            Assert.That(((CombatCastIntentMessage)value).AimZ, Is.EqualTo(20));
        }
        [Test] public void UnknownField_IsRejectedBeforeItAllocatesUnknownFields()
        {
            var codec = Create(); byte[] original = Intent().ToByteArray(); var bytes = new byte[original.Length + 2];
            Array.Copy(original, bytes, original.Length); bytes[original.Length] = 120; bytes[original.Length + 1] = 1;
            Assert.That(Decode(codec, bytes, "Intent", out _), Is.False);
        }
        [Test] public void DuplicateCommandField_IsRejectedInsteadOfLastWins()
        {
            var codec = Create(); byte[] original = Intent().ToByteArray(); var bytes = new byte[original.Length + 9];
            Array.Copy(original, bytes, original.Length); bytes[original.Length] = 25; bytes[original.Length + 1] = 2;
            Assert.That(Decode(codec, bytes, "Intent", out _), Is.False);
        }
        [Test] public void NonFiniteAim_IsRejected()
        {
            var codec = Create(); var message = Intent(); message.AimX = float.NaN;
            Assert.That(Decode(codec, message.ToByteArray(), "Intent", out _), Is.False);
        }
        [Test] public void MissingEpochOrVersion_IsRejected()
        {
            var codec = Create(); var message = Intent(); message.ConnectionEpoch = 0;
            Assert.That(Decode(codec, message.ToByteArray(), "Intent", out _), Is.False);
            message.ConnectionEpoch = 1; message.ProtocolVersion = 0;
            Assert.That(Decode(codec, message.ToByteArray(), "Intent", out _), Is.False);
        }
        [Test] public void OversizedOrNonIdentityString_IsRejected()
        {
            var codec = Create(); var message = Intent(); message.WandInstanceId = new string('a', 65);
            Assert.That(Decode(codec, message.ToByteArray(), "Intent", out _), Is.False);
            message.WandInstanceId = "Assets/Untrusted.prefab";
            Assert.That(Decode(codec, message.ToByteArray(), "Intent", out _), Is.False);
        }
        [Test] public void SpawnWithoutWhitelistedPrefabKey_IsRejected()
        {
            var codec = Create(); var message = Fact(); message.Kind = 4; message.OutputId = 1;
            Assert.That(Decode(codec, message.ToByteArray(), "Fact", out _), Is.False);
            message.PrefabKey = 1; message.RotationW = 1;
            Assert.That(Decode(codec, message.ToByteArray(), "Fact", out _), Is.True);
        }
        [Test] public void Reuse_ClearsFieldsOmittedFromNextFact()
        {
            var codec = Create(); var first = Fact(); first.Kind = 1; first.WandInstanceId = new string('a', 32); first.ProgramRevision = 2;
            Assert.That(Decode(codec, first.ToByteArray(), "Fact", out var borrowed), Is.True);
            var next = Fact(); next.Kind = 7; next.MaxHp = next.CurrentHp = 100; next.MoveMultiplier = 1;
            Assert.That(Decode(codec, next.ToByteArray(), "Fact", out var second), Is.True);
            Assert.That(ReferenceEquals(borrowed, second), Is.True, "接收消息复用同一对象；调用方只能借到下一次解码。");
            Assert.That(((CombatFactMessage)second).WandInstanceId, Is.Empty);
            Assert.That(((CombatFactMessage)second).ProgramRevision, Is.Zero);
        }
        private static CombatFactMessage Fact() => new CombatFactMessage
        { ProtocolVersion = 1, GameplayHash = 123, Sequence = 1, ServerTick = 1, ActorId = 1, ConnectionEpoch = 1 };
        [Test] public void ActorSourceAndNegativeAnimatorHashPreserveTheirBitsAndReset()
        {
            var codec = Create(); var fact = Fact(); fact.Kind = 3; fact.AttackerActorId = 7;
            fact.AnimationHash = unchecked((uint)-123456789); fact.ActionKind = 1; fact.DamageType = 2;
            Assert.That(Decode(codec, fact.ToByteArray(), "Fact", out var first), Is.True);
            Assert.That(unchecked((int)((CombatFactMessage)first).AnimationHash), Is.EqualTo(-123456789));
            Assert.That(((CombatFactMessage)first).AttackerActorId, Is.EqualTo(7));
            fact.ResetForReuse(); fact = Fact(); fact.Kind = 9;
            Assert.That(Decode(codec, fact.ToByteArray(), "Fact", out var second), Is.True);
            Assert.That(((CombatFactMessage)second).AnimationHash, Is.Zero);
            Assert.That(((CombatFactMessage)second).AttackerActorId, Is.Zero);
        }
    }
}
