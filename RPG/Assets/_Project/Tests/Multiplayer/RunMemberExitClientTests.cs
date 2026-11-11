using System;
using System.Reflection;
using Game.Core;
using Game.Networking;
using Game.NetworkProtocol;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>
    /// 固定退出命令与当前连接回包的本机关联。仅驱动纯结果接收边界，不启动网络、场景或输入。
    /// 未知/迟到响应不得把“正在保存”伪装成可以返回Hub的可靠成功。
    /// </summary>
    public sealed class RunMemberExitClientTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Command = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        private GameObject _root;
        private OnlineRoomClient _client;

        [SetUp] public void CreateInactiveClient()
        {
            _root = new GameObject("Inactive personal exit client fixture"); _root.SetActive(false);
            _client = _root.AddComponent<OnlineRoomClient>();
        }
        [TearDown] public void ReleaseClient() { if (_root != null) Object.DestroyImmediate(_root); }

        private void BeginPending()
        {
            SetClient("_exitCommandId", Command); SetClient("_exitEpoch", 7u); SetClient("_exitCommitted", false);
            typeof(OnlineRoomClient).GetField("_phase", Private).SetValue(_client, OnlineRoomPhase.Connected);
        }
        private void SetClient(string field, object value)
        {
            var found = typeof(OnlineRoomClient).GetField(field, Private);
            Assert.That(found, Is.Not.Null, "缺少固定退出请求本地状态：" + field);
            found.SetValue(_client, value);
        }
        private bool Committed => (bool)typeof(OnlineRoomClient).GetField("_exitCommitted", Private).GetValue(_client);

        private static object Status(string phase, string command = Command, uint epoch = 7, uint version = 10, string hash = Hash)
        {
            var type = typeof(GameLog).Assembly.GetType("Game.Core.OnlineRunExitStatus");
            Assert.That(type, Is.Not.Null, "缺少中立个人退出结果，不能复用普通Disconnect作为保存成功。");
            object value = Activator.CreateInstance(type);
            Set(value, "CommandId", command); Set(value, "ConnectionEpoch", epoch);
            var phaseField = type.GetField("Phase"); Assert.That(phaseField, Is.Not.Null);
            phaseField.SetValue(value, Enum.Parse(phaseField.FieldType, phase));
            var reasonField = type.GetField("Reason"); reasonField.SetValue(value, Enum.Parse(reasonField.FieldType, "VoluntaryExit"));
            Set(value, "CommittedVersion", version); Set(value, "PayloadHash", hash);
            return value;
        }
        private static void Set(object value, string field, object content)
        {
            var found = value.GetType().GetField(field); Assert.That(found, Is.Not.Null, field);
            found.SetValue(value, content);
        }
        private bool Apply(object status)
        {
            var method = typeof(OnlineRoomClient).GetMethod("TryApplyMemberExitStatus", Private);
            Assert.That(method, Is.Not.Null, "缺少保存确认边界；旧Cancel不得直接StopConnection/返Hub。");
            return (bool)method.Invoke(_client, new[] { status });
        }

        [TestCase("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 7u)]
        [TestCase(Command, 6u)]
        public void DifferentCommandOrPreviousTransportEpochCannotConfirmExit(string command, uint epoch)
        {
            BeginPending(); Assert.That(Apply(Status("Committed", command, epoch)), Is.False);
            Assert.That(Committed, Is.False); Assert.That(_client.Phase, Is.EqualTo(OnlineRoomPhase.Connected));
        }

        [TestCase("Draining")]
        [TestCase("Submitting")]
        [TestCase("Uncertain")]
        [TestCase("Rejected")]
        public void NonCommittedResultCannotUnlockAssetsOrReturnToHub(string phase)
        {
            BeginPending(); Apply(Status(phase, version: 0, hash: ""));
            Assert.That(Committed, Is.False);
            Assert.That(_client.Phase, Is.EqualTo(OnlineRoomPhase.Connected));
            Assert.That(typeof(OnlineRoomClient).GetField("_exitCommandId", Private).GetValue(_client), Is.EqualTo(Command),
                "失败/未知不能换ID，以免丢响应后再次执行最终提交。");
        }

        [TestCase(0u, Hash)]
        [TestCase(10u, "")]
        public void ClaimedSuccessWithoutFullReceiptIsNotCommitted(uint version, string hash)
        {
            BeginPending(); Assert.That(Apply(Status("Committed", version: version, hash: hash)), Is.False);
            Assert.That(Committed, Is.False);
        }

        [Test] public void CommittedReceiptIsStableAgainstLatePendingAndDoesNotChangeCommandId()
        {
            BeginPending(); Assert.That(Apply(Status("Committed")), Is.True); Assert.That(Committed, Is.True);
            Apply(Status("Draining", version: 0, hash: ""));
            Assert.That(Committed, Is.True, "迟到旧阶段不能把已确认保存回退为未提交。");
            Assert.That(typeof(OnlineRoomClient).GetField("_exitCommandId", Private).GetValue(_client), Is.EqualTo(Command));
        }

        [TestCase("11111111111111111111111111111111", true)]
        [TestCase("22222222222222222222222222222222", false)]
        public void ActualStatusHandlerKeepsTheAccountThatBeganExitWhenCurrentSessionChanges(string currentPlayer, bool expectedCommit)
        {
            const string originalPlayer = "11111111111111111111111111111111";
            var account = _root.AddComponent<OnlineAccountClient>();
            var auth = _root.AddComponent<JoinTicketAuthenticator>();
            var accountView = typeof(OnlineAccountClient).GetField("_view", Private);
            accountView.SetValue(account, new OnlineProfileReply { PlayerId = originalPlayer, Version = 9 });
            typeof(JoinTicketAuthenticator).GetProperty("ClientAuthenticated").GetSetMethod(true).Invoke(auth, new object[] { true });
            typeof(JoinTicketAuthenticator).GetProperty("ClientIdentity").GetSetMethod(true).Invoke(auth, new object[]
            { new JoinConnectionIdentity(originalPlayer, "room", "run", 1, 7, 1, "p7") });
            SetClient("_account", account); SetClient("_auth", auth);
            SetClient("_room", new RoomReply { RoomId = "room", RunId = "run", RoomEpoch = 1, Members = Array.Empty<RoomMemberReply>() });
            SetClient("_phase", OnlineRoomPhase.Connected);
            // 走真正Begin入口建立A的上下文；无连接/无HTTP，不造资产或启动网络。
            typeof(OnlineRoomClient).GetMethod("BeginMemberExit", Private).Invoke(_client, null);
            SetClient("_exitCommandId", Command); SetClient("_exitEpoch", 7u);
            accountView.SetValue(account, new OnlineProfileReply { PlayerId = currentPlayer, Version = 9 });
            var changed = new OnlineRunExitStatusChangedEvent { Status = (OnlineRunExitStatus)Status("Committed") };
            typeof(OnlineRoomClient).GetMethod("OnMemberExitStatus", Private).Invoke(_client, new object[] { changed });
            Assert.That(Committed, Is.EqualTo(expectedCommit), "A的退出不能由当前B会话接受并继续安装B的Profile。");
            Assert.That(_client.Phase, Is.EqualTo(OnlineRoomPhase.Connected));
            Assert.That(typeof(OnlineRoomClient).GetField("_exitHubReady", Private).GetValue(_client), Is.False);
            Assert.That(typeof(OnlineRoomClient).GetField("_exitCommandId", Private).GetValue(_client), Is.EqualTo(Command),
                "换账号不得自动换候选或清除原退出请求。");
        }
    }
}
