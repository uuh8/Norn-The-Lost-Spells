using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Game.Rendering
{
    /// <summary>
    /// 与 HLSL FluidAnisotropyTransform 精确对应的 64-byte 记录。前三个 float4 是
    /// world point -> normalized kernel point 的 3x4 affine row；第四行 xyz 是世界椭球尺度，w=1
    /// 表示邻域足够，w=0 表示安全的 Isotropic fallback。矩阵只服务 Surface Reconstruction。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct FluidAnisotropyTransform
    {
        public readonly Vector4 Row0;
        public readonly Vector4 Row1;
        public readonly Vector4 Row2;
        public readonly Vector4 ScaleValidity;

        public FluidAnisotropyTransform(
            Vector4 row0,
            Vector4 row1,
            Vector4 row2,
            Vector4 scaleValidity)
        {
            Row0 = row0;
            Row1 = row1;
            Row2 = row2;
            ScaleValidity = scaleValidity;
        }
    }

    /// <summary>
    /// Density/Marching Cubes 共用的长期 GPU 资源所有者。构造中任何一步失败都会释放此前资源；
    /// OnDisable/OnDestroy 可安全重复 Dispose，不依赖 Finalizer 回收 VRAM。
    /// </summary>
    public sealed class FluidSurfaceGpuResources : IDisposable
    {
        public const int TriangleStride = 96;
        public const int AnisotropyStride = 64;

        public FluidSurfaceGridSettings GridSettings { get; private set; }
        public int ParticleCapacity { get; }
        public RenderTexture DensityTexture { get; private set; }
        public GraphicsBuffer TriangleBuffer { get; private set; }
        public GraphicsBuffer TriangleCounter { get; private set; }
        public GraphicsBuffer OverflowCounter { get; private set; }
        public GraphicsBuffer IndirectArguments { get; private set; }
        public GraphicsBuffer AnisotropyBuffer { get; private set; }
        public GraphicsBuffer AnisotropyCounters { get; private set; }
        public GraphicsBuffer SurfaceSupportBuffer { get; private set; }

        public FluidSurfaceGpuResources(
            in FluidSurfaceGridSettings gridSettings,
            int particleCapacity)
        {
            if (particleCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(particleCapacity));
            if (!IsSupported(in gridSettings, out string reason))
                throw new NotSupportedException(reason);

            GridSettings = gridSettings;
            ParticleCapacity = particleCapacity;

            try
            {
                DensityTexture = CreateDensityTexture(in gridSettings);
                TriangleBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    gridSettings.MaximumTriangleCount,
                    TriangleStride);
                TriangleCounter = CreateUIntBuffer();
                OverflowCounter = CreateUIntBuffer();
                IndirectArguments = new GraphicsBuffer(
                    // Unity 6.3 会把 IndirectArguments 暴露给 Compute 的 RWByteAddressBuffer；
                    // 再叠加 Raw 属于无效 Flag 组合，D3D11 会忽略 Raw 并逐帧报警。
                    GraphicsBuffer.Target.IndirectArguments,
                    1,
                    GraphicsBuffer.IndirectDrawArgs.size);
                AnisotropyBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    particleCapacity,
                    AnisotropyStride);
                AnisotropyCounters = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    4,
                    sizeof(uint));
                // 一个 float/particle（8192 粒子约 32 KiB）。它只描述 Surface 邻域支持度，
                // 不复制 Density、速度或 Gameplay Amount，也不会产生 CPU Readback。
                SurfaceSupportBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    particleCapacity,
                    sizeof(float));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Resolution/Buffer 容量不变时允许更新 Origin 与 Voxel Size。Resident Far 层扩大时
        /// Texture 形状不变，只降低远景采样密度，因此不会在热路径重分配 VRAM。
        /// </summary>
        public bool TryUpdateGrid(in FluidSurfaceGridSettings gridSettings)
        {
            if (!GridSettings.IsResourceCompatibleWith(in gridSettings))
                return false;

            GridSettings = gridSettings;
            return true;
        }

        public static bool IsSupported(
            in FluidSurfaceGridSettings gridSettings,
            out string reason)
        {
            if (!SystemInfo.supports3DRenderTextures)
            {
                reason = "Current Graphics Device does not support 3D RenderTexture.";
                return false;
            }

            Vector3Int resolution = gridSettings.Resolution;
            if (resolution.x > SystemInfo.maxTexture3DSize
                || resolution.y > SystemInfo.maxTexture3DSize
                || resolution.z > SystemInfo.maxTexture3DSize)
            {
                reason = "Surface Grid resolution exceeds SystemInfo.maxTexture3DSize.";
                return false;
            }

            if (!SystemInfo.IsFormatSupported(
                    GraphicsFormat.R32_SFloat,
                    GraphicsFormatUsage.LoadStore))
            {
                reason = "R32_SFloat does not support random-write LoadStore on this Graphics Device.";
                return false;
            }

            reason = null;
            return true;
        }

        public void Dispose()
        {
            DensityTexture = DisposeTexture(DensityTexture);
            TriangleBuffer = DisposeBuffer(TriangleBuffer);
            TriangleCounter = DisposeBuffer(TriangleCounter);
            OverflowCounter = DisposeBuffer(OverflowCounter);
            IndirectArguments = DisposeBuffer(IndirectArguments);
            AnisotropyBuffer = DisposeBuffer(AnisotropyBuffer);
            AnisotropyCounters = DisposeBuffer(AnisotropyCounters);
            SurfaceSupportBuffer = DisposeBuffer(SurfaceSupportBuffer);
        }

        private static RenderTexture CreateDensityTexture(
            in FluidSurfaceGridSettings gridSettings)
        {
            var descriptor = new RenderTextureDescriptor(
                gridSettings.Resolution.x,
                gridSettings.Resolution.y)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = gridSettings.Resolution.z,
                graphicsFormat = GraphicsFormat.R32_SFloat,
                depthStencilFormat = GraphicsFormat.None,
                enableRandomWrite = true,
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
                mipCount = 1
            };
            var texture = new RenderTexture(descriptor)
            {
                name = "GpuFluid.DensityField",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.Create())
            {
                DestroyTextureObject(texture);
                throw new InvalidOperationException("Failed to create R32_SFloat 3D density texture.");
            }

            return texture;
        }

        private static GraphicsBuffer CreateUIntBuffer()
        {
            return new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
        }

        private static GraphicsBuffer DisposeBuffer(GraphicsBuffer buffer)
        {
            if (buffer == null)
                return null;
            buffer.Dispose();
            return null;
        }

        private static RenderTexture DisposeTexture(RenderTexture texture)
        {
            if (texture == null)
                return null;
            texture.Release();
            DestroyTextureObject(texture);
            return null;
        }

        private static void DestroyTextureObject(RenderTexture texture)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(texture);
            else
                UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
