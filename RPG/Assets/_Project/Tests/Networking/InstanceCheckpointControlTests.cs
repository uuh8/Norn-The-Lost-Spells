using System;
using System.Globalization;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine.Networking;

namespace Game.Networking.Tests
{
    public sealed class InstanceCheckpointControlTests
    {
        [TestCase(0u)] [TestCase(1u)] [TestCase(2u)] [TestCase(3u)] [TestCase(4u)]
        public void ReadyMustExplicitlyAdvertiseTheMatchingCompleteCheckpointSchema(uint schema)
        {
            // 仅反射测试缺失接缝；不把false桩接入实际Ready，不临时禁用房间。
            var reply = new InstanceReadyReply(); var field = typeof(InstanceReadyReply).GetField("CheckpointSchemaVersion");
            if (field != null) field.SetValue(reply, schema);
            var method = typeof(InstanceControlHttp).GetMethod("SupportsCompleteCheckpoints");
            Assert.That(method, Is.Not.Null, "实时协议同号还需显式核对完整检查点Schema能力。");
            Assert.That((bool)method.Invoke(null, new object[] { reply }), Is.EqualTo(schema == OnlineRunAssetCheckpoint.CompleteSchemaVersion));
            Assert.That((bool)method.Invoke(null, new object[] { null }), Is.False);
        }
        private static InstanceLaunchDescriptor Launch(string url = "http://127.0.0.1:5080") => new InstanceLaunchDescriptor
            { InstanceId = new string('a', 32), BusinessBaseUrl = url, RoomEpoch = 7 };
        private static OnlineRunAssetCheckpoint Value() => new OnlineRunAssetCheckpoint
        {
            SchemaVersion = OnlineRunAssetCheckpoint.CompleteSchemaVersion, CheckpointId = new string('b', 32),
            InstanceId = new string('a', 32), RunId = new string('c', 32), RoomEpoch = 7, Sequence = 1,
            Members = new[] { new OnlineRunAssetCheckpointMember { PlayerId = new string('d', 32),
                Assets = new OnlineOwnedAssetSnapshot { Items = new[] { new OnlineOwnedItemSnapshot { InstanceId = "card", ContentId = "card.test" } } },
                Wands = new[] { new OnlineRunAssetCheckpointWandResource { InstanceId = "wand", CurrentMana = 12.25f } },
                SpellUses = Array.Empty<OnlineRunAssetCheckpointSpellUse>() } },
            World = new OnlineRunAssetCheckpointWorld { WorldId = "world", Chunks = Array.Empty<OnlineRunAssetCheckpointChunk>() },
            Flights = Array.Empty<OnlineRunAssetCheckpointFlight>(), MaterialOutputs = Array.Empty<OnlineRunAssetCheckpointMaterialOutput>(),
            LostMaterials = Array.Empty<OnlineRunAssetCheckpointLostMaterial>(),
            // 当前完整Schema4须明确声明没有Coin；缺失null仍应被生产编码器拒绝。
            Coins = Array.Empty<OnlineRunAssetCheckpointCoin>()
        };
        [Test] public void CompleteJsonKeepsNullOptionalStatesAndEscapesActualStringValues()
        {
            var value = Value(); value.World.WorldId = "world\"\\\n\t";
            Assert.That(InstanceCheckpointJson.TrySerialize(value, out var json, out var error), Is.True, error);
            StringAssert.Contains("\"WandStats\":null", json); StringAssert.Contains("\"ContainerState\":null", json);
            StringAssert.Contains("world\\\"\\\\\\n\\t", json);
            StringAssert.Contains("\"Flights\":[]", json);
        }
        [Test] public void FloatingPointWireTextIsInvariantAndTheFrozenBodyCannotChangeWithLaterDtoEdits()
        {
            var prior = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); var value = Value();
                Assert.That(InstanceCheckpointJson.TrySerialize(value, out var body, out var error), Is.True, error);
                StringAssert.Contains("\"CurrentMana\":12.25", body);
                value.Members[0].Wands[0].CurrentMana = 99;
                StringAssert.Contains("\"CurrentMana\":12.25", body);
            }
            finally { CultureInfo.CurrentCulture = prior; }
        }
        [Test] public void NonFiniteResourcesAreRejectedBeforeCreatingAnUpload()
        {
            var value = Value(); value.Members[0].Wands[0].CurrentMana = float.NaN;
            Assert.That(InstanceCheckpointJson.TrySerialize(value, out var body, out var error), Is.False);
            Assert.That(body, Is.Null); Assert.That(error, Is.Not.Null.And.Not.Empty);
        }
        [Test] public void PostAndQueriesUseOnlyTheAuthorisedInstanceAndEpochWithoutMakingANetworkCall()
        {
            const string secret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            using var post = InstanceControlHttp.CreateCheckpoint(Launch(), secret, "{}"); Assert.That(post, Is.Not.Null);
            Assert.That(post.method, Is.EqualTo("POST")); StringAssert.EndsWith("/checkpoints?roomEpoch=7", post.url);
            Assert.That(post.GetRequestHeader("X-Norn-Instance-Key"), Is.EqualTo(secret));
            Assert.That(post.uploadHandler.data, Is.EqualTo(new byte[] { 123, 125 }));
            using var receipt = InstanceControlHttp.QueryCheckpoint(Launch(), secret, new string('b', 32)); Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.method, Is.EqualTo("GET")); StringAssert.EndsWith("/checkpoints/" + new string('b', 32) + "?roomEpoch=7", receipt.url);
            using var head = InstanceControlHttp.ReadCheckpointHead(Launch(), secret); Assert.That(head, Is.Not.Null);
            StringAssert.EndsWith("/checkpoints/head?roomEpoch=7", head.url);
            // 没有SendWebRequest：本检查不向主业务服提交检查点，也不登录/开房间。
        }
        [TestCase("https://example.com")] [TestCase("http://192.168.1.10:5080")]
        public void CheckpointControlCannotSilentlyUseARemoteHost(string url)
        {
            Assert.Throws<ArgumentException>(() => InstanceControlHttp.CreateCheckpoint(Launch(url), new string('a', 64), "{}"));
            Assert.Throws<ArgumentException>(() => InstanceControlHttp.ReadCheckpointHead(Launch(url), new string('a', 64)));
        }
    }
}
