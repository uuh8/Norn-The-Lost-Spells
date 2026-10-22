using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;
namespace Game.Rendering
{
    // ContextContainer每摄像机独立，避免多Camera/Stack之间复用上一幅Coverage。
    internal sealed class ItemGlowFrameData:ContextItem
    {public TextureHandle Source;public override void Reset(){Source=TextureHandle.nullHandle;}}
    internal sealed class WorldItemGlowCapturePass:ScriptableRenderPass
    {
        private readonly List<ShaderTagId> _coverageTags=new List<ShaderTagId>{new ShaderTagId("ItemGlowCoverage")};
        private sealed class PassData{public RendererListHandle Coverage;}
        private static readonly Unity.Profiling.ProfilerMarker RecordMarker=new Unity.Profiling.ProfilerMarker("Norn.ItemGlow.RecordCapture");
        public WorldItemGlowCapturePass(){renderPassEvent=RenderPassEvent.BeforeRenderingPostProcessing;}
        public override void RecordRenderGraph(RenderGraph graph,ContextContainer frame)
        {
            using var scope=RecordMarker.Auto();
            var resources=frame.Get<UniversalResourceData>();var camera=frame.Get<UniversalCameraData>();
            var rendering=frame.Get<UniversalRenderingData>();var light=frame.Get<UniversalLightData>();
            // Color与原Depth共享尺寸/MSAA，Stencil仍在；写入只清Color，绝不清场景Depth。
            var desc=graph.GetTextureDesc(resources.activeDepthTexture);desc.depthBufferBits=DepthBits.None;
            desc.colorFormat=GraphicsFormat.R16G16B16A16_SFloat;desc.name="Item glow RGB + coverage A";desc.clearBuffer=true;desc.clearColor=Color.clear;desc.bindTextureMS=false;
            var source=graph.CreateTexture(desc);frame.GetOrCreate<ItemGlowFrameData>().Source=source;
            using(var builder=graph.AddRasterRenderPass<PassData>("Norn.ItemGlow.Capture",out var data))
            {
                var filter=new FilteringSettings(RenderQueueRange.transparent,camera.camera.cullingMask);
                var coverage=RenderingUtils.CreateDrawingSettings(_coverageTags,rendering,camera,light,SortingCriteria.CommonTransparent);
                data.Coverage=graph.CreateRendererList(new RendererListParams(rendering.cullResults,coverage,filter));
                builder.UseRendererList(data.Coverage);
                builder.SetRenderAttachment(source,0,AccessFlags.Write);builder.SetRenderAttachmentDepth(resources.activeDepthTexture,AccessFlags.Read);
                builder.SetRenderFunc(static(PassData data,RasterGraphContext context)=>context.cmd.DrawRendererList(data.Coverage));
            }
        }
    }
    internal sealed class WorldItemGlowPass:ScriptableRenderPass
    {
        private readonly Material _material;
        private static readonly int CoverageId=Shader.PropertyToID("_ItemCoverage"),HaloId=Shader.PropertyToID("_ItemHalo");
        private sealed class PassData{public TextureHandle Color,Coverage,Halo;public Material Material;}
        private static readonly Unity.Profiling.ProfilerMarker RecordMarker=new Unity.Profiling.ProfilerMarker("Norn.ItemGlow.RecordCompose");
        public WorldItemGlowPass(Material material){_material=material;renderPassEvent=RenderPassEvent.AfterRenderingPostProcessing;requiresIntermediateTexture=true;}
        public override void RecordRenderGraph(RenderGraph graph,ContextContainer frame)
        {
            using var scope=RecordMarker.Auto();
            var source=frame.GetOrCreate<ItemGlowFrameData>().Source;var resources=frame.Get<UniversalResourceData>();
            if(!source.IsValid() || resources.isActiveTargetBackBuffer)return;
            var desc=graph.GetTextureDesc(source);desc.msaaSamples=MSAASamples.None;desc.clearBuffer=false;desc.name="Item glow blur X";
            var horizontal=graph.CreateTexture(desc);desc.name="Item glow blur Y";var halo=graph.CreateTexture(desc);
            desc.name="Item visible outline";var sharp=graph.CreateTexture(desc);
            graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(source,sharp,_material,3),passName:"Norn.ItemGlow.SilhouetteEdge");
            // 分离Gaussian横/纵两个方向，9+9采样替代二维81采样，宽度使用像素尺寸。
            graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(sharp,horizontal,_material,0),passName:"Norn.ItemGlow.BlurX");
            graph.AddBlitPass(new RenderGraphUtils.BlitMaterialParameters(horizontal,halo,_material,1),passName:"Norn.ItemGlow.BlurY");
            var color=resources.activeColorTexture;var colorDesc=graph.GetTextureDesc(color);colorDesc.name="Item glow composed";colorDesc.clearBuffer=false;
            var destination=graph.CreateTexture(colorDesc);
            using(var builder=graph.AddRasterRenderPass<PassData>("Norn.ItemGlow.Composite",out var data))
            {
                data.Color=color;data.Coverage=sharp;data.Halo=halo;data.Material=_material;
                builder.UseTexture(color);builder.UseTexture(sharp);builder.UseTexture(halo);builder.SetRenderAttachment(destination,0,AccessFlags.Write);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static(PassData data,RasterGraphContext context)=>
                {
                    context.cmd.SetGlobalTexture(CoverageId,data.Coverage);context.cmd.SetGlobalTexture(HaloId,data.Halo);
                    Blitter.BlitTexture(context.cmd,data.Color,new Vector4(1,1,0,0),data.Material,2);
                });
            }
            resources.cameraColor=destination;
        }
    }
}
