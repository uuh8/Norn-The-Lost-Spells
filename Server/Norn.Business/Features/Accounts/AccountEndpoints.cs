using System.Security.Claims;
using Dapper;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Accounts;

/// <summary>路由只翻译 HTTP/身份/结果；资产规则与事务集中在 Service。</summary>
public static class AccountEndpoints
{
    public static void MapOnlineEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/online");
        group.MapPost("/accounts", async (RegisterAccountRequest request, AccountService service, CancellationToken ct) =>
            Results.Json(await service.RegisterAsync(request, ct), statusCode: 201)).RequireRateLimiting("online-auth");
        group.MapPost("/sessions", async (LoginAccountRequest request, AccountService service, CancellationToken ct) =>
            Results.Json(await service.LoginAsync(request, ct))).RequireRateLimiting("online-auth");
        group.MapDelete("/sessions/current", async (HttpContext context, OnlineDatabase database) =>
        {
            string token = SessionAuthenticationHandler.ReadToken(context.Request)!;
            await using var connection = await database.OpenAsync(context.RequestAborted);
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM AuthSessions WHERE TokenHash=@Hash", new { Hash = AccountService.HashToken(token) }, cancellationToken: context.RequestAborted));
            return Results.NoContent();
        }).RequireAuthorization();
        group.MapGet("/profile", async (HttpContext context, InventoryService inventory, ContentManifestValidator content) =>
        {
            RequireContent(context, content);
            return Results.Json(await inventory.GetProfileAsync(Player(context), context.RequestAborted));
        }).RequireAuthorization();
        group.MapPost("/inventory/move", async (InventoryMoveRequest request, HttpContext context, InventoryService inventory, ContentManifestValidator content) =>
        {
            RequireContent(context, content);
            return Results.Json(await inventory.TryApplyInventoryAsync(Player(context), request, context.RequestAborted));
        }).RequireAuthorization();
        app.MapGet("/health/ready", async (OnlineDatabase database, CancellationToken ct) =>
        {
            await using var connection = await database.OpenAsync(ct);
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct)) == 1 ? Results.Ok("ready") : Results.StatusCode(503);
        });
    }
    private static string Player(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new OnlineFailure(401, "InvalidSession");
    private static void RequireContent(HttpContext context, ContentManifestValidator content)
    {
        uint.TryParse(context.Request.Headers["X-Norn-Protocol"].ToString(), out uint version);
        content.RequireVersion(version, context.Request.Headers["X-Norn-Manifest"].ToString());
    }
}
