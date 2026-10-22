using System;
using System.IO;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using Game.Skills;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// 显式安装 Authoring 资产与场景接线；不是每次启动都运行的隐式 Migration。
    /// 已加载场景仅 MarkDirty，保护用户未保存修改；自己打开的场景才保存和关闭。
    /// </summary>
    [InitializeOnLoad]
    internal static class MaterialWorldSolidGasSetup
    {
        private const string Request = "Temp/ElementPerformance/install-solid-gas.request";
        private const string Result = "Temp/ElementPerformance/solid-gas-setup.txt";
        private const string MaterialsPath = "Assets/_Project/ScriptableObjects/Materials/";
        private const string PhasesPath = "Assets/_Project/ScriptableObjects/ElementField/Phases/";
        private const string PrefabsPath = "Assets/_Project/Art/Elemental/Prefabs/";
        private static double _nextPoll;
        static MaterialWorldSolidGasSetup() => EditorApplication.update += Poll;
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + 1;
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode || SessionState.GetBool("ElementPerformance.GateRunning", false)) return;
            if(EditorUtility.scriptCompilationFailed) { File.WriteAllText(Result,"BLOCKED: current source compilation failed.\n"); return; }
            File.Delete(Request);
            try { Install(); }
            catch (Exception exception) { File.WriteAllText(Result, "FAILED\n" + exception); GameLog.Error(exception.ToString(), "MaterialSetup"); }
        }
        [MenuItem("Tools/Element World/Install Fixed Solids and Real Gases")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("请先退出 Play Mode。");
            if(EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("请先解决当前编译错误；不使用旧 Assembly 安装资产。");
            Directory.CreateDirectory(PhasesPath);
            string backup = "Backups/MaterialWorldSolidGas/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            Directory.CreateDirectory(backup);
            var catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(MaterialsPath + "MaterialCatalog_Default.asset");
            Backup(catalog, backup);
            var ids = new[] { MaterialId.Ice, MaterialId.DriedSlime, MaterialId.Steam, MaterialId.ToxicVapor };
            var definitions = new MaterialDefinition[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                definitions[i] = GetOrCreate<MaterialDefinition>(MaterialsPath + "Material_" + ids[i] + ".asset");
                var serialized = new SerializedObject(definitions[i]);
                serialized.FindProperty("_id").intValue = (int)ids[i];
                serialized.FindProperty("_behavior").intValue = i < 2 ? (int)MaterialBehaviorKind.Solid : (int)MaterialBehaviorKind.Gas;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                AppendObject(catalog, "_definitions", definitions[i]);
            }
            var solidProfile = GetOrCreate<FixedSolidWorldProfile>(PhasesPath + "FixedSolidWorld_Default.asset");
            var gasProfile = GetOrCreate<GasWorldProfile>(PhasesPath + "GasWorld_Default.asset");
            var conversionProfile = GetOrCreate<MaterialConversionProfile>(PhasesPath + "MaterialConversions_Default.asset");
            Backup(solidProfile,backup);
            // 冷气体保留在 CPU Store，不占活动格预算。起始池采用计划的 256 Chunk，
            // 不把常驻容量误设为小显示池，导致跨房间后新材料长期排队。
            SetInt(gasProfile, "_maximumChunks", 256);
            // 生产 Gas 的同质量体积大于水；保持 Installer 与共享 Profile 一致。
            SetInt(gasProfile, "_cellCapacityGmu", 32);
            SetInt(solidProfile, "_maximumCellsPerTick", 32);
            SetInt(solidProfile, "_maximumColliderChunksPerTick", 8);
            var solidMaterial = GetMaterial("Assets/_Project/Art/Elemental/Materials/M_FixedSolidWorld.mat", "Game/Materials/FixedSolid");
            var gasMaterial = GetMaterial("Assets/_Project/Art/Elemental/Materials/M_RealGasWorld.mat", "Game/Materials/RealGasVolume");
            var projection = AssetDatabase.LoadAssetAtPath<MaterialStatusProjectionProfile>(MaterialsPath + "MaterialStatusProjection_Default.asset");
            Backup(projection, backup); AddGasProjection(projection);
            string report = DateTime.UtcNow.ToString("O") + "\nBackup=" + backup + "\n";
            int sharedSolidCapacity=0;
            Scene active = SceneManager.GetActiveScene();
            foreach (string path in new[] { "Assets/_Project/Scenes/P7_DemoRun.unity", "Assets/_Project/Scenes/P8_BossField.unity" })
            {
                File.Copy(path, Path.Combine(backup, Path.GetFileName(path)), true);
                Scene scene = SceneManager.GetSceneByPath(path);
                bool opened = !scene.isLoaded;
                if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    int worlds = 0;
                    foreach (var root in scene.GetRootGameObjects()) foreach (var world in root.GetComponentsInChildren<ElementWorldRuntime>(true))
                    {
                        worlds++;
                        var serialized = new SerializedObject(world);
                        var profile = serialized.FindProperty("_profile").objectReferenceValue as ElementWorldProfile;
                        if (profile == null) throw new InvalidOperationException(path + " 缺少 ElementWorldProfile。");
                        var p = new SerializedObject(profile);
                        var routing = p.FindProperty("_materialSimulationRouting").objectReferenceValue as MaterialSimulationRoutingProfile;
                        Backup(routing, backup); AddRoutes(routing, ids);
                        var solid = world.GetComponent<FixedSolidWorldRuntime>() ?? world.gameObject.AddComponent<FixedSolidWorldRuntime>();
                        var gas = world.GetComponent<GasWorldRuntime>() ?? world.gameObject.AddComponent<GasWorldRuntime>();
                        SetReference(solid, "_profile", solidProfile); SetReference(gas, "_profile", gasProfile);
                        SetReference(world, "_fixedSolidRuntime", solid); SetReference(world, "_gasRuntime", gas);
                        SetReference(world,"_conversionProfile",conversionProfile);
                        // 水与冰使用相同 GMU 密度：粗液体格转换为细格实体时，避免“结冰突然缩水”。
                        var liquid=serialized.FindProperty("_gridLiquidRuntime").objectReferenceValue as LiquidWorldRuntime;
                        if(liquid==null) throw new InvalidOperationException(path+" 缺少 Grid Liquid Owner。");
                        var liquidProfile=new SerializedObject(liquid).FindProperty("_profile").objectReferenceValue as LiquidWorldProfile;
                        if(liquidProfile==null) throw new InvalidOperationException(path+" 缺少 Grid Liquid Profile。");
                        var volume=liquidProfile.CreateVolumeSettings(profile.CellSize);
                        int fineCapacity=Mathf.Max(1,Mathf.RoundToInt(volume.CapacityGmu/(float)(volume.ResolutionScale*volume.ResolutionScale*volume.ResolutionScale)));
                        if(sharedSolidCapacity!=0 && sharedSolidCapacity!=fineCapacity)
                            throw new InvalidOperationException("P7/P8 的 GMU 密度不同，需要各自 Solid Profile，不能覆盖共享容量。");
                        sharedSolidCapacity=fineCapacity;
                        SetInt(solidProfile,"_cellCapacityGmu",fineCapacity);
                        report+=path+" Density: liquid="+volume.CapacityGmu+", scale="+volume.ResolutionScale+", solid="+fineCapacity+" GMU/cell\n";
                        var solids = world.GetComponent<FixedSolidSurfaceRenderer>() ?? world.gameObject.AddComponent<FixedSolidSurfaceRenderer>();
                        var gases = world.GetComponent<GasFieldVolumeRenderer>() ?? world.gameObject.AddComponent<GasFieldVolumeRenderer>();
                        SetReference(solids, "_world", world); SetReference(solids, "_material", solidMaterial);
                        SetReference(gases, "_world", world); SetReference(gases, "_material", gasMaterial);
                    }
                    if (worlds != 1) throw new InvalidOperationException(path + " 必须有一个 World，实际=" + worlds);
                    EditorSceneManager.MarkSceneDirty(scene);
                    if (opened) { EditorSceneManager.SaveScene(scene); report += path + " SAVED\n"; }
                    else report += path + " CONFIGURED IN MEMORY; UNSAVED USER SCENE PRESERVED\n";
                }
                finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
            }
            if (active.isLoaded) SceneManager.SetActiveScene(active);
            var full = AssetDatabase.LoadAssetAtPath<SpellLibrary>("Assets/_Project/ScriptableObjects/SpellLibrary.asset");
            var starting = AssetDatabase.LoadAssetAtPath<SpellLibrary>("Assets/_Project/ScriptableObjects/P7/P7_StartingSpellLibrary.asset");
            Backup(full, backup); Backup(starting, backup);
            var source = AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Spell_Emit_WaterField.asset");
            string[] names = { "冰块球", "固化粘液球", "蒸汽球", "毒气球" };
            string[] descriptions = { "在命中处生成固定冰块，阻挡移动与投射物。", "在命中处生成可阻挡移动的固化粘液。", "在命中处释放上升扩散的蒸汽，遇冷逐渐凝结成水。", "在命中处释放会扩散的毒气；吸入后积累中毒。" };
            for (int i = 0; i < ids.Length; i++)
            {
                // Steam 与水球同级质量；Gas 格浓度容量不同，不能据此多发十倍水系材料。
                GameObject prefab = MakeProjectile(ids[i], i < 2 ? 1024 : ids[i] == MaterialId.Steam ? 1600 : 16384, i == 1 ? .4f : .75f);
                string path = "Assets/_Project/ScriptableObjects/Spell/Spell_Emit_" + ids[i] + "Field.asset";
                var spell = AssetDatabase.LoadAssetAtPath<SpellDefinition>(path);
                if (spell == null) { spell = UnityEngine.Object.Instantiate(source); AssetDatabase.CreateAsset(spell, path); }
                spell.DisplayName = names[i]; spell.Description = descriptions[i]; spell.ProjectilePrefab = prefab;
                spell.Element = i == 1 ? SpellElement.Sticky : i == 3 ? SpellElement.Poison : SpellElement.Water;
                spell.BaseDamage = 0; EditorUtility.SetDirty(spell);
                AppendObject(full, "Available", spell); AppendObject(starting, "Available", spell);
            }
            AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            File.WriteAllText(Result, report + "Four canonical materials, phase profiles, shaders, projectiles and palette entries installed.\n");
            GameLog.Info("固定固体与真实气体资产已接入；详见 " + Result, "MaterialSetup");
        }
        private static GameObject MakeProjectile(MaterialId material, int amount, float radius)
        {
            string path = PrefabsPath + "PF_Element" + material + "Projectile.prefab";
            if (!File.Exists(path)) AssetDatabase.CopyAsset(PrefabsPath + "PF_ElementWaterProjectile.prefab", path);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                root.name = "PF_Element" + material + "Projectile";
                var deposit = root.GetComponent<ElementDepositOnImpact>(); var s = new SerializedObject(deposit);
                s.FindProperty("_materialKind").intValue = (int)material; s.FindProperty("_totalAmount").intValue = amount;
                s.FindProperty("_radius").floatValue = radius; s.FindProperty("_fluidInitialSpeed").floatValue = 0;
                s.ApplyModifiedPropertiesWithoutUndo();
                var status = root.GetComponent<ProjectileStatusOnImpact>();
                if (material == MaterialId.ToxicVapor) { SetInt(status, "_status", (int)StatusKind.Poisoned); }
                else if (status != null) UnityEngine.Object.DestroyImmediate(status);
                var impact = root.GetComponent<ProjectileImpactVfx>();
                if (impact != null) UnityEngine.Object.DestroyImmediate(impact); // 复用飞行模型，不误播水花。
                // 飞行颜色属于独立 Material Asset，不能修改水球共享 Material。
                var renderer = root.GetComponent<MeshRenderer>();
                string materialPath = "Assets/_Project/Art/Elemental/Materials/M_" + material + "Projectile.mat";
                var flightMaterial = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (flightMaterial == null) { flightMaterial = UnityEngine.Object.Instantiate(renderer.sharedMaterial); AssetDatabase.CreateAsset(flightMaterial, materialPath); }
                Color color = material == MaterialId.ToxicVapor ? new Color(.35f, .8f, .12f)
                    : material == MaterialId.DriedSlime ? new Color(.75f, .55f, .2f) : material == MaterialId.Steam ? new Color(.85f, .9f, .95f) : new Color(.2f, .7f, 1f);
                if (flightMaterial.HasProperty("_BaseColor")) flightMaterial.SetColor("_BaseColor", color);
                if (flightMaterial.HasProperty("_Color")) flightMaterial.SetColor("_Color", color);
                EditorUtility.SetDirty(flightMaterial); renderer.sharedMaterial = flightMaterial;
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        private static void AddRoutes(MaterialSimulationRoutingProfile routing, MaterialId[] ids)
        {
            if (routing == null) throw new InvalidOperationException("World 缺少 Routing Profile。");
            var s = new SerializedObject(routing); var list = s.FindProperty("_routes");
            for (int i = 0; i < ids.Length; i++)
            {
                SerializedProperty route = null;
                for (int n = 0; n < list.arraySize; n++)
                    if (list.GetArrayElementAtIndex(n).FindPropertyRelative("_material").intValue == (int)ids[i]) route = list.GetArrayElementAtIndex(n);
                if (route == null) { int n = list.arraySize++; route = list.GetArrayElementAtIndex(n); }
                route.FindPropertyRelative("_material").intValue = (int)ids[i];
                route.FindPropertyRelative("_backend").intValue = i < 2 ? (int)MaterialSimulationBackendKind.FixedSolid : (int)MaterialSimulationBackendKind.GridGas;
            }
            s.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void AddGasProjection(MaterialStatusProjectionProfile projection)
        {
            var s = new SerializedObject(projection); var list = s.FindProperty("_bindings");
            for (int n = 0; n < list.arraySize; n++) if (list.GetArrayElementAtIndex(n).FindPropertyRelative("Material").intValue == (int)MaterialId.ToxicVapor) return;
            var binding = list.GetArrayElementAtIndex(list.arraySize++);
            binding.FindPropertyRelative("Material").intValue = (int)MaterialId.ToxicVapor;
            binding.FindPropertyRelative("Status").intValue = (int)StatusKind.Poisoned;
            binding.FindPropertyRelative("MaximumApplyPerExposureTick").floatValue = 10;
            s.ApplyModifiedPropertiesWithoutUndo();
        }
        private static T GetOrCreate<T>(string path) where T : ScriptableObject
        { var asset = AssetDatabase.LoadAssetAtPath<T>(path); if (asset == null) { asset = ScriptableObject.CreateInstance<T>(); AssetDatabase.CreateAsset(asset, path); } return asset; }
        private static Material GetMaterial(string path, string shaderName)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { var shader = Shader.Find(shaderName); if (shader == null) throw new InvalidOperationException(shaderName + " 尚未导入。"); material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            return material;
        }
        private static void AppendObject(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var s = new SerializedObject(target); var list = s.FindProperty(field);
            for (int i = 0; i < list.arraySize; i++) if (list.GetArrayElementAtIndex(i).objectReferenceValue == value) return;
            list.GetArrayElementAtIndex(list.arraySize++).objectReferenceValue = value; s.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void SetReference(UnityEngine.Object target, string field, UnityEngine.Object value)
        { var s = new SerializedObject(target); s.FindProperty(field).objectReferenceValue = value; s.ApplyModifiedPropertiesWithoutUndo(); }
        private static void SetInt(UnityEngine.Object target, string field, int value)
        { var s = new SerializedObject(target); s.FindProperty(field).intValue = value; s.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Backup(UnityEngine.Object asset, string directory)
        {
            if (asset == null) return;
            string path = AssetDatabase.GetAssetPath(asset); string target = Path.Combine(directory, Path.GetFileName(path));
            if (!File.Exists(target)) File.Copy(path, target);
        }
    }
}
