using System.Security.Claims;
using Game.NetworkProtocol;
using Norn.Business.Content;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Trading;

/// <summary>路由只负责Session身份/内容版本和DTO转换；金币、供货、归属与幂等全由Service事务裁决。</summary>
public static class AccountShopEndpoints
{
    public static void MapAccountShopEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/online/shop").RequireAuthorization();
        group.MapGet("", async (HttpContext context, AccountShopService shop, ContentManifestValidator content) =>
        { RequireContent(context, content); return Results.Json(await shop.GetAsync(Player(context), context.RequestAborted)); });
        group.MapPost("/trade", async (AccountShopTradeRequest request, HttpContext context, AccountShopService shop, ContentManifestValidator content) =>
        { RequireContent(context, content); return Results.Json(await shop.TradeAsync(Player(context), request, context.RequestAborted)); });
        group.MapGet("/trades/{command}", async (string command, HttpContext context, AccountShopService shop, ContentManifestValidator content) =>
        { RequireContent(context, content); return Results.Json(await shop.FindReceiptAsync(Player(context), command, context.RequestAborted)); });
    }
    private static string Player(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new OnlineFailure(401, "InvalidSession");
    private static void RequireContent(HttpContext context, ContentManifestValidator content)
    {
        uint.TryParse(context.Request.Headers["X-Norn-Protocol"].ToString(), out uint version);
        content.RequireVersion(version, context.Request.Headers["X-Norn-Manifest"].ToString());
    }
}
