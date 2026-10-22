using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Accounts;

/// <summary>从随机 Token 的摘要解析身份；PlayerId 来自 DB JOIN，永远不从 JSON 或 URL 接受。</summary>
public sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> schemes, ILoggerFactory logger,
    UrlEncoder encoder, OnlineDatabase database, TimeProvider clock, OnlineOptions online) : AuthenticationHandler<AuthenticationSchemeOptions>(schemes, logger, encoder)
{
    public const string SchemeName = "NornSession";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!online.Enabled) return AuthenticateResult.NoResult();
        string? token = ReadToken(Request);
        if (token == null) return AuthenticateResult.NoResult();
        await using var connection = await database.OpenAsync(Context.RequestAborted);
        var identity = await connection.QuerySingleOrDefaultAsync<IdentityRow>(new CommandDefinition("""
            SELECT c.CharacterId,a.Username FROM AuthSessions s
            JOIN Accounts a ON a.AccountId=s.AccountId JOIN Characters c ON c.AccountId=a.AccountId
            WHERE s.TokenHash=@Hash AND s.ExpiresAtUtc>@Now
            """, new { Hash = AccountService.HashToken(token), Now = clock.GetUtcNow().UtcDateTime }, cancellationToken: Context.RequestAborted));
        if (identity == null) return AuthenticateResult.Fail("InvalidSession");
        var claims = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, identity.CharacterId), new Claim(ClaimTypes.Name, identity.Username)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(claims), SchemeName));
    }

    public static string? ReadToken(HttpRequest request)
    {
        string value = request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length != 71) return null;
        string token = value[7..];
        foreach (char c in token) if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        return token;
    }
    private sealed class IdentityRow { public string CharacterId { get; set; } = ""; public string Username { get; set; } = ""; }
}
