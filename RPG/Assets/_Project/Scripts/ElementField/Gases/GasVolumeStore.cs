using Game.Materials;

namespace Game.ElementField
{
    /// <summary>气体可在同格混合；共享容量，真实量不会随透明度或镜头变化消失。</summary>
    public sealed class GasVolumeStore : SparseMaterialVolumeStore
    {
        public GasVolumeStore(int chunkSize, int maximumChunks, uint capacity,
            MaterialId[] materials) : base(chunkSize, maximumChunks, capacity, materials) { }
    }
}
