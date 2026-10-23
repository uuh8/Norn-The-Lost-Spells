using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Game.Multiplayer;
using Game.UI;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.Project.Editor
{
    /// <summary>显式建立两端共同运动世界；坐标/几何持久化为Prefab，运行时不硬编码生成地图。</summary>
    public static class OnlineMovementTools
    {
        public const string ProfilePath = "Assets/_Project/Resources/Online/MovementProfile.asset";
        public const string WorldPath = "Assets/_Project/Prefabs/Online/MovementWorld.prefab";
        public const string NavigationPath = "Assets/_Project/ScriptableObjects/World/Online/MovementNavigation.asset";
        private const string PlayerPath = "Assets/_Project/Art/Prefabs/WizardPlayer.prefab";
        private const string EnemyPath = "Assets/_Project/Art/Prefabs/BaseEnemy/SwordEnemy_Base.prefab";
        [MenuItem("Tools/Norn/Online/Setup Online Movement World")]
        public static void Setup()
        {
            OnlineSceneSetup.RequireCleanEditor();
            Folder("Assets/_Project/ScriptableObjects/World/Online"); Folder("Assets/_Project/Art/Materials/Online");
            var original = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(WorldPath) == null) CreateWorld();
                var profile = AssetDatabase.LoadAssetAtPath<OnlineMovementProfile>(ProfilePath);
                if (profile == null) { profile = ScriptableObject.CreateInstance<OnlineMovementProfile>(); AssetDatabase.CreateAsset(profile, ProfilePath); }
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("_playerPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath);
                serialized.FindProperty("_enemyPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(EnemyPath);
                serialized.FindProperty("_worldPrefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(WorldPath);
                var spawns = serialized.FindProperty("_playerSpawns"); spawns.arraySize = 4;
                for (int i = 0; i < 4; i++) spawns.GetArrayElementAtIndex(i).vector3Value = new Vector3(-4.5f + i * 3, .1f, -8);
                serialized.FindProperty("_enemySpawn").vector3Value = new Vector3(0, .1f, 10);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                serialized.Update(); serialized.FindProperty("_simulationHash").longValue = unchecked((long)Hash(profile));
                serialized.ApplyModifiedPropertiesWithoutUndo(); EditorUtility.SetDirty(profile); AssetDatabase.SaveAssets();
                UpgradeScene(RoomLifecycleTools.ScenePath, false); UpgradeScene(RoomLifecycleTools.PortalScenePath, true);
            }
            finally { EditorSceneManager.RestoreSceneManagerSetup(original); }
            Verify();
        }
        public static void Verify()
        {
            var profile = AssetDatabase.LoadAssetAtPath<OnlineMovementProfile>(ProfilePath);
            if (profile == null || profile.PlayerPrefab == null || profile.EnemyPrefab == null || profile.WorldPrefab == null ||
                profile.SimulationHash == 0 || profile.SimulationHash != Hash(profile) || AssetDatabase.LoadAssetAtPath<NavMeshData>(NavigationPath) == null)
                throw new InvalidOperationException("运动世界/配置Hash不一致，请先Setup Online Movement World再构建两端。");
            VerifySceneWorld(RoomLifecycleTools.ScenePath);
            VerifySceneWorld(RoomLifecycleTools.PortalScenePath);
        }
        private static void VerifySceneWorld(string path)
        {
            // Additive只读检查，既不保存场景，也不改全局Build列表；复用已打开场景避免双重加载。
            var scene = SceneManager.GetSceneByPath(path);
            bool openedHere = !scene.IsValid() || !scene.isLoaded;
            if (openedHere) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                int count = 0;
                OnlineWorldBootstrap world = null;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var candidate in root.GetComponentsInChildren<OnlineWorldBootstrap>(true))
                    { count++; world = candidate; }
                if (count != 1 || world == null || !world.enabled || !world.gameObject.activeInHierarchy)
                    throw new InvalidOperationException(path + "必须包含唯一且启用的OnlineWorldBootstrap，拒绝构建无Tick或重复Tick的副本。");
            }
            finally { if (openedHere) EditorSceneManager.CloseScene(scene, true); }
        }
        private static ulong Hash(OnlineMovementProfile profile)
        {
            // 显式列出影响模拟的配置，避免把Hash字段自身再纳入Hash造成每次重算都变化。
            var text = new StringBuilder("NornMovementV1|30|4|128|players-ignore-each-other|");
            text.Append(AssetDatabase.GetAssetDependencyHash(PlayerPath)).Append('|')
                .Append(AssetDatabase.GetAssetDependencyHash(EnemyPath)).Append('|').Append(AssetDatabase.GetAssetDependencyHash(WorldPath));
            for (int i = 0; i < 4; i++) Append(text, profile.PlayerSpawn(i));
            Append(text, profile.EnemySpawn);
            text.Append('|').Append(profile.SnapshotInterval.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            using var sha = SHA256.Create(); var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            ulong hash = BitConverter.ToUInt64(bytes, 0); return hash == 0 ? 1 : hash;
        }
        private static void Append(StringBuilder text, Vector3 v)
        { var culture = System.Globalization.CultureInfo.InvariantCulture; text.Append('|').Append(v.x.ToString("R", culture)).Append(',').Append(v.y.ToString("R", culture)).Append(',').Append(v.z.ToString("R", culture)); }
        private static void CreateWorld()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            var root = new GameObject("MovementWorld"); SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var ground = Material("MovementGround", new Color(.23f, .34f, .27f));
                var wall = Material("MovementObstacle", new Color(.33f, .36f, .42f));
                var slope = Material("MovementRamp", new Color(.55f, .38f, .18f));
                Box(root.transform, "Floor", new Vector3(0, -.25f, 0), new Vector3(32, .5f, 32), Quaternion.identity, ground);
                Box(root.transform, "OcclusionAndNavigationWall", new Vector3(0, 1, 2), new Vector3(6, 2, .8f), Quaternion.identity, wall);
                Box(root.transform, "Walkable30DegreeRamp", new Vector3(-9, 1.9f, 4), new Vector3(5, .4f, 8), Quaternion.Euler(-30, 0, 0), slope);
                Box(root.transform, "Steep55DegreeRamp", new Vector3(9, 2.2f, 4), new Vector3(4, .4f, 6), Quaternion.Euler(-55, 0, 0), slope);
                for (int i = 0; i < 5; i++) Box(root.transform, "Step" + i, new Vector3(-10, .1f * (i + 1), -8 + i * .8f), new Vector3(3, .2f * (i + 1), .8f), Quaternion.identity, wall);
                Box(root.transform, "NorthWall", new Vector3(0, 1, 16), new Vector3(33, 2, 1), Quaternion.identity, wall);
                Box(root.transform, "SouthWall", new Vector3(0, 1, -16), new Vector3(33, 2, 1), Quaternion.identity, wall);
                Box(root.transform, "EastWall", new Vector3(16, 1, 0), new Vector3(1, 2, 32), Quaternion.identity, wall);
                Box(root.transform, "WestWall", new Vector3(-16, 1, 0), new Vector3(1, 2, 32), Quaternion.identity, wall);
                var light = new GameObject("OnlineSun").AddComponent<Light>(); light.transform.SetParent(root.transform, false);
                light.type = LightType.Directional; light.intensity = 1.4f; light.transform.rotation = Quaternion.Euler(45, -30, 0);
                var surface = root.AddComponent<NavMeshSurface>(); surface.collectObjects = CollectObjects.Children;
                surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders; surface.BuildNavMesh();
                if (surface.navMeshData == null) throw new InvalidOperationException("在线NavMesh烘焙失败。");
                AssetDatabase.CreateAsset(surface.navMeshData, NavigationPath);
                PrefabUtility.SaveAsPrefabAsset(root, WorldPath); AssetDatabase.SaveAssets();
            }
            finally { UnityEngine.Object.DestroyImmediate(root); EditorSceneManager.CloseScene(scene, true); }
        }
        private static void UpgradeScene(string path, bool client)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            bool hasWorld = false, hasHint = false;
            foreach (var root in scene.GetRootGameObjects()) { hasWorld |= root.GetComponent<OnlineWorldBootstrap>() != null; hasHint |= root.GetComponentInChildren<OnlineMovementHint>(true) != null; }
            if (!hasWorld) { var root = new GameObject("OnlineMovementWorld"); SceneManager.MoveGameObjectToScene(root, scene); root.AddComponent<OnlineWorldBootstrap>(); }
            if (client && !hasHint)
            {
                var canvasRoot = new GameObject("OnlineMovementControls", typeof(Canvas), typeof(CanvasScaler)); SceneManager.MoveGameObjectToScene(canvasRoot, scene);
                canvasRoot.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = canvasRoot.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1280, 800);
                var text = new GameObject("Controls", typeof(RectTransform), typeof(Text), typeof(OnlineMovementHint)); text.transform.SetParent(canvasRoot.transform, false);
                var rect = text.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(.5f, 1); rect.anchoredPosition = new Vector2(0, -25); rect.sizeDelta = new Vector2(1100, 45);
                var label = text.GetComponent<Text>(); label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); label.fontSize = 20; label.alignment = TextAnchor.MiddleCenter; label.raycastTarget = false;
            }
            EditorSceneManager.SaveScene(scene); EditorSceneManager.CloseScene(scene, true);
        }
        private static Material Material(string name, Color color)
        {
            string path = "Assets/_Project/Art/Materials/Online/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path); if (material != null) return material;
            material = new Material(Shader.Find("Universal Render Pipeline/Lit")); material.SetColor("_BaseColor", color); AssetDatabase.CreateAsset(material, path); return material;
        }
        private static void Box(Transform parent, string name, Vector3 position, Vector3 size, Quaternion rotation, Material material)
        { var box = GameObject.CreatePrimitive(PrimitiveType.Cube); box.name = name; box.transform.SetParent(parent, false); box.transform.SetPositionAndRotation(position, rotation); box.transform.localScale = size; box.GetComponent<Renderer>().sharedMaterial = material; }
        private static void Folder(string path)
        { if (AssetDatabase.IsValidFolder(path)) return; Folder(Path.GetDirectoryName(path).Replace('\\', '/')); AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\', '/'), Path.GetFileName(path)); }
    }
}
