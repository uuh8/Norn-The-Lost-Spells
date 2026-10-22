using System.Collections;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.ElementField;
using Game.Materials;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Rendering.Tests
{
    public sealed class GridLiquidFieldGpuTests
    {
        [UnityTest]
        public IEnumerator EqualQuantityHasComparableVisibleVolumeOnFlatAndSteepSupport()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要实际GPU。");
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var records=new List<SupportVolumeRecord>();
            for(int scenario=0;scenario<4;scenario++)
            {
                float angle=scenario==1?30:scenario==2?60:0;int width=scenario==3?2:4;
                var host=new GameObject("Support visual volume comparison");host.SetActive(false);
                var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
                var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
                var renderProfile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
                try
                {
                    JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":64,\"_maximumActiveCells\":256,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                    typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water});
                    floor.transform.position=new Vector3(2,-.25f,2);floor.transform.localScale=new Vector3(12,.5f,8);floor.transform.rotation=Quaternion.Euler(0,0,-angle);Physics.SyncTransforms();
                    var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                    Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                    float rad=angle*Mathf.Deg2Rad;
                    for(int z=0;z<width;z++)for(int x=0;x<width;x++)
                    {
                        int y=Mathf.CeilToInt((-.25f+.25f/Mathf.Cos(rad)+(2-x*.5f)*Mathf.Tan(rad))/.5f);
                        var cell=new Vector3Int(x,y,z);while(source.IsSolidForSurface(cell))cell.y++;
                        uint quantity=width==2?510u:(uint)(127+(x+z)%2);
                        Assert.That(source.TryAddInitial(cell,MaterialId.Water,quantity,false),Is.True);
                    }
                    ((LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source)).BeginTick();
                    JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":128,\"_views\":64,\"_fineViews\":64}",renderProfile);
                    var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,renderProfile);
                    typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",fields).Invoke(renderer,null);typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",fields).Invoke(renderer,null);
                    Assert.That(renderer.IsReady && !renderer.CpuFallback,Is.True);
                    var readback=new MeshReadback(renderer);yield return readback.Wait();
                    var vs=readback.Vertices;var ids=readback.Indices;var args=readback.Arguments;double volume=0;int triangles=0;
                    var points=new Dictionary<Vector3Int,WeldedPoint>();var edges=new Dictionary<long,int>();
                    float maximumDepth=0;Vector3 plane=floor.transform.TransformPoint(new Vector3(0,.5f,0)),normal=floor.transform.up;
                    for(int page=0;page<renderer.SurfacePages;page++)for(int at=(int)args[page*5+2],end=at+(int)args[page*5];at<end;at+=3)
                    {
                        Vector3 a=vs[(int)ids[at]].Position,b=vs[(int)ids[at+1]].Position,c=vs[(int)ids[at+2]].Position;
                        Accumulate(a,b,c,ref volume,ref triangles,points,edges);
                        maximumDepth=Mathf.Max(maximumDepth,Mathf.Max(Vector3.Dot(a-plane,normal),Mathf.Max(Vector3.Dot(b-plane,normal),Vector3.Dot(c-plane,normal))));
                    }
                    records.Add(new SupportVolumeRecord{Angle=angle,Width=width,Quantity=source.TotalQuantity,Volume=Math.Abs(volume),Depth=maximumDepth,Triangles=triangles,Subdivision=(int)typeof(GridLiquidFieldSurfaceRenderer).GetField("_subdivision",fields).GetValue(renderer)});
                }
                finally{Object.DestroyImmediate(host);Object.DestroyImmediate(floor);Object.DestroyImmediate(profile);Object.DestroyImmediate(water);Object.DestroyImmediate(renderProfile);}
            }
            File.WriteAllText("Temp/ElementPerformance/continuous-support-volume.json",JsonUtility.ToJson(new SupportVolumeReport{Utc=DateTime.UtcNow.ToString("O"),Records=records.ToArray()},true));
            // 比较真正GPU外壳，不能仅以GMU相等或Shader参数相同断言玩家看到的体积相等。
            foreach(var r in records){Assert.That(r.Quantity,Is.EqualTo(2040UL));Assert.That(r.Triangles,Is.GreaterThan(0));Assert.That(r.Volume,Is.EqualTo(records[0].Volume).Within(records[0].Volume*.25),r.Angle+"°/"+r.Width+"格宽：同量液体的显示体积不能随坡度或分散程度明显膨胀/缩水。");}
        }
        [Serializable]private sealed class SupportVolumeRecord{public float Angle,Depth;public ulong Quantity;public double Volume;public int Width,Triangles,Subdivision;}
        [Serializable]private sealed class SupportVolumeReport{public string Utc;public SupportVolumeRecord[] Records;}
        [UnityTest]
        public IEnumerator SustainedSlopeMeshDoesNotSinkIntoActualCollider()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要实际GPU。");
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var reports=new List<SlopeContactReport>();
            foreach(float angle in new[]{30f,60f})
            {
                var host=new GameObject("Sustained slope contact regression");host.SetActive(false);
                var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
                var original=AssetDatabase.LoadAssetAtPath<LiquidWorldProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/LiquidWorldProfile_P7_DemoRun_Grid.asset");
                var profile=Object.Instantiate(original);
                var renderProfile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
                try
                {
                    JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":64,\"_maximumActiveCells\":2048,\"_maximumCellsPerTick\":256,\"_maximumStableRegions\":8,\"_maximumStableRegionCells\":4096,\"_maximumStableSurfacePatches\":512,\"_enableAutoStableDetection\":false}",profile);
                    floor.transform.position=new Vector3(2,-.25f,2);floor.transform.localScale=new Vector3(6,.5f,6);floor.transform.rotation=Quaternion.Euler(0,0,-angle);Physics.SyncTransforms();
                    var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                    Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                    float rad=angle*Mathf.Deg2Rad;var inlet=new Vector3Int(2,Mathf.CeilToInt((-.25f+.25f/Mathf.Cos(rad)+Mathf.Tan(rad))/.5f),3);
                    Assert.That(source.TryAddInitial(inlet,MaterialId.Water,510,true),Is.True);
                    for(int tick=1;tick<=30;tick++){if(tick%3==0)Assert.That(source.TryAddInitial(inlet,MaterialId.Water,170,true),Is.True);source.Step(.1f);}
                    JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":128,\"_views\":64,\"_fineViews\":64}",renderProfile);
                    var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,renderProfile);
                    typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",fields).Invoke(renderer,null);
                    typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",fields).Invoke(renderer,null);
                    Assert.That(renderer.IsReady && !renderer.CpuFallback,Is.True);
                    var readback=new MeshReadback(renderer);yield return readback.Wait();
                    var vs=readback.Vertices;var ids=readback.Indices;var args=readback.Arguments;
                    var report=new SlopeContactReport{Angle=angle,Minimum=float.PositiveInfinity,MinimumTop=float.PositiveInfinity};
                    var planePoint=floor.transform.TransformPoint(new Vector3(0,.5f,0));var normal=floor.transform.up;
                    var underPoints=new List<string>();var normalPoints=new List<string>();var sampler=new GridLiquidFieldSampler(renderer.Frame);
                    // 可见Triangle朝坡外，但插值Normal朝坡内时，PBR会把连续液面照成暗色断条。
                    // 检查真正绘制的面与Normal一致性，数量守恒/封闭Mesh无法覆盖这个回归。
                    for(int page=0;page<renderer.SurfacePages;page++)for(int at=(int)args[page*5+2],end=at+(int)args[page*5];at<end;at+=3)
                    {
                        var a=vs[(int)ids[at]];var b=vs[(int)ids[at+1]];var c=vs[(int)ids[at+2]];
                        Vector3 center=(a.Position+b.Position+c.Position)/3,localCenter=floor.transform.InverseTransformPoint(center);
                        if(Mathf.Abs(localCenter.x)>.48f || Mathf.Abs(localCenter.z)>.48f)continue;
                        Vector3 face=Vector3.Cross(b.Position-a.Position,c.Position-a.Position);
                        // 面积不足10cm²的退化折边不能代表主体光照，接触薄边另由截图检查。
                        // 主体顶面应接近平行真实坡面；坡外水平备用底板和圆肩侧壁单独观察。
                        if(face.sqrMagnitude<4e-6f || Vector3.Dot(face.normalized,normal)<.7f)continue;
                        // 12cm接触带内的封闭侧缘可共用顶/底Vertex；其最终Normal由Fragment连续约束。
                        // 此处验收真实坡面上足够厚的主体，排除坡外备用地板和隐藏接地底壳。
                        if(Vector3.Dot(center-planePoint,normal)<.12f)continue;
                        report.NormalTriangles++;
                        float agreement=Vector3.Dot(face.normalized,(a.Normal+b.Normal+c.Normal).normalized);
                        report.MinimumNormalAgreement=Mathf.Min(report.MinimumNormalAgreement,agreement);
                        if(agreement<-.05f){report.ReversedTopNormals++;if(normalPoints.Count<8)normalPoints.Add($"a={a.Position}/n={a.Normal},b={b.Position}/n={b.Normal},c={c.Position}/n={c.Normal},face={face.normalized},agreement={agreement}");}
                    }
                    // 只统计Collider顶部内部，真正离坡下落的外侧液体允许低于无限延长平面。
                    for(int page=0;page<renderer.SurfacePages;page++)for(int at=(int)args[page*5+2],end=at+(int)args[page*5];at<end;at++)
                    {
                        Vector3 p=vs[(int)ids[at]].Position,local=floor.transform.InverseTransformPoint(p);
                        if(Mathf.Abs(local.x)>.48f || Mathf.Abs(local.z)>.48f)continue;
                        float distance=Vector3.Dot(p-planePoint,normal);report.Minimum=Mathf.Min(report.Minimum,distance);report.Vertices++;
                        if(Vector3.Dot(vs[(int)ids[at]].Normal,normal)>.2f){report.MinimumTop=Mathf.Min(report.MinimumTop,distance);if(distance<-.001f)report.UnderTop++;}
                        if(distance<-.001f)
                        {
                            report.Under++;
                            if(underPoints.Count<12)
                            {Vector3 adjusted=p,n=vs[(int)ids[at]].Normal;sampler.CompressGroundProxy(ref adjusted,ref n,renderer.InterpolationAlpha);
                                underPoints.Add($"p={p},n={vs[(int)ids[at]].Normal},local={local},CPU adjusted={adjusted},alpha={renderer.InterpolationAlpha}");}
                        }
                    }
                    var diagnostics=new List<string>();var frame=renderer.Frame;
                    for(int page=0;page<frame.Count;page++)for(int local=0;local<64;local++)
                    {
                        var key=frame.Keys[page];var c=new Vector3Int(key.X*4+local%4,key.Y*4+local/4%4,key.Z*4+local/16);
                        var sample=frame.Read(c,0,false);if(sample.Quantity==0 || !frame.Supported(c,false))continue;
                        var terrain=frame.Terrain(c);diagnostics.Add($"{c}:q={sample.Quantity},terrain={terrain},offset={sample.OffsetCells},v={sample.VelocityWorld}");
                    }
                    report.Cells=diagnostics.ToArray();report.UnderPoints=underPoints.ToArray();report.NormalPoints=normalPoints.ToArray();reports.Add(report);
                }
                finally{Object.DestroyImmediate(host);Object.DestroyImmediate(floor);Object.DestroyImmediate(profile);Object.DestroyImmediate(renderProfile);}
            }
            File.WriteAllText("Temp/ElementPerformance/continuous-slope-contact.json",JsonUtility.ToJson(new SlopeContactReports{Utc=DateTime.UtcNow.ToString("O"),Reports=reports.ToArray()},true));
            // Cull Back不绘制底壳。粗格边缘的底壳可以被Solid遮挡；验收真正朝外的上表面不穿坡。
            foreach(var report in reports){Assert.That(report.Vertices,Is.GreaterThan(0));Assert.That(float.IsFinite(report.MinimumTop),Is.True);Assert.That(report.MinimumTop,Is.GreaterThanOrEqualTo(-.001f),report.Angle+"°坡面可见液面不能沉入真实Collider。");Assert.That(report.Minimum,Is.GreaterThanOrEqualTo(-.0201f),"只允许接地的封闭底壳最多嵌入2cm；液帘不能穿入坡体。");Assert.That(report.NormalTriangles,Is.GreaterThan(0),"法线回归必须覆盖实际主体Triangle，不能空集通过。");Assert.That(report.ReversedTopNormals,Is.Zero,report.Angle+"°可见主体Normal不能朝Triangle的反半球，最小一致性="+report.MinimumNormalAgreement);}
        }
        [Serializable]private sealed class SlopeContactReport{public float Angle,Minimum,MinimumTop,MinimumNormalAgreement=1;public int Vertices,Under,UnderTop,NormalTriangles,ReversedTopNormals;public string[] Cells,UnderPoints,NormalPoints;}
        [Serializable]private sealed class SlopeContactReports{public string Utc;public SlopeContactReport[] Reports;}
        [UnityTest]
        public IEnumerator RealSlopeGpuMeshMovesBetweenTicksAndRetainsBoundaryPose()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要实际GPU。");
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Slope temporal geometry regression");host.SetActive(false);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            var renderProfile=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":32,\"_maximumActiveCells\":128,\"_maximumCellsPerTick\":128,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water});
                floor.transform.position=new Vector3(2,-.25f,2);floor.transform.localScale=new Vector3(6,.5f,6);floor.transform.rotation=Quaternion.Euler(0,0,-30);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                Assert.That(source.TryAddInitial(new Vector3Int(2,2,3),MaterialId.Water,510,true),Is.True);source.Step(.1f);source.Step(.1f);
                JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":64,\"_views\":64,\"_fineViews\":64}",renderProfile);
                var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,renderProfile);
                typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",fields).Invoke(renderer,null);
                var update=typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",fields);var captured=typeof(GridLiquidFieldSurfaceRenderer).GetField("_capturedAt",fields);
                update.Invoke(renderer,null);Vector3[] centers=new Vector3[6];var historyRows=new List<string>();
                var frame=renderer.Frame;
                for(int page=0;page<frame.Count;page++)for(int local=0;local<64;local++)for(int phase=0;phase<2;phase++)
                {var key=frame.Keys[page];var c=new Vector3Int(key.X*4+local%4,key.Y*4+local/4%4,key.Z*4+local/16);var s=frame.Read(c,0,phase==0);if(s.Quantity>0)historyRows.Add($"{phase}/{c}: q={s.Quantity},support={frame.Supported(c,phase==0)},terrain={frame.Terrain(c)},shape={frame.ShapeScale(c,phase==0)},offset={s.OffsetCells},v={s.VelocityWorld}");}
                for(int pose=0;pose<6;pose++)
                {
                    if(pose==5){source.Step(.1f);update.Invoke(renderer,null);}
                    captured.SetValue(renderer,Time.time-(pose==5?0:pose*.025f));update.Invoke(renderer,null);
                    var readback=new MeshReadback(renderer);yield return readback.Wait();
                    var vs=readback.Vertices;var ids=readback.Indices;var args=readback.Arguments;
                    // 面积加权中心是实际绘制Triangle的几何统计，不用Solver速度替代画面证据。
                    double total=0;Vector3 center=default;
                    for(int page=0;page<renderer.SurfacePages;page++)for(int i=(int)args[page*5+2],end=i+(int)args[page*5];i<end;i+=3)
                    {Vector3 a=vs[(int)ids[i]].Position,b=vs[(int)ids[i+1]].Position,c=vs[(int)ids[i+2]].Position;float area=Vector3.Cross(b-a,c-a).magnitude;center+=(a+b+c)*(area/3);total+=area;}
                    Assert.That(total,Is.GreaterThan(0),"真实坡面浅水不能漏画。");centers[pose]=center/(float)total;
                }
                File.WriteAllText("Temp/ElementPerformance/continuous-slope-temporal.json",JsonUtility.ToJson(new TemporalReport{Utc=DateTime.UtcNow.ToString("O"),Centers=centers,Cells=historyRows.ToArray()},true));
                float distance=Vector3.Distance(centers[0],centers[4]);Assert.That(distance,Is.GreaterThan(.005f));
                for(int i=1;i<5;i++)
                {
                    float step=Vector3.Distance(centers[i-1],centers[i]);Assert.That(step,Is.GreaterThan(.0002f),"不能在Tick之间冻结几何。");
                    // 圆冠变形时面积中心可先上移再下移；首末净位移不能作为单步上限。
                    // 直接按实际Grid尺度限制25ms子帧移动小于一格的10%，并独立检查Tick接续。
                    Assert.That(step,Is.LessThan(source.CellSize*.1f),"单一子帧不能出现明显格级跳动。");
                }
                Assert.That(Vector3.Distance(centers[4],centers[5]),Is.LessThan(.002f),"Tick切换必须接续上一帧实际几何。");
                Assert.That(source.TotalQuantity,Is.EqualTo(510UL));
            }
            finally{Object.DestroyImmediate(host);Object.DestroyImmediate(floor);Object.DestroyImmediate(profile);Object.DestroyImmediate(water);Object.DestroyImmediate(renderProfile);}
        }
        [Serializable]private sealed class TemporalReport{public string Utc;public Vector3[] Centers;public string[] Cells;}

        [UnityTest]
        public IEnumerator ProductionFrameMeshHasClosedSurfaceAndQuantityCalibratedVolume()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要Compute及GPU readback。");
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var records=new List<VolumeRecord>();
            for(int scenario=0;scenario<12;scenario++)
            {
                bool drop=scenario==3 || scenario==10,cpu=scenario==2 || scenario==5 || scenario==11,cartoon=scenario>=7;int width=scenario==0?1:scenario>=9?12:4;
                uint quantity=scenario==3 || scenario==4 || scenario==5 || scenario==8?32u:scenario>=6?128u:510u;
                var host=new GameObject("GPU quantity volume calibration");host.SetActive(false);
                var floor=drop?null:GameObject.CreatePrimitive(PrimitiveType.Cube);
                var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();
                var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
                var renderProfile=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>("Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
                try
                {
                    JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":16,\"_maximumActiveCells\":128,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}",profile);
                    typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water});
                    if(floor!=null){floor.transform.position=new Vector3(2,-.25f,2);floor.transform.localScale=new Vector3(8,.5f,8);Physics.SyncTransforms();}
                    var source=host.AddComponent<LiquidWorldRuntime>();var baker=host.AddComponent<ElementFieldSolidBaker>();
                    typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                    Assert.That(source.TryInitialize(Vector3.zero,.25f,4,baker),Is.True);
                    for(int z=0;z<width;z++)for(int x=0;x<width;x++)
                        Assert.That(source.TryAddInitial(new Vector3Int(x,drop?4:0,z),MaterialId.Water,quantity,false),Is.True);
                    ((LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history",fields).GetValue(source)).BeginTick();
                    // 校准模式明确使用0.15；默认美术模式另行检查，不能把增厚体积冒称物理守恒。
                    JsonUtility.FromJsonOverwrite("{\"_cartoonCrownStrength\":"+(cartoon?"0.8":"0.15")+",\"_budgetMiB\":32,\"_inputChunks\":32,\"_views\":64,\"_fineViews\":"+(cpu?0:64)+"}",renderProfile);
                    var renderer=host.AddComponent<GridLiquidFieldSurfaceRenderer>();renderer.ConfigureForValidation(source,renderProfile);
                    typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start",fields).Invoke(renderer,null);
                    typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",fields).Invoke(renderer,null);
                    Assert.That(renderer.IsReady,Is.True);
                    Assert.That(renderer.CpuFallback,Is.EqualTo(cpu));
                    double volume=0;float maximumHeight=float.NegativeInfinity;int triangles=0;var points=new Dictionary<Vector3Int,WeldedPoint>();var edges=new Dictionary<long,int>();
                    if(cpu)
                    {
                        double deadline=EditorApplication.timeSinceStartup+10;
                        while(renderer.IsPresentationPending)
                        {Assert.That(EditorApplication.timeSinceStartup,Is.LessThan(deadline));typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate",fields).Invoke(renderer,null);yield return null;}
                        var surface=typeof(GridLiquidFieldSurfaceRenderer).GetField("_cpu",fields).GetValue(renderer);
                        var meshes=(Mesh[])surface.GetType().GetField("_meshes",fields).GetValue(surface);
                        int visible=(int)surface.GetType().GetField("_visible",fields).GetValue(surface);
                        for(int i=0;i<visible;i++)
                        {var positions=meshes[i].vertices;var faces=meshes[i].triangles;for(int at=0;at<faces.Length;at+=3){maximumHeight=Mathf.Max(maximumHeight,Mathf.Max(positions[faces[at]].y,Mathf.Max(positions[faces[at+1]].y,positions[faces[at+2]].y)));Accumulate(positions[faces[at]],positions[faces[at+1]],positions[faces[at+2]],ref volume,ref triangles,points,edges);}}
                    }
                    else
                    {
                    var readback=new MeshReadback(renderer);yield return readback.Wait();
                    var vs=readback.Vertices;var ids=readback.Indices;var args=readback.Arguments;
                    for(int page=0;page<renderer.SurfacePages;page++)
                    {
                    Assert.That(args[page*5]+args[page*5+2],Is.LessThanOrEqualTo((uint)ids.Length),"Indirect Arguments必须限制在真实Index Buffer内。");
                    for(int at=(int)args[page*5+2],end=at+(int)args[page*5];at<end;at+=3)
                    {
                        Vector3 a=vs[(int)ids[at]].Position,b=vs[(int)ids[at+1]].Position,c=vs[(int)ids[at+2]].Position;
                        maximumHeight=Mathf.Max(maximumHeight,Mathf.Max(a.y,Mathf.Max(b.y,c.y)));
                        Accumulate(a,b,c,ref volume,ref triangles,points,edges);
                    }
                    }
                    }
                    double expected=source.TotalQuantity/(double)source.CellCapacityGmu*Math.Pow(source.CellSize,3);int open=0;
                    var edgeErrors=new List<string>();var locations=new Vector3Int[points.Count];foreach(var entry in points)locations[entry.Value.Id]=entry.Key;
                    foreach(var edge in edges)if(edge.Value!=2){open++;if(edgeErrors.Count<20)edgeErrors.Add(edge.Value+":"+locations[(int)(edge.Key>>32)]+"->"+locations[(int)(uint)edge.Key]);}
                    records.Add(new VolumeRecord{Case=scenario==10?"wide-air-body":scenario>=9?"wide-ground-body":drop?"free-liquid-sheet":quantity<510?"thin-ground-"+quantity:width==1?"single-ground":"patch-ground",Path=cpu?"CPU budget fallback":"GPU",Cartoon=cartoon,VolumeScale=cartoon?renderProfile.CartoonVolumeScale:1,Subdivision=(int)typeof(GridLiquidFieldSurfaceRenderer).GetField("_subdivision",fields).GetValue(renderer),Expected=expected,Actual=Math.Abs(volume),MaximumHeight=maximumHeight,Triangles=triangles,OpenEdges=open,EdgeErrors=edgeErrors.ToArray(),Drops=renderer.SmallDropCount,Components=ConnectedComponents(edges,points.Count)});
                    File.WriteAllText("Temp/ElementPerformance/continuous-volume-calibration.json",JsonUtility.ToJson(new VolumeReport{Utc=DateTime.UtcNow.ToString("O"),Records=records.ToArray()},true));
                }
                finally{UnityEngine.Object.DestroyImmediate(host);if(floor!=null)UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(water);UnityEngine.Object.DestroyImmediate(renderProfile);}
            }
            foreach(var record in records)
            {
                if(record.Case!="free-liquid-sheet")
                {
                    Assert.That(record.Drops,Is.Zero,"贴地浅水必须是连续外壳，不能按粗格画珠子。");
                    Assert.That(record.Triangles,Is.GreaterThan(0),"薄于显示采样间距的浅水也必须有连续Mesh。");
                    Assert.That(record.Components,Is.EqualTo(1),"相邻浅水格必须合成一张连通外壳。");
                }
                Assert.That(record.OpenEdges,Is.Zero,record.Case+"跨Owner的统一外壳必须闭合。");
                if(record.Cartoon)
                {
                    // 本次美术明确使用1.8倍显示体积；必须明示倍率并校准，不能冒称物理GMU守恒。
                    // 同量在坡面/地面共用该倍率；Dome只重新分配高度，其额外误差仍限制在25%。
                    double stylized=record.Expected*record.VolumeScale;
                    Assert.That(record.Actual,Is.EqualTo(stylized).Within(stylized*.25),"卡通外壳不能超出明确显示倍率的25%离散误差；不能任意膨胀。");
                    Assert.That(record.MaximumHeight,Is.GreaterThan(record.Case=="thin-ground-32"?.18f:.29f),"检查实际顶点高度，不能只有MapHeight参数看起来增厚。");
                    if(record.Case=="wide-ground-body")Assert.That(record.MaximumHeight,Is.GreaterThan(.60f),"宽浅水主体应鼓起，不能只在边缘卷一条边。");
                }
                else Assert.That(record.Actual,Is.EqualTo(record.Expected).Within(record.Expected*.2),record.Case+"实际封闭Mesh体积应与GMU在20%校准范围内。");
            }
            Assert.That(records[3].Drops,Is.Zero,"空中相邻液体也不能绘制独立圆珠。");
            Assert.That(records[3].Components,Is.EqualTo(1),"同层相邻空中格应形成连续液帘。");
            Assert.That(records[10].Components,Is.EqualTo(1),"大空中液体也必须是连通外壳。");
            Assert.That(records[10].MaximumHeight,Is.GreaterThan(2.40f),"空中主体真实中心上方必须有15cm以上截面。");
            Assert.That(records[10].Actual,Is.EqualTo(records[9].Actual).Within(records[9].Actual*.25),"同量液体从空中落地不能明显缩水。");
        }
        private static void Accumulate(Vector3 a,Vector3 b,Vector3 c,ref double volume,ref int triangles,Dictionary<Vector3Int,WeldedPoint> points,Dictionary<long,int> edges)
        {if(Vector3.Cross(b-a,c-a).sqrMagnitude<1e-14f)return;volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6.0;triangles++;int ia=Weld(a,points),ib=Weld(b,points),ic=Weld(c,points);Edge(ia,ib,edges);Edge(ib,ic,edges);Edge(ic,ia,edges);}
        private readonly struct WeldedPoint
        {public readonly int Id;public readonly Vector3 Position;public WeldedPoint(int id,Vector3 position){Id=id;Position=position;}}
        private static int Weld(Vector3 p,Dictionary<Vector3Int,WeldedPoint> points)
        {
            var key=Vector3Int.RoundToInt(p*10000);
            // Owner重建同一顶点会有微米级Float舍入。仅比较Round后的桶，会把恰好位于
            // 0.1mm桶边界两侧的同一点报为裂缝；邻桶也按真实欧氏距离焊接，不放宽几何容差。
            for(int z=-1;z<=1;z++)for(int y=-1;y<=1;y++)for(int x=-1;x<=1;x++)
                if(points.TryGetValue(key+new Vector3Int(x,y,z),out var value) && (value.Position-p).sqrMagnitude<=1e-8f)return value.Id;
            int id=points.Count;var target=key;
            // 极少数不同点落在同桶时保留两点身份，不能机械量化把真实小Triangle焊没。
            while(points.ContainsKey(target))target.x++;
            points.Add(target,new WeldedPoint(id,p));return id;
        }
        private static void Edge(int a,int b,Dictionary<long,int> edges)
        {if(a==b)return;long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);edges.TryGetValue(key,out int count);edges[key]=count+1;}
        private static int ConnectedComponents(Dictionary<long,int> edges,int count)
        {
            int[] parents=new int[count];for(int i=0;i<count;i++)parents[i]=i;
            foreach(long edge in edges.Keys)
            {int a=(int)(edge>>32),b=(int)(uint)edge;while(parents[a]!=a)a=parents[a];while(parents[b]!=b)b=parents[b];parents[b]=a;}
            int components=0;for(int i=0;i<count;i++)if(parents[i]==i)components++;return components;
        }
        [Serializable]private sealed class VolumeRecord{public string Case,Path;public bool Cartoon;public int Subdivision,Triangles,OpenEdges,Drops,Components;public double Expected,Actual;public float MaximumHeight,VolumeScale;public string[] EdgeErrors;}
        [Serializable]private sealed class VolumeReport{public string Utc;public VolumeRecord[] Records;}

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex{public Vector3 Position;public float Valid;public Vector3 Normal;public uint A,B,C,D,Pad;}
        private sealed class MeshReadback
        {
            public Vertex[] Vertices;public uint[] Indices,Arguments;
            private int _pending=3;private bool _error;
            public MeshReadback(GridLiquidFieldSurfaceRenderer renderer)
            {
                var v=AsyncGPUReadback.Request(renderer.DebugVertices,OnVertices);v.forcePlayerLoopUpdate=true;
                var i=AsyncGPUReadback.Request(renderer.DebugIndices,OnIndices);i.forcePlayerLoopUpdate=true;
                var a=AsyncGPUReadback.Request(renderer.DebugArguments,OnArguments);a.forcePlayerLoopUpdate=true;
            }
            // Request结果仅在完成后的一个PlayerLoop有效。分别在callback复制，不能等最慢
            // Buffer完成后才读取另外两个已过期Request；这里的分配只属于Editor测试。
            private void OnVertices(AsyncGPUReadbackRequest r){_error|=r.hasError;if(!r.hasError)Vertices=r.GetData<Vertex>().ToArray();_pending--;}
            private void OnIndices(AsyncGPUReadbackRequest r){_error|=r.hasError;if(!r.hasError)Indices=r.GetData<uint>().ToArray();_pending--;}
            private void OnArguments(AsyncGPUReadbackRequest r){_error|=r.hasError;if(!r.hasError)Arguments=r.GetData<uint>().ToArray();_pending--;}
            public IEnumerator Wait()
            {double deadline=EditorApplication.timeSinceStartup+10;while(_pending>0){Assert.That(EditorApplication.timeSinceStartup,Is.LessThan(deadline),"GPU callback timed out.");yield return null;}Assert.That(_error,Is.False,"GPU readback callback reported an actual error.");}
        }
        [UnityTest]
        public IEnumerator RealGpuBuildProducesClosedFieldLayerWeightsAndBoundedIndices()
        {
            if(!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)Assert.Ignore("需要Compute和AsyncGPUReadback。");
            var original=AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/_Project/Art/Elemental/Compute/GridLiquidField.compute");
            var compute=Object.Instantiate(original);var budget=new GridLiquidFieldBudget(4,2,2,2,1,16);
            using(var gpu=new GridLiquidFieldResources(budget))
            {
                var cells=new GridLiquidPackedCell[4*4*4*2*2];
                cells[0]=GridLiquidPackedCell.Pack(new Game.ElementField.LiquidCellPresentationSample(306,Vector3.zero,Vector3.zero),1);
                cells[1]=GridLiquidPackedCell.Pack(new Game.ElementField.LiquidCellPresentationSample(204,Vector3.zero,Vector3.zero),1);
                var terrain=new Vector4[128];for(int i=0;i<terrain.Length;i++)terrain[i]=new Vector4(0,1,0,i/4%4*.5f);
                var lookup=new GridLiquidChunkLookup[budget.LookupSlots];lookup[0]=new GridLiquidChunkLookup(0,0,0,1);
                gpu.Previous.SetData(cells);gpu.Current.SetData(cells);gpu.Terrain.SetData(terrain);gpu.Lookup.SetData(lookup);
                gpu.Owners.SetData(new[]{new GridLiquidChunkLookup(0,0,0,1)});
                gpu.OwnerBounds.SetData(new[]{new Vector4(-2,-2,-2,0),new Vector4(3,3,3,0)});
                compute.SetInt("_K",4);compute.SetInt("_MaterialCount",2);compute.SetInt("_GroupCount",2);compute.SetInts("_Groups0",new[]{0,1,0,0});compute.SetInts("_Groups1",new[]{0,0,0,0});
                compute.SetInt("_LookupMask",budget.LookupSlots-1);compute.SetInt("_OwnerCount",1);compute.SetInt("_Resolution",2);compute.SetInt("_NodeSide",15);compute.SetInt("_CubeSide",14);
                compute.SetInt("_IndexCapacity",18*8*8*8);compute.SetVector("_OriginSize",new Vector4(0,0,0,.5f));compute.SetFloat("_Capacity",510);compute.SetFloat("_Interval",.1f);compute.SetFloat("_Alpha",.5f);
                compute.SetFloat("_CrownStrength",.15f);
                string[] names={"ClearCounters","SampleField","BuildVertices","SmoothVertices","FinishVertices","BuildFaces","FinishArguments"};
                int[] kernels=new int[names.Length];for(int i=0;i<names.Length;i++){kernels[i]=compute.FindKernel(names[i]);gpu.Bind(compute,kernels[i]);}
                compute.Dispatch(kernels[0],1,1,1);compute.Dispatch(kernels[1],(15*15*15+63)/64,1,1);compute.Dispatch(kernels[2],(14*14*14+63)/64,1,1);
                compute.SetInt("_SmoothPass",0);compute.Dispatch(kernels[3],(14*14*14+63)/64,1,1);compute.SetInt("_SmoothPass",1);compute.Dispatch(kernels[3],(14*14*14+63)/64,1,1);
                compute.Dispatch(kernels[4],(14*14*14+63)/64,1,1);compute.Dispatch(kernels[5],8,1,1);compute.Dispatch(kernels[6],1,1,1);
                var field=AsyncGPUReadback.Request(gpu.Field);var vertices=AsyncGPUReadback.Request(gpu.Vertices);var args=AsyncGPUReadback.Request(gpu.Arguments);
                // EditMode readback 需要真实 PlayerLoop；限时失败比无限等待更能保护未保存关卡。
                field.forcePlayerLoopUpdate=true;vertices.forcePlayerLoopUpdate=true;args.forcePlayerLoopUpdate=true;
                double deadline=EditorApplication.timeSinceStartup+10;
                while(!field.done || !vertices.done || !args.done)
                {Assert.That(EditorApplication.timeSinceStartup,Is.LessThan(deadline),"GPU readback timed out.");yield return null;}
                Assert.That(field.hasError || vertices.hasError || args.hasError,Is.False);
                Assert.That(field.GetData<float>()[4+15*(4+15*4)],Is.GreaterThan(0),"球团中心必须位于实际GPU液体场内。");
                Assert.That(field.GetData<float>()[8+15*(4+15*4)],Is.LessThan(0));
                uint count=args.GetData<uint>()[0];Assert.That(count,Is.GreaterThan(0));Assert.That(count,Is.LessThanOrEqualTo(18*8*8*8));Assert.That(count%6,Is.Zero);
                bool water=false,poison=false;var vs=vertices.GetData<Vertex>();
                for(int i=0;i<14*14*14;i++)if(vs[i].Valid>0){Assert.That(float.IsFinite(vs[i].Position.x),Is.True);water|=GridLiquidPackedCell.Low(vs[i].A)>.9f;poison|=GridLiquidPackedCell.High(vs[i].A)>.9f;}
                Assert.That(water && poison,Is.True,"同一外壳应包含重层侧面与轻层顶面的权重。");
            }
            Object.DestroyImmediate(compute);
        }
    }
}
