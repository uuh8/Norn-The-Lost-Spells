using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Game.NetworkProtocol;
using Microsoft.AspNetCore.Identity;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Features.Inventory;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Accounts;

/// <summary>账户/角色初始化由一个事务提交；Client 没有初始物品、余额或 PlayerId 的指定权。</summary>
public sealed class AccountService(OnlineDatabase database, ContentManifestValidator content, OnlineOptions options, TimeProvider clock, InventoryService inventory)
{
    // 新角色创建时的初始余额，不是登录奖励或余额保底。只在注册事务里插入新钱包，
    // 账号唯一键/钱包主键与事务一起保证成功一次；旧号和已花掉的金币绝不由登录补回。
    private const long RegistrationGold = 10000;
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
            await connection.ExecuteAsync(new CommandDefinition("INSERT INTO Characters VALUES(@Player,@Account,1,'online.hub.spawn'); INSERT INTO Wallets VALUES(@Player,@Gold)", new { Player = player, Account = account, Gold = RegistrationGold }, transaction, cancellationToken: ct));
            string wand = Guid.NewGuid().ToString("N"), spell = Guid.NewGuid().ToString("N");
            for (int i = 0; i < options.StarterContentIds.Length; i++)
            {
                string id = options.StarterContentIds[i]; content.RequireItem(id);
                string instance = i == 0 ? wand : spell;
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO Items(InstanceId,OwnerCharacterId,ContentId,Revision,RemainingCharges,HasUnlimitedCharges)
                    VALUES(@Instance,@Player,@Content,1,@Charges,FALSE);
                    INSERT INTO ItemLocations(InstanceId,OwnerCharacterId,Area,Slot,CombatLocationKind,ParentInstanceId)
                    VALUES(@Instance,@Player,NULL,0,@Kind,@Parent)
                    """, new { Instance = instance, Player = player, Content = id, Charges = i == 1 ? options.StarterSpellCharges : 0,
                    Kind = i == 0 ? 3 : 2, Parent = i == 0 ? null : wand }, transaction, cancellationToken: ct));
            }
            // 注册是明确业务授权点；DS 只读取冻结结果，不补发、复制或推导无限资格。
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO CharacterCombatLoadouts(CharacterId,SelectedWandInstanceId) VALUES(@Player,@Wand);
                INSERT INTO OwnedCombatStarterAudits(CharacterId,GrantVersion,Status,WandInstanceId,SpellInstanceId,BeforeJson,RecordedAtUtc)
                VALUES(@Player,1,'Applied',@Wand,@Spell,JSON_OBJECT('Reason','RegistrationV1','Charges',@Charges),UTC_TIMESTAMP(6))
                """, new { Player = player, Wand = wand, Spell = spell, Charges = options.StarterSpellCharges }, transaction, cancellationToken: ct));
            // 最小包属于账号初始资产，必须先于第一次Profile存在。与杖卡、钱包同事务提交，
            // 来源明确为AccountBaseline，不创建假Run，也不借首次GET掩盖注册漏发。
            await inventory.GrantRegistrationBackpackAsync(connection, transaction, player, ct);
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
    /// <summary>只供已认证Endpoint派生和平scope；原Bearer和认证表TokenHash都不出服务边界、不写日志。</summary>
    internal static string PracticeSessionScope(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Norn.PracticeSession.v1:" + HashToken(token))));

    private static void ValidateCredentials(string? username, string? password)
    {
        if (username == null || username.Length < 3 || username.Length > 24 || password == null || password.Length < 10 || password.Length > 128)
            throw new OnlineFailure(400, "InvalidCredentialsFormat");
        foreach (char c in username)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) throw new OnlineFailure(400, "InvalidCredentialsFormat");
    }

    private sealed class AccountRow { public string AccountId { get; set; } = ""; public string PasswordHash { get; set; } = ""; }
}
