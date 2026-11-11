using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Multiplayer.Tests
{
    public sealed class OnlineRuntimeRoleTests
    {
        [Test] public void MissingMovementWorldCannotReportInstanceReady()
        {
            Assert.False(RoomInstanceBootstrap.IsMovementWorldReady(null),
                "端口已监听不代表有Authority；漏装世界必须阻止业务Ready和入局。");
        }
        [Test] public void ExplicitInterestProbe_InvalidThenHomeRecovers()
        {
            string error = "previous diagnostic error";
            Assert.That(ResolveProbe("invalid", true, ref error, out var offset), Is.False);
            Assert.That(error, Is.EqualTo("Unknown explicit interest diagnostic command"));
            Assert.That(offset, Is.EqualTo(Vector3.zero));
            Assert.That(ResolveProbe("home", true, ref error, out offset), Is.True);
            Assert.That(error, Is.Null, "前一次非法请求不能阻止后续合法home，也不能重启夹具才能恢复。");
            Assert.That(offset, Is.EqualTo(Vector3.zero));
        }

        [Test] public void ExplicitInterestProbe_NoAuthThenAuthenticatedAwayRecovers()
        {
            string error = null;
            Assert.That(ResolveProbe("away", false, ref error, out var offset), Is.False);
            Assert.That(error, Is.EqualTo("No authenticated DS Actor for explicit position diagnostic"));
            Assert.That(offset, Is.EqualTo(Vector3.zero));
            Assert.That(ResolveProbe("away", true, ref error, out offset), Is.True);
            Assert.That(error, Is.Null);
            Assert.That(offset, Is.EqualTo(Vector3.right * 60), "仍是原显式60m诊断偏移，不更改正式Interest范围。");
        }

        [TestCase(null)] [TestCase("")] [TestCase("HOME")] [TestCase("Away")]
        public void ExplicitInterestProbe_UnknownCommandsRemainRejected(string command)
        {
            string error = "old";
            Assert.That(ResolveProbe(command, true, ref error, out var offset), Is.False);
            Assert.That(error, Is.EqualTo("Unknown explicit interest diagnostic command"));
            Assert.That(offset, Is.EqualTo(Vector3.zero));
            Assert.That(ResolveProbe(command, false, ref error, out offset), Is.False);
            Assert.That(error, Is.EqualTo("No authenticated DS Actor for explicit position diagnostic"));
            Assert.That(offset, Is.EqualTo(Vector3.zero));
        }

        private static bool ResolveProbe(string command, bool authenticated, ref string error, out Vector3 offset)
        {
            // 生产文件入口只编译进Development DS；Editor只验证其共享纯判断，既不监听也不移动Actor。
            // Reflection允许缺私有实现先成为明确RED，而不是引入新的公开跨模块诊断API。
            var method = typeof(RoomInstanceBootstrap).GetMethod("TryResolveExplicitInterestProbeCommand",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "显式诊断需要每请求重算结果，旧错误不得永久锁存。");
            var args = new object[] { command, authenticated, Vector3.zero, error };
            bool result = (bool)method.Invoke(null, args);
            offset = (Vector3)args[2]; error = (string)args[3]; return result;
        }

        [Test] public void OpenEditorRemainsClientAfterBuildingDedicatedServer()
        {
            Assert.False(OnlineWorldBootstrap.IsDedicatedRuntime,
                "UNITY_SERVER也可能进入Editor的编译符号；仅实际Dedicated构建可以接管权威Physics。");
        }
    }
}
