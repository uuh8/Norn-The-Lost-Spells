using System;
using Game.ElementField;

namespace Game.Rendering
{
    /// <summary>
    /// 把一个 Chunk 及其三维邻域的 Gameplay Version 混合为 Water Presentation 签名。
    ///
    /// 体积水的 SDF Primitive、Sample Halo 和 Crossing Direction Normal 都会跨越 Chunk 边界读取数据；
    /// 因此只监听自身或六轴邻居不足以覆盖对角依赖。该纯函数不分配临时集合，可独立测试。
    /// </summary>
    public static class WorldWaterVisualVersion
    {
        public static uint Calculate(
            IElementWorldReadOnly world,
            ElementChunkKey center,
            int neighborRadiusInChunks)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (neighborRadiusInChunks < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(neighborRadiusInChunks));
            }

            unchecked
            {
                uint hash = 2166136261u;
                for (int z = -neighborRadiusInChunks;
                     z <= neighborRadiusInChunks;
                     z++)
                {
                    for (int y = -neighborRadiusInChunks;
                         y <= neighborRadiusInChunks;
                         y++)
                    {
                        for (int x = -neighborRadiusInChunks;
                             x <= neighborRadiusInChunks;
                             x++)
                        {
                            var key = new ElementChunkKey(
                                center.X + x,
                                center.Y + y,
                                center.Z + z);
                            hash =
                                (hash ^ world.GetChunkVersion(key)) *
                                16777619u;
                        }
                    }
                }

                return hash;
            }
        }
    }
}
