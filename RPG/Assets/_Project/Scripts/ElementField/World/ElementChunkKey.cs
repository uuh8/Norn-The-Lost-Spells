using System;

namespace Game.ElementField
{
    /// <summary>
    /// 稀疏元素世界中一个 Chunk 的整数身份。它描述的是固定世界坐标，不是 GameObject 的 Transform。
    /// 使用值相等语义后，两个坐标相同的 Key 在 Dictionary 中会命中同一份 Chunk 数据。
    /// </summary>
    public readonly struct ElementChunkKey : IEquatable<ElementChunkKey>
    {
        public ElementChunkKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public int X { get; }
        public int Y { get; }
        public int Z { get; }

        public bool Equals(ElementChunkKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is ElementChunkKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            // 使用 unchecked 允许整数溢出自然回绕；Hash 只用于分桶，不要求保留原值。
            // 三个不同质数降低规则坐标（例如一整排 Chunk）产生重复 Hash 的概率。
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + X;
                hash = hash * 37 + Y;
                hash = hash * 41 + Z;
                return hash;
            }
        }

        public static bool operator ==(ElementChunkKey left, ElementChunkKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ElementChunkKey left, ElementChunkKey right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return $"({X}, {Y}, {Z})";
        }
    }
}
