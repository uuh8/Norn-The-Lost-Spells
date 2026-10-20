using System;
using System.IO;
using System.Text;
using Game.Core;
using Game.Run;
using Game.Skills;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Game.EditorTools
{
    /// <summary>本轮资源迁移独立于场景保存：修复实际 Sprite 子资产引用，只增补容量层，不覆盖作者布局。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerIconFillSetup
    {
        private const string Request="Temp/SinglePlayer/icon-fill.request";
        private const string Evidence="TestResults/SinglePlayer/M6-T17-IconFill";
        private const string Art="Assets/_Project/Art/UI/Funtion_UI2/";
        static SinglePlayerIconFillSetup(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，不使用旧 Assembly。");
                if(command=="inspect")Inspect();
                else if(command=="assets")Assets();
                else if(command=="apply-current")
                {
                    var scene=SceneManager.GetActiveScene();
                    if(scene.path!="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity")throw new InvalidOperationException("仅正式据点。");
                    Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("F提示共用行与对齐迁移");
                    ApplyPrompts(scene,true);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);
                    EditorSceneManager.MarkSceneDirty(scene);Write("current-applied.txt","Undo迁移完成；正式Scene未保存。请自行保存后生效。");
                }
                else throw new ArgumentException(command);
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"IconFill");}
        }
        private static Sprite SpriteAt(string path)
        {
            Sprite result=null;
            foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(path))if(asset is Sprite sprite)
            {if(result!=null)throw new InvalidOperationException("多 Sprite 无明确候选："+path);result=sprite;}
            if(result==null)
            {
                var importer=AssetImporter.GetAtPath(path) as TextureImporter;
                // 新 PNG 被设成 Multiple 却没有切任何 Sprite；仅这种空配置改成 Single。
                // 已有裁剪的一律保留，避免修复冰球时把用户定义的 Sprite Rect 清掉。
#pragma warning disable CS0618
                if(importer!=null && importer.spriteImportMode==SpriteImportMode.Multiple && importer.spritesheet.Length==0)
#pragma warning restore CS0618
                {importer.spriteImportMode=SpriteImportMode.Single;importer.SaveAndReimport();return SpriteAt(path);}
                throw new InvalidOperationException("没有 Sprite："+path);
            }
            return result;
        }
        private static void Assets()
        {
            var report=new StringBuilder();
            const string materialPath="Assets/_Project/Resources/ContainerIconFill.mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if(material==null)
            {
                var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Art/Shaders/ContainerCapacityFill.shader");
                if(shader==null)throw new InvalidOperationException("容量 Shader 尚未导入。");
                material=new Material(shader);AssetDatabase.CreateAsset(material,materialPath);
            }
            var shop=AssetDatabase.LoadAssetAtPath<ShopDefinition>("Assets/_Project/ScriptableObjects/World/Hub/Trading/Shop.asset");
            string[] names={"","water","Fire","poison","Mucus","Ice","Dry mucus","Steam","poison gas"};
            foreach(var offer in shop.Offers)if(offer.IsMaterial)
            {offer.Icon=SpriteAt(Art+names[offer.MaterialKey]+".png");report.AppendLine(offer.DisplayName+"="+AssetDatabase.GetAssetPath(offer.Icon)+"/"+offer.Icon.name);}
            EditorUtility.SetDirty(shop);AssetDatabase.SaveAssetIfDirty(shop);
            foreach(var guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/"}))
            {
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if(item.Kind!=ItemKind.Material)continue;
                var data=new SerializedObject(item);int key=data.FindProperty("_materialKey").intValue;
                if(key<1 || key>=names.Length)throw new InvalidOperationException("材料物品键无图标映射："+item.name);
                data.FindProperty("_icon").objectReferenceValue=SpriteAt(Art+names[key]+".png");data.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssetIfDirty(item);report.AppendLine("MaterialItem="+item.ContentId);
            }
            // 不修改 TextureImporter，保留用户裁剪；只修复空引用，已配置的有效图标保持原样。
            foreach(var guid in AssetDatabase.FindAssets("t:SpellDefinition",new[]{"Assets/_Project/ScriptableObjects"}))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);var spell=AssetDatabase.LoadAssetAtPath<SpellDefinition>(path);
                if(spell.Icon!=null)continue;
                string iconPath="Assets/_Project/Art/UI/Skill_Icon/"+spell.name+".png";
                if(!File.Exists(iconPath))continue;
                spell.Icon=SpriteAt(iconPath);EditorUtility.SetDirty(spell);AssetDatabase.SaveAssetIfDirty(spell);report.AppendLine("Repair="+path+" -> "+spell.Icon.name);
            }
            foreach(var guid in AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/_Project/Prefabs/UI/InventorySlots"}))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);var root=PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Component component=root.GetComponent<InventoryItemView>();if(component==null)component=root.GetComponent<ToolbeltSlotView>();
                    if(component==null)continue;
                    var icon=new SerializedObject(component).FindProperty("_icon").objectReferenceValue as Image;
                    if(icon==null)continue;
                    ContainerIconFill.Ensure(icon).material=material;PrefabUtility.SaveAsPrefabAsset(root,path);report.AppendLine("FillPrefab="+path);
                }
                finally{PrefabUtility.UnloadPrefabContents(root);}
            }
            Write("asset-wiring.txt",report.ToString());
        }
        internal static void ApplyPrompts(Scene scene,bool undo)
        {
            foreach(var root in scene.GetRootGameObjects())foreach(var view in root.GetComponentsInChildren<NearbyPickupListView>(true))
            {
                var data=new SerializedObject(view);var visual=(GameObject)data.FindProperty("_visual").objectReferenceValue;
                if(visual!=null)for(int i=visual.transform.childCount-1;i>=0;i--)
                {var child=visual.transform.GetChild(i);if(child.name.StartsWith("Station entry")){if(undo)Undo.DestroyObjectImmediate(child.gameObject);else UnityEngine.Object.DestroyImmediate(child.gameObject);}}
                var labels=data.FindProperty("_labels");
                for(int i=0;i<labels.arraySize;i++)
                {
                    var label=(Text)labels.GetArrayElementAtIndex(i).objectReferenceValue;if(label==null)continue;
                    if(undo)Undo.RecordObject(label,"F提示垂直居中");
                    label.alignment=TextAnchor.MiddleLeft;
                    // UGUI 默认按 Font 的行高/descender 对齐；中文 fallback 字体的可见字形会偏下。
                    // 按实际 glyph geometry 居中，无需偷偷修改作者的 RectTransform。
                    label.alignByGeometry=true;
                }
            }
        }
        private static void Inspect()
        {
            var report=new StringBuilder();var scene=SceneManager.GetActiveScene();report.AppendLine("Scene="+scene.path+" Dirty="+scene.isDirty);
            foreach(var root in scene.GetRootGameObjects())foreach(var view in root.GetComponentsInChildren<NearbyPickupListView>(true))
            {
                var data=new SerializedObject(view);var labels=data.FindProperty("_labels");
                report.AppendLine("Panel="+view.Panel.rect+" Anchors="+view.Panel.anchorMin+" Offset="+view.Panel.anchoredPosition);
                for(int i=0;i<labels.arraySize;i++)
                {var label=(Text)labels.GetArrayElementAtIndex(i).objectReferenceValue;report.AppendLine(label.name+" align="+label.alignment+" font="+label.font.name+" size="+label.fontSize+" rect="+label.rectTransform.rect+" offsets="+label.rectTransform.offsetMin+"/"+label.rectTransform.offsetMax+" pos="+label.rectTransform.anchoredPosition);}
            }
            foreach(var root in scene.GetRootGameObjects())foreach(var shop in root.GetComponentsInChildren<SinglePlayerShopSession>(true))
            {var data=new SerializedObject(shop);report.AppendLine("ShopSession="+shop.name+" Active="+shop.gameObject.activeInHierarchy+" Inventory="+data.FindProperty("_inventory").objectReferenceValue+" Definition="+data.FindProperty("_definition").objectReferenceValue);}
            Write("inspection.txt",report.ToString());
        }
        private static void Write(string name,string text)
        {Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,DateTime.UtcNow.ToString("O")+"\n"+text);}
    }
}
