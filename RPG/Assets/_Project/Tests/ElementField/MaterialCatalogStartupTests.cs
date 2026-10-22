using System;
using System.Reflection;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed class MaterialCatalogStartupTests
    {
        private const string Root="Assets/_Project/ScriptableObjects/Materials/";
        [Test]public void SavedCatalogContainsAllThreePhaseMaterialIdentities()
        {
            var catalog=AssetDatabase.LoadAssetAtPath<MaterialCatalog>("Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset");
            var snapshot=catalog.CreateSnapshot();
            for(int id=1;id<=8;id++)Assert.That(snapshot.TryGet((MaterialId)id,out _),Is.True,"持久化材料目录缺少身份 "+id);
        }
        [Test]public void LegacyInstallerPreservesExtensionsAndIsIdempotent()
        {
            // 安装器仅作用于内存克隆，不写生产SO；两次旧Task安装也不能截断现有冰/气体引用。
            var clone=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<MaterialCatalog>("Assets/_Project/ScriptableObjects/Materials/MaterialCatalog_Default.asset"));
            try
            {
                var before=new SerializedObject(clone).FindProperty("_definitions");var references=new UnityEngine.Object[before.arraySize];
                for(int i=0;i<references.Length;i++)references[i]=before.GetArrayElementAtIndex(i).objectReferenceValue;
                Type installer=null;foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {installer=assembly.GetType("Game.EditorTools.MaterialCatalogSetup");if(installer!=null)break;}
                var required=new[]{AssetDatabase.LoadAssetAtPath<MaterialDefinition>("Assets/_Project/ScriptableObjects/Materials/Material_Water.asset"),AssetDatabase.LoadAssetAtPath<MaterialDefinition>("Assets/_Project/ScriptableObjects/Materials/Material_Fire.asset")};
                var method=installer.GetMethod("EnsureCatalogEntries",BindingFlags.NonPublic|BindingFlags.Static);
                method.Invoke(null,new object[]{clone,required});method.Invoke(null,new object[]{clone,required});
                var after=new SerializedObject(clone).FindProperty("_definitions");Assert.That(after.arraySize,Is.EqualTo(references.Length));
                for(int i=0;i<references.Length;i++)Assert.That(after.GetArrayElementAtIndex(i).objectReferenceValue,Is.SameAs(references[i]));
            }
            finally{UnityEngine.Object.DestroyImmediate(clone);}
        }
    }
}
