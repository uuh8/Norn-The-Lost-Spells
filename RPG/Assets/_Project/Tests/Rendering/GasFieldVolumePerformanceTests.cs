using System;
using System.Reflection;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Game.Rendering.Tests
{
    public sealed class GasFieldVolumePerformanceTests
    {
        private const BindingFlags Members=BindingFlags.Instance|BindingFlags.NonPublic;
        // 真实 Store 的薄只读包装；测试比较旧逐 Voxel 路径和新批量路径，不伪造浓度结果。
        private sealed class Reader:IGasVolumeReadOnly
        {
            internal readonly GasVolumeStore Store=new GasVolumeStore(8,32,4096,new[]{MaterialId.Steam,MaterialId.ToxicVapor});
            internal int BulkReads,CellReads;
            public int ChunkSize=>Store.ChunkSize;
            public int MaximumChunks=>Store.MaximumChunks;
            public int ResidentChunkCount=>Store.ResidentChunkCount;
            public int ResidentCellCount=>Store.ResidentCellCount;
            public uint Version=>Store.Version;
            public uint CellCapacity=>Store.CellCapacity;
            public ulong TotalQuantity=>Store.TotalQuantity;
            public ElementChunkKey GetChunkKey(int index)=>Store.GetChunkKey(index);
            public uint GetChunkVersion(ElementChunkKey key)=>Store.GetChunkVersion(key);
            public bool TryGetCell(int index,out MaterialVolumeCell cell)=>Store.TryGetCell(index,out cell);
            public bool TryGetAmount(Vector3Int cell,MaterialId material,out byte amount)=>Store.TryGetAmount(cell,material,out amount);
            public bool TryGetQuantity(Vector3Int cell,MaterialId material,out uint quantity)
            { CellReads++; return Store.TryGetQuantity(cell,material,out quantity); }
            public void CopyMaterialBox(ElementChunkKey key,Vector3Int minimum,Vector3Int size,MaterialId material,
                uint[] destination,int offset,int rowStride,int sliceStride)
            { BulkReads++; Store.CopyMaterialBox(key,minimum,size,material,destination,offset,rowStride,sliceStride); }
        }
        private sealed class Fixture:IDisposable
        {
            internal readonly GameObject Host=new GameObject("Gas Rendering Performance Test");
            internal readonly GameObject CameraHost=new GameObject("Gas Test Camera");
            private readonly GasWorldProfile _profile=ScriptableObject.CreateInstance<GasWorldProfile>();
            private readonly Material _material=new Material(Shader.Find("Game/Materials/RealGasVolume"));
            internal readonly GasVolumeStore Store;
            internal readonly GasFieldVolumeRenderer Renderer;
            internal readonly Action Frame;
            internal Fixture()
            {
                try
                {
                Host.hideFlags=HideFlags.DontSave; CameraHost.hideFlags=HideFlags.DontSave;
                Host.SetActive(false);
                var world=Host.AddComponent<ElementWorldRuntime>();
                var gas=Host.AddComponent<GasWorldRuntime>();
                // 这里隔离缓存/Halo 算法，显式保留比较样本的容量；生产密度另由 Scene Contract 验证。
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_cellCapacityGmu\":4096,\"_maximumActiveCells\":64}",_profile);
                typeof(GasWorldRuntime).GetField("_profile",Members).SetValue(gas,_profile);
                Assert.That(gas.TryInitialize(null,null,Vector3.zero,1,2),Is.True);
                Store=(GasVolumeStore)typeof(GasWorldRuntime).GetField("_store",Members).GetValue(gas);
                typeof(ElementWorldRuntime).GetField("_gasRuntime",Members).SetValue(world,gas);
                typeof(ElementWorldRuntime).GetField("_origin",Members).SetValue(world,Vector3.zero);
                typeof(ElementWorldRuntime).GetField("_settings",Members).SetValue(world,new ElementFieldSimulationSettings(1,0,0,0,2,default));
                typeof(ElementWorldRuntime).GetField("<IsInitialized>k__BackingField",Members).SetValue(world,true);
                var camera=CameraHost.AddComponent<Camera>(); camera.enabled=false;
                camera.orthographic=true; camera.orthographicSize=50; camera.nearClipPlane=.1f; camera.farClipPlane=100;
                CameraHost.transform.position=new Vector3(10,0,-20);
                Renderer=Host.AddComponent<GasFieldVolumeRenderer>();
                typeof(GasFieldVolumeRenderer).GetField("_world",Members).SetValue(Renderer,world);
                typeof(GasFieldVolumeRenderer).GetField("_material",Members).SetValue(Renderer,_material);
                typeof(GasFieldVolumeRenderer).GetMethod("Start",Members).Invoke(Renderer,null);
                typeof(GasFieldVolumeRenderer).GetField("_camera",Members).SetValue(Renderer,camera);
                Frame=(Action)Delegate.CreateDelegate(typeof(Action),Renderer,typeof(GasFieldVolumeRenderer).GetMethod("LateUpdate",Members));
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {Object.DestroyImmediate(Host);Object.DestroyImmediate(CameraHost);Object.DestroyImmediate(_profile);Object.DestroyImmediate(_material);}
        }
        [Test]
        public void DefaultDensityTransitionStillMovesAfterOneTickAndNewBirthIsImmediate()
        {
            using var fixture=new Fixture();
            var frame=(Action<float>)Delegate.CreateDelegate(typeof(Action<float>),fixture.Renderer,typeof(GasFieldVolumeRenderer).GetMethod("RenderFrame",Members));
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(0);
            Assert.That(fixture.Renderer.DrawnChunksLastFrame,Is.EqualTo(1));
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(.1f);
            frame(.25f);
            var properties=(MaterialPropertyBlock)typeof(GasFieldVolumeRenderer).GetField("_properties",Members).GetValue(fixture.Renderer);
            Assert.That(properties.GetFloat("_DensityBlend"),Is.EqualTo(.5f).Within(.001f),"显示应跨过 0.1s Tick 间隔持续变化，稀薄或被工作预算延后的通量也不直接跳变。");
            Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.Zero);
        }
        [Test]
        public void LegacySerializedShortBlendUsesTheNewMinimumWithoutReloadingScene()
        {
            using var fixture=new Fixture();
            // 已加载 Scene 的 Domain Reload 会保留旧序列化字段；改 C# 初始值不会自动迁移。
            typeof(GasFieldVolumeRenderer).GetField("_densityBlendSeconds",Members).SetValue(fixture.Renderer,.1f);
            var frame=(Action<float>)Delegate.CreateDelegate(typeof(Action<float>),fixture.Renderer,typeof(GasFieldVolumeRenderer).GetMethod("RenderFrame",Members));
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(0);
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(.1f); frame(.25f);
            var properties=(MaterialPropertyBlock)typeof(GasFieldVolumeRenderer).GetField("_properties",Members).GetValue(fixture.Renderer);
            Assert.That(properties.GetFloat("_DensityBlend"),Is.EqualTo(.5f).Within(.001f),"当前 P7 的旧值必须在运行时生效迁移，不能要求重载/保存用户关卡。");
            Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.Zero);
        }
        [Test]
        public void InterruptedSmoothDensityTransitionRebasesFromActualDisplayedValue()
        {
            using var fixture=new Fixture();
            var frame=(Action<float>)Delegate.CreateDelegate(typeof(Action<float>),fixture.Renderer,typeof(GasFieldVolumeRenderer).GetMethod("RenderFrame",Members));
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(0);
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(.1f); frame(.2f);
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(.2f);
            var slots=(System.Collections.Generic.Dictionary<ElementChunkKey,int>)typeof(GasFieldVolumeRenderer).GetField("_textureSlots",Members).GetValue(fixture.Renderer);
            var textures=(Texture3D[])typeof(GasFieldVolumeRenderer).GetField("_textures",Members).GetValue(fixture.Renderer);
            Color32 pixel=textures[slots[new ElementChunkKey(0,0,0)]].GetPixels32()[21];
            int first=Mathf.RoundToInt(255f*1000/4096),second=Mathf.RoundToInt(255f*2000/4096);
            Assert.That(pixel.r,Is.EqualTo(Mathf.RoundToInt(Mathf.Lerp(first,second,7f/27f))),"1/3 时间处 SmoothStep=7/27；重定起点必须采用屏幕当时显示的值。");
            Assert.That(pixel.b,Is.EqualTo(Mathf.RoundToInt(255f*3000/4096)));
            frame(.35f); Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.Zero);
        }
        [Test]
        public void LastVisibleDensityFadesThroughInterpolationBeforeQuantizedTailStopsDrawing()
        {
            using var fixture=new Fixture();
            var frame=(Action<float>)Delegate.CreateDelegate(typeof(Action<float>),fixture.Renderer,typeof(GasFieldVolumeRenderer).GetMethod("RenderFrame",Members));
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(0);
            fixture.Store.TryRemove(Vector3Int.zero,MaterialId.Steam,999); frame(.1f);
            Assert.That(fixture.Renderer.DrawnChunksLastFrame,Is.EqualTo(1),"量化后的当前值为零，前态仍需平滑淡出。");
            frame(.4f); Assert.That(fixture.Renderer.DrawnChunksLastFrame,Is.Zero);
            Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.Zero);
        }
        [Test]
        public void DensityBlendAdvancesBetweenTicksWithoutUploadingAgainAndPauseKeepsTimeFixed()
        {
            using var fixture=new Fixture();
            var method=typeof(GasFieldVolumeRenderer).GetMethod("RenderFrame",Members);
            Assert.That(method,Is.Not.Null,"需要按显示时间推进过渡，而非依赖另一次 Solver Tick。");
            var frame=(Action<float>)Delegate.CreateDelegate(typeof(Action<float>),fixture.Renderer,method);
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(0);
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); frame(.1f);
            frame(.25f);
            var properties=(MaterialPropertyBlock)typeof(GasFieldVolumeRenderer).GetField("_properties",Members).GetValue(fixture.Renderer);
            Assert.That(properties.GetFloat("_DensityBlend"),Is.EqualTo(.5f).Within(.001f));
            Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.Zero);
            frame(.25f); Assert.That(properties.GetFloat("_DensityBlend"),Is.EqualTo(.5f).Within(.001f));
            Assert.That(properties.GetFloat("_PresentationTime"),Is.EqualTo(.25f));
            frame(.4f); Assert.That(properties.GetFloat("_DensityBlend"),Is.EqualTo(1f).Within(.001f));
        }
        [Test]
        public void ChangingGasDensityKeepsOldAndNewFieldsInTheSameTextureForFrameInterpolation()
        {
            using var fixture=new Fixture();
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); fixture.Frame();
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); fixture.Frame();
            var slots=(System.Collections.Generic.Dictionary<ElementChunkKey,int>)typeof(GasFieldVolumeRenderer).GetField("_textureSlots",Members).GetValue(fixture.Renderer);
            var textures=(Texture3D[])typeof(GasFieldVolumeRenderer).GetField("_textures",Members).GetValue(fixture.Renderer);
            Color32 pixel=textures[slots[new ElementChunkKey(0,0,0)]].GetPixels32()[21];
            Assert.That(pixel.r,Is.EqualTo(Mathf.RoundToInt(255f*1000/4096)),"后端改变后保留显示起点，不能每 0.1 秒直接跳到新密度。");
            Assert.That(pixel.b,Is.EqualTo(Mathf.RoundToInt(255f*2000/4096)));
            fixture.Frame(); Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.Zero,"逐帧插值不能逐帧重传 Texture。");
        }
        [Test]
        public void TemporalDensityPackingHandlesBirthAndInterruptedInterpolationWithoutAllocating()
        {
            var method=typeof(GasDensitySnapshot).GetMethod("PackTemporalHistory",BindingFlags.Public|BindingFlags.Static);
            Assert.That(method,Is.Not.Null,"缺少复用 RGBA 双快照的连续显示处理。");
            var pack=(Action<Color32[],Color32[],int,float,bool>)Delegate.CreateDelegate(typeof(Action<Color32[],Color32[],int,float,bool>),method);
            var pixels=new Color32[1]; var history=new Color32[1];
            pixels[0]=new Color32(100,0,0,255); pack(pixels,history,0,1,true);
            Assert.That(pixels[0],Is.EqualTo(new Color32(100,0,100,0)),"新出生直接显示，不能增加插值等待。");
            pixels[0]=new Color32(0,200,0,255); pack(pixels,history,0,1,false);
            Assert.That(pixels[0],Is.EqualTo(new Color32(100,0,0,200)));
            pixels[0]=new Color32(60,0,0,255); pack(pixels,history,0,.5f,false);
            Assert.That(pixels[0],Is.EqualTo(new Color32(50,100,60,0)),"过渡中再次改变，应从当时可见值继续，避免回跳。");
            for(int i=0;i<30;i++) pack(pixels,history,0,.5f,false);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<300;i++) pack(pixels,history,0,.5f,false);
            Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
        }
        [Test]
        public void DensityTextureUsesLinearConcentrationsInsteadOfSrgbColorDecoding()
        {
            using var fixture=new Fixture();
            var textures=(Texture3D[])typeof(GasFieldVolumeRenderer).GetField("_textures",Members).GetValue(fixture.Renderer);
            Assert.That(textures[0].graphicsFormat,Is.EqualTo(UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm),
                "浓度是线性数据；sRGB 自动解码会让低浓度显示远低于 Gameplay Amount。");
        }
        [Test]
        public void RemovingDistantChunkDoesNotUploadUnchangedRemainingGasTexture()
        {
            using var fixture=new Fixture();
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000);
            fixture.Store.TryAdd(Vector3Int.right*20,MaterialId.ToxicVapor,1000);
            fixture.Frame(); Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.EqualTo(2));
            fixture.Store.TryRemove(Vector3Int.zero,MaterialId.Steam,1000); fixture.Store.ReclaimEmptyChunks();
            fixture.Frame();
            Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.Zero,
                "常驻索引交换不能使相隔十个 Chunk 的不变气体重新采集/上传。");
            Assert.That(fixture.Renderer.DrawnChunksLastFrame,Is.EqualTo(1));
        }
        [Test]
        public void FullHaloMatchesPreviousPixelFormulaIncludingNegativeKeysAndCorners()
        {
            var reader=new Reader(); var key=new ElementChunkKey(-2,1,-3);
            for(int z=-1;z<=1;z++) for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++)
            {
                var cell=ElementWorldCoordinates.ComposeGlobalCell(new ElementChunkKey(key.X+x,key.Y+y,key.Z+z),
                    new Vector3Int(x<0?7:0,y<0?7:0,z<0?7:0),8);
                reader.Store.TryAdd(cell,MaterialId.Steam,(uint)(100+10*(x+1)+100*(y+1)+1000*(z+1)));
                reader.Store.TryAdd(cell,MaterialId.ToxicVapor,333);
            }
            var pixels=new Color32[1000]; var steam=new uint[1000]; var toxic=new uint[1000];
            Assert.That(GasDensitySnapshot.Pack(reader,key,pixels,steam,toxic),Is.True);
            Assert.That(reader.BulkReads,Is.EqualTo(54)); Assert.That(reader.CellReads,Is.Zero);
            int index=0;
            for(int z=-1;z<=8;z++) for(int y=-1;y<=8;y++) for(int x=-1;x<=8;x++)
            {
                // Halo 允许 -1/size，不能传给限定在 Chunk 内的 ComposeGlobalCell。
                var cell=new Vector3Int(key.X*8+x,key.Y*8+y,key.Z*8+z);
                reader.TryGetQuantity(cell,MaterialId.Steam,out uint water);
                reader.TryGetQuantity(cell,MaterialId.ToxicVapor,out uint poison);
                var expected=new Color32((byte)Mathf.RoundToInt(255f*water/4096),(byte)Mathf.RoundToInt(255f*poison/4096),0,255);
                Assert.That(pixels[index++],Is.EqualTo(expected),"完整 Halo 和原 RGBA32 公式必须逐像素一致。");
            }
            Assert.That(reader.CellReads,Is.EqualTo(2000));
        }
        [Test]
        public void ReusedDensityBuffersClearReclaimedHaloAndAllocateZeroAfterWarmup()
        {
            var reader=new Reader(); var key=new ElementChunkKey(0,0,0);
            var corner=new Vector3Int(8,8,8); reader.Store.TryAdd(corner,MaterialId.ToxicVapor,4096);
            var pixels=new Color32[1000]; var steam=new uint[1000]; var toxic=new uint[1000];
            GasDensitySnapshot.Pack(reader,key,pixels,steam,toxic); Assert.That(pixels[999].g,Is.EqualTo(255));
            reader.Store.TryRemove(corner,MaterialId.ToxicVapor,4096); reader.Store.ReclaimEmptyChunks();
            Assert.That(GasDensitySnapshot.Pack(reader,key,pixels,steam,toxic),Is.False);
            Assert.That(pixels[999].g,Is.Zero);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<300;i++) GasDensitySnapshot.Pack(reader,key,pixels,steam,toxic);
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(allocated,Is.Zero);
        }
        [Test]
        public void DiagonalHaloChangeInvalidatesTextureButColdFrameDoesNotUpload()
        {
            using var fixture=new Fixture();
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); fixture.Frame();
            fixture.Frame(); Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.Zero);
            fixture.Store.TryAdd(new Vector3Int(2,2,2),MaterialId.ToxicVapor,1000); fixture.Frame();
            Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.EqualTo(2));
            var textureSlots=(System.Collections.Generic.Dictionary<ElementChunkKey,int>)typeof(GasFieldVolumeRenderer)
                .GetField("_textureSlots",Members).GetValue(fixture.Renderer);
            var textures=(Texture3D[])typeof(GasFieldVolumeRenderer).GetField("_textures",Members).GetValue(fixture.Renderer);
            var pixels=textures[textureSlots[new ElementChunkKey(0,0,0)]].GetPixels32();
            // 同一 Texture 现在 RG 保存前态、BA 保存本次真实浓度；Halo 的空间值未改变。
            Assert.That(pixels[63].a,Is.EqualTo(Mathf.RoundToInt(255f*1000/4096)));
        }
        [Test]
        public void FullTexturePoolReusesReclaimedSlotAndRepresentsNewGas()
        {
            using var fixture=new Fixture();
            for(int i=0;i<8;i++) Assert.That(fixture.Store.TryAdd(Vector3Int.right*(i*4),MaterialId.Steam,1000),Is.True);
            fixture.Frame(); Assert.That(fixture.Renderer.DrawnChunksLastFrame,Is.EqualTo(8));
            fixture.Store.TryRemove(Vector3Int.right*12,MaterialId.Steam,1000); fixture.Store.ReclaimEmptyChunks();
            Assert.That(fixture.Store.TryAdd(Vector3Int.right*32,MaterialId.ToxicVapor,1000),Is.True);
            fixture.Frame();
            Assert.That(fixture.Renderer.DrawnChunksLastFrame,Is.EqualTo(8));
            Assert.That(fixture.Renderer.UploadedChunksLastFrame,Is.EqualTo(1));
        }
        [Test]
        public void RendererReportsUploadsAndMissingRepresentationsWithoutScanningInventory()
        {
            using var fixture=new Fixture();
            var missing=typeof(GasFieldVolumeRenderer).GetProperty("UnrepresentedChunksLastFrame");
            var copied=typeof(GasFieldVolumeRenderer).GetProperty("CopiedDensityChunksLastFrame");
            var bytes=typeof(GasFieldVolumeRenderer).GetProperty("UploadedDensityBytesLastFrame");
            Assert.That(missing,Is.Not.Null); Assert.That(copied,Is.Not.Null); Assert.That(bytes,Is.Not.Null);
            fixture.Store.TryAdd(Vector3Int.zero,MaterialId.Steam,1000); fixture.Frame();
            Assert.That(missing.GetValue(fixture.Renderer),Is.EqualTo(0));
            Assert.That(copied.GetValue(fixture.Renderer),Is.EqualTo(54));
            Assert.That(bytes.GetValue(fixture.Renderer),Is.EqualTo(256L));
            fixture.Frame();
            Assert.That(copied.GetValue(fixture.Renderer),Is.EqualTo(0));
            Assert.That(bytes.GetValue(fixture.Renderer),Is.EqualTo(0L));
        }
    }
}
