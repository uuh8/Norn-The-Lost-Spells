using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Rendering.Tests
{
    public sealed class MaterialWorldSolidGasSceneContractTests
    {
        [TestCase(1)]
        [TestCase(63)]
        [TestCase(64)]
        [TestCase(255)]
        public void SandMeshMatchesAmountOnBothSidesOfHotAndColdPageBoundary(int amount)
        {
            const uint capacity = 255;
            const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var host = new GameObject("Sand mesh boundary");
            host.SetActive(false);
            var hotMesh = new Mesh();
            var wallMesh = new Mesh();
            Mesh coldMesh = null;
            try
            {
                var world = host.AddComponent<ElementWorldRuntime>();
                var renderer = host.AddComponent<CellularMaterialRenderer>();
                typeof(CellularMaterialRenderer).GetField("_world", fields).SetValue(renderer, world);
                var samples = (Dictionary<Vector3Int, CellularMaterialRenderer.Sample>)typeof(CellularMaterialRenderer)
                    .GetField("_samples", fields).GetValue(renderer);
                var cold = new CellularRestingLiquidStore(null, capacity, 32);
                typeof(ElementWorldRuntime).GetField("<RestingLiquids>k__BackingField", fields).SetValue(world, cold);
                // x=7 和 x=8 横跨两个渲染页。右侧的同一格先以动态格出现，再真实安装到压缩树。
                var edge = new Vector3Int(CellularMaterialRenderer.PageSize - 1, 0, 0);
                var grain = edge + Vector3Int.right;
                samples[edge] = new CellularMaterialRenderer.Sample { Material = MaterialId.Sand, Quantity = capacity };
                samples[grain] = new CellularMaterialRenderer.Sample { Material = MaterialId.Sand, Quantity = (uint)amount };
                var mesher = new CellularMaterialSurfaceMesh();
                mesher.Build(hotMesh, new ElementChunkKey(1,0,0), new List<Vector3Int> { grain }, renderer, capacity);
                samples.Remove(grain);
                typeof(CellularRestingLiquidStore).GetMethod("SetReplicaQuantity", fields)
                    .Invoke(cold, new object[] { grain, MaterialId.Sand, (uint)amount });
                var resting = new CellularRestingLiquidSurface();
                resting.Refresh(cold, renderer);
                // Build 是正常可见性调度最后一步。这里只立刻烘焙同一份页，避免 EditMode 冒充真实相机验收。
                var regions = (IList)typeof(CellularRestingLiquidSurface).GetField("_regions", fields).GetValue(resting);
                int regionIndex = cold.FindRegionIndex(grain);
                object region = regions[regionIndex];
                typeof(CellularRestingLiquidSurface).GetMethod("Build", fields).Invoke(resting, new[] { region, renderer });
                coldMesh = (Mesh)region.GetType().GetField("Mesh", fields).GetValue(region);
                Assert.That(coldMesh, Is.Not.Null);
                var coldOrigin = cold.GetRegion(regionIndex).Cells.min;
                AssertSandBounds(hotMesh, Vector3.zero, amount, capacity);
                AssertSandBounds(coldMesh, grain - coldOrigin, amount, capacity);

                // 少量沙的足迹没有触到左格时必须看得到整个邻格侧壁；两边满格时应省掉整张内部面。
                mesher.Build(wallMesh, new ElementChunkKey(0,0,0), new List<Vector3Int> { edge }, renderer, capacity);
                var size = ElementCellMaterialRules.GrainDimensions((uint)amount, capacity);
                float expectedSide = size.x < 1 ? 1f : 1f - size.y;
                float area = 0f;
                var vertices = wallMesh.vertices;
                var tris = wallMesh.GetTriangles(CellularMaterialSurfaceMesh.Slot(MaterialId.Sand));
                for (int i = 0; i < tris.Length; i += 3)
                {
                    var a = vertices[tris[i]];
                    var cross = Vector3.Cross(vertices[tris[i + 1]] - a, vertices[tris[i + 2]] - a);
                    area += Mathf.Max(0f, cross.x) * .5f;
                }
                Assert.That(area, Is.EqualTo(expectedSide).Within(.0001f));
            }
            finally
            {
                Object.DestroyImmediate(hotMesh);
                Object.DestroyImmediate(wallMesh);
                if (coldMesh != null) Object.DestroyImmediate(coldMesh);
                Object.DestroyImmediate(host);
            }
        }

        private static void AssertSandBounds(Mesh mesh, Vector3 origin, int amount, uint capacity)
        {
            // 逐顶点计算实体包围盒：Mesh.bounds 为整页裁剪范围，不能拿来证明真实厚度。
            var vertices = mesh.vertices;
            Assert.That(vertices, Is.Not.Empty);
            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (var v in vertices) bounds.Encapsulate(v);
            Assert.That(bounds.center.x - origin.x, Is.EqualTo(.5f).Within(.0001f));
            Assert.That(bounds.center.z - origin.z, Is.EqualTo(.5f).Within(.0001f));
            Assert.That(bounds.min.y - origin.y, Is.EqualTo(0f).Within(.0001f));
            Vector3 expected = ElementCellMaterialRules.GrainDimensions((uint)amount, capacity);
            Assert.That(Vector3.Distance(bounds.size, expected), Is.LessThan(.0001f));
            Assert.That(bounds.size.x * bounds.size.y * bounds.size.z, Is.EqualTo(amount / (float)capacity).Within(.0001f));
        }

        [Test]
        public void PartialWaterHasSlopedShoreAndMatchesAcrossHotColdPageBoundary()
        {
            const uint capacity = 255, amount = 185;
            const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var host = new GameObject("Water shore across pages");
            host.SetActive(false);
            var hot = new Mesh();
            Mesh restingMesh = null;
            try
            {
                var world = host.AddComponent<ElementWorldRuntime>();
                var renderer = host.AddComponent<CellularMaterialRenderer>();
                typeof(CellularMaterialRenderer).GetField("_world", fields).SetValue(renderer, world);
                var samples = (Dictionary<Vector3Int, CellularMaterialRenderer.Sample>)typeof(CellularMaterialRenderer)
                    .GetField("_samples", fields).GetValue(renderer);
                var left = new Vector3Int(CellularMaterialRenderer.PageSize - 1, 0, 0);
                var right = left + Vector3Int.right;
                samples[left] = new CellularMaterialRenderer.Sample { Material = MaterialId.Water, Quantity = amount };
                var cold = new CellularRestingLiquidStore(null, capacity, 32);
                typeof(CellularRestingLiquidStore).GetMethod("SetReplicaQuantity", fields)
                    .Invoke(cold, new object[] { right, MaterialId.Water, amount });
                typeof(ElementWorldRuntime).GetField("<RestingLiquids>k__BackingField", fields).SetValue(world, cold);
                new CellularMaterialSurfaceMesh().Build(hot, new ElementChunkKey(0,0,0), new List<Vector3Int> { left }, renderer, capacity);
                var resting = new CellularRestingLiquidSurface();
                resting.Refresh(cold, renderer);
                var regions = (IList)typeof(CellularRestingLiquidSurface).GetField("_regions", fields).GetValue(resting);
                int index = cold.FindRegionIndex(right);
                var region = regions[index];
                typeof(CellularRestingLiquidSurface).GetMethod("Build", fields).Invoke(resting, new[] { region, renderer });
                restingMesh = (Mesh)region.GetType().GetField("Mesh", fields).GetValue(region);
                var coldOrigin = (Vector3)cold.GetRegion(index).Cells.min;
                var hotVertices = hot.vertices;
                var coldVertices = restingMesh.vertices;
                float max = amount / (float)capacity;
                for (int i = 0; i <= 2; i++)
                {
                    var point = new Vector3(right.x, 0, i * .5f);
                    float a = ColumnHeight(hotVertices, point), b = ColumnHeight(coldVertices, point - coldOrigin);
                    Assert.That(a, Is.EqualTo(b).Within(.0001f), "相邻格在同一条页边上必须使用相同水位。");
                    if (i == 1) Assert.That(a, Is.EqualTo(max).Within(.0001f));
                    else Assert.That(a, Is.LessThan(max * .2f));
                }
                Assert.That(ColumnHeight(hotVertices, left), Is.LessThan(0), "真正露出的外凸直角应收圆。");
                Assert.That(System.Array.Exists(hot.normals, n => n.y > .05f && n.y < .95f), Is.True);
                foreach (var p in hotVertices) Assert.That(p.y, Is.LessThanOrEqualTo(max + .0001f));
                Assert.That(cold.GetTotalQuantity(MaterialId.Water) + samples[left].Quantity, Is.EqualTo(2ul * amount));
            }
            finally
            {
                Object.DestroyImmediate(hot);
                if (restingMesh != null) Object.DestroyImmediate(restingMesh);
                Object.DestroyImmediate(host);
            }
        }

        private static float ColumnHeight(Vector3[] vertices, Vector3 position)
        {
            float height = -1;
            foreach (var point in vertices)
                if (Mathf.Abs(point.x - position.x) < .0001f && Mathf.Abs(point.z - position.z) < .0001f)
                    height = Mathf.Max(height, point.y - position.y);
            return height;
        }

        [TestCase("Assets/_Project/Scenes/P7_DemoRun.unity")]
        [TestCase("Assets/_Project/Scenes/P8_BossField.unity")]
        public void SceneHasItsOwnPhaseOwnersAndReadOnlyPresentation(string path)
        {
            Scene scene = SceneManager.GetSceneByPath(path); bool opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                int count = 0;
                foreach (var root in scene.GetRootGameObjects()) foreach (var world in root.GetComponentsInChildren<ElementWorldRuntime>(true))
                {
                    count++; var s = new SerializedObject(world);
                    var solids = s.FindProperty("_fixedSolidRuntime").objectReferenceValue as FixedSolidWorldRuntime;
                    var gases = s.FindProperty("_gasRuntime").objectReferenceValue as GasWorldRuntime;
                    Assert.That(solids, Is.Not.Null); Assert.That(gases, Is.Not.Null);
                    Assert.That(solids.gameObject.scene, Is.EqualTo(scene)); Assert.That(gases.gameObject.scene, Is.EqualTo(scene));
                    var solidProfile=new SerializedObject(solids).FindProperty("_profile").objectReferenceValue as FixedSolidWorldProfile;
                    Assert.That(solidProfile, Is.Not.Null);
                    bool residueCollision=false;
                    for(int i=0;i<solidProfile.MaterialCount;i++)
                    {
                        var material=solidProfile.GetMaterial(i);
                        if(material.Material==MaterialId.DriedSlime) residueCollision=material.BlocksMovement && material.UsesPartialVolume;
                    }
                    Assert.That(residueCollision,Is.True,"场景实际 Profile 必须提供固化残留 Collider，并保留部分占据语义。");
                    var gasProfile=new SerializedObject(gases).FindProperty("_profile").objectReferenceValue as GasWorldProfile;
                    Assert.That(gasProfile,Is.Not.Null);
                    Assert.That(gasProfile.CellCapacityGmu,Is.LessThanOrEqualTo(solidProfile.CellCapacityGmu/2),"同质量气体应比液体/冰占更多空间，不能用高浓度容量让小气团产出大水滩。");
                    Assert.That(solidProfile.MaximumCellsPerTick,Is.GreaterThanOrEqualTo(16),"单颗普通固体球要在首 Tick 生成。");
                    var conversion=s.FindProperty("_conversionProfile").objectReferenceValue as MaterialConversionProfile;
                    Assert.That(conversion,Is.Not.Null); Assert.That(conversion.IsValid,Is.True);
                    var condensation=conversion.GetRule(1);
                    Assert.That(condensation.Source,Is.EqualTo(MaterialId.Steam));
                    Assert.That(condensation.InputFractionPerSecond,Is.GreaterThan(0));
                    Assert.That(condensation.MinimumInputGmu,Is.GreaterThan(1u));
                    var solidRenderer = world.GetComponent<FixedSolidSurfaceRenderer>(); var gasRenderer = world.GetComponent<GasFieldVolumeRenderer>();
                    Assert.That(solidRenderer, Is.Not.Null); Assert.That(gasRenderer, Is.Not.Null);
                    Assert.That(new SerializedObject(solidRenderer).FindProperty("_world").objectReferenceValue, Is.EqualTo(world));
                    Assert.That(new SerializedObject(gasRenderer).FindProperty("_world").objectReferenceValue, Is.EqualTo(world));
                    // 组件存在不代表可显示；缺少 Material 会让 Gameplay 正确而 Renderer 自行禁用。
                    var solidMaterial=new SerializedObject(solidRenderer).FindProperty("_material").objectReferenceValue as Material;
                    var gasMaterial=new SerializedObject(gasRenderer).FindProperty("_material").objectReferenceValue as Material;
                    Assert.That(solidMaterial,Is.Not.Null);Assert.That(gasMaterial,Is.Not.Null);
                    Assert.That(solidMaterial.shader.name,Is.EqualTo("Game/Materials/FixedSolid"));
                    Assert.That(gasMaterial.shader.name,Is.EqualTo("Game/Materials/RealGasVolume"));
                    var profile = s.FindProperty("_profile").objectReferenceValue as ElementWorldProfile;
                    var liquid=s.FindProperty("_gridLiquidRuntime").objectReferenceValue as LiquidWorldRuntime;
                    Assert.That(liquid,Is.Not.Null);
                    var liquidProfile=new SerializedObject(liquid).FindProperty("_profile").objectReferenceValue as LiquidWorldProfile;
                    var volume=liquidProfile.CreateVolumeSettings(profile.CellSize);
                    double ratio=solidProfile.CellCapacityGmu*(double)(volume.ResolutionScale*volume.ResolutionScale*volume.ResolutionScale)/volume.CapacityGmu;
                    Assert.That(ratio,Is.EqualTo(1).Within(.01),"生产 Water/Ice 的 GMU 体积映射不能在相变时显著缩水。");
                    var catalog = profile.MaterialCatalog.CreateSnapshot(); var decision = WaterSimulationCompatibilityPolicy.Resolve(WaterSimulationMode.GridLiquid, true);
                    var routes = MaterialSimulationRouteSnapshot.Create(catalog, profile.MaterialSimulationRouting, in decision);
                    Assert.That(routes.TryResolve(MaterialId.Ice, out var backend) && backend == MaterialSimulationBackendKind.FixedSolid, Is.True);
                    Assert.That(routes.TryResolve(MaterialId.ToxicVapor, out backend) && backend == MaterialSimulationBackendKind.GridGas, Is.True);
                    var exposure = world.GetComponent<ElementWorldExposureSystem>(); Assert.That(exposure, Is.Not.Null);
                    var projection = profile.MaterialStatusProjection.CreateSnapshot(catalog); bool toxic = false;
                    for (int i = 0; i < projection.Count; i++) toxic |= projection.Get(i).Material == MaterialId.ToxicVapor;
                    Assert.That(toxic, Is.True);
                }
                Assert.That(count, Is.EqualTo(1));
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
