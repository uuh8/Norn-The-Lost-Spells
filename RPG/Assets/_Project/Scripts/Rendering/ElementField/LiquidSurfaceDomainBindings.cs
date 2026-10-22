using UnityEngine;

namespace Game.Rendering
{
    /// <summary>独立液面没有连续场的归属域，但仍要满足 Shader 的 StructuredBuffer 绑定契约。</summary>
    internal static class LiquidSurfaceDomainBindings
    {
        public static GraphicsBuffer CreateEmpty(Material material)
        {
            // D3D12 在提交 Draw 时检查 SRV，而不是等 Shader 运行时分支决定是否读取。
            // 一个全零 int4 表示查询表空项；两个查询名复用同一资源，无每帧上传。
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(int) * 4);
            buffer.SetData(new int[4]);
            material.SetBuffer("_LiquidOwnerLookup", buffer);
            material.SetBuffer("_LiquidSecondaryOwnerLookup", buffer);
            material.SetInt("_LiquidOwnerLookupMask", 0);
            material.SetInt("_LiquidSecondaryOwnerLookupMask", 0);
            material.SetInt("_LiquidDomainEnabled", 0);
            material.SetInt("_LiquidSecondaryDomainEnabled", 0);
            // Buffer 由调用 Renderer 持有/释放；连续场存在时仍按既有路径绑定真实归属域。
            return buffer;
        }
    }
}
