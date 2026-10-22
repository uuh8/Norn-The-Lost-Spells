using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Rooms;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

public sealed class RoomLauncherTests
{
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task InvalidBuildPath_ReleasesBothMembersAssets()
    {
        await using var f = new OnlineFactory(instances: true); var run = await RoomInstanceTests.Reserve(f);
        f.Services.GetRequiredService<OnlineOptions>().Instances.BuildPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe");
        var e = await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<RoomLauncher>().StartReservedAsync(run.Run, default)); Assert.Equal("BuildNotFound", e.Code);
        await using var db = new MySqlConnection(f.Connection);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Run", new { Run = run.Run.RunId }));
        Assert.Equal("Open", (await RoomAdmissionTests.View(run.ClientA, run.Run.RoomId)).State);
    }
    [Fact, Trait("Category", "DatabaseIntegration")]
    public async Task OccupiedUdpPort_ReleasesReservationWithoutStartingProcess()
    {
        await using var f = new OnlineFactory(instances: true); var run = await RoomInstanceTests.Reserve(f);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var options = f.Services.GetRequiredService<OnlineOptions>().Instances; options.FirstPort = options.LastPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        options.BuildPath = Environment.ProcessPath!; // 文件存在，但被占端口必须在Process.Start之前拒绝。
        var e = await Assert.ThrowsAsync<OnlineFailure>(() => f.Services.GetRequiredService<RoomLauncher>().StartReservedAsync(run.Run, default)); Assert.Equal("PortUnavailable", e.Code);
        await using var db = new MySqlConnection(f.Connection); Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AssetLeases WHERE RunId=@Run", new { Run = run.Run.RunId }));
    }
    [Fact]
    public void StartInfo_UsesLiteralArguments_HiddenWindow_AndSecretOnlyInEnvironment()
    {
        string secret = new string('c', 64); var info = RoomLauncher.CreateStartInfo("C:/Build Folder/server.exe", "C:/Room Folder/launch.json", "C:/Room Folder/server.log", secret);
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow); Assert.Empty(info.Arguments);
        Assert.Contains("C:/Room Folder/launch.json", info.ArgumentList); Assert.DoesNotContain(secret, info.ArgumentList);
        Assert.Equal(secret, info.Environment["NORN_INSTANCE_SECRET"]);
        Assert.DoesNotContain(info.Environment.Keys, k => k.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("Online__", StringComparison.OrdinalIgnoreCase) || k.StartsWith("NORN_N1_", StringComparison.OrdinalIgnoreCase));
    }
}
