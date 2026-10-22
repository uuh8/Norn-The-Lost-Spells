using MySqlConnector;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;

/// <summary>Host退出不杀DS；新Host从数据库核对同一PID/Ticks/路径，租约决定何时回收。</summary>
public sealed class RoomInstanceSupervisor(IServiceProvider services, OnlineOptions options, ILogger<RoomInstanceSupervisor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Online关闭时不能提前构造Manifest/数据库；配置Provider完成后才解析服务。
        if (!options.Enabled || !options.RoomsEnabled || !options.Instances.Enabled) return;
        options.Instances.Validate(); var instances = services.GetRequiredService<RoomInstanceService>(); var launcher = services.GetRequiredService<RoomLauncher>();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await services.GetRequiredService<RoomMatchmakingService>().ExpireAsync(stoppingToken);
                var before = await instances.ActiveAsync(stoppingToken); await instances.ExpireDueAsync(stoppingToken);
                var remaining = (await instances.ActiveAsync(stoppingToken)).Select(r => r.InstanceId).ToHashSet(StringComparer.Ordinal);
                foreach (var row in before)
                {
                    if (row.Identity is not { } identity) continue;
                    if (!remaining.Contains(row.InstanceId)) InstanceProcesses.StopMatching(identity);
                    else if (!InstanceProcesses.Matches(identity)) await instances.FailAsync(row.InstanceId, "ProcessExited", stoppingToken);
                }
                await launcher.ResumeUnlaunchedAsync(stoppingToken);
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is MySqlException or OnlineFailure or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogWarning("Instance supervision deferred: {Kind}", e.GetType().Name); // 不打印凭据或请求体。
                try { await Task.Delay(2000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
