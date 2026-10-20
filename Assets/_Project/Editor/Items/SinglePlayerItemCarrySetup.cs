using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Game.Character;
using Game.Core;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace Game.EditorTools
{
    /// <summary>用已保存作者UI生成独立练习Prefab；正式Scene只接引用、单组Undo、不自动保存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerItemCarrySetup
    {
        private const string Formal="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ItemCarryReview.unity";
        private const string Prefab="Assets/_Project/Prefabs/SinglePlayer/PracticeInventoryWorkspace.prefab";
        private const string Request="Temp/SinglePlayer/item-carry.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T3-ItemCarry";
        static SinglePlayerItemCarrySetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("当前编译失败。");
                if(command=="prepare")Prepare();else if(command=="apply-current")ApplyCurrent();
                else if(command=="inspect")Write("inspection.txt",SceneManager.GetActiveScene().path+"\nDirty="+SceneManager.GetActiveScene().isDirty);
                else throw new ArgumentException(command);
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"ItemCarrySetup");}
        }
        private static T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}throw new InvalidOperationException("缺少"+typeof(T).Name);}
        private static SinglePlayerInventoryController Backpack(Scene scene)
        {foreach(var root in scene.GetRootGameObjects())foreach(var ui in root.GetComponentsInChildren<SinglePlayerInventoryController>(true))if(ui.name=="InventoryCanvas_背包")return ui;throw new InvalidOperationException("缺少原背包Canvas");}
        private static void Set(Object target,string field,Object value)
        {var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void SetBool(Object target,string field,bool value)
        {var data=new SerializedObject(target);data.FindProperty(field).boolValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        internal static GameObject Generate(Scene scene)
        {
            var source=Find<SinglePlayerInventorySession>(scene);var sourceNearby=Find<NearbyPickupController>(scene);var sourceBackpack=Backpack(scene);
            var root=new GameObject("PracticeInventoryWorkspace");root.SetActive(false);
            try
            {
                var session=root.AddComponent<SinglePlayerInventorySession>();EditorUtility.CopySerialized(source,session);Set(session,"_practiceWorkspacePrefab",null);
                var nearby=root.AddComponent<NearbyPickupController>();EditorUtility.CopySerialized(sourceNearby,nearby);Set(nearby,"_session",session);Set(nearby,"_player",null);
                var input=root.AddComponent<NearbyPickupInput>();Set(input,"_pickups",nearby);
                var workspace=root.AddComponent<PracticeInventoryWorkspace>();Set(workspace,"_session",session);Set(workspace,"_nearby",nearby);
                var bag=Object.Instantiate(sourceBackpack.gameObject,root.transform,false);bag.name="PracticeInventoryCanvas_背包";bag.SetActive(true);
                var inventory=bag.GetComponent<SinglePlayerInventoryController>();SetBool(inventory,"_openOnStart",false);
                var panel=(GameObject)new SerializedObject(inventory).FindProperty("_panelRoot").objectReferenceValue;panel.SetActive(false);
                var listSource=Find<NearbyPickupListView>(scene);
                var listCanvas=listSource.GetComponentInParent<Canvas>(true);
                if(listCanvas==null)throw new InvalidOperationException("拾取列表Canvas缺失");
                var list=Object.Instantiate(listCanvas.gameObject,root.transform,false);list.name="PracticePickupListCanvas";list.SetActive(true);
                var sourceHud=Find<HeldItemHud>(scene);
                var hud=Object.Instantiate(sourceHud.GetComponentInParent<Canvas>(true).gameObject,root.transform,false);
                hud.name="PracticeHeldHUD";hud.SetActive(true);
                SinglePlayerUnifiedItemsSetup.ConfigureHud(hud, false);
                foreach(var node in hud.GetComponentsInChildren<Transform>(true))if(node.name=="Crosshair")node.name="ScreenCenterDot";
                GameObject containerHud=null;
                foreach(var sceneRoot in scene.GetRootGameObjects())foreach(var candidate in sceneRoot.GetComponentsInChildren<Canvas>(true))
                    if(candidate.name=="ContainerHUDCanvas_容器状态")
                    {containerHud=Object.Instantiate(candidate.gameObject,root.transform,false);containerHud.name="PracticeContainerHUD";containerHud.SetActive(true);}
                if(containerHud==null)throw new InvalidOperationException("缺少据点容器HUD模板");
                var eventSource=Find<EventSystem>(scene);var events=Object.Instantiate(eventSource.gameObject,root.transform,false);events.name="PracticeEventSystem";events.SetActive(false);
                var remap=new Dictionary<Object,Object>{{source,session},{sourceNearby,nearby},{sourceBackpack,inventory}};
                // Instantiate只自动重绑复制子树内部引用。跨Canvas/Systems引用逐项重绑，外部Scene对象一律清空。
                foreach(var component in root.GetComponentsInChildren<Component>(true))
                {
                    if(component==null)throw new InvalidOperationException("模板含Missing Script");
                    var data=new SerializedObject(component);var property=data.GetIterator();bool enter=true;
                    while(property.Next(enter))
                    {
                        enter=true;if(property.propertyType!=SerializedPropertyType.ObjectReference || property.name=="m_Script")continue;
                        var reference=property.objectReferenceValue;if(reference==null || EditorUtility.IsPersistent(reference))continue;
                        if(remap.TryGetValue(reference,out var mapped))property.objectReferenceValue=mapped;
                        else
                        {
                            var target=reference is Component c?c.transform:reference is GameObject go?go.transform:null;
                            if(target!=null && !target.IsChildOf(root.transform))property.objectReferenceValue=null;
                        }
                    }
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                var binding=root.AddComponent<PracticeInventoryWorkspaceBinding>();Set(binding,"_workspace",workspace);Set(binding,"_inventory",inventory);Set(binding,"_eventSystem",events);
                Set(binding,"_heldHud",hud.GetComponentInChildren<HeldItemHud>(true));
                Set(binding,"_containerHud",containerHud.GetComponentInChildren<ContainerCompositionView>(true));Set(binding,"_containerCanvas",containerHud.GetComponent<Canvas>());
                var character=root.AddComponent<PracticePlayerInventoryBinding>();Set(character,"_workspace",workspace);
                var adapter=Find<OwnedWandCastAdapter>(scene);var adapterData=new SerializedObject(adapter);
                foreach(var field in new[]{"_select1","_select2","_select3","_materialWaitingMaterial"})Set(character,field,adapterData.FindProperty(field).objectReferenceValue);
                Set(character,"_containerDefinition",new SerializedObject(Find<ContainerUseController>(scene)).FindProperty("_definition").objectReferenceValue);
                var collectionData = new SerializedObject(Find<MaterialCollectionController>(scene));
                var characterData = new SerializedObject(character); characterData.FindProperty("_solidContentId").stringValue = collectionData.FindProperty("_solidContentId").stringValue; characterData.ApplyModifiedPropertiesWithoutUndo();
                var reward=root.AddComponent<PracticeSpellRewardAdapter>();Set(reward,"_workspace",workspace);
                if(inventory.Session!=session)throw new InvalidOperationException("背包Session未重绑");
                Directory.CreateDirectory(Path.GetDirectoryName(Prefab));var asset=PrefabUtility.SaveAsPrefabAsset(root,Prefab);
                if(asset==null)throw new InvalidOperationException("生成Prefab失败");return asset;
            }
            finally{Object.DestroyImmediate(root);}
        }
        private static void Prepare()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("当前Scene未保存；不代保存或丢弃。");
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("Review已打开");
            string hash=Hash();var original=SceneManager.GetActiveScene();
            if(!File.Exists(Review)){if(!AssetDatabase.CopyAsset(Formal,Review))throw new IOException("复制Review失败");}
            else{File.Copy(Formal,Review,true);AssetDatabase.ImportAsset(Review,ImportAssetOptions.ForceUpdate);}
            var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);Set(Find<SinglePlayerInventorySession>(scene),"_practiceWorkspacePrefab",Generate(scene));
                EditorSceneManager.SaveScene(scene,Review);SessionState.SetString("Norn.HubAlignment.ReviewPath",Review);Write("prepared.txt","Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Hub_ItemCarryReview.unity\\n"+Prefab);
            }
            finally
            {EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);Write("review-isolation.txt","FormalHashUnchanged="+(Hash()==hash)+"\nFormalDirty="+original.isDirty);}
        }
        [MenuItem("Tools/Norn/物品/应用据点携带入口（Undo，不保存）")]
        private static void ApplyCurrent()
        {
            var scene=SceneManager.GetActiveScene();if(scene.path!=Formal)throw new InvalidOperationException("必须在正式据点操作");
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);if(prefab==null)throw new InvalidOperationException("请先准备Review和Prefab");
            string hash=Hash();Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("练习地图物品携带接线");
            var session=Find<SinglePlayerInventorySession>(scene);Undo.RecordObject(session,"练习地图物品携带接线");Set(session,"_practiceWorkspacePrefab",prefab);
            Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(scene);
            Write("current-applied.txt","FormalHashUnchanged="+(Hash()==hash)+"\n仅增量Prefab引用；单组Undo，未保存场景。");
        }
        private static string Hash(){using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Formal)));}
        private static void Write(string name,string text){Directory.CreateDirectory(Evidence);File.WriteAllText(Path.Combine(Evidence,name),text);}
    }
}
