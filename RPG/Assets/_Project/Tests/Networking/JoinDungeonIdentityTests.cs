using System;
using System.Reflection;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Networking.Tests
{
    /// <summary>验证入场身份必须先匹配地图，再消费 Epoch；不启动 Mirror 连接或业务 HTTP。</summary>
    public sealed class JoinDungeonIdentityTests
    {
        [TestCase("")]
        [TestCase("../p7")]
        [TestCase("P7")]
        public void ConfiguringHandshakeRejectsMissingOrMalformedDungeon(string dungeon)
        {
            var root = new GameObject("JoinConfigTest"); root.SetActive(false);
            var auth = root.AddComponent<JoinTicketAuthenticator>();
            try
            {
                Assert.Throws<ArgumentException>(() => auth.ConfigureClient(new JoinTicketReply
                    { Ticket = new string('a', 64), DungeonId = dungeon }));
                Assert.Throws<ArgumentException>(() => auth.ConfigureServer(new InstanceLaunchDescriptor
                    { DungeonId = dungeon }, new string('a', 64)));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ClientCopiesExpectedIdentityWithoutDuplicatingTicketSecret()
        {
            var root = new GameObject("JoinExpectedIdentityTest"); root.SetActive(false);
            var auth = root.AddComponent<JoinTicketAuthenticator>();
            var ticket = new JoinTicketReply { Ticket = new string('a', 64), PlayerId = "player", RoomId = "room", RunId = "run", DungeonId = "p7" };
            try
            {
                auth.ConfigureClient(ticket); ticket.DungeonId = "p8";
                var expected = (JoinTicketReply)typeof(JoinTicketAuthenticator).GetField("_expected", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(auth);
                Assert.That(expected.DungeonId, Is.EqualTo("p7"), "HTTP DTO 重用不能改变在途握手已经绑定的地图。");
                Assert.That(expected.Ticket, Is.Empty, "期望身份副本只保存公开标识，不多保留一份票据。");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void AdmissionDungeonMustMatchBeforeConnectionEpochCanAdvance()
        {
            var rules = typeof(JoinTicketAuthenticator).Assembly.GetType("Game.Networking.JoinAdmissionRules");
            Assert.That(rules, Is.Not.Null, "业务回复的地图身份必须通过同一纯验证入口，不能只在 Client UI 比较。");
            var launch = new InstanceLaunchDescriptor { RoomId = "room", RunId = "run", RoomEpoch = 3 };
            var reply = new ConsumeJoinTicketReply { PlayerId = "player", RoomId = "room", RunId = "run", RoomEpoch = 3, ActorId = 1, ConnectionEpoch = 2 };
            SetDungeon(launch, "p7"); SetDungeon(reply, "p8");
            var epochs = new JoinConnectionEpochs();
            bool accepted = Server(rules, reply, launch, "player") && epochs.TryAdvance(reply.ActorId, reply.ConnectionEpoch);
            Assert.That(accepted, Is.False);
            SetDungeon(reply, "p7");
            Assert.That(Server(rules, reply, launch, "player") && epochs.TryAdvance(reply.ActorId, reply.ConnectionEpoch), Is.True,
                "错误地图回复不能提前占用正确回复的 Epoch。");
            Assert.That(epochs.TryAdvance(reply.ActorId, reply.ConnectionEpoch), Is.False, "同一确认回放不能重复接管 Actor。");
            SetDungeon(reply, ""); Assert.That(Server(rules, reply, launch, "player"), Is.False);
        }

        [Test]
        public void ClientConfirmationRequiresTicketDungeonAndCurrentProtocol()
        {
            var rules = typeof(JoinTicketAuthenticator).Assembly.GetType("Game.Networking.JoinAdmissionRules");
            Assert.That(rules, Is.Not.Null);
            var ticket = new JoinTicketReply { PlayerId = "player", RoomId = "room", RunId = "run", RoomEpoch = 3 };
            SetDungeon(ticket, "p8");
            var reply = new JoinTicketReplyMessage { ProtocolVersion = ProtocolCompatibility.CurrentVersion, Nonce = 9,
                PlayerId = "player", RoomId = "room", RunId = "run", RoomEpoch = 3, ActorId = 2, ConnectionEpoch = 1 };
            var field = JoinTicketReplyMessage.Descriptor.FindFieldByName("dungeon_id"); Assert.That(field, Is.Not.Null);
            field.Accessor.SetValue(reply, "p8"); Assert.That(Client(rules, reply, ticket, 9), Is.True);
            field.Accessor.SetValue(reply, "p7"); Assert.That(Client(rules, reply, ticket, 9), Is.False);
            field.Accessor.SetValue(reply, ""); Assert.That(Client(rules, reply, ticket, 9), Is.False);
            field.Accessor.SetValue(reply, "p8"); reply.ProtocolVersion = 1; Assert.That(Client(rules, reply, ticket, 9), Is.False);
            reply.ProtocolVersion = ProtocolCompatibility.CurrentVersion; Assert.That(Client(rules, reply, ticket, 10), Is.False, "迟到旧握手不能满足新请求 nonce。");
        }

        private static bool Server(Type rules, ConsumeJoinTicketReply reply, InstanceLaunchDescriptor launch, string player) =>
            (bool)rules.GetMethod("AcceptsServerReply", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { reply, launch, player });
        private static bool Client(Type rules, JoinTicketReplyMessage reply, JoinTicketReply expected, ulong nonce) =>
            (bool)rules.GetMethod("AcceptsClientReply", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { reply, expected, nonce });
        private static void SetDungeon(object value, string dungeon)
        {
            var field = value.GetType().GetField("DungeonId"); Assert.That(field, Is.Not.Null);
            field.SetValue(value, dungeon);
        }
    }
}
