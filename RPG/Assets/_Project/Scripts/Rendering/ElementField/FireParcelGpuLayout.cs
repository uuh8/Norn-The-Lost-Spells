using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct FireParcelSpawnGpu
    {
        public Vector3 Position;
        public float Lifetime;
        public Vector3 InitialVelocity;
        public float Heat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FireParcelStateGpu
    {
        public Vector3 Position;
        public float Age;
        public Vector3 Velocity;
        public float Lifetime;
        public Vector3 SourcePosition;
        public float Radius;
        public float SurfaceWeight;
        public float GasWeight;
        public float Heat;
        public float Active;
    }

    public static class FireParcelGpuLayout
    {
        public const int SpawnStride = 32;
        public const int StateStride = 64;
    }

    public static class FireParcelPoolMath
    {
        public static int ResolveRingSlot(uint sequence, int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            return (int)(sequence % (uint)capacity);
        }
    }
}
