using System.Net;
using System.Net.Http.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>首先验证真实 HTTP 身份边界；没有 Session 时不得读取任何人的线上角色。</summary>
public sealed class OnlineAccountsTests
{
    [Fact, Trait("Category", "Accounts")]
    public async Task Profile_WithoutSession_IsUnauthorized()
    {
        await using var factory = new OnlineFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/online/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact, Trait("Category", "Accounts")]
    public async Task WrongPasswordAndUnknownAccount_AreRejectedEqually()
    {
        await using var factory = new OnlineFactory();
        var player = await factory.NewPlayerAsync();
        using var client = factory.CreateClient();
        var request = factory.LoginRequest(player.Username); request.Password = "incorrect_password!";
        using var wrong = await client.PostAsJsonAsync("/api/online/sessions", request, OnlineFactory.Json);
        request.Username = "missing_" + Guid.NewGuid().ToString("N")[..12];
        using var missing = await client.PostAsJsonAsync("/api/online/sessions", request, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(wrong.StatusCode, missing.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await missing.Content.ReadAsStringAsync());
    }

    [Fact, Trait("Category", "Accounts")]
    public async Task DatabaseContainsPasswordHashAndTokenDigest_Only()
    {
        await using var factory = new OnlineFactory();
        var player = await factory.NewPlayerAsync();
        await using var connection = new MySqlConnection(factory.Connection); await connection.OpenAsync();
        string passwordHash = await connection.QuerySingleAsync<string>("SELECT PasswordHash FROM Accounts WHERE Username=@Name", new { Name = player.Username });
        Assert.NotEqual(OnlineFactory.Password, passwordHash);
        Assert.DoesNotContain(OnlineFactory.Password, passwordHash);
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
        Assert.NotEqual(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed, hasher.VerifyHashedPassword("test", passwordHash, OnlineFactory.Password));
        string stored = await connection.QuerySingleAsync<string>("SELECT TokenHash FROM AuthSessions s JOIN Accounts a ON a.AccountId=s.AccountId WHERE a.Username=@Name", new { Name = player.Username });
        Assert.NotEqual(player.Token, stored);
        Assert.Equal(64, stored.Length);
    }

    [Fact, Trait("Category", "Accounts")]
    public async Task ExpiredAndRevokedSession_CannotReadProfile()
    {
        await using var factory = new OnlineFactory();
        var player = await factory.NewPlayerAsync();
        using var loggedOut = await player.Client.DeleteAsync("/api/online/sessions/current");
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await player.Client.GetAsync("/api/online/profile")).StatusCode);
        using var next = factory.CreateClient(); await factory.LoginAsync(next, player.Username);
        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, (await next.GetAsync("/api/online/profile")).StatusCode);
    }

    [Fact, Trait("Category", "Accounts")]
    public async Task UsernameUnique_IgnoresCaseAndDoesNotCreateHalfProfile()
    {
        await using var factory = new OnlineFactory();
        var player = await factory.NewPlayerAsync();
        using var duplicate = await player.Client.PostAsJsonAsync("/api/online/accounts", new RegisterAccountRequest { Username = player.Username.ToUpperInvariant(), Password = OnlineFactory.Password }, OnlineFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await using var connection = new MySqlConnection(factory.Connection);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Accounts a JOIN Characters c ON c.AccountId=a.AccountId JOIN Wallets w ON w.CharacterId=c.CharacterId WHERE a.NormalizedUsername=@Name", new { Name = player.Username.ToUpperInvariant() }));
    }

    [Fact, Trait("Category", "Accounts")]
    public async Task WrongManifest_IsRejectedBeforeIssuingSession()
    {
        await using var factory = new OnlineFactory();
        var player = await factory.NewPlayerAsync();
        var request = factory.LoginRequest(player.Username); request.ContentManifestHash = new string('0', 64);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/online/sessions", request, OnlineFactory.Json)).StatusCode);
    }

    [Fact, Trait("Category", "Accounts")]
    public async Task AuthenticationRequests_AreRateLimitedWithoutUnboundedUsernameBuckets()
    {
        await using var factory = new OnlineFactory(2); using var client = factory.CreateClient();
        var request = factory.LoginRequest("missing_account");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/online/sessions", request, OnlineFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/online/sessions", request, OnlineFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/online/sessions", request, OnlineFactory.Json)).StatusCode);
    }
}
