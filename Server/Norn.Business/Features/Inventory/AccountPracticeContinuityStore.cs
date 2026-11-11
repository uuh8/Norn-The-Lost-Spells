using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Game.NetworkProtocol;
using MySqlConnector;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Inventory;

/// <summary>
/// 同Bearer派生作用域下的和平资源连续性载体。它不拥有物品/金币，不运行Skills解释器，
/// 只保存可信Home最终事务的压缩壳；新登录的不同scope既不读出旧壳，也不把旧壳删成“已恢复”。
/// </summary>
internal static class AccountPracticeContinuityStore
{
    internal static bool IsScope(string? value) => value != null && value.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static async Task<string> ReadAsync(MySqlConnection db, MySqlTransaction tx, string player,
        string? scope, string gameplayHash, CancellationToken ct)
    {
        if (scope == null) return ""; // 原内部读口无会话信息，不猜测别人的连续性，也不触发兼容写入。
        if (!IsScope(scope)) throw Invalid();
        var row = await db.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            "SELECT PayloadJson,PayloadHash FROM AccountPracticeContinuity WHERE CharacterId=@Player AND SessionScopeHash=@Scope",
            new { Player = player, Scope = scope }, tx, cancellationToken: ct));
        if (row == null) return "";
        Validate(row.PayloadJson, row.PayloadHash, player, gameplayHash); return row.PayloadJson;
    }
    internal static async Task WriteAsync(MySqlConnection db, MySqlTransaction tx, string player, string run,
        string scope, string json, string hash, string gameplayHash, CancellationToken ct)
    {
        if (!IsScope(scope)) throw Invalid();
        Validate(json, hash, player, gameplayHash);
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO AccountPracticeContinuity(CharacterId,SessionScopeHash,SourceRunId,PayloadJson,PayloadHash,RecordedAtUtc)
            VALUES(@Player,@Scope,@Run,@Payload,@Hash,UTC_TIMESTAMP(6))
            ON DUPLICATE KEY UPDATE SessionScopeHash=@Scope,SourceRunId=@Run,PayloadJson=@Payload,PayloadHash=@Hash,RecordedAtUtc=UTC_TIMESTAMP(6)
            """, new { Player = player, Scope = scope, Run = run, Payload = json, Hash = hash }, tx, cancellationToken: ct));
    }
    /// <summary>
    /// 业务严格核wrapper形状/身份/SHA/编码预算，不另写Core图规则。实际gzip解压与完整图/Trace
    /// 由DS的Shared流式24MiB硬上限+Core严格decode完成；不能按声明长度直接分配无界数组。
    /// </summary>
    internal static void Validate(string json, string hash, string player, string gameplayHash)
    {
        if (string.IsNullOrEmpty(json) || !IsScope(hash) || !IsScope(gameplayHash) || Encoding.UTF8.GetByteCount(json) > PracticeContinuityEnvelopeRules.MaximumEnvelopeBytes
            || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) != hash) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            var required = new HashSet<string>(StringComparer.Ordinal) { "SchemaVersion", "PlayerId", "GameplayConfigHash", "Encoding", "UncompressedBytes", "Data" };
            foreach (var field in document.RootElement.EnumerateObject()) if (!required.Remove(field.Name)) throw Invalid();
            if (required.Count != 0) throw Invalid();
            var envelope = JsonSerializer.Deserialize<OnlinePracticeContinuityEnvelope>(json, Norn.Business.Content.ContentManifestValidator.Json);
            if (envelope == null || envelope.SchemaVersion != 1 || envelope.PlayerId != player || envelope.GameplayConfigHash != gameplayHash
                || envelope.Encoding != "gzip-base64" || envelope.UncompressedBytes < 2 || envelope.UncompressedBytes > PracticeContinuityEnvelopeRules.MaximumRawBytes
                || string.IsNullOrEmpty(envelope.Data) || envelope.Data.Length > PracticeContinuityEnvelopeRules.MaximumEnvelopeBytes - 1024) throw Invalid();
            byte[] compressed = Convert.FromBase64String(envelope.Data);
            if (compressed.Length < 3 || compressed[0] != 0x1f || compressed[1] != 0x8b || compressed[2] != 8
                || Convert.ToBase64String(compressed) != envelope.Data) throw Invalid();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException) { throw Invalid(); }
    }
    private static OnlineFailure Invalid() => new(409, "PracticeContinuityInvalid");
    private sealed class Row { public string PayloadJson { get; set; } = ""; public string PayloadHash { get; set; } = ""; }
}
