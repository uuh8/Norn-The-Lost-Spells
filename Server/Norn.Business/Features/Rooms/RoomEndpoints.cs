using System.Security.Claims;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Rooms;

/// <summary>所有房间HTTP操作先验证真实Session和Manifest；不接收自报PlayerId，不提供裸资产解锁接口。</summary>
public static class RoomEndpoints
{
    public static void MapRoomEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/online/rooms").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            uint.TryParse(request.Headers["X-Norn-Protocol"].ToString(), out uint version);
            context.HttpContext.RequestServices.GetRequiredService<ContentManifestValidator>().RequireVersion(version, request.Headers["X-Norn-Manifest"].ToString());
            return await next(context);
        });
        group.MapPost("", async (CreateRoomRequest request, HttpContext context, RoomService rooms) => Results.Json(await rooms.CreateAsync(Player(context), request, context.RequestAborted), statusCode: 201));
        group.MapPost("/match", async (CreateRoomRequest request, HttpContext c, RoomMatchmakingService matchmaking) => Results.Json(await matchmaking.MatchAsync(Player(c), request, c.RequestAborted)));
        group.MapPost("/{room}/presence", async (string room, HttpContext c, RoomMatchmakingService matchmaking) => Results.Json(await matchmaking.PresenceAsync(Player(c), room, c.RequestAborted)));
        if (app.Services.GetRequiredService<OnlineOptions>().Instances.Enabled)
        {
            group.MapPost("/{room}/start", async (string room, StartRoomRequest request, HttpContext c, RoomLauncher launcher) => Results.Json(await launcher.StartRunAsync(Player(c), room, request.ExpectedRevision, c.RequestAborted), statusCode: 202));
            group.MapGet("/{room}/instance", async (string room, HttpContext c, RoomInstanceService instances) => Results.Json(await instances.ViewAsync(Player(c), room, c.RequestAborted)));
            group.MapPost("/{room}/ticket", async (string room, HttpContext c, RoomInstanceService instances) => Results.Json(await instances.MintTicketAsync(Player(c), room, c.RequestAborted)));
        }
        group.MapGet("/current", async (HttpContext context, RoomService rooms) => Results.Json(await rooms.GetCurrentAsync(Player(context), context.RequestAborted)));
        group.MapGet("/{room}", async (string room, HttpContext context, RoomService rooms) => Results.Json(await rooms.GetAsync(Player(context), room, context.RequestAborted)));
        group.MapPost("/{room}/members", async (string room, HttpContext context, RoomService rooms) => Results.Json(await rooms.JoinAsync(Player(context), room, context.RequestAborted)));
        group.MapPost("/{room}/ready", async (string room, RoomReadyRequest request, HttpContext context, RoomService rooms) => Results.Json(await rooms.SetReadyAsync(Player(context), room, request, context.RequestAborted)));
        group.MapDelete("/{room}/members/me", async (string room, HttpContext context, RoomService rooms) => { await rooms.LeaveAsync(Player(context), room, context.RequestAborted); return Results.NoContent(); });
    }
    private static string Player(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new OnlineFailure(401, "InvalidSession");
}
