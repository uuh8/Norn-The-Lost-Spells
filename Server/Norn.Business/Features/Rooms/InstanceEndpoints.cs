using System.Buffers;
using System.Net;
using System.Text.Json;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;

/// <summary>实例密钥独立于玩家Bearer；当前只接收loopback控制请求，不记录Header/正文。</summary>
public static class InstanceEndpoints
{
    public static void MapInstanceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/instances/{instance}");
        group.AddEndpointFilter(async (context, next) =>
        {
            var ip = context.HttpContext.Connection.RemoteIpAddress;
            if (ip == null || !IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) throw new OnlineFailure(403, "LocalControlOnly");
            return await next(context);
        });
        group.MapPost("/ready", async (string instance, InstanceReadyRequest request, HttpContext c, RoomInstanceService s) => Results.Json(await s.ReadyAsync(instance, Key(c), request, c.RequestAborted)));
        group.MapPost("/heartbeat", async (string instance, InstanceHeartbeatRequest request, HttpContext c, RoomInstanceService s) => Results.Json(await s.HeartbeatAsync(instance, Key(c), request.RoomEpoch, c.RequestAborted)));
        group.MapPost("/complete", async (string instance, InstanceCompleteRequest request, HttpContext c, RoomInstanceService s)
            => Results.Json(await s.CompleteAsync(instance, Key(c), request, c.RequestAborted)));
        group.MapPost("/admit", async (string instance, ConsumeJoinTicketRequest request, HttpContext c, RoomInstanceService s) => Results.Json(await s.ConsumeAsync(instance, Key(c), request, c.RequestAborted)));
        group.MapPost("/member-exits",async(string instance,HttpContext c,RunMemberExitService s)=>
        {
            await s.AuthorizeAsync(instance,Key(c),c.RequestAborted);
            var request=await ReadMemberExit(c);
            return Results.Json(await s.CommitAsync(instance,Key(c),request,c.RequestAborted));
        });
        group.MapGet("/member-exits/{settlement}",async(string instance,string settlement,uint roomEpoch,HttpContext c,RunMemberExitService s)
            =>Results.Json(await s.FindAsync(instance,Key(c),roomEpoch,settlement,c.RequestAborted)));
        // 不绑定正文参数：必须先校验实例密钥及query Epoch，再放大此单一路由的技术预算/读取资产JSON。
        // 全局其他HTTP端点仍保留原4KiB上限；这里的64MiB也不是全部极限背包可落库的保证。
        group.MapPost("/checkpoints", async (string instance, uint roomEpoch, HttpContext c, RunAssetCheckpointService s) =>
        {
            await s.AuthorizeAsync(instance, Key(c), roomEpoch, c.RequestAborted);
            var request = await ReadCheckpoint(c);
            return Results.Json(await s.SaveAsync(instance, Key(c), roomEpoch, request, c.RequestAborted));
        });
        // 空活动成员只能走这个显式控制入口；仍复用原64MiB预算、凭据与同一Journal/Head。
        group.MapPost("/terminal-checkpoints", async (string instance, uint roomEpoch, HttpContext c, RunAssetCheckpointService s) =>
        {
            await s.AuthorizeAsync(instance, Key(c), roomEpoch, c.RequestAborted);
            return Results.Json(await s.SaveTerminalAsync(instance, Key(c), roomEpoch, await ReadCheckpoint(c), c.RequestAborted));
        });
        group.MapGet("/checkpoints/{checkpoint}", async (string instance, string checkpoint, uint roomEpoch,
            HttpContext c, RunAssetCheckpointService s) => Results.Json(await s.FindAsync(instance, Key(c), roomEpoch, checkpoint, c.RequestAborted)));
        group.MapGet("/checkpoints/head", async (string instance, uint roomEpoch, HttpContext c, RunAssetCheckpointService s)
            // 保留同一鉴权/验证与200语义，仅避免新DTO把历史Owned2补出不存在的金币字段。
            => Results.Content(await s.ReadHeadJsonAsync(instance, Key(c), roomEpoch, c.RequestAborted), "application/json; charset=utf-8"));
    }
    private static async Task<OnlineRunAssetCheckpoint> ReadCheckpoint(HttpContext context)
    {
        const int maximum = RunAssetCheckpointService.MaximumPayloadBytes;
        if (context.Request.ContentLength > maximum) throw new OnlineFailure(413, "RunCheckpointPayloadTooLarge");
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature != null && !feature.IsReadOnly) feature.MaxRequestBodySize = maximum;
        // TestServer/代理未提供Kestrel feature时仍逐块计数；不能只信Content-Length或先无界ReadToEnd。
        int capacity = context.Request.ContentLength is > 0 and <= maximum ? (int)context.Request.ContentLength.Value : 4096;
        using var bytes = new MemoryStream(capacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                int read = await context.Request.Body.ReadAsync(buffer.AsMemory(), context.RequestAborted);
                if (read == 0) break;
                if (bytes.Length + read > maximum) throw new OnlineFailure(413, "RunCheckpointPayloadTooLarge");
                bytes.Write(buffer, 0, read);
            }
            var options = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            return JsonSerializer.Deserialize<OnlineRunAssetCheckpoint>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), options)
                ?? throw new OnlineFailure(400, "InvalidRunCheckpointJson");
        }
        catch (JsonException) { throw new OnlineFailure(400, "InvalidRunCheckpointJson"); }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    private static async Task<OnlineRunMemberExitRequest> ReadMemberExit(HttpContext context)
    {
        const int maximum=OnlineRunMemberExitRequest.MaximumPayloadBytes;
        if(context.Request.ContentLength>maximum)throw new OnlineFailure(413,"RunMemberExitPayloadTooLarge");
        var feature=context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if(feature!=null&&!feature.IsReadOnly)feature.MaxRequestBodySize=maximum;
        int capacity=context.Request.ContentLength is >0 and <=maximum?(int)context.Request.ContentLength.Value:4096;
        using var bytes=new MemoryStream(capacity);byte[] buffer=ArrayPool<byte>.Shared.Rent(16*1024);
        try
        {
            while(true)
            {
                int count=await context.Request.Body.ReadAsync(buffer.AsMemory(),context.RequestAborted);
                if(count==0)break;
                if(bytes.Length+count>maximum)throw new OnlineFailure(413,"RunMemberExitPayloadTooLarge");
                bytes.Write(buffer,0,count);
            }
            var options=context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            return JsonSerializer.Deserialize<OnlineRunMemberExitRequest>(bytes.GetBuffer().AsSpan(0,(int)bytes.Length),options)
                ??throw new OnlineFailure(400,"InvalidRunMemberExitJson");
        }
        catch(JsonException){throw new OnlineFailure(400,"InvalidRunMemberExitJson");}
        finally{ArrayPool<byte>.Shared.Return(buffer,clearArray:true);}
    }
    private static string Key(HttpContext c) => c.Request.Headers["X-Norn-Instance-Key"].ToString();
}
