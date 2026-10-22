using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Game.Core;
using Game.Run;
using Game.UI;
using Game.Skills;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>地牢交付只保存隔离Review/指定资产；正式Scene必须由作者保存。</summary>
    [InitializeOnLoad]
    internal static class P7DungeonContentSetup
    {
        private const string Request="Temp/SinglePlayer/p7-dungeon.request";
        private const string Map="Assets/_Project/Scenes/P7_DemoRun.unity";
        private const string Evidence="TestResults/SinglePlayer/M6-T18-Dungeon";
        private const string Review="Assets/_Project/Scenes/Tests/Hub/P7_DungeonReview.unity";
        private const string Data="Assets/_Project/ScriptableObjects/World/Dungeon";
        private const string Prefabs="Assets/_Project/Prefabs/SinglePlayer/P7Dungeon";
        private static DungeonLootTable[] _tables;
        private static WorldCurrencyPickup _coin;
        private static DungeonRewardProfile _rewards;
        static P7DungeonContentSetup(){EditorApplication.update+=Poll;}
        [MenuItem("Tools/Norn/P7 Dungeon/Prepare isolated Review")]
        private static void PrepareMenu()=>Prepare();
        [MenuItem("Tools/Norn/P7 Dungeon/Open saved P7")]
        private static void OpenMenu()=>OpenP7();
        [MenuItem("Tools/Norn/P7 Dungeon/Apply to current P7 (Undo, no save)")]
        private static void ApplyMenu()=>ApplyCurrent();
        private static void Poll()
        {
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("当前编译失败");
                if(command=="inspect")Inspect();else if(command=="prepare")Prepare();
                else if(command=="apply-current")ApplyCurrent();else if(command=="open-p7")OpenP7();else throw new ArgumentException(command);
            }
            catch(Exception error){Write("setup-error.txt",error.ToString());GameLog.Error(error.ToString(),"DungeonSetup");}
        }
        private static void Write(string name,string value)
        {Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+name,DateTime.UtcNow.ToString("O")+"\n"+value);}
        private static void RequireClean()
        {
            if(SceneManager.sceneCount!=1)throw new InvalidOperationException("只允许一张干净场景，未关闭任何地图。");
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)
                throw new InvalidOperationException("当前Scene未保存，没有保存/丢弃作者修改。");
        }
        private static void Set(UnityEngine.Object target,string field,UnityEngine.Object value)
        {var data=new SerializedObject(target);data.FindProperty(field).objectReferenceValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static void Text(UnityEngine.Object target,string field,string value)
        {var data=new SerializedObject(target);data.FindProperty(field).stringValue=value;data.ApplyModifiedPropertiesWithoutUndo();}
        private static T Find<T>(Scene scene)where T:Component
        {foreach(var root in scene.GetRootGameObjects()){var found=root.GetComponentInChildren<T>(true);if(found!=null)return found;}return null;}
        private static Material Material(string path,string shader,Color color)
        {
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);if(material!=null)return material;
            var selected=Shader.Find(shader);if(selected==null)throw new InvalidOperationException("找不到Shader："+shader);
            material=new Material(selected);material.SetColor("_BaseColor",color);AssetDatabase.CreateAsset(material,path);return material;
        }
        private static bool IsGroundMaterial(ItemDefinition item)
        {
            if(item.Category==ItemCategory.Food||item.Category==ItemCategory.Herb)return true;
            if(item.Kind!=ItemKind.Material)return false;
            // 稳定键5/6是可直接拾取的两种固体；气液只能通过容器拥有，不投放成空材料卡。
            int key=new SerializedObject(item).FindProperty("_materialKey").intValue;return key==5||key==6;
        }
        private static void Assets()
        {
            Directory.CreateDirectory(Data);Directory.CreateDirectory(Prefabs);AssetDatabase.Refresh();
            string spellPath="Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/GroundSpell.prefab";
            var visual=AssetDatabase.LoadAssetAtPath<GameObject>(spellPath);
            if(visual==null)
            {
                var content=PrefabUtility.LoadPrefabContents("Assets/_Project/Art/Prefabs/Rewards/Spell.prefab");
                try
                {
                    foreach(var reward in content.GetComponentsInChildren<SpellRewardPickup>(true))UnityEngine.Object.DestroyImmediate(reward);
                    foreach(var collider in content.GetComponentsInChildren<Collider>(true))UnityEngine.Object.DestroyImmediate(collider);
                    content.transform.position=Vector3.zero;
                    var cube=content.transform.Find("Visual/Cube");if(cube==null)throw new InvalidOperationException("法术源Prefab缺少Visual/Cube");
                    cube.GetComponent<MeshRenderer>().sharedMaterial=Material("Assets/_Project/Art/Materials/P7/GroundSpellIcon.mat","Universal Render Pipeline/Unlit",Color.white);
                    visual=PrefabUtility.SaveAsPrefabAsset(content,spellPath);
                }
                finally{PrefabUtility.UnloadPrefabContents(content);}
            }
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
            var entries=new SerializedObject(catalog).FindProperty("_definitions");
            var spells=new List<ItemDefinition>();var wands=new List<ItemDefinition>();var tools=new List<ItemDefinition>();var materials=new List<ItemDefinition>();
            for(int i=0;i<entries.arraySize;i++)
            {
                var item=(ItemDefinition)entries.GetArrayElementAtIndex(i).objectReferenceValue;
                if(item.Kind==ItemKind.Spell){spells.Add(item);Set(item,"_pickupPrefab",visual);}
                else if(item.Kind==ItemKind.Wand)wands.Add(item);
                else if(item.Kind==ItemKind.Container)tools.Add(item);
                else if(IsGroundMaterial(item))materials.Add(item);
            }
            if(spells.Count==0||wands.Count==0||tools.Count==0||materials.Count==0)throw new InvalidOperationException("Catalog缺少掉落分类。");
            string rewardPath="Assets/_Project/ScriptableObjects/World/Dungeon/DungeonRewards.asset";_rewards=AssetDatabase.LoadAssetAtPath<DungeonRewardProfile>(rewardPath);
            if(_rewards==null)
            {
                _rewards=ScriptableObject.CreateInstance<DungeonRewardProfile>();var pool=new List<SpellDefinition>();
                foreach(var item in spells)if(!pool.Contains(item.SpellDefinition))pool.Add(item.SpellDefinition);
                _rewards.Pool=pool.ToArray();AssetDatabase.CreateAsset(_rewards,rewardPath);
            }
            string wandPath="Assets/_Project/ScriptableObjects/World/Dungeon/DungeonWandRolls.asset";var rolls=AssetDatabase.LoadAssetAtPath<DungeonWandRollProfile>(wandPath);
            if(rolls==null){rolls=ScriptableObject.CreateInstance<DungeonWandRollProfile>();AssetDatabase.CreateAsset(rolls,wandPath);}
            var coin=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/GroundCoins.prefab");
            if(coin==null)
            {
                var root=new GameObject("GroundCoins");
                try
                {
                    var model=GameObject.CreatePrimitive(PrimitiveType.Cylinder);model.name="Gold coin";model.transform.SetParent(root.transform,false);
                    model.transform.localScale=new Vector3(.38f,.045f,.38f);UnityEngine.Object.DestroyImmediate(model.GetComponent<Collider>());
                    model.GetComponent<MeshRenderer>().sharedMaterial=Material("Assets/_Project/Art/Materials/P7/GroundCoins.mat","Universal Render Pipeline/Lit",new Color(1,.65f,.06f,1));
                    var trigger=root.AddComponent<SphereCollider>();trigger.isTrigger=true;trigger.radius=.75f;
                    var body=root.AddComponent<Rigidbody>();body.isKinematic=true;body.useGravity=false;root.AddComponent<WorldCurrencyPickup>();
                    coin=PrefabUtility.SaveAsPrefabAsset(root,"Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/GroundCoins.prefab");
                }
                finally{UnityEngine.Object.DestroyImmediate(root);}
            }
            var coinContent=PrefabUtility.LoadPrefabContents("Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/GroundCoins.prefab");
            try
            {
                var model=coinContent.transform.Find("Gold coin");
                if(model==null)throw new InvalidOperationException("金币Prefab缺少Gold coin视觉子物体。");
                model.localRotation=Quaternion.Euler(90,0,0);model.localPosition=new Vector3(0,.2f,0);
                coinContent.GetComponent<SphereCollider>().center=new Vector3(0,.2f,0);
                Set(coinContent.GetComponent<WorldCurrencyPickup>(),"_visual",model);
                coin=PrefabUtility.SaveAsPrefabAsset(coinContent,"Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/GroundCoins.prefab");
            }
            finally{PrefabUtility.UnloadPrefabContents(coinContent);}
            _coin=coin.GetComponent<WorldCurrencyPickup>();_tables=new DungeonLootTable[4];
            for(int stage=0;stage<4;stage++)
            {
                string path="Assets/_Project/ScriptableObjects/World/Dungeon/Stage"+(stage+1)+"Loot.asset";var table=AssetDatabase.LoadAssetAtPath<DungeonLootTable>(path);
                if(table==null)
                {
                    table=ScriptableObject.CreateInstance<DungeonLootTable>();table.CoinChance=.75f;
                    table.MinCoins=10+stage*8;table.MaxCoins=25+stage*15;
                    table.Items=new[]{new DungeonItemDrop{Pool=spells.ToArray(),Chance=.25f+stage*.04f,SpellCharges=8},
                        new DungeonItemDrop{Pool=wands.ToArray(),Chance=.04f+stage*.02f},
                        new DungeonItemDrop{Pool=tools.ToArray(),Chance=.12f},
                        new DungeonItemDrop{Pool=materials.ToArray(),Chance=.35f}};
                    AssetDatabase.CreateAsset(table,path);
                }
                _tables[stage]=table;
                if(table.WandRolls==null){table.WandRolls=rolls;EditorUtility.SetDirty(table);}
                foreach(var entry in table.Items)
                {
                    bool hasSpell=false;foreach(var item in entry.Pool)if(item.Kind==ItemKind.Spell)hasSpell=true;
                    // 仅迁移本轮前生成的8次模板；已有作者自定范围不覆盖。
                    if(hasSpell&&entry.MaxSpellCharges==0&&entry.SpellCharges==8)
                    {entry.SpellCharges=20;entry.MaxSpellCharges=40;entry.UnlimitedSpell=false;EditorUtility.SetDirty(table);}
                }
                // 仅纠正无容器气液/空材料这一非法配置，不覆盖作者已经修改的概率或数量。
                for(int entry=0;entry<table.Items.Length;entry++)
                {
                    var pool=new List<ItemDefinition>();bool changed=false;
                    foreach(var item in table.Items[entry].Pool)
                        if(item.Kind==ItemKind.Material&&!IsGroundMaterial(item))changed=true;else pool.Add(item);
                    if(changed){table.Items[entry].Pool=pool.Count>0?pool.ToArray():materials.ToArray();EditorUtility.SetDirty(table);}
                }
            }
            AssetDatabase.SaveAssets();Write("asset-wiring.txt","SpellDefinitions="+spells.Count+"\nWands="+wands.Count+"\nContainers="+tools.Count+"\nMaterials="+materials.Count+"\nTables=4\nSourceRewardsPrefabUnchanged=true");
        }
        private static void Prepare()
        {
            RequireClean();string original=SceneManager.GetActiveScene().path;
            var hash=System.Security.Cryptography.SHA256.Create();byte[] before=hash.ComputeHash(File.ReadAllBytes(Map));
            Assets();File.Copy(Map,Review,true);AssetDatabase.ImportAsset(Review,ImportAssetOptions.ForceUpdate);
            try
            {
                var scene=EditorSceneManager.OpenScene(Review,OpenSceneMode.Single);Apply(scene,false);
                Bake(scene,true);EditorSceneManager.SaveScene(scene);
                Write("review-ready.txt","Assets/_Project/Scenes/Tests/Hub/P7_DungeonReview.unity\\nFormalP7Unchanged="+(Convert.ToBase64String(before)==Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(Map)))));
            }
            finally{EditorSceneManager.OpenScene(original,OpenSceneMode.Single);hash.Dispose();}
        }
        private static void OpenP7(){RequireClean();EditorSceneManager.OpenScene(Map,OpenSceneMode.Single);Write("formal-opened.txt","Assets/_Project/Scenes/P7_DemoRun.unity\\nSavedAutomatically=false");}
        private static void ApplyCurrent()
        {
            RequireClean();var scene=SceneManager.GetActiveScene();if(scene.path!=Map)throw new InvalidOperationException("只对单独打开的P7应用。");
            Assets();Undo.SetCurrentGroupName("P7地牢与掉落");int group=Undo.GetCurrentGroup();Apply(scene,true);Bake(scene,false);Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);Write("formal-applied.txt","P7 modified via Undo; not saved. Please save manually.");
        }
        private static void Remove(UnityEngine.Object value,bool undo)
        {if(value==null)return;if(undo)Undo.DestroyObjectImmediate(value);else UnityEngine.Object.DestroyImmediate(value);}
        private static void Apply(Scene scene,bool undo)
        {
            var run=Find<DemoRunController>(scene);if(run==null)throw new InvalidOperationException("P7缺少DemoRunController。");
            var runData=new SerializedObject(run);var refs=runData.FindProperty("_encounters");var encounters=new DemoEncounterController[refs.arraySize];
            for(int i=0;i<encounters.Length;i++)encounters[i]=(DemoEncounterController)refs.GetArrayElementAtIndex(i).objectReferenceValue;
            if(encounters.Length!=4)throw new InvalidOperationException("必须保留四关。");
            var previous=Find<DungeonRunContent>(scene);
            // 本轮只调整奖励/实例属性，不重新生成作者已保存的墙、材料位置或Prefab overrides。
            var root=previous!=null?previous.gameObject:new GameObject("P7 Dungeon · 固定迷宫与探索");
            if(previous==null){SceneManager.MoveGameObjectToScene(root,scene);if(undo)Undo.RegisterCreatedObjectUndo(root,"增加P7地牢配置");}
            var dungeon=previous!=null?previous:root.AddComponent<DungeonRunContent>();if(undo)Undo.RecordObject(dungeon,"更新随机奖励配置");var data=new SerializedObject(dungeon);
            var stages=data.FindProperty("_encounters");var tables=data.FindProperty("_tables");stages.arraySize=tables.arraySize=4;
            for(int i=0;i<4;i++){stages.GetArrayElementAtIndex(i).objectReferenceValue=encounters[i];tables.GetArrayElementAtIndex(i).objectReferenceValue=_tables[i];}
            data.FindProperty("_coinPrefab").objectReferenceValue=_coin;data.FindProperty("_rewards").objectReferenceValue=_rewards;data.ApplyModifiedPropertiesWithoutUndo();
            if(previous==null)Layout(root.transform,dungeon);
            // 只删除P7上方玩法提示及其消费者，避免Start又启用已隐藏的GameplayHUD。
            var hud=Find<DemoRunHudController>(scene);
            if(hud!=null)
            {
                var fields=new SerializedObject(hud);var gameplay=fields.FindProperty("_gameplayRoot").objectReferenceValue;
                var notification=fields.FindProperty("_notificationRoot").objectReferenceValue;
                Remove(hud,undo);Remove(gameplay,undo);if(notification!=gameplay)Remove(notification,undo);
            }
            foreach(var sceneRoot in scene.GetRootGameObjects())foreach(var reward in sceneRoot.GetComponentsInChildren<SpellRewardPickup>(true))
            {
                var fields=new SerializedObject(reward);var old=(GameObject)fields.FindProperty("_visualRoot").objectReferenceValue;
                if(old!=null&&old.transform.Find("Visual/Cube")!=null)continue;
                var model=PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/GroundSpell.prefab"),scene)as GameObject;
                model.name="Visual";model.transform.SetParent(reward.transform,false);model.transform.localPosition=Vector3.zero;
                // 奖励根已带源Scale，子模型不再把1.75重复乘一遍。
                model.transform.localScale=Vector3.one;
                int stage=fields.FindProperty("_encounterIndex").intValue;
                WorldSpellIconPresentation.Apply(model,run.Definition.GetEncounter(stage).RewardSpell?.Icon);
                if(undo){Undo.RegisterCreatedObjectUndo(model,"奖励视觉");Undo.RecordObject(reward,"奖励视觉接线");}
                Set(reward,"_visualRoot",model);if(old!=null&&old!=reward.gameObject)Remove(old,undo);
            }
            foreach(var sceneRoot in scene.GetRootGameObjects())foreach(var reward in sceneRoot.GetComponentsInChildren<DungeonFinalRewardPickup>(true))
            {
                var fields=new SerializedObject(reward);var old=(GameObject)fields.FindProperty("_visualRoot").objectReferenceValue;
                if(old!=null&&old.transform.Find("Visual/Cube")!=null)continue;
                var model=PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/GroundSpell.prefab"),scene)as GameObject;
                model.name="Visual";model.transform.SetParent(reward.transform,false);model.transform.localPosition=Vector3.zero;model.transform.localScale=Vector3.one;
                WorldSpellIconPresentation.Apply(model,_rewards.Pool[0].Icon);
                if(undo){Undo.RegisterCreatedObjectUndo(model,"最终随机奖励视觉");Undo.RecordObject(reward,"最终随机奖励接线");}
                Set(reward,"_visualRoot",model);if(old!=null&&old!=reward.gameObject)Remove(old,undo);
            }
            Write("layout-applied.txt","Stages=4\nFixedWalls=8\nMaterialPoints=8\nRandom finite rewards / final three / rolled wands / upright coins\nGameplayHUD removed\nFormalSaved=false");
        }
        private static void Wall(Transform root,Vector3 position,Vector3 scale,Material material)
        {
            var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.name="Maze wall";wall.transform.SetParent(root,false);
            wall.transform.position=position;wall.transform.localScale=scale;wall.GetComponent<MeshRenderer>().sharedMaterial=material;
        }
        private static void Layout(Transform parent,DungeonRunContent dungeon)
        {
            string path="Assets/_Project/Prefabs/SinglePlayer/P7Dungeon/FixedFourStageLayout.prefab";var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if(prefab==null)
            {
                var layout=new GameObject("FixedFourStageLayout");
                try
                {
                    var material=AssetDatabase.LoadAssetAtPath<Material>("Assets/_Project/Art/Materials/P7/M_P7_Gate.mat");
                    var walls=new[]{new Vector3(2,1.5f,22),new Vector3(-10,1.5f,21),new Vector3(26,1.5f,16),new Vector3(34,1.5f,13),
                        new Vector3(27,1.5f,43),new Vector3(30,1.5f,52),new Vector3(-4,1.5f,47),new Vector3(-12,1.5f,51)};
                    var sizes=new[]{new Vector3(.5f,3,12),new Vector3(10,3,.5f),new Vector3(.5f,3,14),new Vector3(7,3,.5f),
                        new Vector3(14,3,.5f),new Vector3(.5f,3,6),new Vector3(.5f,3,14),new Vector3(10,3,.5f)};
                    for(int i=0;i<walls.Length;i++)Wall(layout.transform,walls[i],sizes[i],material);
                    var pointPositions=new[]{new Vector3(-16,0,25),new Vector3(-7,0,16),new Vector3(37,0,10),new Vector3(21,0,25),
                        new Vector3(35,0,53),new Vector3(20,0,40),new Vector3(-15,0,56),new Vector3(2,0,55)};
                    var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/ScriptableObjects/World/Hub/Inventory/Catalog.asset");
                    var definitions=new SerializedObject(catalog).FindProperty("_definitions");var materials=new List<ItemDefinition>();
                    for(int i=0;i<definitions.arraySize;i++){var item=(ItemDefinition)definitions.GetArrayElementAtIndex(i).objectReferenceValue;
                        if(IsGroundMaterial(item))materials.Add(item);}
                    for(int i=0;i<pointPositions.Length;i++)
                    {
                        var node=new GameObject("Explore material · "+(i+1));node.transform.SetParent(layout.transform,false);node.transform.position=pointPositions[i];
                        var point=node.AddComponent<WorldItemSpawnPoint>();Set(point,"_definition",materials[i%materials.Count]);Text(point,"_spawnId","p7.explore."+i);
                        var seed=new SerializedObject(point);seed.FindProperty("_initialMaterialQuantity").intValue=256;seed.ApplyModifiedPropertiesWithoutUndo();
                        Text(point,"_sceneKey",Map);
                        var model=materials[i%materials.Count].WorldModel;
                        if(model!=null){var copy=(GameObject)PrefabUtility.InstantiatePrefab(model);copy.transform.SetParent(node.transform,false);copy.transform.localPosition=Vector3.zero;}
                    }
                    prefab=PrefabUtility.SaveAsPrefabAsset(layout,path);
                }
                finally{UnityEngine.Object.DestroyImmediate(layout);}
            }
            // 本轮引入字段后修复已生成模板：保留墙与作者变换，仅规范材料定义/初始数量及导航排除。
            var contents=PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach(var point in contents.GetComponentsInChildren<WorldItemSpawnPoint>(true))
                {
                    var pointData=new SerializedObject(point);
                    if(!IsGroundMaterial(point.Definition))
                        pointData.FindProperty("_definition").objectReferenceValue=AssetDatabase.LoadAssetAtPath<ItemDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Definitions/Item_Container_Ice.asset");
                    if(pointData.FindProperty("_initialMaterialQuantity").intValue==0)pointData.FindProperty("_initialMaterialQuantity").intValue=256;
                    pointData.ApplyModifiedPropertiesWithoutUndo();
                    var modifier=point.GetComponent<NavMeshModifier>();if(modifier==null)modifier=point.gameObject.AddComponent<NavMeshModifier>();modifier.ignoreFromBuild=true;
                }
                prefab=PrefabUtility.SaveAsPrefabAsset(contents,path);
            }
            finally{PrefabUtility.UnloadPrefabContents(contents);}
            var instance=(GameObject)PrefabUtility.InstantiatePrefab(prefab,parent.gameObject.scene);instance.transform.SetParent(parent,false);
            var checks=new List<Transform>();foreach(var node in instance.GetComponentsInChildren<Transform>())
                if(node.GetComponent<WorldItemSpawnPoint>()!=null)checks.Add(node);
            var data=new SerializedObject(dungeon);var routes=data.FindProperty("_routeChecks");routes.arraySize=checks.Count;
            for(int i=0;i<checks.Count;i++)routes.GetArrayElementAtIndex(i).objectReferenceValue=checks[i];data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void Bake(Scene scene,bool create)
        {
            var surface=Find<NavMeshSurface>(scene);if(surface==null)throw new InvalidOperationException("P7缺少NavMeshSurface。");
            if(create)
            {
                var gates=new List<ArenaGate>();foreach(var root in scene.GetRootGameObjects())gates.AddRange(root.GetComponentsInChildren<ArenaGate>(true));
                var blockers=new List<Collider>();var enabled=new List<bool>();var visuals=new List<GameObject>();var active=new List<bool>();
                foreach(var gate in gates)
                {
                    var fields=new SerializedObject(gate);var colliders=fields.FindProperty("_blockingColliders");var roots=fields.FindProperty("_visualRoots");
                    for(int i=0;i<colliders.arraySize;i++){var blocker=colliders.GetArrayElementAtIndex(i).objectReferenceValue as Collider;
                        if(blocker!=null){blockers.Add(blocker);enabled.Add(blocker.enabled);}}
                    for(int i=0;i<roots.arraySize;i++){var visual=roots.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                        if(visual!=null){visuals.Add(visual);active.Add(visual.activeSelf);}}
                    gate.SetClosed(false);
                }
                // 战斗门属于动态阻挡；以关门几何Bake会永久断路，Runtime打开Collider不能恢复NavMesh。
                try{surface.BuildNavMesh();}
                finally{for(int i=0;i<blockers.Count;i++)blockers[i].enabled=enabled[i];for(int i=0;i<visuals.Count;i++)visuals[i].SetActive(active[i]);}
                if(surface.navMeshData==null)throw new InvalidOperationException("NavMesh构建失败。");
                string path="Assets/_Project/ScriptableObjects/World/Dungeon/DungeonNavigation.asset";
                // 新数据与原导航资产独立，重建不改原NavMeshData。
                var previous=AssetDatabase.LoadAssetAtPath<UnityEngine.AI.NavMeshData>(path);
                if(previous==null)AssetDatabase.CreateAsset(surface.navMeshData,path);
                else{EditorUtility.CopySerialized(surface.navMeshData,previous);surface.navMeshData=previous;EditorUtility.SetDirty(previous);}
                AssetDatabase.SaveAssets();
            }
            else
            {Undo.RecordObject(surface,"接入地牢Navigation");surface.navMeshData=AssetDatabase.LoadAssetAtPath<UnityEngine.AI.NavMeshData>("Assets/_Project/ScriptableObjects/World/Dungeon/DungeonNavigation.asset");}
            Write("navigation.txt","独立地牢NavMeshData；原导航资产保留。\n"+AssetDatabase.GetAssetPath(surface.navMeshData));
        }
        private static void Inspect()
        {
            // Preview Scene不加入用户打开的正式场景列表，关闭它不保存任何作者Scene。
            var scene=EditorSceneManager.OpenPreviewScene(Map);
            try
            {
                var report=new StringBuilder();
                foreach(var root in scene.GetRootGameObjects())foreach(var node in root.GetComponentsInChildren<Transform>(true))
                {
                    if(node.name=="Floor" || node.name.StartsWith("Encounter_") || node.name.StartsWith("Corridor")
                        || node.GetComponent<EncounterEntryTrigger>()!=null || node.GetComponent<ArenaGate>()!=null
                        || node.GetComponent<Game.Combat.HealthComponent>()!=null || node.GetComponent<SpellRewardPickup>()!=null)
                    {
                        report.Append(node.name).Append(" pos=").Append(node.position.ToString("F2"))
                            .Append(" scale=").Append(node.lossyScale.ToString("F2"));
                        var renderer=node.GetComponent<Renderer>();if(renderer!=null)report.Append(" bounds=").Append(renderer.bounds.ToString());
                        report.AppendLine();
                    }
                    foreach(var component in node.GetComponents<Component>())
                        if(component!=null && component.GetType().Name=="NavMeshSurface")
                            report.Append("NAV ").Append(node.name).AppendLine(EditorJsonUtility.ToJson(component));
                }
                Write("layout-before.txt",report.ToString());
            }
            finally{EditorSceneManager.ClosePreviewScene(scene);}
        }
    }
}
