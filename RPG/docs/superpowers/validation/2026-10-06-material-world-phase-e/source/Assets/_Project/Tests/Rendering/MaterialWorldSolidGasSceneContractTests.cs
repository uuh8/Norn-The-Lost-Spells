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
                    Assert.That(new SerializedObject(gases).FindProperty("_profile").objectReferenceValue, Is.Not.Null);
                    var conversion=s.FindProperty("_conversionProfile").objectReferenceValue as MaterialConversionProfile;
                    Assert.That(conversion,Is.Not.Null); Assert.That(conversion.IsValid,Is.True);
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
