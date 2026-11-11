using System.Reflection;
using System.Runtime.ExceptionServices;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 只使用假连接串验证测试基础设施的隔离门；不创建Factory、不改环境变量，也不打开MySQL。
/// Runtime指向test而Migration误指dev同样危险：AutoMigrate会在Host监听前执行DDL。
/// </summary>
public sealed class OnlineFactoryIsolationTests
{
    private const string Safe = "Server=127.0.0.1;Port=13384;Database=norn_online_test;User ID=fake;Password=must-never-be-logged";

    [Fact]
    public void BothRuntimeAndMigrationMustExplicitlyUseThePrivateTestSchema()
        => Require(Safe, Safe);

    [Theory]
    [InlineData("Server=127.0.0.1;Port=13384;Database=norn_online_dev", false)]
    [InlineData("Server=127.0.0.1;Port=13384;Database=norn_online_dev", true)]
    [InlineData("Server=127.0.0.1;Port=3306;Database=norn_online_test", false)]
    [InlineData("Server=127.0.0.1;Port=3306;Database=norn_online_test", true)]
    [InlineData("Server=localhost;Port=13384;Database=norn_online_test", false)]
    [InlineData("Server=localhost;Port=13384;Database=norn_online_test", true)]
    [InlineData("Server=192.0.2.1;Port=13384;Database=norn_online_test", false)]
    [InlineData("Server=192.0.2.1;Port=13384;Database=norn_online_test", true)]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("invalid-connection-string", false)]
    [InlineData("invalid-connection-string", true)]
    public void UnsafeRuntimeOrMigrationIsRejectedBeforeAnyHostCanStart(string unsafeConnection, bool migration)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Require(
            migration ? Safe : unsafeConnection, migration ? unsafeConnection : Safe));
        Assert.DoesNotContain("must-never-be-logged", failure.ToString());
    }

    private static void Require(string runtime, string migration)
    {
        // 先以反射表达缺失的保护接缝，RED阶段仍能编译；不能为观察失败而实例化带迁移副作用的Host。
        var guard = typeof(OnlineFactory).GetMethod("RequireIsolatedConnections", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(guard != null, "OnlineFactory尚未在Host启动前同时检查Runtime与Migration的test Schema隔离。");
        try { guard!.Invoke(null, [runtime, migration]); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
