using Game.Core;
using Game.Materials;
using UnityEditor;

namespace Game.EditorTools
{
    /// <summary>
    /// Task 18 的确定性 Authoring 入口。Asset 必须由 Unity 创建以获得合法 GUID，
    /// 因此这里不手写 .meta/YAML；重复执行只校正字段，不会复制 Catalog 或 Definition。
    /// </summary>
    [InitializeOnLoad]
    internal static class MaterialCatalogSetup
    {
        private const string RootFolder = "Assets/_Project/ScriptableObjects/Materials";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset";
        private const string SessionKey = "Game.Materials.Task18.SetupOnce";

        static MaterialCatalogSetup()
        {
            if (!SessionState.GetBool(SessionKey, false))
            {
                SessionState.SetBool(SessionKey, true);
                EditorApplication.delayCall += EnsureInitialCatalog;
            }
        }

        private static void EnsureInitialCatalog()
        {
            // 自动入口只负责首次缺失资产。重启Editor不能用历史Task覆盖已有三相材料配置。
            if (AssetDatabase.LoadAssetAtPath<MaterialCatalog>(CatalogPath) == null) Setup();
        }

        [MenuItem("Tools/Material World/Setup Catalog")]
        private static void Setup()
        {
            EnsureFolder("Assets/_Project/ScriptableObjects", "Materials");

            MaterialDefinition water = CreateOrUpdateDefinition(
                "Assets/_Project/ScriptableObjects/Materials/Material_Water.asset",
                MaterialId.Water,
                MaterialBehaviorKind.Liquid);
            MaterialDefinition fire = CreateOrUpdateDefinition(
                "Assets/_Project/ScriptableObjects/Materials/Material_Fire.asset",
                MaterialId.Fire,
                MaterialBehaviorKind.ReactiveField);
            MaterialDefinition poison = CreateOrUpdateDefinition(
                "Assets/_Project/ScriptableObjects/Materials/Material_Poison.asset",
                MaterialId.Poison,
                MaterialBehaviorKind.Liquid);
            MaterialDefinition sticky = CreateOrUpdateDefinition(
                "Assets/_Project/ScriptableObjects/Materials/Material_Sticky.asset",
                MaterialId.Sticky,
                MaterialBehaviorKind.Liquid);

            MaterialCatalog catalog = AssetDatabase.LoadAssetAtPath<MaterialCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = UnityEngine.ScriptableObject.CreateInstance<MaterialCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            EnsureCatalogEntries(catalog,new[]{water,fire,poison,sticky});

            EditorUtility.SetDirty(catalog);
            // 只保存本工具的明确目标，避免保存用户正在编辑的无关SO/材质。
            AssetDatabase.SaveAssetIfDirty(water);AssetDatabase.SaveAssetIfDirty(fire);
            AssetDatabase.SaveAssetIfDirty(poison);AssetDatabase.SaveAssetIfDirty(sticky);AssetDatabase.SaveAssetIfDirty(catalog);
            AssetDatabase.Refresh();
            GameLog.Info("Task 18 Material Catalog assets are ready.", "Editor");
        }

        internal static void EnsureCatalogEntries(MaterialCatalog catalog,MaterialDefinition[] required)
        {
            var serialized=new SerializedObject(catalog);var definitions=serialized.FindProperty("_definitions");
            foreach(var entry in required)
            {
                bool found=false;
                for(int i=0;i<definitions.arraySize;i++)
                {
                    var existing=definitions.GetArrayElementAtIndex(i).objectReferenceValue as MaterialDefinition;
                    if(existing!=null && existing.Id==entry.Id){found=true;break;}
                }
                // MaterialId是持久化身份；保留已有身份的用户配置及后续扩展，重复安装仅补缺。
                if(!found)definitions.GetArrayElementAtIndex(definitions.arraySize++).objectReferenceValue=entry;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static MaterialDefinition CreateOrUpdateDefinition(
            string path,
            MaterialId id,
            MaterialBehaviorKind behavior)
        {
            MaterialDefinition definition = AssetDatabase.LoadAssetAtPath<MaterialDefinition>(path);
            if (definition == null)
            {
                definition = UnityEngine.ScriptableObject.CreateInstance<MaterialDefinition>();
                AssetDatabase.CreateAsset(definition, path);
            }

            var serialized = new SerializedObject(definition);
            serialized.FindProperty("_id").intValue = (byte)id;
            serialized.FindProperty("_behavior").intValue = (byte)behavior;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
            return definition;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, child);
        }
    }
}
