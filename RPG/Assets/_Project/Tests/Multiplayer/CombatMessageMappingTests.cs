using System;
using System.Reflection;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using Google.Protobuf;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    /// <summary>点击坐标和实例身份必须原样映射；复用消息不能携带上次解析留下的未知权限字段。</summary>
    public sealed class CombatMessageMappingTests
    {
        private static Type MappingType()
        {
            var type = typeof(MovementMessageMapping).Assembly.GetType("Game.Multiplayer.CombatMessageMapping");
            Assert.That(type, Is.Not.Null, "缺少 Core 施法意图到正式协议的映射。");
            return type;
        }

        private static void Write(in OnlineCastIntent intent, ulong hash, CombatCastIntentMessage message)
        {
            var method = MappingType().GetMethod("Write", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            method.Invoke(null, new object[] { intent, hash, message });
        }

        private static OnlineCastIntent Read(CombatCastIntentMessage message)
        {
            var method = MappingType().GetMethod("Read", BindingFlags.Public | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (OnlineCastIntent)method.Invoke(null, new object[] { message });
        }

        private static OnlineCastIntent Intent() => new OnlineCastIntent
        {
            CommandId = 0xfedcba9876543210UL, ConnectionEpoch = 19, ProgramRevision = 37,
            WandInstanceId = "0123456789abcdef0123456789abcdef",
            ClaimedOrigin = new Vector3(-7.25f, 1.125f, 0f), AimPoint = new Vector3(0f, -5.75f, 123.5f)
        };

        [Test]
        public void Intent_RoundTripsExactCommandAndClickInstantWorldCoordinates()
        {
            var intent = Intent(); var message = new CombatCastIntentMessage();
            const ulong hash = 0x123456789abcdef0UL;
            Write(intent, hash, message);
            Assert.That(message.ProtocolVersion, Is.EqualTo(ProtocolCompatibility.CurrentVersion));
            Assert.That(message.GameplayHash, Is.EqualTo(hash));
            var codec = new CombatCodec(); var bytes = new byte[CombatCodec.MaxPayloadBytes];
            Assert.That(codec.TryEncodeIntent(message, new ArraySegment<byte>(bytes), out int count), Is.True);
            Assert.That(codec.TryDecodeIntent(new ArraySegment<byte>(bytes, 0, count), out var decoded), Is.True);
            var restored = Read(decoded);
            Assert.That(restored.CommandId, Is.EqualTo(0xfedcba9876543210UL));
            Assert.That(restored.ConnectionEpoch, Is.EqualTo(19));
            Assert.That(restored.ProgramRevision, Is.EqualTo(37));
            Assert.That(restored.WandInstanceId, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(restored.ClaimedOrigin, Is.EqualTo(new Vector3(-7.25f, 1.125f, 0f)));
            Assert.That(restored.AimPoint, Is.EqualTo(new Vector3(0f, -5.75f, 123.5f)));
        }

        [Test]
        public void Read_CopiesValuesBeforeBorrowedProtobufObjectIsReused()
        {
            var message = new CombatCastIntentMessage(); Write(Intent(), 7, message);
            var captured = Read(message);
            message.ResetForReuse(); message.CommandId = 1; message.OriginX = 999; message.AimZ = -999;
            Assert.That(captured.CommandId, Is.EqualTo(0xfedcba9876543210UL));
            Assert.That(captured.WandInstanceId, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(captured.ClaimedOrigin.x, Is.EqualTo(-7.25f));
            Assert.That(captured.AimPoint.z, Is.EqualTo(123.5f));
        }

        [Test]
        public void Write_ClearsUnknownAuthorityFieldsInsteadOfForwardingThem()
        {
            var message = new CombatCastIntentMessage();
            // 未声明的 field 99 模拟夹带 Actor/伤害权限。正式 Codec 拒绝它，Mapper 复用时必须彻底 Reset。
            message.MergeFrom(new byte[] { 0x98, 0x06, 0x63 });
            var codec = new CombatCodec();
            Assert.That(codec.TryDecodeIntent(new ArraySegment<byte>(message.ToByteArray()), out _), Is.False);
            Write(Intent(), 7, message);
            var bytes = new byte[CombatCodec.MaxPayloadBytes];
            Assert.That(codec.TryEncodeIntent(message, new ArraySegment<byte>(bytes), out int count), Is.True,
                "Mapping 不能把 pooled UnknownFieldSet 带入新的合法命令。");
            Assert.That(codec.TryDecodeIntent(new ArraySegment<byte>(bytes, 0, count), out var accepted), Is.True);
            Assert.That(Read(accepted).CommandId, Is.EqualTo(0xfedcba9876543210UL));
        }
    }
}
