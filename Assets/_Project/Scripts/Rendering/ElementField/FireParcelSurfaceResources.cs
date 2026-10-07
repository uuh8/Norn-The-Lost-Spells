using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    public sealed class FireParcelSurfaceResources : IDisposable
    {
        public const int TriangleStride = 96;
        public RenderTexture Density { get; }
        public GraphicsBuffer Triangles { get; }
        public GraphicsBuffer Counter { get; }
        public GraphicsBuffer Overflow { get; }
        public GraphicsBuffer DrawArgs { get; }
        public GraphicsBuffer DensityAccumulation { get; }

        public FireParcelSurfaceResources(in FireParcelSurfaceGridSettings grid, int maximumTriangles)
        {
            var descriptor = new RenderTextureDescriptor(grid.Resolution.x, grid.Resolution.y)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = grid.Resolution.z,
                graphicsFormat = GraphicsFormat.R32_SFloat,
                depthStencilFormat = GraphicsFormat.None,
                enableRandomWrite = true,
                msaaSamples = 1
            };
            Density = new RenderTexture(descriptor) { name = "GpuFire.Density", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (!Density.Create()) throw new InvalidOperationException("无法创建 Fire Density Texture。");
            // uint Atomic Add 不依赖浮点原子指令；每格固定4字节，Resolve 后仍使用可过滤的 Float Texture。
            DensityAccumulation = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                grid.Resolution.x * grid.Resolution.y * grid.Resolution.z, sizeof(uint));
            Triangles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maximumTriangles, TriangleStride);
            Counter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            Overflow = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
            DrawArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawArgs.size);
        }

        public void Dispose()
        {
            if (Density != null)
            {
                Density.Release();
                // EditMode GPU验证也会持有资源；Destroy 只在 Play 中延迟销毁，Editor 使用立即释放。
                if (Application.isPlaying) UnityEngine.Object.Destroy(Density);
                else UnityEngine.Object.DestroyImmediate(Density);
            }
            Triangles?.Dispose(); Counter?.Dispose(); Overflow?.Dispose(); DrawArgs?.Dispose(); DensityAccumulation?.Dispose();
        }
    }
}
