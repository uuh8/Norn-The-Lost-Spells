using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class GridLiquidFieldFrameTests
    {
        [TestCase(4096)]
        [TestCase(16)]
        public void SparseFrozenPoolsKeepPackedShapeAndQuantityWithZeroCaptureAllocation(int maximumActiveCells)
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Sparse frozen presentation benchmark");host.SetActive(false);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":128,\"_maximumActiveCells\":"+maximumActiveCells+",\"_enableAutoStableDetection\":false}",profile);
                floor.transform.position=new Vector3(24,-.25f,32);floor.transform.localScale=new Vector3(64,.5f,80);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.5f,8,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                // 模拟分散的多次落点：每个8³ Chunk只有少量湿格，含三材料、接地和空中输入。
                // 锁定真实Capture的上传数据和Owner Bounds，避免以简化厚度/混色换取更短时间。
                for(int chunk=0;chunk<48;chunk++)for(int z=0;z<2;z++)for(int x=0;x<2;x++)
                {
                    var cell=new Vector3Int((chunk%6)*16+x,chunk%7==0?6:0,(chunk/6)*16+z);
                    Assert.That(source.TryAddInitial(cell,MaterialId.Water,128,false),Is.True);
                    Assert.That(source.TryAddInitial(cell,MaterialId.Poison,64,false),Is.True);
                    Assert.That(source.TryAddInitial(cell,MaterialId.Sticky,48,false),Is.True);
                }
                ((LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source)).BeginTick();
                var frame=new GridLiquidFieldFrame(source,256,1024,.8f);frame.Capture();
                uint signature=PresentationSignature(frame);ulong quantity=source.TotalQuantity;
                // 极小容量同时限制Owner输出（128+16页）；比较各配置自身的原实现，
                // 保留原有Overflow告警。此案例验证塑形工作表满时仍沿用完整遍历。
                uint expected=maximumActiveCells==4096?3405202243u:4186262329u;
                Assert.That(signature,Is.EqualTo(expected),"同一配置的三材料稀疏场、地形和Owner Bounds必须逐位保持。");
                Assert.That(quantity,Is.EqualTo(46080UL));Assert.That(frame.Overflow,Is.EqualTo(maximumActiveCells==16));
                for(int i=0;i<4;i++)frame.Capture();
                const int rounds=6,captures=8;var times=new double[rounds];var fullTimes=new double[rounds];
                long allocated=GC.GetAllocatedBytesForCurrentThread();
                for(int round=0;round<rounds;round++)
                {
                    for(int mode=0;mode<2;mode++)
                    {
                        bool full=(round+mode)%2==0;frame.SetValidationFullRowScan(full);
                        long started=System.Diagnostics.Stopwatch.GetTimestamp();
                        for(int i=0;i<captures;i++)frame.Capture();
                        double ms=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency/captures;
                        if(full)fullTimes[round]=ms;else times[round]=ms;
                    }
                }
                allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
                Assert.That(PresentationSignature(frame),Is.EqualTo(signature),"静止输入的再次采集必须保留逐位相同的姿态、数量、地形和输出范围。");
                Assert.That(source.TotalQuantity,Is.EqualTo(quantity));Assert.That(allocated,Is.Zero);
                string report=maximumActiveCells==4096?"sparse-presentation-capture.json":"sparse-presentation-dense-fallback.json";
                File.WriteAllText("Temp/ElementPerformance/"+report,JsonUtility.ToJson(new SparseCaptureReport
                {Utc=DateTime.UtcNow.ToString("O"),Signature=signature,Quantity=quantity,Pages=frame.Count,Owners=frame.OwnerCount,Overflow=frame.Overflow,AllocatedBytes=allocated,CaptureMilliseconds=times,FullScanMilliseconds=fullTimes},true));
                Directory.CreateDirectory("Backups/ElementPerformance/L427");
                File.Copy("Temp/ElementPerformance/"+report,"Backups/ElementPerformance/L427/"+report,true);
                frame.SetValidationFullRowScan(true);frame.Capture();
                Assert.That(PresentationSignature(frame),Is.EqualTo(signature),"完整扫描与稀疏扫描必须逐位相同，包括地形与Owner Bounds。");
                if(maximumActiveCells==4096)
                {
                    // 48个稀疏落点是实际累积场景的固定工作量；记录交替原始计时，
                    // 性能断言只针对空气占多数的配置，固定表已满时允许原完整路径。
                    Array.Sort(times);Array.Sort(fullTimes);
                    Assert.That(times[3],Is.LessThan(fullTimes[3]*.8),"空气占多数时应有实际采集收益，不能只增加工作表。");
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);}
        }
        [Test]
        public void RemovedWaterKeepsPreviousOnlyPageUntilItsPresentationTickEnds()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Previous-only water page");host.SetActive(false);var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            try
            {
                floor.transform.position=new Vector3(0,-.25f,0);floor.transform.localScale=new Vector3(4,.5f,4);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.5f,8,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                var cell=new Vector3Int(-1,0,-1);Assert.That(source.TryAddInitial(cell,MaterialId.Water,128,false),Is.True);
                var history=(LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source);
                history.BeginTick();Assert.That(source.TryConsume(cell,MaterialId.Water,128),Is.True);
                var frame=new GridLiquidFieldFrame(source,64,256,.8f);frame.Capture();
                // Gameplay已经清空，但插值仍需要Previous表面；空格快跳不能让旧水瞬间消失。
                Assert.That(source.TotalQuantity,Is.Zero);Assert.That(frame.Read(cell,0,true).Quantity,Is.EqualTo(128));
                Assert.That(frame.Read(cell,0,false).Quantity,Is.Zero);Assert.That(frame.OwnerCount,Is.GreaterThan(0));
                history.BeginTick();frame.Capture();Assert.That(frame.OwnerCount,Is.Zero);
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);}
        }
        private static uint PresentationSignature(GridLiquidFieldFrame frame)
        {
            uint hash=2166136261u;int rows=frame.Count*frame.ChunkEdge*frame.ChunkEdge*frame.ChunkEdge;
            unchecked
            {
                for(int phase=0;phase<2;phase++)for(int i=0;i<rows*frame.MaterialCount;i++)
                {
                    var cell=(phase==0?frame.Previous:frame.Current)[i];hash=(hash^cell.QuantityFlags)*16777619u;
                    hash=(hash^cell.VelocityXY)*16777619u;hash=(hash^cell.VelocityZOffsetX)*16777619u;hash=(hash^cell.OffsetYZ)*16777619u;
                }
                for(int i=0;i<rows;i++)for(int axis=0;axis<4;axis++)hash=(hash^(uint)BitConverter.SingleToInt32Bits(frame.Terrains[i][axis]))*16777619u;
                for(int i=0;i<frame.OwnerCount;i++)
                {
                    var key=frame.Owners[i];hash=(hash^(uint)key.X)*16777619u;hash=(hash^(uint)key.Y)*16777619u;hash=(hash^(uint)key.Z)*16777619u;
                    var bounds=frame.OwnerBounds(i);
                    for(int axis=0;axis<3;axis++){hash=(hash^(uint)BitConverter.SingleToInt32Bits(bounds.center[axis]))*16777619u;hash=(hash^(uint)BitConverter.SingleToInt32Bits(bounds.size[axis]))*16777619u;}
                }
            }
            return hash;
        }
        [Serializable]private sealed class SparseCaptureReport{public string Utc;public uint Signature;public ulong Quantity;public int Pages,Owners;public bool Overflow;public long AllocatedBytes;public double[] CaptureMilliseconds,FullScanMilliseconds;}
        [Test]
        public void DeepCartoonColumnStaysWithinFieldReachWithoutLosingQuantity()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Deep cartoon column reach");host.SetActive(false);var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            try
            {
                floor.transform.position=new Vector3(0,-.25f,0);floor.transform.localScale=new Vector3(8,.5f,8);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                Assert.That(source.TryAddInitial(Vector3Int.zero,MaterialId.Water,510,false),Is.True);
                Assert.That(source.TryAddInitial(Vector3Int.up,MaterialId.Water,510,false),Is.True);
                Assert.That(source.TryAddInitial(Vector3Int.up*2,MaterialId.Water,200,false),Is.True);
                ((LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source)).BeginTick();
                var frame=new GridLiquidFieldFrame(source,64,128,.8f);frame.Capture();var row=frame.Read(Vector3Int.zero,0,false);
                Assert.That(row.Quantity,Is.EqualTo(1220));Assert.That(source.TotalQuantity,Is.EqualTo(1220UL));
                Assert.That(row.OffsetCells.y,Is.InRange(1220f/510,3.3f),"深液柱的美术增厚要有界，不能超出175邻格的查找范围后被截顶。");
                Assert.That(new GridLiquidFieldSampler(frame).Evaluate(new Vector3(.25f,1.4f,.25f),1),Is.GreaterThan(0));
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);}
        }
        [TestCase(false)]
        [TestCase(true)]
        public void WideShallowBodyKeepsBulkOnGroundAndInAir(bool airborne)
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Wide body silhouette regression");host.SetActive(false);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":64,\"_maximumActiveCells\":2048,\"_enableAutoStableDetection\":false}",profile);
                floor.transform.position=new Vector3(3,-.25f,3);floor.transform.localScale=new Vector3(12,.5f,12);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                int y=airborne?4:0;
                for(int z=0;z<12;z++)for(int x=0;x<12;x++)Assert.That(source.TryAddInitial(new Vector3Int(x,y,z),MaterialId.Water,128,false),Is.True);
                ((LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source)).BeginTick();
                var frame=new GridLiquidFieldFrame(source,128,512,.8f);frame.Capture();var sampler=new GridLiquidFieldSampler(frame,3);
                // 12×12格已超过旧的大团淡出阈值。低侧视角需要真实截面，不能只靠法线/高光遮掩。
                float center=airborne?2.25f:0;
                Assert.That(sampler.Evaluate(new Vector3(3,center+(airborne?.15f:.30f),3),1),Is.GreaterThan(0),"大浅水团/空中液舌不能退回约12cm的薄片。");
                if(airborne)Assert.That(sampler.Evaluate(new Vector3(3,center-.15f,3),1),Is.GreaterThan(0),"自由液体的真实中心两侧都需要可见截面。");
                Assert.That(source.TotalQuantity,Is.EqualTo(18432UL));
                for(int i=0;i<8;i++)frame.Capture();long before=GC.GetAllocatedBytesForCurrentThread();frame.Capture();
                Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);}
        }
        [Test]
        public void FrozenMixedAndAirFieldKeepsPackedPresentationSignature()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Packed shape optimization equivalence");host.SetActive(false);var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":64,\"_maximumActiveCells\":2048,\"_enableAutoStableDetection\":false}",profile);
                floor.transform.position=new Vector3(0,-.25f,0);floor.transform.localScale=new Vector3(12,.5f,12);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                var cells=new Vector3Int[64];int cellAt=0;
                for(int z=-4;z<4;z++)for(int x=-4;x<4;x++)
                {
                    var cell=new Vector3Int(x,(x+z)%3==0?1:0,z);cells[cellAt++]=cell;
                    Assert.That(source.TryAddInitial(cell,MaterialId.Water,(uint)(40+Mathf.Abs(x*7+z*3)%61),false),Is.True);
                    Assert.That(source.TryAddInitial(cell,MaterialId.Poison,(uint)(32+Mathf.Abs(z*5+x)%33),false),Is.True);
                    Assert.That(source.TryAddInitial(cell,MaterialId.Sticky,(uint)(16+Mathf.Abs(x-z)%5*7),false),Is.True);
                }
                ((LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source)).BeginTick();
                var frame=new GridLiquidFieldFrame(source,128,512,.8f);frame.Capture();uint signature=2166136261u;
                // 固定负坐标、跨Chunk、三材料和空中输入，锁定本次厚圆冠的上传位。
                // 旧1543324119属于L4.22的外形；本次主动改变形状，旧签名不能作为当前验收依据。
                unchecked
                {
                    signature=(signature^(uint)frame.Count)*16777619u;signature=(signature^(uint)frame.OwnerCount)*16777619u;
                    for(int page=0;page<frame.Count;page++)
                    {
                        var key=frame.Keys[page];signature=(signature^(uint)key.X)*16777619u;signature=(signature^(uint)key.Y)*16777619u;signature=(signature^(uint)key.Z)*16777619u;
                    }
                    int length=frame.Count*frame.ChunkEdge*frame.ChunkEdge*frame.ChunkEdge*frame.MaterialCount;
                    for(int phase=0;phase<2;phase++)for(int i=0;i<length;i++)
                    {
                        var cell=(phase==0?frame.Previous:frame.Current)[i];signature=(signature^cell.QuantityFlags)*16777619u;
                        signature=(signature^cell.VelocityXY)*16777619u;signature=(signature^cell.VelocityZOffsetX)*16777619u;signature=(signature^cell.OffsetYZ)*16777619u;
                    }
                }
                // 本轮主动收窄圆肩并把离散补偿从25%校准到15%；旧签名仅属于历史外形。
                // 几何/体积/层序通过后锁定新副本，后续性能优化仍不得改变它。
                Assert.That(signature,Is.EqualTo(3548606679u),"固定厚圆冠快照应保持确定性，不能靠后续优化改变已验收的外形。");
                Assert.That(source.TotalQuantity,Is.EqualTo(8022UL));
                // 固定工作量的Stopwatch诊断与真实FPS分开：时间只记录，不用易抖动的阈值判失败。
                // 先暖机缓存/JIT，重复Capture及三材料读取均不能改变数量，也不能分配。
                const int rounds=6,captures=32,reads=512;
                var captureTimes=new double[rounds];var readTimes=new double[rounds];
                var materials=new[]{MaterialId.Water,MaterialId.Poison,MaterialId.Sticky};
                for(int i=0;i<8;i++)frame.Capture();
                long gc=GC.GetAllocatedBytesForCurrentThread();double captureTotal=0,readTotal=0;ulong checksum=0;
                for(int round=0;round<rounds;round++)
                {
                    long started=System.Diagnostics.Stopwatch.GetTimestamp();
                    for(int i=0;i<captures;i++)frame.Capture();
                    double time=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
                    captureTimes[round]=time/captures;captureTotal+=time;
                    started=System.Diagnostics.Stopwatch.GetTimestamp();
                    for(int i=0;i<reads;i++)for(int c=0;c<cells.Length;c++)for(int m=0;m<materials.Length;m++)
                    {source.TryGetDynamicQuantity(cells[c],materials[m],out uint q);checksum+=q;}
                    time=(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency;
                    readTimes[round]=time;readTotal+=time;
                }
                long allocated=GC.GetAllocatedBytesForCurrentThread()-gc;
                File.WriteAllText("Temp/ElementPerformance/frozen-presentation-signature.json",JsonUtility.ToJson(new PackedSignatureReport
                {Utc=DateTime.UtcNow.ToString("O"),Signature=signature,Quantity=source.TotalQuantity,Pages=frame.Count,Owners=frame.OwnerCount,
                 CaptureSamples=rounds*captures,CaptureMeanMs=captureTotal/(rounds*captures),CaptureRoundsMs=captureTimes,
                 QuantityReads=rounds*reads*cells.Length*materials.Length,QuantityReadMeanNs=readTotal*1e6/(rounds*reads*cells.Length*materials.Length),
                 QuantityReadRoundsMs=readTimes,QuantityChecksum=checksum,GcBytes=allocated},true));
                Assert.That(checksum,Is.EqualTo(8022UL*(ulong)rounds*(ulong)reads));
                Assert.That(allocated,Is.Zero,"等价优化不能在暖机Capture/数量查询中分配。");
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);}
        }
        [Serializable]private sealed class PackedSignatureReport
        {public string Utc;public uint Signature;public ulong Quantity,QuantityChecksum;public int Pages,Owners,CaptureSamples,QuantityReads;
         public double CaptureMeanMs,QuantityReadMeanNs;public double[] CaptureRoundsMs,QuantityReadRoundsMs;public long GcBytes;}
        [Test]
        public void ColdSlopeSupportCannotChangeCommittedGeometryWhenBakeBudgetRenews()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Cold slope support budget continuity");host.SetActive(false);var ramp=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":128,\"_maximumActiveCells\":4096,\"_maximumCellsPerTick\":256,\"_enableAutoStableDetection\":false}",profile);
                const float angle=45;float rad=angle*Mathf.Deg2Rad;ramp.transform.position=new Vector3(4,-.25f,4);ramp.transform.localScale=new Vector3(32,.5f,32);ramp.transform.rotation=Quaternion.Euler(0,0,-angle);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                var cells=new Vector3Int[256];int at=0;
                for(int z=0;z<16;z++)for(int x=0;x<16;x++)
                {
                    int y=Mathf.CeilToInt((-.25f+.25f/Mathf.Cos(rad)+(4-x*.5f)*Mathf.Tan(rad))/.5f);
                    var cell=new Vector3Int(x,y,z);while(source.IsSolidForSurface(cell))cell.y++;
                    Assert.That(source.TryAddInitial(cell,MaterialId.Water,128,false),Is.True);cells[at++]=cell;
                }
                var supportCache=(LiquidTerrainSupportCache)typeof(LiquidWorldRuntime).GetField("_terrain",fields).GetValue(source);
                int gameplayBudget=(int)typeof(LiquidTerrainSupportCache).GetField("_remaining",fields).GetValue(supportCache);
                var frame=new GridLiquidFieldFrame(source,512,1024,.8f);frame.Capture();var terrain=new Vector4[cells.Length];
                for(int i=0;i<cells.Length;i++)terrain[i]=frame.Terrain(cells[i]);uint firstFallbacks=source.TerrainFallbackCount;
                // 同一静态斜坡、同一液量，只恢复下一个Tick的128次支撑采样预算。
                // 若第一次把“暂未采到”当作平地，第二次就会突然换高度/Normal，画面必然抖动。
                ((LiquidTerrainSupportCache)typeof(LiquidWorldRuntime).GetField("_terrain",fields).GetValue(source)).BeginTick();frame.Capture();
                float maximumHeightChange=0,maximumNormalChange=0;
                for(int i=0;i<cells.Length;i++)
                {var after=frame.Terrain(cells[i]);maximumHeightChange=Mathf.Max(maximumHeightChange,Mathf.Abs(after.w-terrain[i].w));maximumNormalChange=Mathf.Max(maximumNormalChange,new Vector3(after.x-terrain[i].x,after.y-terrain[i].y,after.z-terrain[i].z).magnitude);}
                File.WriteAllText("Temp/ElementPerformance/slope-cold-support.json",JsonUtility.ToJson(new ColdSupportReport{Utc=DateTime.UtcNow.ToString("O"),Quantity=source.TotalQuantity,FirstFallbacks=firstFallbacks,LastFallbacks=source.TerrainFallbackCount,MaximumHeightChange=maximumHeightChange,MaximumNormalChange=maximumNormalChange},true));
                Assert.That(source.TotalQuantity,Is.EqualTo(32768UL));
                Assert.That((int)typeof(LiquidTerrainSupportCache).GetField("_remaining",fields).GetValue(supportCache),Is.EqualTo(gameplayBudget),"只读显示查询不能消耗Solver的128次采样预算。");
                Assert.That(((Dictionary<Vector3Int,LiquidTerrainSupport>)typeof(LiquidTerrainSupportCache).GetField("_cache",fields).GetValue(supportCache)).Count,Is.Zero,"Rendering不能提前预热Gameplay的支撑缓存。");
                Assert.That(firstFallbacks,Is.Zero,"显示缺缓存不应增加Gameplay预算耗尽计数。");
                Assert.That(maximumHeightChange,Is.LessThan(.005f),"仅采样预算恢复就改变静态坡脚高度："+maximumHeightChange);
                Assert.That(maximumNormalChange,Is.LessThan(.001f),"未采样的支撑不能暂时当成水平地板再翻回坡面。");
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(ramp);UnityEngine.Object.DestroyImmediate(profile);}
        }
        [Serializable]private sealed class ColdSupportReport{public string Utc;public ulong Quantity;public uint FirstFallbacks,LastFallbacks;public float MaximumHeightChange,MaximumNormalChange;}
        [Test]
        public void CommittedSlopeShapeDoesNotChangeWhenItBecomesPreviousAtNextTick()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Committed slope snapshot continuity");host.SetActive(false);
            var ramp=GameObject.CreatePrimitive(PrimitiveType.Cube);var receiving=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":64,\"_maximumActiveCells\":2048,\"_maximumCellsPerTick\":256,\"_maximumStableRegions\":8,\"_maximumStableRegionCells\":4096,\"_maximumStableSurfacePatches\":512,\"_enableAutoStableDetection\":false}",profile);
                const float angle=60;float rad=angle*Mathf.Deg2Rad;
                ramp.transform.position=new Vector3(2,-.25f,2);ramp.transform.localScale=new Vector3(6,.5f,6);ramp.transform.rotation=Quaternion.Euler(0,0,-angle);
                Vector3 toe=ramp.transform.TransformPoint(new Vector3(.5f,.5f,0));receiving.transform.position=new Vector3(toe.x+3,toe.y-.25f,2);receiving.transform.localScale=new Vector3(6,.5f,8);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                var inlet=new Vector3Int(2,Mathf.CeilToInt((-.25f+.25f/Mathf.Cos(rad)+Mathf.Tan(rad))/.5f),3);
                Assert.That(source.TryAddInitial(inlet,MaterialId.Water,510,true),Is.True);
                for(int tick=1;tick<=30;tick++){if(tick%3==0)Assert.That(source.TryAddInitial(inlet,MaterialId.Water,170,true),Is.True);source.Step(.1f);}
                var frame=new GridLiquidFieldFrame(source,128,512,.8f);frame.Capture();
                var saved=new Dictionary<Vector3Int,GridLiquidPackedCell>();var changes=new List<string>();float maximumFieldDifference=0;int quantityChanges=0,supportChanges=0,poseChanges=0;
                // 同一份已提交结果从Current变为下一Tick的Previous时应完全一致。
                // 面积中心测试无法发现某个坡脚格的支撑/圆冠被重新解释，所以逐格核对。
                for(int tick=0;tick<8;tick++)
                {
                    saved.Clear();int k=frame.ChunkEdge,cells=k*k*k;
                    for(int page=0;page<frame.Count;page++)for(int local=0;local<cells;local++)
                    {
                        var packed=frame.Current[(page*cells+local)*frame.MaterialCount];if((packed.QuantityFlags&65535u)==0)continue;
                        var key=frame.Keys[page];var cell=new Vector3Int(key.X*k+local%k,key.Y*k+local/k%k,key.Z*k+local/(k*k));saved.Add(cell,packed);
                    }
                    var positions=new Vector3[81];var values=new float[81];var beforeSampler=new GridLiquidFieldSampler(frame);int at=0;
                    for(int z=0;z<9;z++)for(int x=0;x<9;x++)
                    {positions[at]=new Vector3(toe.x-.4f+x*.1f,toe.y+.08f,1.6f+z*.1f);values[at]=beforeSampler.EvaluateGroundProxy(positions[at],1);at++;}
                    source.Step(.1f);frame.Capture();var afterSampler=new GridLiquidFieldSampler(frame);
                    for(int i=0;i<positions.Length;i++)maximumFieldDifference=Mathf.Max(maximumFieldDifference,Mathf.Abs(values[i]-afterSampler.EvaluateGroundProxy(positions[i],0)));
                    foreach(var item in saved)
                    {
                        var sample=frame.Read(item.Key,0,true);var old=item.Value.Decode();
                        if(sample.Quantity!=old.Quantity)quantityChanges++;
                        if(frame.Supported(item.Key,true)!=((item.Value.QuantityFlags&65536u)!=0))supportChanges++;
                        if(Vector3.Distance(sample.OffsetCells,old.OffsetCells)>.001f)poseChanges++;
                        if(changes.Count<20 && (sample.Quantity!=old.Quantity || Vector3.Distance(sample.OffsetCells,old.OffsetCells)>.001f))changes.Add(tick+"/"+item.Key+": q "+old.Quantity+"→"+sample.Quantity+", offset "+old.OffsetCells+"→"+sample.OffsetCells);
                    }
                }
                File.WriteAllText("Temp/ElementPerformance/slope-committed-snapshot.json",UnityEngine.JsonUtility.ToJson(new SlopeSnapshotReport{Utc=DateTime.UtcNow.ToString("O"),MaximumFieldDifference=maximumFieldDifference,QuantityChanges=quantityChanges,SupportChanges=supportChanges,PoseChanges=poseChanges,Changes=changes.ToArray()},true));
                Assert.That(quantityChanges,Is.Zero,"提交结果的数量不能在显示换相位时再次变化。");
                Assert.That(supportChanges,Is.Zero,"Previous必须保留提交时支撑，不能使用下一Tick的地板/下层数量。");
                Assert.That(maximumFieldDifference,Is.LessThan(.0001f),"坡脚同一提交快照切换Current→Previous时跳变："+maximumFieldDifference);
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(ramp);UnityEngine.Object.DestroyImmediate(receiving);UnityEngine.Object.DestroyImmediate(profile);}
        }
        [Serializable]private sealed class SlopeSnapshotReport{public string Utc;public float MaximumFieldDifference;public int QuantityChanges,SupportChanges,PoseChanges;public string[] Changes;}
        [Test]
        public void PendingCpuFallbackRetainsGpuStableClippingDomain()
        {
            if(!SystemInfo.supportsComputeShaders)Assert.Ignore("此GPU不支持Compute。");
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Stable domain during CPU handoff");host.SetActive(false);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            var renderProfile=UnityEngine.Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":64,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water});
                JsonUtility.FromJsonOverwrite("{\"_inputChunks\":32,\"_views\":16,\"_fineViews\":8,\"_budgetMiB\":32}",renderProfile);
                floor.transform.position=new Vector3(8,-.25f,2);floor.transform.localScale=new Vector3(64,.5f,8);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                source.TryAddInitial(new Vector3Int(3,0,3),MaterialId.Water,128,false);
                var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,renderProfile);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",fields).Invoke(renderer,null);
                var update=(Action)Delegate.CreateDelegate(typeof(Action),renderer,typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",fields));
                update();Assert.That(renderer.CpuFallback,Is.False,"先建立可保留的完整GPU表面。");
                var block=new MaterialPropertyBlock();renderer.BindStableDomain(block);int gpuMask=block.GetInt("_LiquidOwnerLookupMask");
                Assert.That(block.GetInt("_LiquidDomainEnabled"),Is.EqualTo(1));
                for(int x=11;x<=35;x+=8)Assert.That(source.TryAddInitial(new Vector3Int(x,0,3),MaterialId.Water,128,false),Is.True);
                // 超出页数现在走近GPU／远CPU Hybrid。此例专门验证真正的GPU→CPU交接，
                // 显式令r2容量为零，仍保留原GPU资源与Lookup供首个CPU批次完成前绘制。
                typeof(GridLiquidFieldSurfaceRenderer).GetField("_budget",fields).SetValue(renderer,new GridLiquidFieldBudget(4,1,32,16,0,32));
                update();Assert.That(renderer.CpuFallback && renderer.IsPresentationPending,Is.True);
                renderer.BindStableDomain(block);
                // 旧GPU外壳还在画，Stable仍须裁它的旧Owner域；否则CPU等待期间两张湖面重叠。
                Assert.That(block.GetInt("_LiquidDomainEnabled"),Is.EqualTo(1));
                Assert.That(block.GetInt("_LiquidOwnerLookupMask"),Is.EqualTo(gpuMask),"不能用尚未上传的CPU域，也不能提前裁掉新页面。");
                int updates=0;while(renderer.IsPresentationPending && updates++<4096)update();Assert.That(renderer.IsPresentationPending,Is.False);
                renderer.BindStableDomain(block);int cpuMask=block.GetInt("_LiquidOwnerLookupMask");Assert.That(cpuMask,Is.Not.EqualTo(gpuMask));
                source.TryAddInitial(new Vector3Int(3,0,3),MaterialId.Water,32,false);update();
                Assert.That(renderer.IsPresentationPending,Is.True);renderer.BindStableDomain(block);
                Assert.That(block.GetInt("_LiquidDomainEnabled"),Is.EqualTo(1));
                Assert.That(block.GetInt("_LiquidOwnerLookupMask"),Is.EqualTo(cpuMask),"下一CPU批次完成前保留上次CPU可见域，不回到更旧GPU域。");
                Assert.That(source.TotalQuantity,Is.EqualTo(672UL));
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(water);UnityEngine.Object.DestroyImmediate(renderProfile);}
        }
        [Test]
        public void CpuFallbackYieldsAndSwapsOnlyACompleteSurface()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("CPU bounded work regression");host.SetActive(false);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            var renderProfile=ScriptableObject.CreateInstance<GridLiquidFieldRenderProfile>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":64,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water});
                floor.transform.position=new Vector3(1,-.25f,1);floor.transform.localScale=new Vector3(8,.5f,8);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                for(int z=0;z<4;z++)for(int x=0;x<4;x++)source.TryAddInitial(new Vector3Int(x,0,z),MaterialId.Water,128,false);
                typeof(GridLiquidFieldRenderProfile).GetField("_shader",fields).SetValue(renderProfile,Shader.Find("Game/Elemental/Liquid Continuous Field"));
                var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,renderProfile);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",fields).Invoke(renderer,null);
                var update=(Action)Delegate.CreateDelegate(typeof(Action),renderer,typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",fields));
                update();object cpu=typeof(GridLiquidFieldSurfaceRenderer).GetField("_cpu",fields).GetValue(renderer);
                // 无Compute的兼容路径应在一页内部让出执行权，不能把一整页宽核采样塞进一帧。
                Assert.That((int)cpu.GetType().GetField("_built",fields).GetValue(cpu),Is.Zero);
                Assert.That(renderer.IsPresentationPending,Is.True);
                int iterations=0;while(renderer.IsPresentationPending && iterations++<4096)update();
                Assert.That(renderer.IsPresentationPending,Is.False,"有界工作必须最终完成，不能永久保留旧结果。");
                int visible=(int)cpu.GetType().GetField("_visible",fields).GetValue(cpu);
                var meshes=(Mesh[])cpu.GetType().GetField("_meshes",fields).GetValue(cpu);
                ulong indices=0;for(int i=0;i<visible;i++)indices+=meshes[i].GetIndexCount(0);Assert.That(indices,Is.GreaterThan(0));
                source.TryAddInitial(Vector3Int.zero,MaterialId.Water,32,false);update();
                Assert.That(renderer.IsPresentationPending,Is.True);
                Assert.That((int)cpu.GetType().GetField("_visible",fields).GetValue(cpu),Is.EqualTo(visible),"新批次完成前保留完整旧表面。");
                ulong retained=0;for(int i=0;i<visible;i++)retained+=meshes[i].GetIndexCount(0);Assert.That(retained,Is.EqualTo(indices));
                long before=GC.GetAllocatedBytesForCurrentThread();update();Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero);
                iterations=0;while(renderer.IsPresentationPending && iterations++<4096)update();
                Assert.That(renderer.IsPresentationPending,Is.False);Assert.That(source.TotalQuantity,Is.EqualTo(2080UL));
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(water);UnityEngine.Object.DestroyImmediate(renderProfile);}
        }
        [Test]
        public void SmallAmountWithLongTailKeepsCrownAndEveryGameplayQuantity()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Rounded long tail regression");host.SetActive(false);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":64,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water});
                floor.transform.position=new Vector3(2,-.25f,0);floor.transform.localScale=new Vector3(16,.5f,8);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                for(int x=0;x<8;x++)Assert.That(source.TryAddInitial(new Vector3Int(x,0,0),MaterialId.Water,x<4?128u:1u,false),Is.True);
                ((LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source)).BeginTick();
                var frame=new GridLiquidFieldFrame(source,64,128,.8f);frame.Capture();
                // 长尾是小量流水，不能因为跨度超过5格就关闭圆冠、重新变成薄膜。
                Assert.That(frame.Read(Vector3Int.zero,0,false).OffsetCells.y,Is.GreaterThan(.35f));
                Assert.That(source.TotalQuantity,Is.EqualTo(516UL));
                for(int x=0;x<8;x++){source.TryGetDynamicQuantity(new Vector3Int(x,0,0),MaterialId.Water,out uint q);Assert.That(q,Is.EqualTo(x<4?128u:1u));}
                for(int i=0;i<10;i++)frame.Capture();long before=GC.GetAllocatedBytesForCurrentThread();frame.Capture();
                Assert.That(GC.GetAllocatedBytesForCurrentThread()-before,Is.Zero,"长尾与Bounds优化不能引入热路径分配。");
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(water);}
        }
        [Test]
        public void SupportedColumnMergesPresentationOnlyAndKeepsEveryGameplayMaterial()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Presentation column regression");host.SetActive(false);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();var poison=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":64,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                typeof(LiquidBehaviorProfile).GetField("_material",fields).SetValue(poison,MaterialId.Poison);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water,poison});
                floor.transform.position=new Vector3(0,-.25f,0);floor.transform.localScale=new Vector3(8,.5f,8);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                Assert.That(source.TryAddInitial(Vector3Int.zero,MaterialId.Water,510,false),Is.True);
                Assert.That(source.TryAddInitial(Vector3Int.up,MaterialId.Poison,128,false),Is.True);
                ((LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source)).BeginTick();
                var frame=new GridLiquidFieldFrame(source,64,128,.8f);frame.Capture();
                for(int phase=0;phase<2;phase++)
                {
                    Assert.That(frame.Read(Vector3Int.zero,0,phase==0).Quantity,Is.EqualTo(510));
                    Assert.That(frame.Read(Vector3Int.zero,1,phase==0).Quantity,Is.EqualTo(128));
                    Assert.That(frame.Read(Vector3Int.up,1,phase==0).Quantity,Is.Zero,"一个接地液柱不能被拆成浮在另一壳上的独立盖片。");
                }
                Assert.That(source.TryGetDynamicQuantity(Vector3Int.up,MaterialId.Poison,out uint q),Is.True);Assert.That(q,Is.EqualTo(128));
                Assert.That(source.TotalQuantity,Is.EqualTo(638UL),"显示合柱不改变世界身份、密度或GMU。");
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(water);UnityEngine.Object.DestroyImmediate(poison);}
        }
        [Test]
        public void EveryDrawMaterialRetainsQuantityPaletteAfterCloning()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Liquid palette cloning regression");host.SetActive(false);
            var profile=UnityEditor.AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset");
            var renderProfile=UnityEditor.AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset");
            try
            {
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,renderProfile);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",fields).Invoke(renderer,null);
                object cpu=typeof(GridLiquidFieldSurfaceRenderer).GetField("_cpu",fields).GetValue(renderer);
                var cpuMaterial=(Material)cpu.GetType().GetField("_material",fields).GetValue(cpu);
                var main=(Material)typeof(GridLiquidFieldSurfaceRenderer).GetField("_material",fields).GetValue(renderer);
                var drops=(Material)typeof(GridLiquidFieldSurfaceRenderer).GetField("_dropMaterial",fields).GetValue(renderer);
                foreach(var material in new[]{main,drops,cpuMaterial})
                {
                    Assert.That(material.GetInt("_PaletteCount"),Is.EqualTo(3),"每条Draw路径必须绑定全部材料，不能回退成水色。");
                    Vector4[] colors=material.GetVectorArray("_PaletteShallow");Assert.That(colors.Length,Is.EqualTo(8));
                    for(int m=0;m<3;m++)Assert.That(colors[m],Is.EqualTo((Vector4)renderProfile.GetMaterial(m).GetColor("_ShallowColor")));
                    Assert.That(material.GetInt("_PaletteGroupCount"),Is.EqualTo(source.DensityGroupCount));
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(host);}
        }

        [Test]
        public void CpuFallbackIncludesIsolatedAirborneDropWithoutCompute()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("CPU fallback isolated drop");host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            var behavior=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            var renderProfile=ScriptableObject.CreateInstance<GridLiquidFieldRenderProfile>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":64,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{behavior});
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                var baker=host.AddComponent<ElementFieldSolidBaker>();
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,baker),Is.True);
                Assert.That(source.TryAddInitial(new Vector3Int(2,4,2),MaterialId.Water,32,true),Is.True);
                typeof(GridLiquidFieldRenderProfile).GetField("_shader",fields).SetValue(renderProfile,Shader.Find("Game/Elemental/Liquid Continuous Field"));
                var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,renderProfile);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",fields).Invoke(renderer,null);
                var update=typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",fields);
                // CPU在页内分段推进，等待完整提交；固定40次不再代表一个兼容表面构建周期。
                int updates=0;do{update.Invoke(renderer,null);}while(renderer.IsPresentationPending && ++updates<4096);
                Assert.That(renderer.CpuFallback,Is.True);Assert.That(renderer.IsPresentationPending,Is.False);
                object cpu=typeof(GridLiquidFieldSurfaceRenderer).GetField("_cpu",fields).GetValue(renderer);
                var meshes=(Mesh[])cpu.GetType().GetField("_meshes",fields).GetValue(cpu);
                int visible=(int)cpu.GetType().GetField("_visible",fields).GetValue(cpu);ulong indices=0;
                for(int i=0;i<visible;i++)if(meshes[i].subMeshCount>0)indices+=meshes[i].GetIndexCount(0);
                Assert.That(indices,Is.GreaterThan(0),"无Compute时孤立小滴也必须画出实际几何。");
                Assert.That(renderer.SmallDropCount,Is.Zero,"自由液体也走连续外壳，不再绘制独立圆珠。");
                Assert.That(source.TotalQuantity,Is.EqualTo(32UL));
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("OnDisable",fields).Invoke(renderer,null);
                Assert.That(renderer.IsReady,Is.False);Assert.That(renderer.GpuAllocatedBytes,Is.Zero);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("OnEnable",fields).Invoke(renderer,null);
                Assert.That(renderer.IsReady,Is.True,"重新启用必须重建Presentation资源。");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(behavior);UnityEngine.Object.DestroyImmediate(renderProfile);
            }
        }

        [Test]
        public void NegativeBoundaryIncludesEmptyOutputOwnersAndWarmCaptureAllocatesNothing()
        {
            var host=new GameObject("Continuous field frame regression");var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
            var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();var poison=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":128,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                typeof(LiquidBehaviorProfile).GetField("_material",fields).SetValue(poison,MaterialId.Poison);
                typeof(LiquidBehaviorProfile).GetField("_relativeDensity",fields).SetValue(poison,.9f);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water,poison});
                var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.transform.SetParent(host.transform);floor.transform.position=new Vector3(0,-.25f,0);floor.transform.localScale=new Vector3(8,.5f,8);Physics.SyncTransforms();
                var baker=host.AddComponent<ElementFieldSolidBaker>();var runtime=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(runtime,profile);
                Assert.That(runtime.TryInitialize(Vector3.zero,.25f,4,baker),Is.True);
                var cell=new Vector3Int(-1,0,-1);runtime.TryAddInitial(cell,MaterialId.Water,306,false);runtime.TryAddInitial(cell,MaterialId.Poison,204,false);runtime.Step(.1f);
                var frame=new GridLiquidFieldFrame(runtime,32,64);frame.Capture();
                Assert.That(frame.GetGroup(0),Is.Not.EqualTo(frame.GetGroup(1)));
                bool positive=false;for(int i=0;i<frame.OwnerCount;i++)positive|=frame.Owners[i].X==0 && frame.Owners[i].Z==0;
                Assert.That(positive,Is.True,"跨负坐标Chunk边缘的圆肩要有空邻页输出，不能被Core截断。");
                for(int i=0;i<frame.OwnerCount;i++)
                {
                    var key=frame.Owners[i];int h=(int)(GridLiquidFieldGpuLayout.Hash(key.X,key.Y,key.Z)&(uint)(frame.OwnerLookup.Length-1)),probes=0;
                    while(frame.OwnerLookup[h].Slot!=0 && (frame.OwnerLookup[h].X!=key.X || frame.OwnerLookup[h].Y!=key.Y || frame.OwnerLookup[h].Z!=key.Z) && probes++<32)h=(h+1)&(frame.OwnerLookup.Length-1);
                    Assert.That(frame.OwnerLookup[h].Slot,Is.EqualTo(i+1),"Stable裁剪域必须包含空输出Owner。");
                }
                long before=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10;i++)frame.Capture();long allocation=GC.GetAllocatedBytesForCurrentThread()-before;
                Assert.That(allocation,Is.Zero,"暖机Capture应只复用固定集合/数组。");Assert.That(runtime.TotalQuantity,Is.EqualTo(510UL));
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(water);UnityEngine.Object.DestroyImmediate(poison);}
        }
    }
}
