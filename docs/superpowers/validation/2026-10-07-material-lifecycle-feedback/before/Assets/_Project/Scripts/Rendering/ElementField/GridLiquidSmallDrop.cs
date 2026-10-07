using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GridLiquidSmallDrop
    {
        public Vector3 Position;
        public float Radius;
        public uint First,Second,Third,Fourth;
    }
}
