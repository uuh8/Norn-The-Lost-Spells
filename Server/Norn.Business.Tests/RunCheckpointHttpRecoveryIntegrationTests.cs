using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>
/// 显式启动的真实HTTP桥：Unity执行生产Bootstrap协程，业务侧只准备真实test Run、注入传输故障并核查落库。
/// 普通测试发现阶段默认Skip，避免没人驱动Editor时把等待误报为一次成功的集成验证。
/// </summary>
public sealed class RunCheckpointHttpRecoveryIntegrationTests
{
    [CheckpointHttpBridgeFact, Trait("Category", "DatabaseIntegration")]
    public async Task UnityBootstrapRecoversUnknownPostAndBoundsFaultsAcrossRealHttpAndMySql()
    {
        string directory = RequireBridgeDirectory();
        string readyPath = Path.Combine(directory, "ready.json");
        string resultPath = Path.Combine(directory, "unity-result.json");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var faults = new BridgeFaults();
        var report = new BackendReport();
        try
        {
            await using var factory = new BridgeFactory(faults);
            faults.Connection = factory.Connection;
            // UseKestrel返回void；任何Services/NewPlayer/CreateClient访问前就要选择真实TCP监听。
            factory.UseKestrel(0);
            using var listener = factory.CreateClient();
            Assert.True(listener.BaseAddress?.IsLoopback == true, "HTTP桥只能监听loopback。");
            await using var recovery = await RunAssetCheckpointServiceTests.Fixture.Create(factory: factory);
            await using var bounded = await RunAssetCheckpointServiceTests.Fixture.Create(factory: factory);
            var fixtures = new[] { recovery, bounded };
            var scenes = fixtures.Select((f, i) => CreateScene(f, i == 0 ? "Recovery" : "Bounded", listener.BaseAddress!)).ToArray();
            for (int i = 0; i < scenes.Length; i++) faults.Register(scenes[i]);
            var before = new[] { await recovery.AssetState(), await bounded.AssetState() };
            // 故障表继续持有真实凭据；只序列化独立wire副本，Temp文件中不出现明文Secret。
            WritePrivateJson(readyPath, new BridgeReady { Scenes = scenes.Select(ProtectedWireScene).ToArray() });
            // 只等本轮私有结果文件，不轮询生产服务或重试Ready；逾时保留脱敏Trace并进入正常清理。
            while (!File.Exists(resultPath)) await Task.Delay(100, deadline.Token);
            var result = JsonSerializer.Deserialize<UnityReport>(await File.ReadAllTextAsync(resultPath, deadline.Token), OnlineFactory.Json);
            Assert.True(result?.Succeeded == true, "Unity生产协程报告失败，详见本轮私有unity-result.json。");
            Assert.True(result.Scenes?.Length == 2, "Unity必须报告两个独立Run。");
            for (int i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i]; var f = fixtures[i];
                var unity = Assert.Single(result.Scenes!, s => s.Name == scene.Name);
                Assert.Equal(3, unity.Attempts);
                Assert.Equal(scene.Name == "Recovery" ? 0 : 3, unity.PendingAttempts);
                Assert.Equal(scene.Name != "Recovery", unity.HasPending);
                Assert.Equal(scene.Name != "Recovery", unity.DeliveryStopped);
                Assert.Equal(scene.Name == "Recovery" ? 1UL : 0UL, unity.LastConfirmedSequence);
                Assert.Equal(scene.Name == "Recovery" ? 17UL : 0UL, unity.LastAcknowledgedRetirementSequence);
                Assert.True(before[i] == await f.AssetState(), "检查点不得修改Items/位置/Wallets/Characters/成员状态/租约。");
                await RequireJournal(f, scene, faults);
            }
            RequireTrace(faults, result);
            report.Passed = true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            report.Passed = false;
            report.FailureCode = "UnityBridgeDeadlineExceeded";
            throw new TimeoutException("Unity检查点HTTP桥超过四分钟，已停止等待并清理本轮实例。");
        }
        catch (Exception error)
        {
            // 只落异常类别，不把可能含Header/连接串/大正文的异常消息复制成公开证据。
            report.Passed = false; report.FailureCode = error.GetType().Name;
            throw;
        }
        finally
        {
            report.Trace = faults.Trace.ToArray();
            WritePrivateJson(Path.Combine(directory, "backend-result.json"), report);
            // ready.json仅含CurrentUser保护后的凭据，结果落盘后仍及时删除；脱敏结果可留作证据。
            if (File.Exists(readyPath)) File.Delete(readyPath);
        }
    }

    private static BridgeScene CreateScene(RunAssetCheckpointServiceTests.Fixture fixture, string name, Uri endpoint)
    {
        var checkpoint = RunAssetCheckpointServiceTests.Complete(fixture.Current());
        // 同一正式HTTP/认证/SQL桥携带活动Coin和已拾墓碑；这里只构造隔离DS事实，不改真实Wallet或发放奖励。
        checkpoint.Coins = [new() { Id = 5, Amount = 23, Position = new() { X = 1, Y = 2, Z = 3 } },
            new() { Id = 6, Amount = 31, Position = new() { X = -4, Y = 0, Z = 2 }, Collected = true, CollectorActorId = 1 }];
        var collector = checkpoint.Members.Single(m => m.ActorId == 1);
        collector.Assets.Gold = checked(collector.Assets.Gold + 31); collector.Assets.CurrencyVersion++;
        var member = checkpoint.Members.First(m => m.Assets.Items.Any(i => i.LocationKind == 8 && i.ContainerState != null));
        var bottle = member.Assets.Items.First(i => i.LocationKind == 8 && i.ContainerState != null);
        member.Assets.Items = member.Assets.Items.Where(i => i.InstanceId != bottle.InstanceId).ToArray();
        member.Assets.InventoryVersion++;
        checkpoint.RetiredItems = [new() { InstanceId = bottle.InstanceId, ContentId = bottle.ContentId,
            Revision = bottle.Revision, Reason = OnlineRunAssetRetiredReason.Destroyed }];
        // 使用可精确表示的小数，完整Head篡改要能被实际Unity比较器发现，而非只比较头部身份。
        foreach (var m in checkpoint.Members) foreach (var wand in m.Wands) wand.CurrentMana = 12.25f;
        fixture.Grant.Descriptor.BusinessBaseUrl = endpoint.AbsoluteUri;
        return new() { Name = name, Launch = fixture.Grant.Descriptor, Secret = fixture.Grant.Secret!, Checkpoint = checkpoint,
            CheckpointSchemaVersion = fixture.Ready.CheckpointSchemaVersion, RetirementSequence = 17 };
    }

    private static async Task RequireJournal(RunAssetCheckpointServiceTests.Fixture f, BridgeScene scene, BridgeFaults faults)
    {
        await using var db = new MySqlConnection(f.Factory.Connection);
        var args = new { Run = scene.Checkpoint.RunId, Checkpoint = scene.Checkpoint.CheckpointId };
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointReceipts WHERE RunId=@Run", args));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointHeads WHERE RunId=@Run", args));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointHeads WHERE RunId=@Run AND CheckpointId=@Checkpoint AND Sequence=1", args));
        int expected = scene.Checkpoint.Members.Sum(m => m.Assets.Items.Length) + scene.Checkpoint.RetiredItems.Length;
        Assert.Equal(expected, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointIdentities WHERE RunId=@Run", args));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RunAssetCheckpointIdentities WHERE RunId=@Run AND InstanceId=@Id AND IsRetired=TRUE AND LastSequence=1",
            new { Run = scene.Checkpoint.RunId, Id = scene.Checkpoint.RetiredItems[0].InstanceId }));
        var head = await f.Service.ReadHeadAsync(scene.Launch.InstanceId, scene.Secret, scene.Launch.RoomEpoch, default);
        Assert.True(head.IsComplete); Assert.Equal(OnlineRunAssetCheckpoint.CompleteSchemaVersion, head.Checkpoint.SchemaVersion);
        Assert.Equal(2, head.Checkpoint.Coins.Length);
        var activeCoin = Assert.Single(head.Checkpoint.Coins, coin => coin.Id == 5);
        Assert.False(activeCoin.Collected); Assert.Equal(23L, activeCoin.Amount); Assert.Equal(1f, activeCoin.Position.X);
        var collectedCoin = Assert.Single(head.Checkpoint.Coins, coin => coin.Id == 6);
        Assert.True(collectedCoin.Collected); Assert.Equal(1UL, collectedCoin.CollectorActorId); Assert.Equal(31L, collectedCoin.Amount);
        Assert.Equal(scene.Checkpoint.Members.Single(m => m.ActorId == 1).Assets.Gold,
            head.Checkpoint.Members.Single(m => m.ActorId == 1).Assets.Gold);
        Assert.Equal(42UL, head.Checkpoint.World.WorldEpoch); Assert.Equal(25u, head.Checkpoint.World.Chunks[0].Words[17]);
        Assert.All(head.Checkpoint.Members, m => Assert.All(m.Wands, w => Assert.Equal(12.25f, w.CurrentMana)));
        Assert.Equal(scene.Checkpoint.RetiredItems[0].InstanceId, Assert.Single(head.Checkpoint.RetiredItems).InstanceId);
        // 包括RecordedAt/UpdatedAt/身份账本的完整指纹：重发不应重写既有Journal，不能只数行数。
        string fingerprint = await JournalFingerprint(f.Factory.Connection, scene.Checkpoint.RunId);
        Assert.Equal(faults.State(scene.Launch.InstanceId).FirstCommittedJournalHash, fingerprint);
    }

    private static void RequireTrace(BridgeFaults faults, UnityReport result)
    {
        var recovery = faults.Trace.Where(t => t.Name == "Recovery").ToArray();
        var bounded = faults.Trace.Where(t => t.Name == "Bounded").ToArray();
        var posts = recovery.Where(t => t.Kind == "Post").ToArray();
        Assert.Equal(new[] { 0, 0, 200 }, posts.Select(t => t.DeliveredStatus).ToArray());
        Assert.All(posts, p => { Assert.Equal(200, p.BackendStatus); Assert.Equal(64, p.BodySha256.Length); });
        Assert.Single(posts.Select(p => p.BodySha256).Distinct(StringComparer.Ordinal));
        Assert.Equal(new[] { 200, 503 }, recovery.Where(t => t.Kind == "Receipt").Select(t => t.DeliveredStatus).ToArray());
        Assert.Equal(new[] { "CoinChanged", "None" }, recovery.Where(t => t.Kind == "Head").Select(t => t.Fault).ToArray());
        Assert.Equal(new[] { 401, 503, 200 }, bounded.Where(t => t.Kind == "Post").Select(t => t.DeliveredStatus).ToArray());
        Assert.Equal(new[] { 401, 404 }, bounded.Where(t => t.Kind == "Receipt").Select(t => t.DeliveredStatus).ToArray());
        var staleHead = Assert.Single(bounded.Where(t => t.Kind == "Head"));
        Assert.Equal("StalePreviousInstanceHead", staleHead.Fault);
        // 旧回复必须逐字节来自此前实际发送的Recovery成功Head，不能靠改某个Epoch字段伪造“过期”。
        var previousHead = Assert.Single(recovery.Where(t => t.Kind == "Head" && t.Fault == "None"));
        Assert.Equal(64, previousHead.BodySha256.Length); Assert.Equal(previousHead.BodySha256, staleHead.BodySha256);
        Assert.Equal(posts[0].BodySha256, Assert.Single(result.Scenes!, s => s.Name == "Recovery").BodySha256);
        foreach (var scene in result.Scenes!)
        {
            var trace = faults.Trace.Where(t => t.Name == scene.Name).ToArray();
            Assert.Equal(trace.Where(t => t.Kind == "Post").Select(t => (long)t.DeliveredStatus).ToArray(), scene.PostStatuses);
            Assert.Equal(trace.Where(t => t.Kind == "Receipt").Select(t => (long)t.DeliveredStatus).ToArray(), scene.ReceiptStatuses);
            Assert.Equal(trace.Where(t => t.Kind == "Head").Select(t => (long)t.DeliveredStatus).ToArray(), scene.HeadStatuses);
        }
    }

    private static async Task<string> JournalFingerprint(string connection, string run)
    {
        await using var db = new MySqlConnection(connection);
        var args = new { Run = run };
        var receipts = await db.QueryAsync("SELECT * FROM RunAssetCheckpointReceipts WHERE RunId=@Run ORDER BY CheckpointId", args);
        var heads = await db.QueryAsync("SELECT * FROM RunAssetCheckpointHeads WHERE RunId=@Run ORDER BY RunId", args);
        var identities = await db.QueryAsync("SELECT * FROM RunAssetCheckpointIdentities WHERE RunId=@Run ORDER BY InstanceId", args);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { receipts, heads, identities }, OnlineFactory.Json)));
    }

    private static string RequireBridgeDirectory()
    {
        string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("NORN_CHECKPOINT_HTTP_BRIDGE")!);
        static bool Under(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        string? repository = AppContext.BaseDirectory;
        while (repository != null && !(Directory.Exists(Path.Combine(repository, "RPG")) && Directory.Exists(Path.Combine(repository, "Server")))) repository = Path.GetDirectoryName(repository);
        Assert.True(Under(directory, Path.GetTempPath()) || repository != null && Under(directory, Path.Combine(repository, "RPG", "Temp")), "HTTP桥只能写本轮独立Temp目录。");
        Assert.True(Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any(), "主Agent须先创建本轮独立空桥目录。");
        return directory;
    }

    private static BridgeScene ProtectedWireScene(BridgeScene source) => new()
    {
        Name = source.Name, Launch = source.Launch, Checkpoint = source.Checkpoint,
        Secret = "", ProtectedSecret = ProtectSecret(source.Secret),
        CheckpointSchemaVersion = source.CheckpointSchemaVersion, RetirementSequence = source.RetirementSequence
    };

    /// <summary>
    /// 本地引用未提供ProtectedData，直接使用Windows DPAPI CurrentUser；不获取或改写任何ACL权限。
    /// 仅CRYPTPROTECT_UI_FORBIDDEN，不设置LOCAL_MACHINE，且不允许系统弹窗。Unity在同一用户内存中解密。
    /// </summary>
    private static string ProtectSecret(string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HTTP桥凭据仅使用Windows CurrentUser DPAPI。");
        byte[] plain = Encoding.UTF8.GetBytes(secret);
        var input = new DataBlob { Length = plain.Length, Data = Marshal.AllocHGlobal(plain.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(plain, 0, input.Data, plain.Length);
            const uint CryptProtectUiForbidden = 1;
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output))
                throw new CryptographicException("HTTP桥CurrentUser凭据保护失败，Windows错误码=" + Marshal.GetLastWin32Error());
            byte[] protectedBytes = new byte[output.Length];
            Marshal.Copy(output.Data, protectedBytes, 0, protectedBytes.Length);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static void WritePrivateJson<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, OnlineFactory.Json));
        File.Move(temporary, path, true);
    }

    public sealed class CheckpointHttpBridgeFactAttribute : FactAttribute
    {
        public CheckpointHttpBridgeFactAttribute()
        { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NORN_CHECKPOINT_HTTP_BRIDGE"))) Skip = "仅主Agent显式设置私有Temp桥目录后运行真实Unity HTTP集成。"; }
    }
    private sealed class BridgeReady { public BridgeScene[] Scenes = []; }
    private sealed class BridgeScene
    {
        public string Name = "", Secret = "", ProtectedSecret = "";
        public InstanceLaunchDescriptor Launch = null!;
        public OnlineRunAssetCheckpoint Checkpoint = null!;
        public uint CheckpointSchemaVersion;
        public ulong RetirementSequence;
    }
    private sealed class UnityReport { public bool Succeeded; public string FailureCode = ""; public UnitySceneReport[]? Scenes; }
    private sealed class UnitySceneReport
    {
        public string Name = "", BodySha256 = "";
        public bool HasPending, DeliveryStopped;
        public ulong LastConfirmedSequence, LastAcknowledgedRetirementSequence;
        public int Attempts, PendingAttempts;
        public long[] PostStatuses = [], ReceiptStatuses = [], HeadStatuses = [];
    }
    private sealed class BackendReport { public bool Passed; public string FailureCode = ""; public TraceEntry[] Trace = []; }
    private sealed class TraceEntry
    {
        public string Name = "", Method = "", Kind = "", BodySha256 = "", Fault = "None";
        public int BackendStatus, DeliveredStatus;
    }
    private sealed class ScenarioState(BridgeScene scene)
    {
        public readonly BridgeScene Scene = scene;
        public int AuthorizedPosts, Receipts, Heads;
        public string? FirstCommittedJournalHash;
    }

    /// <summary>故障属于此测试Host的专有实例；其它账户/房间/实例请求原样交给生产pipeline。</summary>
    private sealed class BridgeFactory(BridgeFaults faults) : OnlineFactory(instances: true)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new FaultStartupFilter(faults)));
        }
    }
    private sealed class FaultStartupFilter(BridgeFaults faults) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        { app.Use((context, continuation) => faults.Invoke(context, continuation)); next(app); };
    }
    private sealed class BridgeFaults
    {
        private readonly ConcurrentDictionary<string, ScenarioState> _states = new(StringComparer.Ordinal);
        private byte[]? _previousInstanceHead;
        public string Connection = "";
        public readonly ConcurrentQueue<TraceEntry> Trace = new();
        public void Register(BridgeScene scene) => Assert.True(_states.TryAdd(scene.Launch.InstanceId, new(scene)));
        public ScenarioState State(string id) => _states[id];
        public async Task Invoke(HttpContext context, Func<Task> next)
        {
            const string prefix = "/api/instances/";
            string path = context.Request.Path.Value ?? "";
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) { await next(); return; }
            int slash = path.IndexOf('/', prefix.Length);
            if (slash < 0 || !_states.TryGetValue(path[prefix.Length..slash], out var state)) { await next(); return; }
            string suffix = path[(slash + 1)..];
            string kind = suffix == "checkpoints" && context.Request.Method == "POST" ? "Post"
                : suffix == "checkpoints/head" ? "Head" : suffix.StartsWith("checkpoints/", StringComparison.Ordinal) ? "Receipt" : "";
            if (kind.Length == 0) { await next(); return; }
            var trace = new TraceEntry { Name = state.Scene.Name, Method = context.Request.Method, Kind = kind };
            bool correctKey = context.Request.Headers["X-Norn-Instance-Key"] == state.Scene.Secret
                && context.Request.Query["roomEpoch"] == state.Scene.Launch.RoomEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int post = kind == "Post" && correctKey ? Interlocked.Increment(ref state.AuthorizedPosts) : 0;
            int receipt = kind == "Receipt" ? Interlocked.Increment(ref state.Receipts) : 0;
            int head = kind == "Head" ? Interlocked.Increment(ref state.Heads) : 0;
            if (state.Scene.Name == "Bounded" && post == 1)
            {
                // 故障前仍检查真实数据库凭据/Epoch/租约；字符串匹配仅限定注入目标，不是替代认证。
                await context.RequestServices.GetRequiredService<RunAssetCheckpointService>().AuthorizeAsync(
                    state.Scene.Launch.InstanceId, state.Scene.Secret, state.Scene.Launch.RoomEpoch, context.RequestAborted);
                trace.Fault = "PostUnavailable"; trace.DeliveredStatus = 503;
                context.Response.StatusCode = 503; Trace.Enqueue(trace); return;
            }
            if (state.Scene.Name == "Recovery" && kind == "Receipt" && receipt == 2 && correctKey)
            {
                trace.Fault = "ReceiptUnavailable"; trace.DeliveredStatus = 503;
                context.Response.StatusCode = 503; Trace.Enqueue(trace); return;
            }
            // 延迟实际响应的Start/Flush，确保await next后的Abort发生在真实事务提交后、响应送出前。
            var original = context.Features.Get<IHttpResponseBodyFeature>()!;
            await using var capture = new BufferedResponse();
            context.Features.Set<IHttpResponseBodyFeature>(capture);
            if (kind == "Post" && correctKey) context.Request.EnableBuffering(16 * 1024, OnlineRunAssetCheckpoint.MaximumPayloadBytes);
            try
            {
                await next(); await capture.CompleteAsync();
                trace.BackendStatus = context.Response.StatusCode;
                if (kind == "Post" && correctKey && trace.BackendStatus == 200)
                {
                    context.Request.Body.Position = 0;
                    trace.BodySha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(context.Request.Body, context.RequestAborted));
                    state.FirstCommittedJournalHash ??= await JournalFingerprint(Connection, state.Scene.Checkpoint.RunId);
                }
                if (state.Scene.Name == "Recovery" && post is 1 or 2 && trace.BackendStatus == 200)
                {
                    trace.Fault = "PostResponseLost"; trace.DeliveredStatus = 0;
                    Trace.Enqueue(trace); context.Abort(); return;
                }
                byte[] bytes = capture.Bytes();
                if (kind == "Head" && head == 1 && trace.BackendStatus == 200 && correctKey)
                {
                    if (state.Scene.Name == "Recovery")
                    {
                        var value = JsonSerializer.Deserialize<OnlineRunAssetCheckpointHeadReply>(bytes, OnlineFactory.Json)!;
                        // 保留原身份/Receipt，仅篡改一个活动Coin值，生产Pending必须拒绝且不推进ACK。
                        value.Checkpoint.Coins.Single(coin => coin.Id == 5).Amount += 1; trace.Fault = "CoinChanged";
                        bytes = JsonSerializer.SerializeToUtf8Bytes(value, OnlineFactory.Json);
                    }
                    else
                    {
                        // 回放上一实例真实保存且已正常发送的完整回复；不改字段制造一个从未存在的旧版本。
                        var previous = Volatile.Read(ref _previousInstanceHead);
                        Assert.True(previous != null, "必须先完成Recovery真实Head读取，才能回放上一实例的旧回复。");
                        bytes = previous!; trace.Fault = "StalePreviousInstanceHead";
                    }
                }
                if (kind == "Head") trace.BodySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
                trace.DeliveredStatus = context.Response.StatusCode;
                context.Features.Set(original);
                context.Response.ContentLength = bytes.Length;
                await original.Stream.WriteAsync(bytes, context.RequestAborted);
                // 只缓存第二次未修改的Recovery Head：原始字节刚由真实Service/DB读回，并成功写入HTTP响应。
                if (state.Scene.Name == "Recovery" && kind == "Head" && head == 2 && trace.BackendStatus == 200 && correctKey)
                    Volatile.Write(ref _previousInstanceHead, bytes);
                Trace.Enqueue(trace);
            }
            finally { context.Features.Set(original); }
        }
    }

    /// <summary>仅截住本轮故障路由的响应Body生命周期；原请求与全部生产认证/事务/序列化照常执行。</summary>
    private sealed class BufferedResponse : IHttpResponseBodyFeature, IAsyncDisposable
    {
        private readonly MemoryStream _stream = new();
        public Stream Stream => _stream;
        public PipeWriter Writer { get; }
        public BufferedResponse() => Writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));
        public void DisableBuffering() { }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task CompleteAsync() { await Writer.FlushAsync(); }
        public async Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default)
        {
            // 检查点不会发文件；接口保留正确有界语义，不能让未来误接路由静默吞正文。
            await using var file = File.OpenRead(path); file.Position = offset;
            long remaining = count ?? file.Length - offset; byte[] buffer = new byte[8192];
            while (remaining > 0)
            { int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken); if (read == 0) break;
                await _stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken); remaining -= read; }
        }
        public byte[] Bytes() => _stream.ToArray();
        public async ValueTask DisposeAsync() { await Writer.CompleteAsync(); await _stream.DisposeAsync(); }
    }
}
