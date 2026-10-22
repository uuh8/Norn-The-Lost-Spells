using System;
using System.IO;
using System.Text;
using Game.Core;
using Game.ElementField;
using Game.Run;
using Game.Character;
using Game.Rendering;
using System.Collections.Generic;
using Object = UnityEngine.Object;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>显式请求审查家与生产世界；先留差异证据，再接线，正式Scene保存权属于用户。</summary>
    [InitializeOnLoad] internal static class HomeElementAlchemySetup
    {
        internal const string Home="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity";
        internal const string Review="Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Home_ElementsReview.unity";
        internal const string Evidence="TestResults/SinglePlayer/M6-T23-HomeElements";
        private const string P7="Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string Request="Temp/SinglePlayer/home-elements.request";
        static HomeElementAlchemySetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try{if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败");if(command=="audit")Audit();else if(command=="prepare")Prepare();else if(command=="apply-current")ApplyCurrent();else throw new ArgumentException(command);}
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"HomeElements");}
        }
        private static void Audit()
        {
            var text=new StringBuilder();
            foreach(string path in new[]{Home,P7,"Assets/_Project/Scenes/P8_BossField.unity"})
            {
                var scene=SceneManager.GetSceneByPath(path);bool opened=!scene.isLoaded;
                if(opened)scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
                try
                {
                    text.AppendLine("SCENE "+path+" Dirty="+scene.isDirty);
                    foreach(var root in scene.GetRootGameObjects())foreach(var world in root.GetComponentsInChildren<ElementWorldRuntime>(true))
                    {
                        text.AppendLine("WORLD "+world.name+" pos="+world.transform.position);
                        foreach(var component in world.GetComponents<MonoBehaviour>())
                        {
                            if(component==null)continue;text.AppendLine(component.GetType().Name+" enabled="+component.enabled);
                            var data=new SerializedObject(component);var property=data.GetIterator();bool enter=true;
                            while(property.NextVisible(enter))
                            {
                                enter=false;if(property.name=="m_Script")continue;
                                if(property.propertyType==SerializedPropertyType.ObjectReference)
                                {var value=property.objectReferenceValue;text.AppendLine("  "+property.name+"="+(value==null?"null":value.name+" @"+AssetDatabase.GetAssetPath(value)));}
                            }
                        }
                    }
                }
                finally{if(opened)EditorSceneManager.CloseScene(scene,true);}
            }
            foreach(string guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/Items/Containers"}))
            {var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));text.AppendLine("ITEM "+item.ContentId+" Use="+item.UseKind+" Model="+item.WorldModel);}
            Write("baseline-audit.txt",text.ToString());
        }
        private static void Write(string name,string value){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,DateTime.UtcNow.ToString("O")+"\n"+value);}
        private static T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var value=root.GetComponentInChildren<T>(true);if(value!=null)return value;}return null;}
        private static void Clean()
        {for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("先保存用户Scene；工具不保存/丢弃正式Scene。");}
        private static void Bind(Object owner,string field,Object value)
        {var data=new SerializedObject(owner);var property=data.FindProperty(field);if(property==null)throw new InvalidOperationException(owner.name+" missing "+field);property.objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void Prepare()
        {
            Clean();SetupItems();var original=SceneManager.GetActiveScene();
            if(SceneManager.GetSceneByPath(Review).isLoaded)throw new InvalidOperationException("Review已打开，拒绝覆盖。");
            if(File.Exists(Review)){File.Copy(Home,Review,true);AssetDatabase.ImportAsset(Review,ImportAssetOptions.ForceUpdate);}
            else if(!AssetDatabase.CopyAsset(Home,Review))throw new IOException("复制Home到隔离Review失败。");
            var review=EditorSceneManager.OpenScene(Review,OpenSceneMode.Additive);
            try{SceneManager.SetActiveScene(review);ApplyWorld(review,false);EditorSceneManager.SaveScene(review);}
            finally{EditorSceneManager.CloseScene(review,true);SceneManager.SetActiveScene(original);}
            Write("prepared.txt","Review="+"Assets/_Project/Scenes/Tests/Hub/SinglePlayer_Home_ElementsReview.unity\\nFormalHomeSaved=false；固体资源已配置，正式Home尚未改动。");
        }
        private static void ApplyCurrent()
        {
            Clean();var home=SceneManager.GetSceneByPath(Home);
            if(!home.isLoaded)home=EditorSceneManager.OpenScene(Home,OpenSceneMode.Single);
            if(SceneManager.sceneCount!=1)throw new InvalidOperationException("仅允许单一正式Home，防止跨Scene Undo。");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("家共用生产元素世界");
            try{ApplyWorld(home,true);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(home);Write("formal-applied.txt","Undo=true FormalSceneSaved=false Dirty="+home.isDirty);}
            catch{Undo.RevertAllDownToGroup(group);throw;}
        }
        private static void ApplyWorld(Scene target,bool undo)
        {
            var source=SceneManager.GetSceneByPath(P7);bool opened=!source.isLoaded;
            if(opened)source=EditorSceneManager.OpenScene(P7,OpenSceneMode.Additive);
            try
            {
                var old=Find<ElementWorldRuntime>(target);var original=Find<ElementWorldRuntime>(source);
                if(original==null||old==null)throw new InvalidOperationException("缺少基准/目标世界");
                // Instantiate保留生产组件的内部引用；跨Scene的玩家/Camera另行绑定，绝不复制副本敌人和奖励。
                var go=Object.Instantiate(original.gameObject);go.name="__ElementWorld";
                SceneManager.MoveGameObjectToScene(go,target);go.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);go.transform.localScale=Vector3.one;
                if(undo)Undo.RegisterCreatedObjectUndo(go,"生产世界");
                var world=go.GetComponent<ElementWorldRuntime>();var player=Find<WizardController>(target).transform;
                foreach(var component in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if(component==null)continue;var data=new SerializedObject(component);var iterator=data.GetIterator();
                    while(iterator.Next(true))if(iterator.propertyType==SerializedPropertyType.ObjectReference)
                    {
                        var value=iterator.objectReferenceValue;
                        var referenced=value is Component c?c.gameObject:value as GameObject;
                        if(referenced!=null&&referenced.scene==source)iterator.objectReferenceValue=null;
                    }
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                Bind(world,"_interestPoint",player);
                Bind(world,"_gpuPbfFluidRuntime",null);Bind(world,"_fluidGameplayOccupancy",null);Bind(world,"_fluidChunkStreamingRuntime",null);
                CopyPresentation<LiquidSurfaceRenderer>(source,target,world,go,undo);
                CopyPresentation<StableLiquidSurfaceRenderer>(source,target,world,go,undo);
                CopyPresentation<GpuFireParcelRenderer>(source,target,world,go,undo);
                if(Find<ReactionBurstVisualController>(target)==null)CopyPresentation<ReactionBurstVisualController>(source,target,world,go,undo);
                var fire=go.GetComponent<GpuFireParcelRenderer>();if(fire!=null)Bind(fire,"_interestCenter",player);
                var run=Find<SinglePlayerRunCoordinator>(target);if(undo)Undo.RecordObject(run,"世界引用");Bind(run,"_hubWorld",go);
                var session=Find<SinglePlayerInventorySession>(target);if(undo)Undo.RecordObject(session,"家实验权限");
                var sd=new SerializedObject(session);sd.FindProperty("_allowWorldExperiments").boolValue=true;sd.ApplyModifiedPropertiesWithoutUndo();
                if(undo)Undo.DestroyObjectImmediate(old.gameObject);else Object.DestroyImmediate(old.gameObject);
                ValidateWorld(target,world);
                Write(undo?"formal-world.txt":"review-world.txt","ProductionProfile shared from P7; all phase renderers enabled; WorldSceneKey unchanged; fresh scene world, persistent item repository.");
            }
            finally{if(opened)EditorSceneManager.CloseScene(source,true);}
        }
        private static void ValidateWorld(Scene scene,ElementWorldRuntime world)
        {
            var text=new StringBuilder();
            foreach(string type in new[]{"LiquidWorldRuntime","FixedSolidWorldRuntime","GasWorldRuntime","GridLiquidFieldSurfaceRenderer","FixedSolidSurfaceRenderer","GasFieldVolumeRenderer","ElementWorldExposureSystem","MaterialInventoryTransferAdapter","ContainerDepositAdapter"})
            {
                var component=world.GetComponent(type) as Behaviour;
                if(component==null||!component.enabled)throw new InvalidOperationException("家生产组件缺失/禁用："+type);
                text.AppendLine(type+" enabled");
            }
            int liquid=world.GetComponentsInChildren<LiquidSurfaceRenderer>(true).Length,stable=world.GetComponentsInChildren<StableLiquidSurfaceRenderer>(true).Length;
            if(liquid!=3||stable!=3)throw new InvalidOperationException("三材料表面缺失/重复："+liquid+"/"+stable);
            foreach(var component in world.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if(component==null)continue;var property=new SerializedObject(component).GetIterator();
                while(property.Next(true))if(property.propertyType==SerializedPropertyType.ObjectReference)
                {
                    var value=property.objectReferenceValue;var go=value is Component c?c.gameObject:value as GameObject;
                    if(go!=null&&go.scene.IsValid()&&go.scene!=scene)throw new InvalidOperationException("外部Scene引用："+component.GetType().Name+"."+property.propertyPath);
                }
            }
            text.AppendLine("Liquid surfaces="+liquid+" Stable surfaces="+stable+" ExternalSceneReferences=0");
            Write(scene.path==Home?"formal-validation.txt":"review-validation.txt",text.ToString());
        }
        private static void CopyPresentation<T>(Scene source,Scene target,ElementWorldRuntime world,GameObject parent,bool undo)where T:MonoBehaviour
        {
            foreach(var root in source.GetRootGameObjects())foreach(var original in root.GetComponentsInChildren<T>(true))
            {
                // 已随世界根复制的组件无需再生一份。
                if(original.transform.IsChildOf(Find<ElementWorldRuntime>(source).transform))continue;
                var child=new GameObject(original.name+"_"+typeof(T).Name);SceneManager.MoveGameObjectToScene(child,target);child.transform.SetParent(parent.transform,false);
                if(undo)Undo.RegisterCreatedObjectUndo(child,"材料表现");
                var component=child.AddComponent<T>();EditorUtility.CopySerialized(original,component);
                var data=new SerializedObject(component);var p=data.FindProperty("_world")??data.FindProperty("_worldRuntime");if(p!=null)p.objectReferenceValue=world;
                p=data.FindProperty("_interestCenter");if(p!=null)p.objectReferenceValue=Find<WizardController>(target).transform;
                p=data.FindProperty("_visualRoot");if(p!=null)p.objectReferenceValue=parent.transform;
                data.ApplyModifiedPropertiesWithoutUndo();component.enabled=original.enabled;
            }
        }
        private static void SetupItems()
        {
            const string folder="Assets/_Project/ScriptableObjects/Items/Containers/";
            const string models="Assets/_Project/Prefabs/Items/ElementSolids";
            Directory.CreateDirectory(models);AssetDatabase.Refresh();
            var ice=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_Ice.asset");
            var dried=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_Dried.asset");
            if(dried==null){dried=Object.Instantiate(ice);dried.name="dried";AssetDatabase.CreateAsset(dried,"Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_Dried.asset");}
            foreach(var item in new[]{ice,dried})
            {
                bool isIce=item==ice;string name=isIce?"Ice":"DriedSlime";string materialPath="Assets/_Project/Prefabs/Items/ElementSolids/"+name+".mat";
                var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if(material==null){material=new Material(Shader.Find("Universal Render Pipeline/Lit"));material.SetColor("_BaseColor",isIce?new Color(.25f,.7f,1):new Color(.72f,.42f,.1f));AssetDatabase.CreateAsset(material,materialPath);}
                string prefabPath="Assets/_Project/Prefabs/Items/ElementSolids/"+name+".prefab";var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if(prefab==null){var model=GameObject.CreatePrimitive(PrimitiveType.Cube);model.name=name;model.transform.localScale=Vector3.one*.3f;model.GetComponent<Renderer>().sharedMaterial=material;Object.DestroyImmediate(model.GetComponent<Collider>());prefab=PrefabUtility.SaveAsPrefabAsset(model,prefabPath);Object.DestroyImmediate(model);}
                var data=new SerializedObject(item);data.FindProperty("_contentId").stringValue=isIce?"containertest.ice":"containertest.dried";
                data.FindProperty("_displayName").stringValue=isIce?"冰":"固态粘液";data.FindProperty("_materialKey").intValue=isIce?5:6;
                data.FindProperty("_useKind").intValue=2;data.FindProperty("_worldModel").objectReferenceValue=prefab;
                data.FindProperty("_icon").objectReferenceValue=AssetDatabase.LoadAssetAtPath<Sprite>("Assets/_Project/Art/UI/Funtion_UI2/"+(isIce?"Ice":"Dry mucus")+".png");
                data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(item);
            }
            foreach(string guid in AssetDatabase.FindAssets("t:ItemCatalog",new[]{"Assets/_Project/ScriptableObjects/"}))
            {
                var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>(AssetDatabase.GUIDToAssetPath(guid));var data=new SerializedObject(catalog);var list=data.FindProperty("_definitions");bool hasIce=false,hasDried=false;
                for(int i=0;i<list.arraySize;i++){hasIce|=list.GetArrayElementAtIndex(i).objectReferenceValue==ice;hasDried|=list.GetArrayElementAtIndex(i).objectReferenceValue==dried;}
                if(hasIce&&!hasDried){int end=list.arraySize++;list.GetArrayElementAtIndex(end).objectReferenceValue=dried;data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(catalog);}
            }
            AssetDatabase.SaveAssets();
        }
    }
}
