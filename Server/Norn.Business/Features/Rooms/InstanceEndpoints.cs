using System.Net;
using Game.NetworkProtocol;
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
        group.MapPost("/admit", async (string instance, ConsumeJoinTicketRequest request, HttpContext c, RoomInstanceService s) => Results.Json(await s.ConsumeAsync(instance, Key(c), request, c.RequestAborted)));
    }
    private static string Key(HttpContext c) => c.Request.Headers["X-Norn-Instance-Key"].ToString();
}
