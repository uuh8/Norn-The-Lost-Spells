using System.Diagnostics;
using Norn.Business.Features.Rooms;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 进程启动与操作系统的模块枚举之间存在短暂时间差。这里用最小的只读 delegate
/// 确定性重现“前几次无元数据”，不实际启动/终止 DS，也不靠偶发调度碰运气。
/// </summary>
public sealed class InstanceProcessesTests
{
    [Fact]
    public async Task MetadataTemporarilyUnavailable_RetriesAndReturnsActualIdentity()
    {
        var actual = new InstanceProcessIdentity(123, 456, Path.GetFullPath("actual-server.exe"));
        int reads = 0, delays = 0;
        var result = await InstanceProcesses.ReadIdentityWithRetryAsync(
            () => ++reads < 3 ? null : actual,
            maximumAttempts: 4, retryDelay: TimeSpan.FromMilliseconds(25),
            delay: (duration, cancellation) =>
            {
                Assert.Equal(TimeSpan.FromMilliseconds(25), duration);
                cancellation.ThrowIfCancellationRequested(); delays++; return Task.CompletedTask;
            }, CancellationToken.None);

        Assert.Same(actual, result);
        Assert.Equal(3, reads); Assert.Equal(2, delays);
    }

    [Fact]
    public async Task MetadataNeverAvailable_StopsAtAttemptLimitWithoutInventingIdentity()
    {
        int reads = 0, delays = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => InstanceProcesses.ReadIdentityWithRetryAsync(
            () => { reads++; return null; }, maximumAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(25),
            delay: (_, _) => { delays++; return Task.CompletedTask; }, CancellationToken.None));

        Assert.Equal(3, reads); Assert.Equal(2, delays);
    }

    [Fact]
    public async Task AlreadyCancelled_DoesNotReadOrDelay()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        int reads = 0, delays = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstanceProcesses.ReadIdentityWithRetryAsync(
            () => { reads++; return null; }, maximumAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(25),
            delay: (_, _) => { delays++; return Task.CompletedTask; }, cancellation.Token));

        Assert.Equal(0, reads); Assert.Equal(0, delays);
    }

    [Fact]
    public async Task CancellationDuringRetry_DoesNotReadMetadataAgain()
    {
        using var cancellation = new CancellationTokenSource();
        int reads = 0, delays = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstanceProcesses.ReadIdentityWithRetryAsync(
            () => { reads++; return null; }, maximumAttempts: 3,
            retryDelay: TimeSpan.FromMilliseconds(25),
            delay: (_, _) => { delays++; cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));

        Assert.Equal(1, reads); Assert.Equal(1, delays);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingActualModulePath_CannotCreateProcessIdentity(string? actualPath)
    {
        Assert.False(InstanceProcesses.TryCreateIdentity(123, 456, actualPath, out var identity));
        Assert.Null(identity);
    }

    [Fact]
    public void UnstartedProcess_HasNoReadableIdentityAndDoesNotThrowNullReference()
    {
        using var process = new Process();
        Assert.False(InstanceProcesses.TryDescribe(process, out var identity));
        Assert.Null(identity);
        // 旧同步 API 保留“必须得到完整身份”的合同，但失败必须是明确状态异常，不能泄露 NRE。
        Assert.Throws<InvalidOperationException>(() => InstanceProcesses.Describe(process));
    }

    [Fact]
    public void CurrentProcess_UsesRealMetadataAndRejectsTicksOrPathMismatch()
    {
        // 仅读当前 Test Host 的实际身份，不向任何进程发送停止指令。
        using var process = Process.GetCurrentProcess();
        Assert.True(InstanceProcesses.TryDescribe(process, out var identity));
        Assert.Equal(process.Id, identity.ProcessId);
        Assert.Equal(process.StartTime.ToUniversalTime().Ticks, identity.StartTicks);
        Assert.Equal(Path.GetFullPath(process.MainModule!.FileName!), identity.Executable);
        Assert.True(InstanceProcesses.Matches(identity));
        Assert.False(InstanceProcesses.Matches(identity with { StartTicks = identity.StartTicks + 1 }));
        Assert.False(InstanceProcesses.Matches(identity with { Executable = Path.GetFullPath("different-server.exe") }));
    }
}
