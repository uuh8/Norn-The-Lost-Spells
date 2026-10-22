using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Identity;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Accounts;

/// <summary>账户/角色初始化由一个事务提交；Client 没有初始物品、余额或 PlayerId 的指定权。</summary>
public sealed class AccountService(OnlineDatabase database, ContentManifestValidator content, OnlineOptions options, TimeProvider clock)
{
    private readonly PasswordHasher<string> _hasher = new();
    // 不存在的账号也做 PasswordHasher 验证，避免直接跳过昂贵计算而明显泄漏用户名存在性。
    private readonly string _dummyHash = new PasswordHasher<string>().HashPassword("dummy", "not-a-real-account-password");

    public async Task<RegisterAccountReply> RegisterAsync(RegisterAccountRequest request, CancellationToken ct)
    {
        ValidateCredentials(request.Username, request.Password);
        string account = Guid.NewGuid().ToString("N"), player = Guid.NewGuid().ToString("N");
        string hash = _hasher.HashPassword(account, request.Password);
        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO Accounts VALUES(@Account,@Username,@Normalized,@Hash,@Now)",
                new { Account = account, request.Username, Normalized = request.Username.ToUpperInvariant(), Hash = hash, Now = clock.GetUtcNow().UtcDateTime }, transaction, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO Characters VALUES(@Player,@Account,1,'online.hub.spawn'); INSERT INTO Wallets VALUES(@Player,0)", new { Player = player, Account = account }, transaction, cancellationToken: ct));
            for (int i = 0; i < options.StarterContentIds.Length; i++)
            {
                string id = options.StarterContentIds[i]; content.RequireItem(id);
                string instance = Guid.NewGuid().ToString("N");
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO Items VALUES(@Instance,@Player,@Content,1);
                    INSERT INTO ItemLocations VALUES(@Instance,@Player,'Backpack',@Slot)
                    """, new { Instance = instance, Player = player, Content = id, Slot = i }, transaction, cancellationToken: ct));
            }
            await transaction.CommitAsync(ct);
            return new RegisterAccountReply { Username = request.Username };
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            // using/DisposeAsync 回滚尚未提交的账户、钱包和实例，不留下半个线上角色。
            throw new OnlineFailure(409, "UsernameTaken");
        }
    }

    public async Task<LoginAccountReply> LoginAsync(LoginAccountRequest request, CancellationToken ct)
    {
        ValidateCredentials(request.Username, request.Password);
        content.RequireVersion(request.ProtocolVersion, request.ContentManifestHash);
        await using var connection = await database.OpenAsync(ct);
        var account = await connection.QuerySingleOrDefaultAsync<AccountRow>(new CommandDefinition("SELECT AccountId,PasswordHash FROM Accounts WHERE NormalizedUsername=@Name", new { Name = request.Username.ToUpperInvariant() }, cancellationToken: ct));
        var verified = _hasher.VerifyHashedPassword(account?.AccountId ?? "dummy", account?.PasswordHash ?? _dummyHash, request.Password);
        if (account == null || verified == PasswordVerificationResult.Failed) throw new OnlineFailure(401, "InvalidCredentials");
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
            await connection.ExecuteAsync(new CommandDefinition("UPDATE Accounts SET PasswordHash=@NewHash WHERE AccountId=@Id AND PasswordHash=@OldHash",
                new { NewHash = _hasher.HashPassword(account.AccountId, request.Password), Id = account.AccountId, OldHash = account.PasswordHash }, cancellationToken: ct));
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        DateTimeOffset expires = clock.GetUtcNow().AddMinutes(options.SessionMinutes);
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO AuthSessions VALUES(@Hash,@Id,@Expires)", new { Hash = HashToken(token), Id = account.AccountId, Expires = expires.UtcDateTime }, cancellationToken: ct));
        return new LoginAccountReply { Token = token, ExpiresUnixMilliseconds = expires.ToUnixTimeMilliseconds() };
    }

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static void ValidateCredentials(string? username, string? password)
    {
        if (username == null || username.Length < 3 || username.Length > 24 || password == null || password.Length < 10 || password.Length > 128)
            throw new OnlineFailure(400, "InvalidCredentialsFormat");
        foreach (char c in username)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) throw new OnlineFailure(400, "InvalidCredentialsFormat");
    }

    private sealed class AccountRow { public string AccountId { get; set; } = ""; public string PasswordHash { get; set; } = ""; }
}
