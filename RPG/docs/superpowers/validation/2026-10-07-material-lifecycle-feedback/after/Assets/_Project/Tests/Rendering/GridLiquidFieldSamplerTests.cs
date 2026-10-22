using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class GridLiquidFieldSamplerTests
    {
        [Test]
        public void CartoonShoulderKeepsThicknessAwayFromTheCenter()
        {
            var sampler=new GridLiquidFieldSampler(new Sample{Water=255,Poison=0,CrownStrength=.8f});
            // 侧面仍须包含主体，不能只有中心峰值厚、离中心33cm就削成薄裙边。
            Assert.That(sampler.Evaluate(new Vector3(.58f,.30f,.25f),1),Is.GreaterThan(0));
        }
        [Test]
        public void FreeSurfaceRoundsBothSidesOfItsShoulder()
        {
            var airborne=new GridLiquidFieldSampler(new Sample{Air=true,Water=510,Poison=0,CrownStrength=.8f});
            var grounded=new GridLiquidFieldSampler(new Sample{Air=false,Water=510,Poison=0,CrownStrength=.8f});
            var underShoulder=new Vector3(.6f,.04f,.25f);
            Assert.That(airborne.Evaluate(underShoulder,1),Is.LessThan(0),"空中肩部的下表面也应收圆，不能保留水平切平的薄盖片。");
            Assert.That(airborne.Evaluate(new Vector3(.6f,.20f,.25f),1),Is.GreaterThan(0),"圆肩的主体不能被整体裁掉。");
            Assert.That(grounded.Evaluate(underShoulder,1),Is.GreaterThan(0),"地面液体保持真实接地，不能为了下表面收圆而悬空。");
        }
        [Test]
        public void CartoonContactHidesClosingUndersideWithoutMovingVisibleTop()
        {
            var sampler=new GridLiquidFieldSampler(new Sample{Water=255,Poison=0,CrownStrength=.8f});
            var bottom=new Vector3(.25f,0,.25f);var bottomNormal=Vector3.down;
            sampler.CompressGroundProxy(ref bottom,ref bottomNormal,1);
            Assert.That(bottom.y,Is.LessThan(-.0001f),"接地底壳应有微小Depth Bias，避免和Collider共面闪烁。");
            Assert.That(bottom.y,Is.GreaterThanOrEqualTo(-.0005f),"圆肩共用Vertex，底壳不能再按2cm埋入并把朝上侧缘带入坡体。");
            var top=new Vector3(.25f,.6f,.25f);var topNormal=Vector3.up;
            sampler.CompressGroundProxy(ref top,ref topNormal,1);
            Assert.That(top.y,Is.GreaterThan(.1f));Assert.That(topNormal.y,Is.GreaterThan(0));
        }
        private sealed class JoinedSamples:IGridLiquidFieldSamples,IGridLiquidCrownSamples
        {
            public Vector3 Origin=>Vector3.zero;public float CellSize=>.5f;public int Capacity=>510;
            public int MaterialCount=>1;public int GroupCount=>1;public float Interval=>.1f;public float CrownStrength=>.8f;
            public float Crown(Vector3Int c,bool previous)=>1;public int GetGroup(int m)=>0;
            public bool Supported(Vector3Int c,bool previous)=>c.y==0 && c.z==0 && (c.x==0 || c.x==1);
            public Vector4 Terrain(Vector3Int c)=>c.x==0?new Vector4(.8660254f,.5f,0,.4330127f):new Vector4(0,1,0,0);
            public LiquidCellPresentationSample Read(Vector3Int c,int m,bool previous)=>Supported(c,previous)?new LiquidCellPresentationSample(32,Vector3.zero,Vector3.zero):default;
        }
        [Test]
        public void JoinedRampCompressionHasNoPlaneSelectionJumpOrBelowFloorVertices()
        {
            var sampler=new GridLiquidFieldSampler(new JoinedSamples());float maximum=0,minimum=1;Vector3 last=default;
            for(int i=0;i<=350;i++)
            {
                var p=new Vector3(.5f+i*.002f,.2f,.25f);Vector3 n=Vector3.up;sampler.CompressGroundProxy(ref p,ref n,1);
                if(i>0)maximum=Mathf.Max(maximum,Vector3.Distance(p,last));minimum=Mathf.Min(minimum,p.y);last=p;
            }
            // 输入移动2mm，连续重建不能在两个候选支撑面的压缩映射之间瞬移。
            Assert.That(maximum,Is.LessThan(.008f),"坡脚映射出现跳变："+maximum);
            Assert.That(minimum,Is.GreaterThanOrEqualTo(-.001f),"坡面外壳不能向外延伸到相接地面下方。");
        }
        [Test]
        public void LowRoundedShoulderKeepsOutwardNormalInsteadOfBeingForcedDown()
        {
            var sampler=new GridLiquidFieldSampler(new Sample{Water=255,Poison=0,CrownStrength=.8f});
            var position=new Vector3(.64f,.035f,.25f);
            var normal=new Vector3(.98f,.2f,0).normalized;
            sampler.CompressGroundProxy(ref position,ref normal,1);
            // 低侧壁仍朝外；把它按高度分支强制成底面，会在插值光照中出现褐色三角齿。
            Assert.That(normal.x,Is.GreaterThan(.2f),"圆肩低侧壁不能丢失向外的Normal分量。");
            Assert.That(normal.y,Is.GreaterThanOrEqualTo(0),"朝上的圆肩不能被翻到底部半球。");
        }
        [Test]
        public void CartoonCrownAddsBoundedThicknessAndReturnsToActualFullCellHeight()
        {
            Assert.That(GridLiquidFieldSampler.MapHeight(32f/510,.8f),Is.GreaterThanOrEqualTo(.42f),"0.5m粗格的小水量应呈现至少21cm的卡通厚度，而非薄膜。");
            Assert.That(GridLiquidFieldSampler.MapHeight(.25f,.8f),Is.GreaterThanOrEqualTo(.65f));
            Assert.That(GridLiquidFieldSampler.MapHeight(.25f,.8f),Is.GreaterThan(GridLiquidFieldSampler.MapHeight(.25f,.15f)*1.5f));
            Assert.That(GridLiquidFieldSampler.MapHeight(1,.8f),Is.EqualTo(1));
            Assert.That(GridLiquidFieldSampler.MapHeight(0,.8f),Is.Zero);
            Assert.That(GridLiquidFieldSampler.MapHeight(2,.8f),Is.EqualTo(2));
        }
        private sealed class SlopeSamples:IGridLiquidFieldSamples
        {
            public Vector3 Origin=>Vector3.zero;public float CellSize=>.5f;public int Capacity=>510;
            public int MaterialCount=>2;public int GroupCount=>1;public float Interval=>.1f;
            internal bool Landing;
            public int GetGroup(int material)=>0;
            private static float Floor(float x)=>.5f-Mathf.Tan(30*Mathf.Deg2Rad)*x;
            public Vector4 Terrain(Vector3Int c)=>new Vector4(.5f,.8660254f,0,Floor((c.x+.5f)*.5f));
            public bool Supported(Vector3Int c,bool previous)=>c.x>=0 && c.x<4 && c.z>=0 && c.z<4 && c.y==Mathf.CeilToInt(Floor(c.x*.5f)/.5f);
            public LiquidCellPresentationSample Read(Vector3Int c,int m,bool previous)=>Supported(c,previous) && (c.x<2?m==0:m==1)
                ?new LiquidCellPresentationSample(128,Landing?new Vector3(0,-.25f,0):Vector3.zero,Landing?new Vector3(1,-9,0):Vector3.zero):default;
        }
        [Test]
        public void GroundSupportPlaneStaysFixedWhenLandingHistoryHasVerticalVelocity()
        {
            var sampler=new GridLiquidFieldSampler(new SlopeSamples{Landing=true});
            for(int pose=0;pose<=4;pose++)
            {
                var p=new Vector3(1,.5f-Mathf.Tan(30*Mathf.Deg2Rad)+.04f,1);
                Assert.That(sampler.EvaluateGround(p,pose*.25f),Is.GreaterThan(0),"已支撑的共同外壳不能用落水History把Collider平面上下搬动。");
            }
        }
        [Test]
        public void OneSlopeAcrossIntegerRowsKeepsFullInteriorAndSmoothContactColor()
        {
            var sampler=new GridLiquidFieldSampler(new SlopeSamples());float[] colors=new float[8];float last=-1;
            for(int i=0;i<=20;i++)
            {
                float x=.8f+i*.02f;float floor=.5f-Mathf.Tan(30*Mathf.Deg2Rad)*x;
                var p=new Vector3(x,floor+.12f,1);
                Assert.That(sampler.EvaluateGround(p,.5f),Is.GreaterThan(0),"同一真实坡面不能按整数Y行形成内部浅沟。");
                sampler.Weights(p,.5f,colors);Assert.That(colors[0]+colors[1],Is.EqualTo(1).Within(.0001f));
                if(last>=0){Assert.That(colors[1],Is.GreaterThanOrEqualTo(last-.001f));Assert.That(colors[1]-last,Is.LessThan(.09f),"同密度接触处不能按Y行突然换材料色。");}last=colors[1];
            }
        }
        private sealed class Sample : IGridLiquidFieldSamples,IGridLiquidCrownSamples
        {
            public float CrownStrength{get;set;}=.15f;
            public float Crown(Vector3Int cell,bool previous)=>1;
            public Vector3 Origin => Vector3.zero;
            public float CellSize => .5f;
            public int Capacity => 510;
            public int MaterialCount => 2;
            public int GroupCount => EqualDensity ? 1 : 2;
            public float Interval => .1f;
            internal bool EqualDensity, Air;
            internal uint Water = 306, Poison = 204;
            public int GetGroup(int m) => EqualDensity ? 0 : m;
            public Vector4 Terrain(Vector3Int c) => new Vector4(0, 1, 0, c.y * .5f);
            public bool Supported(Vector3Int c, bool previous) => !Air && c == Vector3Int.zero;
            public LiquidCellPresentationSample Read(Vector3Int c, int m, bool previous) => c == Vector3Int.zero
                ? new LiquidCellPresentationSample(m == 0 ? Water : Poison, Vector3.zero, Vector3.zero) : default;
        }

        [Test]
        public void EqualDensityWeightsUseQuantityAndDistinctLayersUseHeight()
        {
            var data = new Sample { EqualDensity = true, Water = 50, Poison = 150 };
            var sampler = new GridLiquidFieldSampler(data); float[] weights = new float[8];
            sampler.Weights(new Vector3(.25f, .1f, .25f), .5f, weights);
            Assert.That(weights[0], Is.EqualTo(.25f).Within(.001f));
            Assert.That(weights[1], Is.EqualTo(.75f).Within(.001f));
            data.EqualDensity = false; data.Water = 306; data.Poison = 204;
            sampler.Weights(new Vector3(.25f, .05f, .25f), .5f, weights);
            Assert.That(weights[0], Is.GreaterThan(.99f));
            sampler.Weights(new Vector3(.25f, .45f, .25f), .5f, weights);
            Assert.That(weights[1], Is.GreaterThan(.99f));
            float[] quantities={306,204,0,0,0,0,0,0};
            sampler.DropWeights(.1f,quantities,weights);Assert.That(weights[0],Is.GreaterThan(.99f));
            sampler.DropWeights(.9f,quantities,weights);Assert.That(weights[1],Is.GreaterThan(.99f));
        }

        [Test]
        public void AirPacketHasQuantityBasedInteriorAtEveryInterpolationPhase()
        {
            var data = new Sample { Air = true, Water = 510, Poison = 0 };
            var sampler = new GridLiquidFieldSampler(data);
            for (int i = 0; i <= 10; i++)
                Assert.That(sampler.Evaluate(new Vector3(.25f, .25f, .25f), i / 10f), Is.GreaterThan(0f));
            Assert.That(sampler.Evaluate(new Vector3(.8f, .25f, .25f), .5f), Is.LessThan(0f));
        }
    }
}
