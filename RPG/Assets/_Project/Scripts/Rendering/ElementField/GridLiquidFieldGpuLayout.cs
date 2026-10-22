using System.Runtime.InteropServices;
using Game.ElementField;
using UnityEngine;

namespace Game.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GridLiquidPackedCell
    {
        public uint QuantityFlags, VelocityXY, VelocityZOffsetX, OffsetYZ;
        public const int Stride = 16;
        // Half只有65536种位模式。一次调用Unity转换生成精确表，后续CPU宽核/圆冠
        // 直接寻址，避免每个湿格六次managed-to-native边界；共享只读表占256KiB。
        // NaN、Subnormal和负零也来自Unity本身，未替换浮点语义或GPU Packed Layout。
        private static readonly float[] HalfToFloatValues=BuildHalfToFloatValues();
        private static float[] BuildHalfToFloatValues()
        {
            var values=new float[65536];
            for(int i=0;i<values.Length;i++)values[i]=Mathf.HalfToFloat((ushort)i);
            return values;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void WarmDecodeTable()=>System.GC.KeepAlive(HalfToFloatValues);
        public static uint HalfPair(float a, float b) => (uint)Mathf.FloatToHalf(a) | ((uint)Mathf.FloatToHalf(b) << 16);
        public static float Low(uint p) => HalfToFloatValues[(ushort)p];
        public static float High(uint p) => HalfToFloatValues[(ushort)(p >> 16)];
        public static GridLiquidPackedCell Pack(LiquidCellPresentationSample sample, uint flags)
        {
            // Sparse Chunk绝大部分格为空。空格无需六次FloatToHalf原生调用，直接清零姿态。
            if(sample.Quantity==0)return new GridLiquidPackedCell{QuantityFlags=flags<<16};
            // 数量16位无损；只压缩有界速度/相对位移，绝不压缩巨大World Position。
            return new GridLiquidPackedCell
            {
                QuantityFlags = sample.Quantity | (flags << 16),
                VelocityXY = HalfPair(sample.VelocityWorld.x, sample.VelocityWorld.y),
                VelocityZOffsetX = HalfPair(sample.VelocityWorld.z, sample.OffsetCells.x),
                OffsetYZ = HalfPair(sample.OffsetCells.y, sample.OffsetCells.z)
            };
        }
        public LiquidCellPresentationSample Decode()
        {
            uint quantity=QuantityFlags&65535u;
            // Sparse Chunk/插值采样的大多数槽为空；空槽姿态无意义，跳过六次HalfToFloat原生调用。
            // 与GPU ReadCell的Quantity早退一致，避免CPU回退把稀疏空气当成完整材料来解码。
            if(quantity==0)return default;
            return new LiquidCellPresentationSample(quantity,
                new Vector3(High(VelocityZOffsetX), Low(OffsetYZ), High(OffsetYZ)),
                new Vector3(Low(VelocityXY), High(VelocityXY), Low(VelocityZOffsetX)));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GridLiquidChunkLookup
    {
        public int X, Y, Z, Slot;
        public GridLiquidChunkLookup(int x, int y, int z, int slot) { X = x; Y = y; Z = z; Slot = slot; }
    }
    public static class GridLiquidFieldGpuLayout
    {
        public const int VertexStride = 48;
        public static uint Hash(int x, int y, int z) => unchecked((uint)x * 73856093u ^ (uint)y * 19349663u ^ (uint)z * 83492791u);
        public static int FloorDiv(int value, int divisor) { int q = value / divisor; return value % divisor < 0 ? q - 1 : q; }
    }
}
