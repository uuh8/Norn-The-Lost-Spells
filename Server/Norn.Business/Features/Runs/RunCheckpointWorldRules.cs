using Game.NetworkProtocol;
using Norn.Business.Infrastructure;

namespace Norn.Business.Features.Runs;

/// <summary>原canonical uint布局的业务中立校验；不引用Unity，也不把Checksum当作领域合法性。</summary>
public static class RunCheckpointWorldRules
{
    public static void Require(OnlineRunAssetCheckpointWorld? world, OnlineRunAssetCheckpointWorld? previous = null)
    {
        if (world == null || string.IsNullOrWhiteSpace(world.WorldId) || world.WorldId.Length > 128 || world.WorldEpoch == 0
            || world.WorldTick < 0 || world.CanRestoreAuthorityRules || world.Chunks == null
            || world.Chunks.Length > OnlineRunAssetCheckpoint.MaximumWorldKeys
            || world.Chunks.Length == 0 && world.CommitSequence != 0) throw Failure();
        var keys = new Dictionary<(uint, int, int, int), OnlineRunAssetCheckpointChunk>();
        var geometries = new Dictionary<uint, uint[]>();
        var queues = new HashSet<(uint, uint)>();
        foreach (var chunk in world.Chunks)
        {
            if (chunk == null || chunk.Domain > 3 || chunk.WorldEpoch != world.WorldEpoch || chunk.SnapshotId == 0
                || chunk.SnapshotId > world.CommitSequence || chunk.Generation == 0 || chunk.Revision == 0
                || chunk.BaseRevision >= chunk.Revision || chunk.ServerTick < 0 || chunk.ServerTick > world.WorldTick
                || chunk.Words == null || chunk.WordCount != chunk.Words.Length || chunk.WordCount < 0
                || chunk.WordCount > OnlineRunAssetCheckpoint.MaximumWordsPerChunk
                || !keys.TryAdd(Key(chunk), chunk) || Checksum(chunk.Words) != chunk.Checksum) throw Failure();
            if (!chunk.IsResident)
            {
                // 删除头仍是历史Key。空FNV值是offset basis，不是0，也没有resident prefix可读取。
                if (chunk.WordCount != 0 || chunk.Checksum != 2166136261) throw Failure();
                continue;
            }
            ValidatePayload(chunk, geometries, queues);
        }
        if (previous == null) return;
        if (world.WorldId != previous.WorldId || world.WorldEpoch != previous.WorldEpoch
            || world.CommitSequence < previous.CommitSequence || world.WorldTick < previous.WorldTick || previous.Chunks == null) throw Failure();
        foreach (var old in previous.Chunks)
        {
            if (old == null || !keys.TryGetValue(Key(old), out var current) || current.Generation < old.Generation
                || current.Generation == old.Generation && current.Revision < old.Revision) throw Failure();
            if (current.Generation == old.Generation && current.Revision == old.Revision)
            {
                // Checkpoint保存的是当前完整Baseline，不是Chunk最后一次内容提交的历史头。
                // DS的TryCopyChunk会沿用Generation/Revision和原payload，却以本次全局CommitSequence、
                // 采样WorldTick重新封装SnapshotId/ServerTick；即使没有材料变化，采样时间也会推进。
                // 因此这两个捕获水位允许单调不降，并继续受前面的World提交序列/时间上界约束。
                // 内容身份仍严格不变：BaseRevision、驻留状态、Checksum及每个uint都必须相同，
                // 不能借“新Baseline”在同Revision下改材料数量/等待队列，或倒退到旧采样水位。
                // 这也保留已持久化Baseline Head的接续语义，无需重写历史正文或其Receipt Hash。
                if (current.SnapshotId < old.SnapshotId || current.BaseRevision != old.BaseRevision || current.ServerTick < old.ServerTick
                    || current.IsResident != old.IsResident || current.Checksum != old.Checksum || !current.Words.SequenceEqual(old.Words)) throw Failure();
            }
            else if (current.SnapshotId <= old.SnapshotId) throw Failure();
        }
    }

    private static void ValidatePayload(OnlineRunAssetCheckpointChunk c, Dictionary<uint, uint[]> geometries, HashSet<(uint, uint)> queues)
    {
        uint[] w = c.Words;
        if (w.Length < 11 || w[0] != 0x4E355731 || w[3] != 1 || w[1] > int.MaxValue || w[2] > int.MaxValue
            || 11L + w[1] * 7L + w[2] * 18L != w.Length || w[4] != c.Domain || w[5] != c.Domain
            || !Finite(w[6]) || !Finite(w[7]) || !Finite(w[8]) || !Finite(w[9]) || Float(w[9]) <= 0
            || w[10] == 0 || w[10] > int.MaxValue) throw Failure();
        if (geometries.TryGetValue(c.Domain, out var geometry))
        { for (int g = 0; g < 7; g++) if (geometry[g] != w[g + 4]) throw Failure(); }
        else geometries.Add(c.Domain, w.AsSpan(4, 7).ToArray());
        int size = (int)w[10], offset = 11;
        (int X, int Y, int Z, uint Material)? last = null;
        for (uint cell = 0; cell < w[1]; cell++, offset += 7)
        {
            int x = unchecked((int)w[offset + 2]), y = unchecked((int)w[offset + 3]), z = unchecked((int)w[offset + 4]);
            uint material = w[offset + 5], quantity = w[offset + 6];
            if (w[offset] != c.Domain || w[offset + 1] != 0 || material is 0 or > 255 || quantity == 0
                || c.Domain == 0 && quantity > 255 || Divide(x, size) != c.X || Divide(y, size) != c.Y || Divide(z, size) != c.Z) throw Failure();
            var point = (x, y, z, material);
            if (last is { } prior && Compare(prior, point) >= 0) throw Failure();
            last = point;
        }
        long lastQueue = -1;
        for (uint pending = 0; pending < w[2]; pending++, offset += 18)
        {
            uint index = w[offset + 1];
            if (w[offset] != c.Domain || index > int.MaxValue || index <= lastQueue || !queues.Add((c.Domain, index))
                || w[offset + 2] is 0 or > 255 || w[offset + 6] is 0 or > ushort.MaxValue || w[offset + 8] > 1
                || w[offset + 15] > w[offset + 6] || w[offset + 16] > int.MaxValue
                || !Vector(w, offset + 3) || !Vector(w, offset + 9) || !Vector(w, offset + 12)
                || !Finite(w[offset + 7]) || Float(w[offset + 7]) < 0
                || !PositionMatches(w, offset + 3, c, size)) throw Failure();
            lastQueue = index;
        }
    }

    private static bool PositionMatches(uint[] words, int index, OnlineRunAssetCheckpointChunk c, int size)
    {
        int[] expected = [c.X, c.Y, c.Z];
        for (int axis = 0; axis < 3; axis++)
        {
            // 按原Codec使用double中间值；不能先float溢出/夹到int，再将错误位置归到合法Key。
            double cell = Math.Floor(((double)Float(words[index + axis]) - Float(words[6 + axis])) / Float(words[9]));
            if (cell < int.MinValue || cell > int.MaxValue || Divide((int)cell, size) != expected[axis]) return false;
        }
        return true;
    }
    private static int Compare((int X, int Y, int Z, uint Material) a, (int X, int Y, int Z, uint Material) b)
    {
        int result = a.X.CompareTo(b.X); if (result != 0) return result;
        result = a.Y.CompareTo(b.Y); if (result != 0) return result;
        result = a.Z.CompareTo(b.Z); return result != 0 ? result : a.Material.CompareTo(b.Material);
    }
    private static (uint, int, int, int) Key(OnlineRunAssetCheckpointChunk c) => (c.Domain, c.X, c.Y, c.Z);
    private static int Divide(int value, int divisor) { int q = value / divisor; return value % divisor < 0 ? q - 1 : q; }
    private static float Float(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    private static bool Finite(uint bits) => float.IsFinite(Float(bits));
    private static bool Vector(uint[] words, int index) => Finite(words[index]) && Finite(words[index + 1]) && Finite(words[index + 2]);
    private static uint Checksum(uint[] words)
    {
        uint hash = 2166136261;
        unchecked { foreach (uint word in words) { uint v = word; for (int b = 0; b < 4; b++) { hash = (hash ^ (v & 255)) * 16777619; v >>= 8; } } }
        return hash;
    }
    private static OnlineFailure Failure() => new(409, "RunCheckpointWorldInvalid");
}
