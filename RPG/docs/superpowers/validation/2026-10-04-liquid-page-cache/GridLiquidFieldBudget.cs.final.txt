using System;

namespace Game.Rendering
{
    /// <summary>只计算初始化资源，不在Update临时扩大GraphicsBuffer。输入Chunk共享Halo。</summary>
    public readonly struct GridLiquidFieldBudget
    {
        public readonly int InputChunks, Views, FineViews, LookupSlots, MaximumCubeSlots, MaximumNodeSlots, MaximumIndices;
        public readonly long Bytes;
        public readonly int ChunkSize, MaterialCount;
        public GridLiquidFieldBudget(int chunkSize, int materialCount, int inputChunks, int views, int fineViews, int mebibytes,long reservedBytes=0)
        {
            if (chunkSize < 1 || materialCount < 1 || materialCount > 8 || inputChunks < 1 || views < 1 || fineViews < 0 || mebibytes < 1)
                throw new ArgumentOutOfRangeException();
            ChunkSize = chunkSize; MaterialCount = materialCount; Views = views;
            int inputs = inputChunks, fine = Math.Min(fineViews, views);
            long limit = mebibytes * 1048576L-reservedBytes;
            while (Estimate(chunkSize, materialCount, inputs, views, fine) > limit)
            {
                if (fine > 0) fine--;
                else if (inputs > views) inputs--;
                else throw new InvalidOperationException("Grid液体GPU预算不足以覆盖配置页面，必须降低页数或使用CPU参考路径。");
            }
            InputChunks = inputs; FineViews = fine;
            LookupSlots = 1; while (LookupSlots < inputs * 2) LookupSlots <<= 1;
            MaximumCubeSlots = checked(Math.Max(Cube(chunkSize + 6) * views, Cube(chunkSize * 2 + 6) * fine));
            MaximumNodeSlots = checked(Math.Max(Cube(chunkSize + 7) * views, Cube(chunkSize * 2 + 7) * fine));
            MaximumIndices = checked(18 * Math.Max(Cube(chunkSize) * views, Cube(chunkSize * 2) * fine));
            Bytes = Estimate(chunkSize, materialCount, inputs, views, fine)+reservedBytes;
        }
        private static int Cube(int v) => checked(v * v * v);
        private static long Estimate(int k, int materials, int inputs, int views, int fine)
        {
            int table = 1; while (table < inputs * 2) table <<= 1;
            long cells = (long)Cube(k) * inputs;
            long cubes = Math.Max((long)Cube(k + 6) * views, (long)Cube(k * 2 + 6) * fine);
            long nodes = Math.Max((long)Cube(k + 7) * views, (long)Cube(k * 2 + 7) * fine);
            long indices = 18L * Math.Max((long)Cube(k) * views, (long)Cube(k * 2) * fine);
            // 两帧16B输入、16B地形、48B顶点、两份12B平滑位置、4B场/索引。
            // 附加空间覆盖Args、页元数据、Lookup和小液滴实例，计入真正的共享预算。
            return cells * (32L * materials + 16L) + cubes * 72L + nodes * 4L + indices * 4L
                + table * 32L + views * 80L + 65536L * 32L + 16384L;
        }
    }
}
