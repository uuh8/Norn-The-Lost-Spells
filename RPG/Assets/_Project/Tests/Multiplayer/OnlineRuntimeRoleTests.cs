using NUnit.Framework;

namespace Game.Multiplayer.Tests
{
    public sealed class OnlineRuntimeRoleTests
    {
        [Test] public void MissingMovementWorldCannotReportInstanceReady()
        {
            Assert.False(RoomInstanceBootstrap.IsMovementWorldReady(null),
                "端口已监听不代表有Authority；漏装世界必须阻止业务Ready和入局。");
        }
        [Test] public void OpenEditorRemainsClientAfterBuildingDedicatedServer()
        {
            Assert.False(OnlineWorldBootstrap.IsDedicatedRuntime,
                "UNITY_SERVER也可能进入Editor的编译符号；仅实际Dedicated构建可以接管权威Physics。");
        }
    }
}
