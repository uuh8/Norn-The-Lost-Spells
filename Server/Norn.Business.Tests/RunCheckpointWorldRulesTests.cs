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
            case "capacity": world.Chunks = new OnlineRunAssetCheckpointChunk[129]; break;
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
    public void PriorHistoricalKeyCannotDisappearInsteadOfBecomingTombstone()
    {
        var previous = Valid(); var next = Valid(); next.CommitSequence++; next.WorldTick++;
        next.Chunks = []; Assert.Throws<OnlineFailure>(() => RunCheckpointWorldRules.Require(next, previous));
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
    private static OnlineRunAssetCheckpointChunk Tombstone() => new() { Domain = 1, X = 2, WorldEpoch = 42,
        SnapshotId = 2, Generation = 2, BaseRevision = 4, Revision = 5, ServerTick = 4, IsResident = false,
        Words = [], WordCount = 0, Checksum = 2166136261 };
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
