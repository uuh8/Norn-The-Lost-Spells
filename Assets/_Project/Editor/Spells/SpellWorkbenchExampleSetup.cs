using System;
using System.IO;
using Game.Core;
using Game.Skills;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>只创建独立测试资产和场景。正式Hub、背包、存档均不在本工具写入范围。</summary>
    [InitializeOnLoad]
    public static class SpellWorkbenchExampleSetup
    {
        public const string ScenePath="Assets/_Project/Scenes/Tests/Spells/SpellWorkbench.unity";
        private const string DataPath="Assets/_Project/Tests/Fixtures/Spells/Workbench";
        private const string Request="Temp/Spells/create-sandbox.request";
        static SpellWorkbenchExampleSetup() { EditorApplication.update+=Poll; }
        private static void Poll()
        {
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating)return;
            string command=File.ReadAllText(Request).Trim();
            File.Delete(Request);
            try { if(command=="repair-links")RepairMissingSampleLinks();else if(command=="icons")FillMissingSampleIcons();else OpenOrCreate(); File.WriteAllText("Temp/Spells/setup-result.txt","OK: "+ScenePath); }
            catch(Exception e) { File.WriteAllText("Temp/Spells/setup-result.txt",e.ToString());GameLog.Error(e.Message,"SpellProgrammingV2"); }
        }
        [MenuItem("Norn/法术/打开独立 UGUI Sandbox")]
        public static void OpenOrCreate()
        {
            // 从不自动保存或丢弃用户场景。拒绝时由用户处理自己的未保存编辑。
            if(EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed)
                throw new InvalidOperationException("请退出Play并先修复编译错误。");
            for(int i=0;i<SceneManager.sceneCount;i++)
                if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("存在未保存Scene；未创建/切换Sandbox。");
            if(File.Exists(ScenePath)){FillMissingSampleIcons();EditorSceneManager.OpenScene(ScenePath);return;}
            EnsureFolder(DataPath);EnsureFolder("Assets/_Project/Scenes/Tests");
            var multi=Spell("Double",SpellKind.Multicast,"双重",0,s=>s.ExtraDraws=1);
            var trigger=Spell("Impact",SpellKind.Emit,"触发弹",10,s=>s.PayloadTrigger=PayloadTriggerMode.OnImpact);
            var fire=Spell("Bolt",SpellKind.Emit,"普通弹",5,null);
            var damage=Spell("Damage6",SpellKind.Modify,"增伤",5,s=>s.ModDamageAddFlat=6);
            var seven=Sample("SevenCard","七卡 · Scope隔离",new[]{multi,trigger,multi,fire,damage,fire,fire},
                "点5号增伤：只影响S1的4、6号弹；根层2、7号弹保持10。子层Mana在根构建时预付。");
            var wrap=Sample("Wrap","Wrapping · 连续牌堆",new[]{fire,damage},
                "样例已先执行一次普通弹；下一Root先读增伤，再回收上次discard的普通弹。hand不能回收。",s=>
                {s.StartAfterRoots=1;s.InitialMana=85;});
            var boost=Spell("Damage10",SpellKind.Modify,"增伤10",0,s=>s.ModDamageAddFlat=10);
            var recharge=Spell("Recharge",SpellKind.Modify,"缩短充能",0,s=>{s.ModDamageAddFlat=0;s.CastDelayDelta=-.1f;s.ReloadDelta=-.3f;});
            var pipeline=Sample("EightCard","八卡 · 根/子层时间",new[]{boost,multi,trigger,multi,fire,fire,recharge,fire},
                "Noita Lua full_pipeline结构的Norn数值映射：根弹20、子弹10。时间单位是Norn秒值，非原版平衡数据。");
            AssetDatabase.SaveAssets();
            FillMissingSampleIcons();
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var host=new GameObject("Spell Programming V2 Sandbox");var presenter=host.AddComponent<WandProgramWorkbenchPresenter>();
            AssignSamples(presenter,false);
            var input=new GameObject("EventSystem",typeof(EventSystem),typeof(InputSystemUIInputModule));
            input.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
            var camera=new GameObject("Sandbox Camera",typeof(Camera));camera.tag="MainCamera";
            camera.GetComponent<Camera>().clearFlags=CameraClearFlags.SolidColor;camera.GetComponent<Camera>().backgroundColor=new Color(.94f,.93f,.89f);
            EditorSceneManager.SaveScene(scene,ScenePath);AssetDatabase.SaveAssets();
            Selection.activeGameObject=host;
            GameLog.Info("独立Sandbox已创建。进入Play查看；此阶段没有真实库存/发射。","SpellProgrammingV2");
        }
        [MenuItem("Norn/法术/修复 Sandbox 缺失的样例引用")]
        public static void RepairMissingSampleLinks()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("请先退出Play。");
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("存在未保存Scene，未执行引用修复。");
            var scene=EditorSceneManager.GetActiveScene();
            if(scene.path!=ScenePath)scene=EditorSceneManager.OpenScene(ScenePath);
            WandProgramWorkbenchPresenter presenter=null;
            foreach(var root in scene.GetRootGameObjects())
            {var candidate=root.GetComponent<WandProgramWorkbenchPresenter>();if(candidate!=null)presenter=candidate;}
            if(presenter==null)throw new InvalidOperationException("Sandbox缺少Presenter，未保存。");
            AssignSamples(presenter,true);EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene,ScenePath);
        }
        private static void AssignSamples(WandProgramWorkbenchPresenter presenter,bool missingOnly)
        {
            var serialized=new SerializedObject(presenter);var list=serialized.FindProperty("_samples");
            if(missingOnly && list.arraySize!=3)throw new InvalidOperationException("样例配置已自定义，未覆盖。");
            list.arraySize=3;string[] files={"SevenCard","Wrap","EightCard"};
            for(int i=0;i<files.Length;i++)
            {
                var property=list.GetArrayElementAtIndex(i);if(missingOnly && property.objectReferenceValue!=null)continue;
                // NewScene之后重新从AssetDatabase取持久资产，不能把失效的临时包装引用写成fileID:0。
                var sample=AssetDatabase.LoadAssetAtPath<SpellProgramSandboxDefinition>("Assets/_Project/Tests/Fixtures/Spells/Workbench/"+files[i]+".asset");
                if(sample==null || !EditorUtility.IsPersistent(sample))throw new InvalidOperationException("样例资产尚未持久化："+files[i]);
                property.objectReferenceValue=sample;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        [MenuItem("Norn/法术/补齐 Sandbox 现有法术图标")]
        public static void FillMissingSampleIcons()
        {
            if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("请先退出Play再更新样例图标。");
            string[] targets={"Double","Impact","Bolt","Damage6","Damage10","Recharge"};
            string[] sources={"Spell_Multicast_DoubleShot","Spell_Emit_TriggerFireball","Spell_Emit_Fireball",
                "Spell_Modify_DamageUp","Spell_Modify_DamageUp","Spell_Modify_SpeedUp"};
            for(int i=0;i<targets.Length;i++)
            {
                var sample=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/Tests/Fixtures/Spells/Workbench/"+targets[i]+".asset");
                if(sample==null || sample.Icon!=null)continue;
                var existing=AssetDatabase.LoadAssetAtPath<SpellDefinition>("Assets/_Project/ScriptableObjects/Spell/Definitions/"+sources[i]+".asset");
                // 只给任务自己的Sandbox补引用，不复制纹理、不改正式Spell，不覆盖开发者已选择的图标。
                if(existing==null || existing.Icon==null)continue;
                sample.Icon=existing.Icon;EditorUtility.SetDirty(sample);
                // 只保存被本工具修改的样例，不能顺便保存用户正在编辑的其他SO资产。
                AssetDatabase.SaveAssetIfDirty(sample);
            }
        }
        private static SpellDefinition Spell(string file,SpellKind kind,string title,float mana,Action<SpellDefinition> configure)
        {
            string path="Assets/_Project/Tests/Fixtures/Spells/Workbench/"+file+".asset";var asset=AssetDatabase.LoadAssetAtPath<SpellDefinition>(path);
            if(asset!=null)return asset;
            asset=ScriptableObject.CreateInstance<SpellDefinition>();asset.Kind=kind;asset.DisplayName=title;
            asset.ManaCost=mana;asset.BaseDamage=10;asset.BaseSpeed=20;configure?.Invoke(asset);
            AssetDatabase.CreateAsset(asset,path);return asset;
        }
        private static SpellProgramSandboxDefinition Sample(string file,string title,SpellDefinition[] spells,string goal,
            Action<SpellProgramSandboxDefinition> configure=null)
        {
            string path="Assets/_Project/Tests/Fixtures/Spells/Workbench/"+file+".asset";var asset=AssetDatabase.LoadAssetAtPath<SpellProgramSandboxDefinition>(path);
            if(asset!=null)return asset;
            asset=ScriptableObject.CreateInstance<SpellProgramSandboxDefinition>();asset.Title=title;asset.Spells=spells;asset.LearningGoal=goal;
            configure?.Invoke(asset);AssetDatabase.CreateAsset(asset,path);return asset;
        }
        private static void EnsureFolder(string path)
        {
            if(AssetDatabase.IsValidFolder(path))return;
            string parent=Path.GetDirectoryName(path).Replace('\\','/');EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent,Path.GetFileName(path));
        }
    }
}
