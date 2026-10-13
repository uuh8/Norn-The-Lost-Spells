using System;
using System.IO;
using System.Collections.Generic;
using Game.Character;
using Game.Run;
using Game.UI;
using Unity.Cinemachine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>Editor装配时读P7最终值；Runtime没有跨Scene查找/参数复制，也不复制P7关卡与库存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerHubP7Alignment
    {
        internal const string ReviewPath = "Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub_P7Review.unity";
        private const string Request = "Temp/SinglePlayer/align-hub-p7.request";
        private const string Evidence = "TestResults/SinglePlayer/M6-T1-P7Alignment";
        static SinglePlayerHubP7Alignment() { EditorApplication.update += Poll; }
        private static void Poll()
        {
            if (!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string command = File.ReadAllText(Request).Trim(); File.Delete(Request);
            try
            {
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("编译失败，拒绝使用旧工具。");
                if (command == "prepare") Prepare();
                else if (command == "repair-review") Prepare(true);
                else if (command == "inspect-sites") InspectSites();
                else if (command == "inspect-route") InspectRoute();
                else if (command == "apply-current") ApplyToCurrent();
                else if (command == "clear-review") SessionState.EraseString("Norn.HubAlignment.ReviewPath");
                else throw new InvalidOperationException("只接受prepare/clear-review。");
            }
            catch (Exception error)
            { Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/alignment-error.txt", error.ToString()); Game.Core.GameLog.Error(error.ToString(), "HubAuthoring"); }
        }
        private static void Prepare(bool repair = false)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("用户Scene未保存，未保存/丢弃。");
            if (File.Exists(ReviewPath) && !repair) throw new InvalidOperationException("已有审阅Scene，拒绝覆盖。");
            if (SceneManager.GetSceneByPath(ReviewPath).isLoaded) throw new InvalidOperationException("审阅Scene由用户打开，拒绝保存。");
            Directory.CreateDirectory(Evidence);
            if (!repair)
            {
                File.Copy(SinglePlayerHubSceneSetup.Path, Evidence + "/hub-before.unity.txt", false);
                if (!AssetDatabase.CopyAsset(SinglePlayerHubSceneSetup.Path, ReviewPath)) throw new IOException("审阅Scene复制失败。");
            }
            Scene original = SceneManager.GetActiveScene(); Scene scene = EditorSceneManager.OpenScene(ReviewPath, OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene); Apply(scene);
                if (!EditorSceneManager.SaveScene(scene, ReviewPath)) throw new IOException("审阅Scene保存失败。");
                File.WriteAllText(Evidence + "/prepared.json", JsonUtility.ToJson(new Report { Source = "Assets/_Project/Scenes/P7_DemoRun.unity", Review = ReviewPath, Original = original.path, OriginalDirty = original.isDirty }, true));
                SessionState.SetString("Norn.HubAlignment.ReviewPath", ReviewPath);
            }
            finally { EditorSceneManager.CloseScene(scene, true); if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original); }
        }
        internal static void Apply(Scene scene, bool undo = false)
        {
            // Preview Scene只读且不进入普通场景列表，P7不会被保存；避免碰到用户正在编辑的同名Scene。
            Scene reference = EditorSceneManager.OpenPreviewScene("Assets/_Project/Scenes/P7_DemoRun.unity");
            try
            {
                var source = Find<WizardController>(reference); var player = Find<WizardController>(scene);
                CopySettings(source, player, true, undo);
                CopySettings(source.GetComponent<CharacterController>(), player.GetComponent<CharacterController>(), false, undo);
                CopySettings(source.GetComponent<GroundChecker>(), player.GetComponent<GroundChecker>(), true, undo);
                CopySettings(source.GetComponentInChildren<Animator>(true), player.GetComponentInChildren<Animator>(true), false, undo);
                var sourcePivot = source.transform.Find("CameraRoot"); var pivot = player.transform.Find("CameraRoot");
                if (undo) Undo.RecordObject(pivot, "统一P7据点操作");
                pivot.localPosition = sourcePivot.localPosition; pivot.localRotation = sourcePivot.localRotation; pivot.localScale = sourcePivot.localScale;
                var camera = Find<Camera>(scene); var sourceCamera = Find<Camera>(reference);
                CopySettings(sourceCamera, camera, false, undo); CopySettings(sourceCamera.GetComponent<CinemachineBrain>(), camera.GetComponent<CinemachineBrain>(), false, undo);
                var sourceRig = Find<CinemachineCamera>(reference); var rig = Find<CinemachineCamera>(scene);
                // Body算法决定是否避障及位置延迟；只复制FOV/偏移会继续保留旧Follow语义。
                var oldComponents = rig.GetComponents<MonoBehaviour>();
                // 反序先移除依赖者，再移除RequireComponent补出的依赖，支持重复修正同一审阅副本。
                for (int i=oldComponents.Length-1;i>=0;i--)
                {
                    var component=oldComponents[i];
                    if (component == rig || (component.GetType().Namespace != "Unity.Cinemachine" && component.GetType().Namespace != "Game.Rendering")) continue;
                    if(undo) Undo.DestroyObjectImmediate(component); else Object.DestroyImmediate(component);
                }
                CopySettings(sourceRig, rig, false, undo);
                foreach (var component in sourceRig.GetComponents<MonoBehaviour>())
                {
                    if (component == sourceRig || (component.GetType().Namespace != "Unity.Cinemachine" && component.GetType().Namespace != "Game.Rendering")) continue;
                    var copy = rig.GetComponent(component.GetType()); if (copy == null) copy = undo ? Undo.AddComponent(rig.gameObject,component.GetType()) : rig.gameObject.AddComponent(component.GetType());
                    CopySettings(component, copy, false, undo);
                }
                var target = rig.Target; target.TrackingTarget = pivot; target.LookAtTarget = null; rig.Target = target;
                if(undo) Undo.RecordObject(rig.transform,"统一P7据点操作");rig.transform.rotation = pivot.rotation;
                var shop = Find<ShopController>(scene); SetReference(shop, "_toggleAction", null, undo);
                SetReference(Find<SinglePlayerInventoryController>(scene), "_hubAccess", Find<HubInteractionController>(scene), undo);
                var shopData = new SerializedObject(shop); var open = (UnityEngine.UI.Button)shopData.FindProperty("_openButton").objectReferenceValue;
                if (open != null) { if(undo)Undo.RecordObject(open.gameObject,"统一据点入口");open.gameObject.SetActive(false); }
                var navigation = Find<HubNavigationView>(scene); SetReference(navigation, "_shopOpenButton", null, undo);
                foreach(var root in scene.GetRootGameObjects())foreach(var label in root.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                {
                    string text=label.text.Replace("I 整备","Tab 编程").Replace("I 编程","Tab 编程").Replace("I 背包","Tab 背包");
                    if(text==label.text)continue;if(undo)Undo.RecordObject(label,"统一按键提示");label.text=text;
                }
                AlignUserStations(scene,undo);
                // 局部对象引用不能指向P7 Preview，SpellCaster/OwnedWand等已有真实资产接线保持原状。
                if (rig.Target.TrackingTarget.gameObject.scene != scene) throw new InvalidOperationException("镜头跟随对象没有重绑到据点。");
            }
            finally { EditorSceneManager.ClosePreviewScene(reference); }
        }
        private static void CopySettings(Object source, Object target, bool userFieldsOnly = false, bool undo = false)
        {
            if (source == null || target == null) throw new InvalidOperationException("对齐组件缺失。");
            if(undo)Undo.RecordObject(target,"统一P7据点操作");
            var from = new SerializedObject(source); var to = new SerializedObject(target); var property = from.GetIterator();
            // 首次必须进入序列化根；后续只遍历顶层字段，Generic结构由Unity整体复制。
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                string name = property.propertyPath;
                if (userFieldsOnly ? !name.StartsWith("_", StringComparison.Ordinal) : name == "m_ObjectHideFlags" || name == "m_GameObject" || name == "m_Script" || name == "m_Name" || name == "m_EditorClassIdentifier" || name == "m_EditorHideFlags") continue;
                if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null && !EditorUtility.IsPersistent(property.objectReferenceValue)) continue;
                to.CopyFromSerializedProperty(property);
            }
            to.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void SetReference(Object target, string field, Object value, bool undo = false)
        { if(undo)Undo.RecordObject(target,"统一据点入口");var data = new SerializedObject(target); data.FindProperty(field).objectReferenceValue = value; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static T Find<T>(Scene scene) where T : Component
        { foreach (var root in scene.GetRootGameObjects()) { var value = root.GetComponentInChildren<T>(true); if (value != null) return value; } throw new InvalidOperationException(typeof(T).Name + "缺失。"); }
        [Serializable] private sealed class Report { public string Source, Review, Original; public bool OriginalDirty; }
        private static void AlignUserStations(Scene scene,bool undo=false)
        {
            var map=SinglePlayerHubHierarchySetup.FindNamed(scene,"Map_1")??throw new InvalidOperationException("Map_1缺失。");
            var houses = map.transform.Find("Houses");
            if (houses.Find("Building_1") == null || houses.Find("Building_3") == null) throw new InvalidOperationException("用户指定的商店/仓库建筑缺失。");
            var ground = map.transform.Find("Ground_Map_1").GetComponent<MeshCollider>(); var definition = Find<HubInteractionController>(scene).Definition;
            for (int i = 0; i < definition.Stations.Length; i++)
            {
                var station = definition.Stations[i]; Vector3 point;
                if (station.Kind == HubStationKind.Shop) point = new Vector3(-7,0,12); // Building_1南侧可站立的门前，避开墙/桶，不改场景碰撞掩盖卡点。
                else if (station.Kind == HubStationKind.Warehouse) point = new Vector3(-8,0,-4.5f); // Building_3北侧木棚入口，不能放在屋顶投影中央。
                else continue; // 家/待定区域、实验和远征配置按用户要求保持。
                if (!ground.Raycast(new Ray(point + Vector3.up*30,Vector3.down),out var hit,60)) throw new InvalidOperationException("站点没有实际地面。");
                station.Position = hit.point + Vector3.up*.15f; definition.Stations[i] = station;
                var marker=SinglePlayerHubHierarchySetup.FindNamed(scene,station.DisplayName+" · E");
                if(marker!=null){if(undo)Undo.RecordObject(marker.transform,"按用户地图设置站点");marker.transform.position=station.Position;}
            }
            EditorUtility.SetDirty(definition); AssetDatabase.SaveAssetIfDirty(definition);
        }
        private static void ApplyToCurrent()
        {
            var scene=SceneManager.GetActiveScene();
            if(scene.path!=SinglePlayerHubSceneSetup.Path || scene.isDirty)throw new InvalidOperationException("只对干净的当前正式据点应用；不替用户保存/丢弃。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("统一P7操作与据点站点");
            Apply(scene,true);Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
            // 仅改变当前Editor对象，保留磁盘原Scene，让用户自行保留；不会把SaveScene当作自动修复步骤。
            File.WriteAllText(Evidence+"/current-applied.json",JsonUtility.ToJson(new Report{Source="Assets/_Project/Scenes/P7_DemoRun.unity",Review=ReviewPath,Original=scene.path,OriginalDirty=scene.isDirty},true));
            SessionState.EraseString("Norn.HubAlignment.ReviewPath");
        }
        private static void InspectSites()
        {
            var report = new Sites();
            foreach (var item in SinglePlayerHubHierarchySetup.All(SceneManager.GetActiveScene())) if (item.name == "Map_1")
            {
                var root=item.gameObject;
                foreach (var node in root.GetComponentsInChildren<Transform>(true))
                    if (node.parent != null && node.parent.name == "Houses")
                    {
                        var renderers = node.GetComponentsInChildren<Renderer>(true); var bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds(node.position, Vector3.zero);
                        foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                        report.Items.Add(new Site { Name = AnimationUtility.CalculateTransformPath(node, root.transform), Position = node.position, Rotation = node.eulerAngles, Bounds = bounds });
                    }
            }
            Directory.CreateDirectory(Evidence); File.WriteAllText(Evidence + "/sites.json", JsonUtility.ToJson(report, true));
            Scene original = SceneManager.GetActiveScene(); Scene capture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            RenderTexture target = null; Texture2D pixels = null; RenderTexture previous = RenderTexture.active;
            try
            {
                SceneManager.SetActiveScene(capture); var camera = new GameObject("ReadOnly Map Top Review").AddComponent<Camera>();
                camera.transform.position = new Vector3(0,100,0); camera.transform.rotation = Quaternion.Euler(90,0,0); camera.orthographic = true; camera.orthographicSize = 50;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                target = new RenderTexture(1200,1200,24); camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                pixels = new Texture2D(1200,1200,TextureFormat.RGB24,false); pixels.ReadPixels(new Rect(0,0,1200,1200),0,0); pixels.Apply();
                File.WriteAllBytes(Evidence + "/map-top.png", pixels.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous; if (pixels != null) Object.DestroyImmediate(pixels); if (target != null) { target.Release(); Object.DestroyImmediate(target); }
                EditorSceneManager.CloseScene(capture,true); if (original.IsValid() && original.isLoaded) SceneManager.SetActiveScene(original);
            }
        }
        [Serializable] private sealed class Sites { public List<Site> Items = new List<Site>(); }
        [Serializable] private sealed class Site { public string Name; public Vector3 Position, Rotation; public Bounds Bounds; }
        private static void InspectRoute()
        {
            var points=new[]{new Vector3(-9,.15f,13.5f),new Vector3(-7,.15f,12),new Vector3(-8,.15f,-4.5f)};var report=new Route();
            Physics.SyncTransforms();
            foreach(var point in points)
            {
                var probe=new Probe{Position=point};
                foreach(var hit in Physics.OverlapCapsule(point+Vector3.up*.65f,point+Vector3.up*1.55f,.35f,~0,QueryTriggerInteraction.Ignore))
                    if(hit.gameObject.scene.path==SinglePlayerHubSceneSetup.Path)probe.Colliders.Add(AnimationUtility.CalculateTransformPath(hit.transform,null));
                report.Points.Add(probe);
            }
            Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/route-colliders.json",JsonUtility.ToJson(report,true));
        }
        [Serializable]private sealed class Route{public List<Probe> Points=new List<Probe>();}
        [Serializable]private sealed class Probe{public Vector3 Position;public List<string> Colliders=new List<string>();}
    }
}
