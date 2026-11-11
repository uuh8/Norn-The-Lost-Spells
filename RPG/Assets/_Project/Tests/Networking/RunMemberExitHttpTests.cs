using System;
using System.Reflection;
using System.Text;
using Game.Networking;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine.Networking;

namespace Game.Networking.Tests
{
    /// <summary>只创建/Dispose本地请求对象并检查编码，不SendWebRequest，不启动业务服或读取真实凭据。</summary>
    public sealed class RunMemberExitHttpTests
    {
        private const string Instance = "11111111111111111111111111111111", Settlement = "22222222222222222222222222222222";
        private static readonly string Secret = new string('s', 64);
        private static InstanceLaunchDescriptor Launch(string origin = "http://127.0.0.1:5080")
            => new InstanceLaunchDescriptor { InstanceId = Instance, RoomEpoch = 7, BusinessBaseUrl = origin };
        private static Type Adapter()
        {
            var type = typeof(InstanceControlHttp).Assembly.GetType("Game.Networking.RunMemberExitHttp");
            Assert.That(type, Is.Not.Null, "个人退出需独立最终提交/查询Adapter，不能把Heartbeat成功当结算。"); return type;
        }
        private static UnityWebRequest Create(string method, params object[] args)
        { var found = Adapter().GetMethod(method); Assert.That(found, Is.Not.Null); return (UnityWebRequest)found.Invoke(null, args); }
        private static bool Serialize(OnlineRunMemberExitRequest value, out string json, out string error)
        {
            var method = typeof(InstanceCheckpointJson).GetMethod("TrySerializeMemberExit"); Assert.That(method, Is.Not.Null);
            object[] args = { value, null, null }; bool result = (bool)method.Invoke(null, args);
            json = args[1] as string; error = args[2] as string; return result;
        }

        [Test] public void PostUsesInstanceIdentityFixedBodyAndEightSecondBound()
        {
            const string json = "{\"SchemaVersion\":1}";
            using var request = Create("Create", Launch(), Secret, json);
            Assert.That(request.url, Is.EqualTo("http://127.0.0.1:5080/api/instances/" + Instance + "/member-exits?roomEpoch=7"));
            Assert.That(request.method, Is.EqualTo("POST")); Assert.That(request.timeout, Is.EqualTo(8));
            Assert.That(request.redirectLimit, Is.Zero);
            Assert.That(request.GetRequestHeader("X-Norn-Instance-Key"), Is.EqualTo(Secret));
            Assert.That(request.GetRequestHeader("Authorization"), Is.Null);
            Assert.That(Encoding.UTF8.GetString(request.uploadHandler.data), Is.EqualTo(json));
            Assert.That(request.GetRequestHeader("Content-Type"), Is.EqualTo("application/json"));
        }
        [Test] public void QueryUsesOriginalSettlementAndSameRoomEpochWithoutUpload()
        {
            using var request = Create("Query", Launch("https://localhost:5080"), Secret, Settlement);
            Assert.That(request.url, Is.EqualTo("https://localhost:5080/api/instances/" + Instance + "/member-exits/" + Settlement + "?roomEpoch=7"));
            Assert.That(request.method, Is.EqualTo("GET")); Assert.That(request.uploadHandler, Is.Null);
            Assert.That(request.GetRequestHeader("X-Norn-Instance-Key"), Is.EqualTo(Secret));
        }
        [TestCase("https://example.com")] [TestCase("http://192.168.1.2:5080")]
        public void InstanceSecretCannotBeSentToNonLoopbackOrigin(string origin)
        {
            var exception = Assert.Throws<TargetInvocationException>(() => Create("Query", Launch(origin), Secret, Settlement));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }
        [Test] public void InvalidQueryIdIsRejectedInsteadOfBecomingUriSuffix()
        {
            var exception = Assert.Throws<TargetInvocationException>(() => Create("Query", Launch(), Secret, "../checkpoints/head"));
            Assert.That(exception.InnerException, Is.TypeOf<ArgumentException>());
        }
        [Test] public void SerializerPreservesRealLongGoldNullStateAndCallerCandidate()
        {
            var candidate = Candidate(); candidate.Assets.Gold = long.MaxValue;
            Assert.That(Serialize(candidate, out var json, out var error), Is.True, error);
            StringAssert.Contains("\"Gold\":9223372036854775807", json);
            StringAssert.Contains("\"WandStats\":null", json);
            Assert.That(candidate.Assets.Items[0].WandStats, Is.Null);
            Assert.That(candidate.Assets.Gold, Is.EqualTo(long.MaxValue));
        }
        [Test] public void PeacefulExitSerializesExplicitRealRetirementsWithoutTreatingThemAsFinalDeath()
        {
            var candidate = Candidate(); candidate.PracticeSessionScope = new string('a', 64);
            candidate.PracticeContinuityJson = "{\"Encoding\":\"gzip-base64\"}";
            candidate.PracticeContinuityHash = new string('b', 64);
            candidate.FinalRetiredItems = new[] { new OnlineRunAssetCheckpointRetired
                { InstanceId = new string('c', 32), ContentId = "spent.card", Revision = 7, Reason = OnlineRunAssetRetiredReason.Consumed } };
            Assert.That(Serialize(candidate, out var json, out var error), Is.True, error);
            StringAssert.Contains("\"PracticeGroundItems\":[]", json);
            StringAssert.Contains("\"Reason\":1", json);
            StringAssert.Contains("\"ContentId\":\"spent.card\"", json);
            Assert.That(candidate.Reason, Is.EqualTo(OnlineRunMemberExitReason.VoluntaryExit));
        }
        [Test] public void SerializerRejectsMissingOwnedOrOldSchemaWithoutPublishingPartialBody()
        {
            var candidate = Candidate(); candidate.SchemaVersion = 0;
            Assert.That(Serialize(candidate, out var json, out var error), Is.False);
            Assert.That(json, Is.Null); Assert.That(error, Is.Not.Empty);
            candidate.SchemaVersion = OnlineRunMemberExitRequest.CurrentSchemaVersion; candidate.Assets = null;
            Assert.That(Serialize(candidate, out json, out _), Is.False); Assert.That(json, Is.Null);
        }
        [Test] public void SerializerRejectsInvalidUnicodeAndUsesExistingControlBudget()
        {
            var candidate = Candidate(); candidate.Assets.Items[0].ContentId = "\ud800";
            Assert.That(Serialize(candidate, out var json, out var error), Is.False);
            Assert.That(json, Is.Null); Assert.That(error, Is.EqualTo("RunMemberExitInvalidUnicode"));
            Assert.That(OnlineRunMemberExitRequest.MaximumPayloadBytes, Is.EqualTo(InstanceCheckpointJson.MaximumUtf8Bytes));
        }
        private static OnlineRunMemberExitRequest Candidate() => new OnlineRunMemberExitRequest
        {
            SchemaVersion = OnlineRunMemberExitRequest.CurrentSchemaVersion, SettlementId = Settlement, InstanceId = Instance,
            RunId = "33333333333333333333333333333333", PlayerId = "44444444444444444444444444444444", ActorId = 1,
            RoomEpoch = 7, ConnectionEpoch = 8, ExpectedPersistentVersion = 3,
            Reason = OnlineRunMemberExitReason.VoluntaryExit,
            FinalRetiredItems = Array.Empty<OnlineRunAssetCheckpointRetired>(),
            FinalDeathLostMaterials = Array.Empty<OnlineOwnedMaterialAmountSnapshot>(),
            PracticeGroundItems = Array.Empty<OnlineRunAssetCheckpointGround>(),
            Assets = new OnlineOwnedAssetSnapshot { SchemaVersion = OnlineOwnedAssetSnapshot.CurrentSchemaVersion,
                Gold = 0, CurrencyVersion = 1, Items = new[] { new OnlineOwnedItemSnapshot { InstanceId = "55555555555555555555555555555555", ContentId = "valid.item" } } }
        };
    }
}
