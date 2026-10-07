using System.Collections.Generic;
using Game.ElementField;
using NUnit.Framework;
using UnityEngine;

namespace Game.Rendering.Tests
{
    public sealed class StableLiquidShoreMesherTests
    {
        [Test]
        public void SharedLiquidShaderDefaultsToDynamicGeometryWithoutStableUvDeformation()
        {
            var shader=Shader.Find("Game/Elemental/Liquid Dormant Cell");
            Assert.That(shader,Is.Not.Null);
            var material=new Material(shader);
            try
            {
                // 动态/静态共用 Shader，但 UV0 含义不同；普通材质必须显式关闭静水重组。
                Assert.That(material.HasProperty("_StableLiquidShape"),Is.True);
                Assert.That(material.GetVector("_StableLiquidShape"),Is.EqualTo(Vector4.zero));
            }
            finally { Object.DestroyImmediate(material); }
        }

        [Test]
        public void StablePuddleKeepsBroadConvexShoulderAfterDepthScaling()
        {
            var patches=new[]{new StableLiquidSurfacePatch(1,1,
                new Vector3(.375f,0,.375f),new Vector2(.75f,.75f))};
            var mesher=new StableLiquidShoreMesher(128,8192);
            var vertices=new List<Vector3>(8192);var normals=new List<Vector3>(8192);
            var uvs=new List<Vector2>(8192);var triangles=new List<int>(24576);
            Assert.That(mesher.Build(patches,0,1,Vector3.zero,.25f,0,0,
                vertices,normals,uvs,triangles,1f),Is.True);
            // 与 Renderer 的 Y 缩放相同，验证休眠后仍是宽圆肩，而不是尖锐的厚边。
            float quarter=ScaledHeightAt(vertices,.0625f,.375f);
            float half=ScaledHeightAt(vertices,.125f,.375f);
            float inner=ScaledHeightAt(vertices,.1875f,.375f);
            float center=ScaledHeightAt(vertices,.375f,.375f);
            Assert.That(quarter,Is.GreaterThan(.22f*.45f).And.LessThan(center*.8f));
            Assert.That(half,Is.GreaterThan(quarter));
            Assert.That(inner,Is.GreaterThan(half));
            Assert.That(inner-half,Is.LessThan(half-quarter));
            Assert.That(ScaledHeightAt(vertices,.25f,.375f),Is.GreaterThan(.22f));
            Assert.That(center-inner,Is.GreaterThan(.006f),"休眠面内部也须保持整体圆冠。");
        }

        private static float ScaledHeightAt(List<Vector3> vertices,float x,float z)
        {
            float height=float.MinValue;
            foreach(Vector3 p in vertices)
                if(Mathf.Abs(p.x-x)<1e-5f && Mathf.Abs(p.z-z)<1e-5f)
                    height=Mathf.Max(height,.22f+p.y*(.22f/.25f));
            Assert.That(height,Is.GreaterThan(float.MinValue));
            return height;
        }

        [Test]
        public void StableQuantityUniformRaisesSameMeshWithoutOvershootOrWarmAllocation()
        {
            var patches=new[]{new StableLiquidSurfacePatch(1,1,
                new Vector3(.75f,0,.75f),new Vector2(1.5f,1.5f))};
            var mesher=new StableLiquidShoreMesher(128,8192);
            var vertices=new List<Vector3>(8192);var normals=new List<Vector3>(8192);
            var shapes=new List<Vector2>(8192);var gradients=new List<Vector4>(8192);
            var triangles=new List<int>(24576);
            Assert.That(mesher.Build(patches,0,1,Vector3.zero,.5f,0,0,
                vertices,normals,shapes,triangles,1f,gradients),Is.True);
            Assert.That(shapes.Count,Is.EqualTo(vertices.Count));
            Assert.That(gradients.Count,Is.EqualTo(vertices.Count));
            float previousPeak=0f;
            for(int level=1;level<=20;level++)
            {
                float fill=level/20f,body=LiquidPuddleShape.BodyHeight(fill,.64f);
                float crown=LiquidPuddleShape.CrownHeight(body),peak=0f;
                for(int i=0;i<vertices.Count;i++)
                {
                    // 对一次生成的轮廓应用 Shader 的高度/法线公式；层内注水不用再次 Build。
                    Vector2 shape=shapes[i];Vector4 gradient=gradients[i];
                    float height=(body*shape.x+crown*shape.y)*.5f;
                    Assert.That(height,Is.InRange(-1e-6f,.500001f));
                    Assert.That(height,Is.LessThanOrEqualTo(fill*.5f*1.9f));
                    var normal=new Vector3(-body*gradient.x-crown*gradient.z,1f,
                        -body*gradient.y-crown*gradient.w).normalized;
                    Assert.That(float.IsFinite(normal.x+normal.y+normal.z),Is.True);
                    peak=Mathf.Max(peak,height);
                }
                Assert.That(peak,Is.GreaterThan(previousPeak));previousPeak=peak;
            }
            Assert.That(previousPeak,Is.EqualTo(.5f).Within(1e-6f));
            vertices.Clear();normals.Clear();shapes.Clear();gradients.Clear();triangles.Clear();
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            bool rebuilt=mesher.Build(patches,0,1,Vector3.zero,.5f,0,0,
                vertices,normals,shapes,triangles,1f,gradients);
            long allocated=System.GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(rebuilt,Is.True);Assert.That(allocated,Is.Zero);
        }

        [Test]
        public void StableIsolatedPuddleHasCurvedSilhouette()
        {
            var patches=new[]{new StableLiquidSurfacePatch(1,1,
                new Vector3(.125f,0,.125f),new Vector2(.25f,.25f))};
            var mesher=new StableLiquidShoreMesher(128,8192);
            var vertices=new List<Vector3>(8192);var normals=new List<Vector3>(8192);
            var uvs=new List<Vector2>(8192);var triangles=new List<int>(24576);
            Assert.That(mesher.Build(patches,0,1,Vector3.zero,.25f,0,0,
                vertices,normals,uvs,triangles,1f),Is.True);
            float minimum=float.MaxValue,maximum=0f,longest=0f;
            foreach(Vector3 p in vertices)
            {
                if(Mathf.Abs(p.y+.25f)>1e-6f)continue;
                float radius=Vector2.Distance(new Vector2(p.x,p.z),new Vector2(.125f,.125f));
                minimum=Mathf.Min(minimum,radius);maximum=Mathf.Max(maximum,radius);
            }
            longest=LiquidSurfaceMeshBuilderTests.LongestCapBoundarySegment(vertices,triangles,-.25f);
            Assert.That(minimum,Is.GreaterThan(0f));
            Assert.That(maximum-minimum,Is.LessThan(.001f));
            Assert.That(longest,Is.GreaterThan(0f).And.LessThan(.05f));
        }

        [Test]
        public void ShallowStablePuddleRetainsRaisedCapAndRoundedRimWithoutAllocation()
        {
            var patches=new[]
            {
                new StableLiquidSurfacePatch(1,1,new Vector3(.375f,0,.375f),new Vector2(.75f,.75f)),
            };
            var mesh=new StableLiquidShoreMesher(128,8192);
            var vertices=new List<Vector3>(8192);var normals=new List<Vector3>(8192);
            var uvs=new List<Vector2>(8192);var triangles=new List<int>(24576);
            Assert.That(mesh.Build(patches,0,1,Vector3.zero,.25f,0,0,vertices,normals,uvs,triangles,1f),Is.True);
            float lowest=float.MaxValue,highest=float.MinValue;bool sloped=false;
            for(int i=0;i<vertices.Count;i++)
            {
                lowest=Mathf.Min(lowest,vertices[i].y);highest=Mathf.Max(highest,vertices[i].y);
                sloped|=normals[i].y>.1f && normals[i].y<.95f;
            }
            Assert.That(highest-lowest,Is.GreaterThan(.2f));
            Assert.That(sloped,Is.True,"稳定交接后仍须保留圆润岸沿，不能恢复为平面纸片。");
            vertices.Clear();normals.Clear();uvs.Clear();triangles.Clear();
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            bool rebuilt=mesh.Build(patches,0,1,Vector3.zero,.25f,0,0,vertices,normals,uvs,triangles,1f);
            long allocated=System.GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(rebuilt,Is.True);Assert.That(allocated,Is.Zero);
        }

        [Test]
        public void RoundedShoreKeepsHoleAndInteriorWithNoWarmRebuildAllocation()
        {
            // 3×3 的稳定水面，中央一格为空；三个行段与两个中间半行保持真实孔洞。
            var patches=new[]
            {
                new StableLiquidSurfacePatch(1,1,new Vector3(1.5f,0,.5f),new Vector2(3,1)),
                new StableLiquidSurfacePatch(1,1,new Vector3(.5f,0,1.5f),Vector2.one),
                new StableLiquidSurfacePatch(1,1,new Vector3(2.5f,0,1.5f),Vector2.one),
                new StableLiquidSurfacePatch(1,1,new Vector3(1.5f,0,2.5f),new Vector2(3,1)),
            };
            var mesh=new StableLiquidShoreMesher(128,8192);
            var vertices=new List<Vector3>(8192);var normals=new List<Vector3>(8192);
            var uvs=new List<Vector2>(8192);var triangles=new List<int>(24576);
            Assert.That(mesh.Build(patches,0,4,Vector3.zero,1,0,0,vertices,normals,uvs,triangles),Is.True);
            Assert.That(triangles.Count,Is.GreaterThan(0));
            bool curves=false,coveredInterior=false;
            var hole=new Vector3(1.5f,0,1.5f);var interior=new Vector3(.5f,0,.5f);
            for(int i=0;i<triangles.Count;i+=3)
            {
                Vector3 a=vertices[triangles[i]],b=vertices[triangles[i+1]],c=vertices[triangles[i+2]];
                Assert.That(Vector3.Cross(b-a,c-a).y,Is.GreaterThan(0f));
                Assert.That(Contains(hole,a,b,c),Is.False,"稳定区域不能把空孔洞画成水。");
                coveredInterior|=Contains(interior,a,b,c);
            }
            foreach(Vector3 p in vertices)
                curves|=Mathf.Abs(p.x-Mathf.Round(p.x*2)*.5f)>.001f
                    || Mathf.Abs(p.z-Mathf.Round(p.z*2)*.5f)>.001f;
            // Iso-contour 的插值位置不能全部落在 Cell 的矩形角点上。
            Assert.That(coveredInterior,Is.True);
            Assert.That(curves,Is.True);
            vertices.Clear();normals.Clear();uvs.Clear();triangles.Clear();
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            bool rebuilt=mesh.Build(patches,0,4,Vector3.zero,1,0,0,vertices,normals,uvs,triangles);
            long allocated=System.GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(rebuilt,Is.True);
            Assert.That(allocated,Is.Zero);
        }

        [Test]
        public void VertexBudgetStopsBeforeGrowingWorkLists()
        {
            // 超复杂岸线应让 Renderer 回退完整行段，不能先扩容 List 再发现超预算。
            var patches=new[]
            {
                new StableLiquidSurfacePatch(1,1,new Vector3(.5f,0,.5f),Vector2.one),
            };
            var mesh=new StableLiquidShoreMesher(16,4);
            var vertices=new List<Vector3>(4);var normals=new List<Vector3>(4);
            var uvs=new List<Vector2>(4);var triangles=new List<int>(12);
            Assert.That(mesh.Build(patches,0,1,Vector3.zero,1,0,0,vertices,normals,uvs,triangles),Is.False);
            Assert.That(vertices.Count,Is.LessThanOrEqualTo(4));
            Assert.That(vertices.Capacity,Is.EqualTo(4));
            Assert.That(normals.Capacity,Is.EqualTo(4));
            Assert.That(uvs.Capacity,Is.EqualTo(4));
            vertices.Clear();normals.Clear();uvs.Clear();triangles.Clear();
            long before=System.GC.GetAllocatedBytesForCurrentThread();
            bool rebuilt=mesh.Build(patches,0,1,Vector3.zero,1,0,0,vertices,normals,uvs,triangles);
            long allocated=System.GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.That(rebuilt,Is.False);
            Assert.That(allocated,Is.Zero);
        }

        private static bool Contains(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
        {
            float first=Vector3.Cross(b-a,p-a).y,second=Vector3.Cross(c-b,p-b).y,third=Vector3.Cross(a-c,p-c).y;
            return first>=-1e-6f && second>=-1e-6f && third>=-1e-6f;
        }
    }
}
