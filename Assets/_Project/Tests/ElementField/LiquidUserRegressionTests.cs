using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class LiquidUserRegressionTests
    {
        private static IgniteGooTuning Ignition => new IgniteGooTuning
        {
            FireThreshold = 20, GooThreshold = 10, WetInhibitThreshold = 10,
            RequiredFireCapacity = 10, GooConsumePerSecond = 40, FirePerGoo = 1.25f,
        };

        [Test]
        public void FullStrengthSourceIgnitesEmptyTargetAndSparseFuel()
        {
            IgniteGooTuning tuning = Ignition;
            Assert.That(LiquidReactionAdapter.TryPlanIgnition(255, 0, 128, 0, .1f,
                in tuning, out int consumed, out int produced), Is.True);
            Assert.That(consumed, Is.GreaterThan(0));
            Assert.That(produced, Is.GreaterThan(0));
            Assert.That(LiquidReactionAdapter.TryPlanIgnition(255, 0, 4, 0, .1f,
                in tuning, out consumed, out produced), Is.True);
            Assert.That(consumed, Is.EqualTo(4));
            Assert.That(produced, Is.GreaterThanOrEqualTo(Mathf.CeilToInt(tuning.FireThreshold*2.55f)),
                "薄燃料产生的火也必须能传递下一次点火。");
            Assert.That(LiquidReactionAdapter.TryPlanIgnition(255, 250, 128, 0, .1f,
                in tuning, out _, out _), Is.False, "产物目标满格时不能扣燃料。");
            Assert.That(LiquidReactionAdapter.TryPlanIgnition(255, 0, 128, 100, .1f,
                in tuning, out _, out _), Is.False, "Wet 仍然抑制引燃。");
        }

        [Test]
        public void SparseFuelProducesEnoughFlameToIgniteTheNextCell()
        {
            IgniteGooTuning tuning = Ignition;
            byte sourceFire = 255;
            // 沿一条真实薄燃料链逐 Tick 传火；下一格不能依赖最初的外部火源。
            for (int cell = 0; cell < 8; cell++)
            {
                Assert.That(LiquidReactionAdapter.TryPlanIgnition(sourceFire,0,4,0,.1f,
                    in tuning,out int consumed,out int produced),Is.True,$"燃烧前沿在第{cell}格熄灭。");
                Assert.That(consumed,Is.EqualTo(4));
                sourceFire=(byte)produced;
            }
            Assert.That(LiquidReactionAdapter.TryPlanIgnition(sourceFire,0,4,100,.1f,
                in tuning,out _,out _),Is.False,"积水必须阻止传播。");
        }

        [Test]
        public void DefaultCubeWallsContainFallingWaterInRealPhysicsBakedBasin()
        {
            var objects=new GameObject[6];Vector3 origin=new Vector3(90,120,80);
            try
            {
                for(int i=0;i<5;i++)objects[i]=GameObject.CreatePrimitive(PrimitiveType.Cube);
                objects[0].transform.position=origin+new Vector3(0,-.125f,0);
                objects[0].transform.localScale=new Vector3(2.4f,.25f,2.4f);
                for(int i=1;i<=4;i++)
                {
                    bool xWall=i<3;float side=(i&1)==0?1f:-1f;
                    objects[i].transform.position=origin+new Vector3(xWall?side:0,.5f,xWall?0:side);
                    objects[i].transform.localScale=xWall?new Vector3(.15f,1f,2.4f):new Vector3(2.4f,1f,.15f);
                }
                objects[5]=new GameObject("Default Cube basin sampling");
                var baker=objects[5].AddComponent<ElementFieldSolidBaker>();
                Physics.SyncTransforms();
                var store=new LiquidVolumeStore(4,32,255,new[]{MaterialId.Water},
                    c=>baker.IsStaticSolidCell(c,origin,.25f));
                var activity=new LiquidActivityScheduler(2048,store);
                var solver=new LiquidFluxSolver(store,activity,null,new[]{MaterialId.Water},
                    new[]{96},new[]{24},new[]{8});
                for(int x=0;x<2;x++)for(int z=0;z<2;z++)
                {
                    var cell=new Vector3Int(x,2,z);
                    Assert.That(store.TryAdd(cell,MaterialId.Water,255),Is.True);
                    activity.WakeNeighbors(cell);
                }
                for(int tick=0;tick<100;tick++)solver.Step(2048,.1f,tick*.1f);
                var samples=new LiquidMaterialCellSample[2048];
                int count=store.CopyOccupiedCells(MaterialId.Water,samples);
                Assert.That(count,Is.GreaterThan(0));
                for(int i=0;i<count;i++)
                {
                    Assert.That(samples[i].GlobalCell.y,Is.EqualTo(0));
                    Assert.That(samples[i].GlobalCell.x,Is.InRange(-3,2));
                    Assert.That(samples[i].GlobalCell.z,Is.InRange(-3,2));
                }
                Assert.That(store.TotalQuantity,Is.EqualTo(1020UL));
            }
            finally {foreach(GameObject item in objects)if(item!=null)Object.DestroyImmediate(item);}
        }

        [TestCase(MaterialId.Water)]
        [TestCase(MaterialId.Poison)]
        [TestCase(MaterialId.Sticky)]
        public void FallingPacketAcceleratesContinuouslyKeepsQuantityAndStopsAtFloor(MaterialId material)
        {
            // 真实 Store + Solver；竖直封闭管道能同时捕获穿墙、空中横向分散和重复 Tick 积分。
            var store = new LiquidVolumeStore(4, 32, 255, new[] { material },
                c => c.x != 0 || c.z != 0 || c.y < 0);
            var activity = new LiquidActivityScheduler(128, store);
            var solver = new LiquidFluxSolver(store, activity, null, new[] { material },
                new[] { 96 }, new[] { 24 }, new[] { 8 });
            var start = new Vector3Int(0, 12, 0);
            Assert.That(store.TryAdd(start, material, 100), Is.True);
            activity.WakeNeighbors(start);
            for(int tick=1;tick<=6;tick++)
            {
                solver.Step(128,.1f,tick*.1f);
                float distance=.5f*9.81f*(tick*.1f)*(tick*.1f)/.25f;
                var cell=start+Vector3Int.down*Mathf.FloorToInt(distance);
                Assert.That(store.TryGetQuantity(cell,material,out uint q),Is.True,
                    $"tick={tick}, expected cell={cell}, queue={activity.Count}");
                Assert.That(q,Is.EqualTo(100));
                Assert.That(solver.TryGetFallingMotion(cell,material,out LiquidFallingMotion motion),Is.True);
                Assert.That(motion.OffsetCells,Is.EqualTo(distance-Mathf.Floor(distance)).Within(.0001f));
                Assert.That(motion.Velocity,Is.EqualTo(9.81f*tick*.1f).Within(.0001f));
                Assert.That(store.TotalQuantity,Is.EqualTo(100UL));
            }
            for(int tick=7;tick<=16;tick++) solver.Step(128,.1f,tick*.1f);
            Assert.That(store.TryGetQuantity(Vector3Int.zero,material,out uint landed),Is.True);
            Assert.That(landed,Is.EqualTo(100));
            Assert.That(store.TryGetQuantity(Vector3Int.down,material,out _),Is.False);
            Assert.That(store.TotalQuantity,Is.EqualTo(100UL));
            Assert.That(solver.TryGetFallingMotion(Vector3Int.zero,material,out _),Is.False);
            Assert.That(activity.Count,Is.EqualTo(0),"落地后不再反复唤醒。");
        }
    }
}
