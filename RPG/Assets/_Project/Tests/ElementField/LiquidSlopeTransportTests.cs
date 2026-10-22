using NUnit.Framework;
using System;
using System.Reflection;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidSlopeTransportTests
    {
        [TestCase(30f,32u)]
        [TestCase(60f,32u)]
        [TestCase(30f,128u)]
        [TestCase(60f,128u)]
        public void SmallQuantityCrossesRealRampToeAndReachesAdjoiningFloor(float angle,uint quantity)
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Actual ramp to floor regression");
            var ramp=GameObject.CreatePrimitive(PrimitiveType.Cube);var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":64,\"_maximumActiveCells\":1024,\"_maximumCellsPerTick\":1024,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4,\"_enableAutoStableDetection\":false}",profile);
                JsonUtility.FromJsonOverwrite("{\"_sideSpreadReserveFill\":0.14,\"_maximumSideFlowPerTick\":112}",water);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water});
                float rad=angle*Mathf.Deg2Rad;
                ramp.transform.position=new Vector3(3,2*Mathf.Sin(rad)-.25f*Mathf.Cos(rad),2);
                ramp.transform.localScale=new Vector3(4,.5f,3);ramp.transform.rotation=Quaternion.Euler(0,0,-angle);
                floor.transform.position=new Vector3(4,-.25f,2);floor.transform.localScale=new Vector3(16,.5f,8);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                float toe=3+2*Mathf.Cos(rad)+.25f*Mathf.Sin(rad);int outside=Mathf.CeilToInt(toe/.5f);
                // 在坡脚上方两个粗格注入少量水，底板与坡顶端精确相接；没有悬空出口。
                int inletX=outside-3,inletY=0;
                while(source.IsSolidForSurface(new Vector3Int(inletX,inletY,3)))inletY++;
                var inlet=new Vector3Int(inletX,inletY,3);
                Assert.That(source.TryAddInitial(inlet,MaterialId.Water,quantity,true),Is.True);
                for(int tick=0;tick<100;tick++)source.Step(.1f);
                ulong received=0;
                for(int z=0;z<8;z++)for(int x=outside;x<outside+8;x++)
                {source.TryGetDynamicQuantity(new Vector3Int(x,0,z),MaterialId.Water,out uint q);received+=q;}
                if(received<quantity/2)
                {
                    var rows=new System.Collections.Generic.List<string>();
                    for(int z=0;z<8;z++)for(int y=0;y<12;y++)for(int x=0;x<outside+8;x++)
                    {var c=new Vector3Int(x,y,z);if(!source.TryGetDynamicQuantity(c,MaterialId.Water,out uint q) || q==0)continue;
                        bool valid=source.TryGetTerrainSupport(c,out var support);source.TryGetPresentationSample(c,MaterialId.Water,false,out var pose);
                        rows.Add($"{c}: q={q},support={valid},height={support.Height},normal={support.Normal},v={pose.VelocityWorld}");}
                    System.IO.File.WriteAllLines("Temp/ElementPerformance/toe-residue-"+angle+"-"+quantity+".txt",rows);
                }
                Assert.That(received,Is.GreaterThanOrEqualTo(quantity/2),"少量液体也应越过坡脚进入相接的地面，不能依靠不断加水挤出。");
                Assert.That(source.TotalQuantity,Is.EqualTo((ulong)quantity));
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(ramp);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(water);}
        }

        [Test]
        public void SmallGroundQuantityCannotBePinnedByFlatPuddleReserveOnSlope()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Small slope reserve regression");var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile=ScriptableObject.CreateInstance<LiquidWorldProfile>();var water=ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":32,\"_maximumActiveCells\":256,\"_maximumCellsPerTick\":256,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4,\"_enableAutoStableDetection\":false}",profile);
                JsonUtility.FromJsonOverwrite("{\"_sideSpreadReserveFill\":0.14,\"_maximumSideFlowPerTick\":112}",water);
                typeof(LiquidWorldProfile).GetField("_materials",fields).SetValue(profile,new[]{water});
                floor.transform.position=new Vector3(2,-.25f,2);floor.transform.localScale=new Vector3(6,.5f,6);floor.transform.rotation=Quaternion.Euler(0,0,-30);Physics.SyncTransforms();
                var source=host.AddComponent<LiquidWorldRuntime>();typeof(LiquidWorldRuntime).GetField("_profile",fields).SetValue(source,profile);
                Assert.That(source.TryInitialize(Vector3.zero,.25f,4,host.AddComponent<ElementFieldSolidBaker>()),Is.True);
                var inlet=new Vector3Int(2,2,3);Assert.That(source.TryAddInitial(inlet,MaterialId.Water,32,true),Is.True);
                for(int tick=0;tick<12;tick++)source.Step(.1f);
                source.TryGetDynamicQuantity(inlet,MaterialId.Water,out uint remaining);
                Assert.That(remaining,Is.LessThan(32),"平地聚团用的Reserve不能把少量水永久钉在真实斜坡上。");
                Assert.That(source.TotalQuantity,Is.EqualTo(32UL));
            }
            finally{UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(floor);UnityEngine.Object.DestroyImmediate(profile);UnityEngine.Object.DestroyImmediate(water);}
        }

        [Test]
        public void PacketLeavingGroundRetainsHorizontalMomentumWithoutPassingWall()
        {
            var store=new LiquidVolumeStore(4,16,510,new[]{MaterialId.Water},c=>c.y<0 || c.x>=2);
            var activity=new LiquidActivityScheduler(128,store);
            var solver=new LiquidFluxSolver(store,activity,null,new[]{MaterialId.Water},new[]{510},new[]{0},new[]{0},cellSize:.5f);
            var source=new Vector3Int(0,8,0);Assert.That(store.TryAdd(source,MaterialId.Water,128),Is.True);
            // 模拟已提交的离坡速度；不修改数量，验证下一Tick进入真实重力输运路径。
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            object ground=typeof(LiquidFluxSolver).GetField("_ground",fields).GetValue(solver);
            MethodInfo put=ground.GetType().GetMethod("Put",fields);Type stateType=put.GetParameters()[2].ParameterType;
            object state=Activator.CreateInstance(stateType);
            stateType.GetField("Velocity",fields).SetValue(state,Vector3.right*2);
            stateType.GetField("Tick",fields).SetValue(state,-1);put.Invoke(ground,new[]{(object)source,MaterialId.Water,state});
            activity.Wake(source);solver.Step(128,.1f,.1f);
            Assert.That(solver.TryGetGroundVelocity(source,MaterialId.Water,out Vector3 velocity),Is.True);
            Assert.That(velocity.x,Is.GreaterThan(1.9f),"离坡后横向动量必须进入下落状态，而非过两Tick失效。");
            for(int i=1;i<12;i++)solver.Step(128,.1f,(i+1)*.1f);
            ulong moved=0;
            for(int y=0;y<=8;y++){store.TryGetQuantity(new Vector3Int(1,y,0),MaterialId.Water,out uint q);moved+=q;}
            Assert.That(moved,Is.EqualTo(128UL),"液包应沿离坡动量前进到墙前。");
            Assert.That(store.TotalQuantity,Is.EqualTo(128UL));
        }

        [Test]
        public void SlopeGravityAcceleratesAlongActualPlane()
        {
            Vector3 n = new Vector3(.5f, .8660254f, 0f);
            Vector3 v = LiquidTransportMath.IntegrateGroundVelocity(Vector3.zero, n, Vector3.zero, 9.81f, 1.5f, .1f, 2f);
            Assert.That(v.x, Is.GreaterThan(.2f));
            Assert.That(v.y, Is.LessThan(-.1f));
            Assert.That(Vector3.Dot(v, n), Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void DragIsIndependentOfDensityAndFlatGravityHasNoHorizontalForce()
        {
            Vector3 water = LiquidTransportMath.IntegrateGroundVelocity(Vector3.right, Vector3.up, Vector3.zero, 9.81f, 1.5f, .1f, 2f);
            Vector3 sticky = LiquidTransportMath.IntegrateGroundVelocity(Vector3.right, Vector3.up, Vector3.zero, 9.81f, 12f, .1f, 2f);
            Assert.That(water.x, Is.LessThan(1f));
            Assert.That(water.x, Is.GreaterThan(sticky.x));
            Assert.That(LiquidTransportMath.IntegrateGroundVelocity(Vector3.zero, Vector3.up, Vector3.zero, 9.81f, 1.5f, .1f, 2f), Is.EqualTo(Vector3.zero));
        }
    }
}
