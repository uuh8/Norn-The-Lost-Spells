using System.Text.Json;
using Game.NetworkProtocol;
using Norn.Business.Features.Runs;
using Norn.Business.Infrastructure;
using Xunit;

namespace Norn.Business.Tests;

/// <summary>原canonical uint布局的独立fixture；坏领域值重新计算正确Checksum，证明不能只验Hash。</summary>
public sealed class RunCheckpointWorldRulesTests
{
    [Fact]
    public void ResidentAndHistoricalTombstoneKeepCommittedFactsWithoutPromisingSolverRecovery()
    {
        var world = Valid(); RunCheckpointWorldRules.Require(world);
        world.Chunks = [.. world.Chunks, Tombstone()]; RunCheckpointWorldRules.Require(world);
    }

    [Fact]
    public void FirstSuccessfulEmptyCaptureCanHaveZeroCommitSequence()
        => RunCheckpointWorldRules.Require(new() { WorldId = "material-world", WorldEpoch = 42, WorldTick = 0,
            CommitSequence = 0, CanRestoreAuthorityRules = false, Chunks = [] });

    [Theory, InlineData("missing"), InlineData("chunks"), InlineData("world-id"), InlineData("epoch"), InlineData("tick"),
        InlineData("restore"), InlineData("capacity"), InlineData("duplicate"), InlineData("checksum"), InlineData("word-count"),
        InlineData("header-epoch"), InlineData("snapshot"), InlineData("generation"), InlineData("revision"), InlineData("future-tick"),
        InlineData("tombstone-payload"), InlineData("words-overflow")]
    public void MissingOversizedOrInvalidHeadersAreRejected(string defect)
    {
        var world = Valid(); var chunk = world.Chunks[0];
        switch (defect)
        {
            case "missing": world = null!; break;
            case "chunks": world.Chunks = null!; break;
            case "world-id": world.WorldId = ""; break;
            case "epoch": world.WorldEpoch = 0; break;
            case "tick": world.WorldTick = -1; break;
            case "restore": world.CanRestoreAuthorityRules = true; break;
            case "capacity": world.Chunks = new OnlineRunAssetCheckpointChunk[OnlineRunAssetCheckpoint.MaximumWorldKeys+1]; break;
            case "duplicate": world.Chunks = [chunk, Clone(chunk)]; break;
            case "checksum": chunk.Checksum++; break;
            case "word-count": chunk.WordCount--; break;
            case "header-epoch": chunk.WorldEpoch++; break;
            case "snapshot": chunk.SnapshotId = 4; break;
            case "generation": chunk.Generation = 0; break;
            case "revision": chunk.Revision = 0; break;
            case "future-tick": chunk.ServerTick = 6; break;
            case "tombstone-payload": chunk.IsResident = false; break;
            case "words-overflow": chunk.Words = new uint[32769]; chunk.WordCount = chunk.Words.Length; chunk.Checksum = Checksum(chunk.Words); break;
        }
        Assert.Throws<OnlineFailure>(() => RunCheckpointWorldRules.Require(world));
    }

    [Theory, InlineData("magic"), InlineData("geometry-count"), InlineData("grid-alias"), InlineData("nan"),
        InlineData("cell-size"), InlineData("chunk-size"), InlineData("domain"), InlineData("storage"), InlineData("material-alias"),
        InlineData("zero-quantity"), InlineData("wrong-key"), InlineData("cell-order"), InlineData("legacy-byte-overflow")]
    public void CorrectChecksumCannotAuthorizeInvalidGeometryCellsOrBitAliases(string defect)
    {
        var world = Valid(); var chunk = world.Chunks[0]; var w = chunk.Words;
        switch (defect)
        {
            case "magic": w[0] = 0; break;
            case "geometry-count": w[3] = 2; break;
            case "grid-alias": w[5] = 2; break;
            case "nan": w[6] = 0x7fc00000; break;
            case "cell-size": w[9] = 0; break;
            case "chunk-size": w[10] = 0; break;
            case "domain": w[11] = 2; break;
            case "storage": w[12] = 1; break;
            case "material-alias": w[16] = 257; break;
            case "zero-quantity": w[17] = 0; break;
            case "wrong-key": w[13] = 16; break;
            case "cell-order": chunk.Words = [.. w, 1, 0, 0, 0, 0, 1, 10]; chunk.Words[1] = 2; chunk.Words[13] = 1; break;
            case "legacy-byte-overflow": chunk.Domain = 0; w[4] = w[5] = w[11] = 0; w[17] = 256; break;
        }
        Refresh(chunk); Assert.Throws<OnlineFailure>(() => RunCheckpointWorldRules.Require(world));
    }

    [Theory, InlineData("total-alias"), InlineData("remaining"), InlineData("falloff"), InlineData("negative-queue"),
        InlineData("nan-velocity"), InlineData("duplicate-queue")]
    public void PendingQueueRetainsFullUnsignedAndFiniteSpatialRules(string defect)
    {
        var world = Valid(); var c = world.Chunks[0];
        c.Words = [0x4E355731, 0, 1, 1, 1, 1, 0, 0, 0, Float(1), 16,
            1, 0, 1, 0, 0, 0, 10, Float(.2f), 1, 0, 0, 0, 0, Float(1), 0, 5, 0, 0];
        switch (defect)
        {
            case "total-alias": c.Words[17] = 65536; break;
            case "remaining": c.Words[26] = 11; break;
            case "falloff": c.Words[19] = 2; break;
            case "negative-queue": c.Words[12] = uint.MaxValue; break;
            case "nan-velocity": c.Words[20] = 0x7fc00000; break;
            case "duplicate-queue": c.Words = [.. c.Words, .. c.Words.Skip(11)]; c.Words[2] = 2; break;
        }
        Refresh(c); Assert.Throws<OnlineFailure>(() => RunCheckpointWorldRules.Require(world));
    }

    [Fact]
    public void PriorResidentKeyCannotDisappearWhileAnotherValidOriginRemains()
    {
        var previous = Valid(); previous.Chunks = [previous.Chunks[0], ResidentAt(1)];
        var next = Next(4, 6);
        // 保留合法origin，排除“空世界且序号非0”的形状错误，精确覆盖旧resident漏报。
        RunCheckpointWorldRules.Require(previous); RunCheckpointWorldRules.Require(next);
        Assert.Throws<OnlineFailure>(() => RunCheckpointWorldRules.Require(next, previous));
    }

    [Fact]
    public void ConfirmedTombstoneMayBeOmittedFromTheNextCurrentHead()
    {
        var previous = Valid(); previous.Chunks = [previous.Chunks[0], Tombstone()];
        var next = Next(4, 6);
        // 业务已确认的上一Head明确记录此Key为空，下一Head才可省略；不依赖DS的Peer ACK冒充入库确认。
        RunCheckpointWorldRules.Require(previous); RunCheckpointWorldRules.Require(next);
        RunCheckpointWorldRules.Require(next, previous);
    }

    [Fact]
    public void ResidentMustBecomeAnExplicitTombstoneBeforeAConfirmedHeadCanOmitIt()
    {
        var previous = Valid(); previous.Chunks = [previous.Chunks[0], ResidentAt(1)];
        var removed = Next(4, 6);
        removed.Chunks = [removed.Chunks[0], new()
        {
            Domain = 1, X = 1, WorldEpoch = 42, SnapshotId = 4, Generation = 1,
            BaseRevision = 0, Revision = 2, ServerTick = 6, IsResident = false,
            Words = [], WordCount = 0, Checksum = 2166136261
        }];
        // DS可能已安全退休网络metadata，但上次业务Head仍有真实量；先提交可校验的空删除头，
        // 读回确认这一Head后，下一次才可省略，保持防止漏活量的原有保护。
        RunCheckpointWorldRules.Require(removed, previous);
        var afterConfirmation = Next(5, 7);
        RunCheckpointWorldRules.Require(afterConfirmation);
        RunCheckpointWorldRules.Require(afterConfirmation, removed);
    }

    [Fact]
    public void FullResidentLimitIsIndependentOfPeerInterestAndStillAcceptsLegacyWords()
    {
        var world = Valid();
        world.Chunks = Enumerable.Range(0, OnlineRunAssetCheckpoint.MaximumCurrentWorldKeys).Select(ResidentAt).ToArray();
        RunCheckpointWorldRules.Require(world);
    }

    [Fact]
    public void WorldWordBudgetUsesDecimalWireSizeAndDoesNotAdvanceOnRejection()
    {
        long bytes = OnlineRunAssetCheckpoint.MaximumPayloadBytes - 13;
        Assert.True(OnlineRunAssetCheckpoint.TryBudgetWorldWords([0, uint.MaxValue], ref bytes));
        Assert.Equal((long)OnlineRunAssetCheckpoint.MaximumPayloadBytes, bytes);
        Assert.False(OnlineRunAssetCheckpoint.TryBudgetWorldWords([0], ref bytes));
        Assert.Equal((long)OnlineRunAssetCheckpoint.MaximumPayloadBytes, bytes);
    }

    [Fact]
    public void AdditionalContinuityHeadersDoNotIncreaseTheResidentWorldLimit()
    {
        var world = Valid();
        world.Chunks = Enumerable.Range(0, OnlineRunAssetCheckpoint.MaximumCurrentWorldKeys+1).Select(ResidentAt).ToArray();
        // 多出的一倍表头预算只容纳零 payload 的删除证据。
        Assert.Throws<OnlineFailure>(() => RunCheckpointWorldRules.Require(world));
    }

    [Fact]
    public void CurrentResidentAndTwoHundredZeroPayloadContinuityHeadersFitTheHeadBudget()
    {
        var world = Valid();
        world.Chunks = [world.Chunks[0], .. Enumerable.Range(1, 200).Select(x =>
        {
            var tombstone = Tombstone(); tombstone.X = x; return tombstone;
        })];
        RunCheckpointWorldRules.Require(world);
    }

    [Fact]
    public void TotalHeadRowsRemainBoundedEvenWhenEveryExtraHeaderHasZeroPayload()
    {
        var world = Valid();
        world.Chunks = [world.Chunks[0], .. Enumerable.Range(1, OnlineRunAssetCheckpoint.MaximumWorldKeys).Select(x =>
        {
            var tombstone = Tombstone(); tombstone.X = x; return tombstone;
        })];
        Assert.Throws<OnlineFailure>(() => RunCheckpointWorldRules.Require(world));
    }

    internal static OnlineRunAssetCheckpointWorld Valid()
    {
        var chunk = new OnlineRunAssetCheckpointChunk { Domain = 1, WorldEpoch = 42, SnapshotId = 3,
            Generation = 1, Revision = 1, ServerTick = 5, IsResident = true,
            Words = [0x4E355731, 1, 0, 1, 1, 1, 0, 0, 0, Float(1), 16, 1, 0, 0, 0, 0, 1, 25] };
        Refresh(chunk);
        return new() { WorldId = "material-world", WorldEpoch = 42, CommitSequence = 3, WorldTick = 5,
            CanRestoreAuthorityRules = false, Chunks = [chunk] };
    }

    [Theory, InlineData(0), InlineData(1), InlineData(2), InlineData(3)]
    public void CellularCheckpointRetainsRealAmountsAndRejectsConflictingOwnersOrBase(int defect)
    {
        var world = Valid(); var c = world.Chunks[0]; c.Domain = 4;
        c.Words = [0x4E355732, 1, 0, 1, 4, 4, 0, 0, 0, Float(1), 16, 55, 0, 255, 3,
            4,0,0,0,0,11,9,
            1,0,0,0,1,1,1,0,0,0,
            2,0,0,0,1,1,1,1,255,0,
            3,1,0,0,4,4,4,5,0,128];
        Refresh(c); RunCheckpointWorldRules.Require(world);
        if (defect == 0) return;
        var previous = Valid(); previous.Chunks = [Clone(c)];
        world.CommitSequence = c.SnapshotId = 4; c.Revision++;
        if (defect == 1) c.Words[11] = 56;       // 同一地图生命期不能换基底，即使另发了新的 Revision。
        if (defect == 2) c.Words[31] = 1;        // 地形形状不能编码成假 GMU。
        if (defect == 3) c.Words[41] = 128;      // 同一地址的静水与沙不能同时拥有正量。
        Refresh(c);
        Assert.Throws<OnlineFailure>(() => RunCheckpointWorldRules.Require(world,previous));
    }
    private static OnlineRunAssetCheckpointChunk Tombstone() => new() { Domain = 1, X = 2, WorldEpoch = 42,
        SnapshotId = 2, Generation = 2, BaseRevision = 4, Revision = 5, ServerTick = 4, IsResident = false,
        Words = [], WordCount = 0, Checksum = 2166136261 };
    private static OnlineRunAssetCheckpointChunk ResidentAt(int x)
    {
        var chunk = Valid().Chunks[0];
        chunk.X = x; chunk.Words[13] = unchecked((uint)(x * 16)); Refresh(chunk); return chunk;
    }
    private static OnlineRunAssetCheckpointWorld Next(ulong sequence, long tick)
    {
        var world = Valid(); world.CommitSequence = sequence; world.WorldTick = tick;
        world.Chunks[0].SnapshotId = sequence; world.Chunks[0].ServerTick = tick; return world;
    }
    private static uint Float(float value) => BitConverter.SingleToUInt32Bits(value);
    private static void Refresh(OnlineRunAssetCheckpointChunk chunk) { chunk.WordCount = chunk.Words.Length; chunk.Checksum = Checksum(chunk.Words); }
    private static uint Checksum(uint[] words)
    {
        uint hash = 2166136261;
        unchecked { foreach (uint word in words) { uint v = word; for (int b = 0; b < 4; b++) { hash = (hash ^ (v & 255)) * 16777619; v >>= 8; } } }
        return hash;
    }
    private static OnlineRunAssetCheckpointChunk Clone(OnlineRunAssetCheckpointChunk chunk)
        => JsonSerializer.Deserialize<OnlineRunAssetCheckpointChunk>(JsonSerializer.Serialize(chunk, OnlineFactory.Json), OnlineFactory.Json)!;
}
