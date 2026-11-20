using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>按同一数量比较不同流速，检查形变来自运动而不是凭空增加水量。</summary>
    public sealed class LiquidFlowPresentationTests
    {
        private sealed class Stream : IGridLiquidFieldSamples,IGridLiquidCrownSamples,IGridLiquidShapeSamples
        {
            internal Vector3 Velocity;
            internal bool Ground;
            internal bool Landing;
            internal int LandingCells=1;
            public Vector3 Origin=>Vector3.zero;
            public float CellSize=>.5f;
            public int Capacity=>510;
            public int MaterialCount=>1;
            public int GroupCount=>1;
            public float Interval=>.1f;
            public float CrownStrength=>.8f;
            public float Crown(Vector3Int c,bool previous)=>1;
            public float ShapeScale(Vector3Int c,bool previous)=>.7f;
            public bool SolidSupported(Vector3Int c,bool previous)=>Ground;
            public int GetGroup(int slot)=>0;
            public bool Supported(Vector3Int c,bool previous)=>Ground && c.y==4;
            public Vector4 Terrain(Vector3Int c)=>new Vector4(0,1,0,2);
            public LiquidCellPresentationSample Read(Vector3Int c,int slot,bool previous)
            {
                if(Landing)return c==new Vector3Int(0,previous?4+LandingCells:4,0)?new LiquidCellPresentationSample(128,Vector3.zero,previous?Vector3.down*(LandingCells*5):Vector3.zero):default;
                return c==new Vector3Int(0,4,0)?new LiquidCellPresentationSample(128,Vector3.zero,Velocity):default;
            }
        }

        [Test]
        public void FallingStreamStretchesVerticallyAndNarrowsLaterallyAtSameQuantity()
        {
            var still=new GridLiquidFieldSampler(new Stream());
            var falling=new GridLiquidFieldSampler(new Stream{Velocity=new Vector3(0,-12,0)});
            var high=new Vector3(.25f,2.45f,.25f);
            var shoulder=new Vector3(.55f,2.25f,.25f);
            Assert.That(still.Evaluate(high,1),Is.LessThan(0));
            Assert.That(falling.Evaluate(high,1),Is.GreaterThan(0),"真实下落速度应生成有方向的连续液舌，不是仅平移扁平格片。");
            Assert.That(still.Evaluate(shoulder,1),Is.GreaterThan(0));
            Assert.That(falling.Evaluate(shoulder,1),Is.LessThan(0),"拉长时横截面须同步缩窄，不能扩大显示体积。");
        }

        [Test]
        public void AirAxesPreserveVolumeForVerticalHorizontalAndDiagonalMotion()
        {
            foreach(var velocity in new[]{Vector3.zero,Vector3.down*12,Vector3.right*8,new Vector3(8,2,4),new Vector3(64,-64,-64)})
            {
                Vector3 axes=GridLiquidFlowPresentation.AirAxes(velocity,.1f,.5f);
                Assert.That(axes.x*axes.y*axes.z,Is.EqualTo(1).Within(.00001f));
                Assert.That(Mathf.Max(axes.x,Mathf.Max(axes.y,axes.z)),Is.LessThanOrEqualTo(1.60001f));
                Assert.That(Mathf.Min(axes.x,Mathf.Min(axes.y,axes.z)),Is.GreaterThan(.6f));
            }
        }
        [Test]
        public void SurfaceDetailPackingKeepsSignedFlowAndImpactWithoutGrowingVertexStride()
        {
            for(int i=-8;i<=8;i++)
            {
                var data=GridLiquidFlowPresentation.Unpack(GridLiquidFlowPresentation.Pack(.37f,new Vector2(i,-i*.5f),.82f));
                Assert.That(data.x,Is.EqualTo(.37f).Within(1f/255));Assert.That(data.y,Is.EqualTo(i).Within(8f/127));
                Assert.That(data.z,Is.EqualTo(-i*.5f).Within(8f/127));Assert.That(data.w,Is.EqualTo(.82f).Within(1f/255));
            }
            Assert.That(GridLiquidFieldGpuLayout.VertexStride,Is.EqualTo(48));
        }
        [TestCase(1)]
        [TestCase(2)]
        public void LandingImpactReadsAirbornePreviousVelocityAfterCurrentHasStoppedOnTheFloor(int cells)
        {
            var sampler=new GridLiquidFieldSampler(new Stream{Ground=true,Landing=true,LandingCells=cells});
            var point=new Vector3(.25f,2.2f,.25f);var normal=Vector3.up;
            // Previous位于上一行、尚无支撑；Current在真实地板上，竖向速度已归零。
            // 落地冲击必须取这同一个完整搬运窗口，不能只在supported行查找负vy。
            sampler.CompressGroundProxy(ref point,ref normal,.5f);
            Assert.That(sampler.LastImpact,Is.GreaterThan(.05f));
        }
        [Test]
        public void GroundFlowAndImpactAreDerivedFromTheSameQuantityWeightedWindow()
        {
            var sampler=new GridLiquidFieldSampler(new Stream{Ground=true,Velocity=new Vector3(3,-4,-2)});
            var point=new Vector3(.25f,2.2f,.25f);var normal=Vector3.up;
            sampler.CompressGroundProxy(ref point,ref normal,1);
            Assert.That(sampler.LastFlowVelocity.x,Is.EqualTo(3).Within(.0001f));
            Assert.That(sampler.LastFlowVelocity.y,Is.EqualTo(-2).Within(.0001f));
            Assert.That(sampler.LastImpact,Is.EqualTo(.48f).Within(.0001f));
        }
        [Test]
        public void TinyDropStoresActualVelocityAndConservesVolumeAtEverySubframe()
        {
            var motion=new GridLiquidFallingMotion{PreviousCenter=Vector3.zero,CurrentCenter=Vector3.one,PreviousVelocity=new Vector3(8,2,0),CurrentVelocity=new Vector3(8,-2,0),Previous0=4,Current0=4};
            for(int i=0;i<=4;i++)
            {
                var drop=motion.Sample(i*.25f,.1f,.5f,1f/510);
                Assert.That(GridLiquidPackedCell.Low(drop.Third),Is.EqualTo(8).Within(.001f));
                Assert.That(GridLiquidPackedCell.High(drop.Third),Is.EqualTo(2-i).Within(.001f));
                Assert.That(GridLiquidPackedCell.High(drop.Fourth),Is.InRange(1f,1.601f));
                Assert.That(4*Mathf.PI/3*drop.Radius*drop.Radius*drop.Radius,Is.EqualTo(4f/510*.125f).Within(.000001f));
            }
        }
        [Test]
        public void SupportedWaterDoesNotStretchItsColliderPlaneWithVerticalHistory()
        {
            var still=new GridLiquidFieldSampler(new Stream{Ground=true});
            var landed=new GridLiquidFieldSampler(new Stream{Ground=true,Velocity=new Vector3(0,-12,0)});
            for(int i=0;i<8;i++)
            {
                var at=new Vector3(.25f,2.02f+i*.04f,.25f);
                Assert.That(landed.Evaluate(at,1),Is.EqualTo(still.Evaluate(at,1)).Within(.000001f));
            }
        }
    }
}
