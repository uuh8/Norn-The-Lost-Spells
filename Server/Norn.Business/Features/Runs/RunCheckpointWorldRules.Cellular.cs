using Game.NetworkProtocol;

namespace Norn.Business.Features.Runs;

public static partial class RunCheckpointWorldRules
{
    // 正文已经通过固定长度、排序、Checksum 和所属 Chunk 检查。盒与点直接按空间相交测试，
    // 无论作者是否配置整片湖，业务进程都不会把体积展开成数组。
    private static void ValidateCellular(OnlineRunAssetCheckpointChunk chunk, int firstSpan)
    {
        uint[] w = chunk.Words;
        int endCells = 15 + (int)w[1] * 7;
        ulong quantity = 0;
        bool bulk = false, solid = false;
        for (int i = 15; i < endCells; i += 7)
        {
            if (i == 15 || w[i+2] != w[i-5] || w[i+3] != w[i-4] || w[i+4] != w[i-3]) { quantity = 0; bulk = solid = false; }
            uint m = w[i+5];
            if (m is 0 or 2 or > 12) throw Failure();
            bool gas = m is 7 or 8;
            if (gas && solid || !gas && bulk) throw Failure();
            if (!gas)
            {
                bulk = true; solid = m is 5 or 9 or 10 or 11;
                if (solid && quantity != 0) throw Failure();
            }
            quantity += w[i+6];
            if (quantity > w[13]) throw Failure();
        }
        (uint Kind,int X,int Y,int Z)? previous = null;
        for (int i = firstSpan; i < w.Length; i += 10)
        {
            uint kind = w[i], material = w[i+7], original = w[i+8], amount = w[i+9];
            if (kind == 1)
            {
                if (material is not (0 or 9 or 10) || original != 0 || amount != 0) throw Failure();
            }
            else if (kind switch {
                2 => material is not (1 or 3 or 4 or 12) || original != w[13] || amount >= w[13],
                3 => material is not (1 or 3 or 4 or 5 or 6 or 11 or 12) || original != 0 || amount == 0 || amount > w[13],
                _ => true }) throw Failure();
            var order = (kind,(int)w[i+1],(int)w[i+2],(int)w[i+3]);
            if (previous.HasValue && previous.Value.CompareTo(order) >= 0) throw Failure();
            previous = order;
            for (int axis = 0; axis < 3; axis++)
            {
                int start = unchecked((int)w[i+1+axis]);
                uint size = w[i+4+axis];
                long end = (long)start + size;
                int expected = axis == 0 ? chunk.X : axis == 1 ? chunk.Y : chunk.Z;
                if (size is 0 or > int.MaxValue || end > int.MaxValue ||
                    Divide(start,(int)w[10]) != expected || Divide((int)(end-1),(int)w[10]) != expected) throw Failure();
            }
            for (int j = firstSpan; j < i; j += 10)
            {
                if (Intersects(w,i,j,false) && (kind == w[j] || kind != 1 && w[j] != 1 || Occupies(w,i) && Occupies(w,j))) throw Failure();
            }
            if (!Occupies(w,i)) continue;
            (uint,uint,uint)? lastGas = null;
            ulong gasQuantity = 0;
            for (int j = 15; j < endCells; j += 7)
            {
                if (!Intersects(w,i,j,true)) continue;
                if (kind == 1 || material is 5 or 11 || w[j+5] is not (7 or 8)) throw Failure();
                var point = (w[j+2],w[j+3],w[j+4]);
                if (lastGas != point) { lastGas = point; gasQuantity = 0; }
                gasQuantity += w[j+6];
                if (gasQuantity + amount > w[13]) throw Failure();
            }
        }
    }

    private static bool Occupies(uint[] w, int i) => w[i] == 1 ? w[i+7] != 0 : w[i+9] != 0;
    private static bool Intersects(uint[] w, int a, int b, bool point)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            long lowA = unchecked((int)w[a+1+axis]), lowB = unchecked((int)w[b+(point?2:1)+axis]);
            if (lowA + w[a+4+axis] <= lowB || lowB + (point?1u:w[b+4+axis]) <= lowA) return false;
        }
        return true;
    }
}
