using Game.NetworkProtocol;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using MySqlConnector;
using Norn.Business.Content;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Inventory;

/// <summary>
/// 真实roll的内部事务载体。调用方必须已在同一获授权事务中锁角色并新建模式1物品；
/// 本类不创建/转移物品、不选择位置、不改模式0、不提供HTTP入口，也不自行提交事务。
/// </summary>
internal static class PersistentWandStateStore
{
    internal const int MaximumStateJsonBytes = 4096;
    private static readonly JsonSerializerOptions Json = new(ContentManifestValidator.Json)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    { "MaxMana", "ManaRegenPerSecond", "BaseCastDelay", "BaseReloadTime", "BaseSpreadDegrees",
        "ProgramCapacity", "BaseDraws", "Shuffle", "AlwaysCastContentIds" };

    internal static async Task WriteNewAsync(MySqlConnection db, MySqlTransaction tx, string owner, string instance,
        OnlineOwnedWandStatsSnapshot stats, ContentManifestValidator content, CancellationToken ct)
    {
        if (!IsId(owner) || !IsId(instance) || stats == null || stats.AlwaysCastContentIds == null
            || stats.AlwaysCastContentIds.Length > 4 || stats.AlwaysCastContentIds.Any(id => !ContentManifestRules.IsContentId(id)))
            throw Failure();
        // 在任何await前复制成完整有界值，调用者后续改DTO不会改写已确认的本次固定属性。
        string payload;
        try { payload = JsonSerializer.Serialize(stats, Json); }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw Failure(); }
        string hash = Hash(payload);
        var frozen = Decode(1, payload, hash);
        // 角色锁仍由上层业务事务按既有顺序持有；这里锁物品行保护同ID并发写，绝不自己开/提交事务。
        var item = await db.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            "SELECT OwnerCharacterId,ContentId,WandStatsMode FROM Items WHERE InstanceId=@Id FOR UPDATE",
            new { Id = instance }, tx, cancellationToken: ct));
        if (item == null || item.OwnerCharacterId != owner || item.WandStatsMode != 1 || content.RequireItem(item.ContentId).Kind != 1)
            throw Failure();
        Validate(item.ContentId, frozen, content);
        var existing = await db.QuerySingleOrDefaultAsync<StateRow>(new CommandDefinition(
            "SELECT InstanceId,OwnerCharacterId,SchemaVersion,PayloadJson,StateHash FROM ItemWandStates WHERE InstanceId=@Id FOR UPDATE",
            new { Id = instance }, tx, cancellationToken: ct));
        if (existing != null)
        {
            if (existing.OwnerCharacterId != owner) throw Failure();
            var current = Decode(existing.SchemaVersion, existing.PayloadJson, existing.StateHash);
            Validate(item.ContentId, current, content);
            if (!Same(current, frozen)) throw new OnlineFailure(409, "PersistentWandStateConflict");
            return; // 同值重试保留原文本/Hash，不UPDATE、不推进任何资产版本。
        }
        await db.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ItemWandStates(InstanceId,OwnerCharacterId,SchemaVersion,PayloadJson,StateHash)
            VALUES(@Id,@Owner,1,@Payload,@Hash)
            """, new { Id = instance, Owner = owner, Payload = payload, Hash = hash }, tx, cancellationToken: ct));
    }

    /// <summary>与Profile/RunEntry共用同一读事务；不按Kind先过滤，仓库或非杖夹带状态也必须显式拒绝。</summary>
    internal static async Task<Dictionary<string, OnlineOwnedWandStatsSnapshot>> ReadAsync(MySqlConnection db,
        MySqlTransaction tx, string owner, int maximumItems, ContentManifestValidator content, CancellationToken ct)
    {
        // 已售出/耗尽身份的原roll行保留审计，但不能再占活跃Profile预算或成为可入局实例。
        // 与容器/材料侧表一致排除账号退休账本，避免多次交易后历史状态挤爆活跃资产上限。
        var rows = (await db.QueryAsync<StateRow>(new CommandDefinition("""
            SELECT s.InstanceId,s.OwnerCharacterId,s.SchemaVersion,s.PayloadJson,s.StateHash,i.ContentId,i.WandStatsMode
            FROM ItemWandStates s JOIN Items i ON i.InstanceId=s.InstanceId AND i.OwnerCharacterId=s.OwnerCharacterId
            WHERE s.OwnerCharacterId=@Owner
              AND NOT EXISTS (SELECT 1 FROM RunItemCustody rc WHERE rc.InstanceId=i.InstanceId AND rc.State IN ('Active','Retired'))
              AND NOT EXISTS (SELECT 1 FROM AccountItemRetirements ar WHERE ar.InstanceId=i.InstanceId)
            ORDER BY s.InstanceId LIMIT @Limit
            """, new { Owner = owner, Limit = maximumItems + 1 }, tx, cancellationToken: ct))).ToArray();
        if (rows.Length > maximumItems) throw new OnlineFailure(409, "InventoryCapacityExceeded");
        var result = new Dictionary<string, OnlineOwnedWandStatsSnapshot>(rows.Length, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.OwnerCharacterId != owner || row.WandStatsMode != 1 || content.RequireItem(row.ContentId).Kind != 1)
                throw Failure();
            var stats = Decode(row.SchemaVersion, row.PayloadJson, row.StateHash);
            Validate(row.ContentId, stats, content);
            if (!result.TryAdd(row.InstanceId, stats)) throw Failure();
        }
        return result;
    }

    internal static OnlineOwnedWandStatsSnapshot? Copy(string instance, Dictionary<string, OnlineOwnedWandStatsSnapshot> states)
    {
        if (!states.TryGetValue(instance, out var s)) return null;
        return new() { MaxMana = s.MaxMana, ManaRegenPerSecond = s.ManaRegenPerSecond, BaseCastDelay = s.BaseCastDelay,
            BaseReloadTime = s.BaseReloadTime, BaseSpreadDegrees = s.BaseSpreadDegrees, ProgramCapacity = s.ProgramCapacity,
            BaseDraws = s.BaseDraws, Shuffle = s.Shuffle, AlwaysCastContentIds = s.AlwaysCastContentIds.ToArray() };
    }

    private static OnlineOwnedWandStatsSnapshot Decode(uint schema, string payload, string hash)
    {
        if (schema != 1 || string.IsNullOrEmpty(payload) || Encoding.UTF8.GetByteCount(payload) > MaximumStateJsonBytes
            || hash == null || hash.Length != 64 || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || Hash(payload) != hash)
            throw Failure();
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Failure();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in document.RootElement.EnumerateObject())
            {
                // 不依赖DTO默认0/false/[]补齐缺字段；重复字段和未知字段也不能覆盖真正固定值。
                if (!Fields.Contains(field.Name) || !seen.Add(field.Name)) throw Failure();
                if (field.Name == "Shuffle")
                { if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Failure(); }
                else if (field.Name == "AlwaysCastContentIds")
                { if (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() > 4) throw Failure(); }
                else if (field.Value.ValueKind != JsonValueKind.Number) throw Failure();
            }
            if (seen.Count != Fields.Count) throw Failure();
            return JsonSerializer.Deserialize<OnlineOwnedWandStatsSnapshot>(payload, Json) ?? throw Failure();
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw Failure(); }
    }

    private static void Validate(string contentId, OnlineOwnedWandStatsSnapshot stats, ContentManifestValidator content)
        => OwnedCombatAssetRules.RequireCheckpointItemState(new OnlineOwnedItemSnapshot
        { ContentId = contentId, UseTemplateWandStats = false, WandStats = stats }, content.RequireItem(contentId), content);

    private static bool Same(OnlineOwnedWandStatsSnapshot a, OnlineOwnedWandStatsSnapshot b)
        => a.MaxMana == b.MaxMana && a.ManaRegenPerSecond == b.ManaRegenPerSecond && a.BaseCastDelay == b.BaseCastDelay
            && a.BaseReloadTime == b.BaseReloadTime && a.BaseSpreadDegrees == b.BaseSpreadDegrees
            && a.ProgramCapacity == b.ProgramCapacity && a.BaseDraws == b.BaseDraws && a.Shuffle == b.Shuffle
            && a.AlwaysCastContentIds.SequenceEqual(b.AlwaysCastContentIds, StringComparer.Ordinal);
    private static bool IsId(string value) => Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static OnlineFailure Failure() => new(409, "InvalidPersistentWandState");
    private sealed class ItemRow
    {
        public string OwnerCharacterId { get; set; } = "";
        public string ContentId { get; set; } = "";
        public int WandStatsMode { get; set; }
    }
    private sealed class StateRow
    {
        public string InstanceId { get; set; } = "";
        public string OwnerCharacterId { get; set; } = "";
        public uint SchemaVersion { get; set; }
        public string PayloadJson { get; set; } = "";
        public string StateHash { get; set; } = "";
        public string ContentId { get; set; } = "";
        public int WandStatsMode { get; set; }
    }
}
