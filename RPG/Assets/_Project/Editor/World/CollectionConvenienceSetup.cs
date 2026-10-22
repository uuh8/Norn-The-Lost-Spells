using System;
using System.IO;
using System.Text;
using Game.Core;
using Game.Run;
using Game.UI;
using Game.Character;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using Object=UnityEngine.Object;
namespace Game.EditorTools
{
    /// <summary>按钮布局只在显式Editor命令中设置，Runtime不覆盖用户编辑；正式Scene始终由用户保存。</summary>
    [InitializeOnLoad] internal static class CollectionConvenienceSetup
    {
        private const string Request="Temp/SinglePlayer/collection-convenience.request";
        private const string Folder="Assets/_Project/Scenes/SinglePlayer/";
        private const string Evidence="TestResults/SinglePlayer/M6-T24-CollectionConvenience";
        private const string PendingHandoff="Norn.CollectionConvenience.PendingHandoff";
        static CollectionConvenienceSetup()
        {EditorApplication.update+=Poll;EditorSceneManager.sceneSaved+=OnSceneSaved;EditorApplication.playModeStateChanged+=BeforePlay;}
        private static void OnSceneSaved(Scene scene)
        {if(SessionState.GetBool(PendingHandoff,false))EditorApplication.delayCall+=FinishSavedHandoff;}
        private static void BeforePlay(PlayModeStateChange state)
        {
            if(state!=PlayModeStateChange.ExitingEditMode||!SessionState.GetBool(PendingHandoff,false))return;
            // 只保护本工具尚未交付的装配，不把正常的多Scene编辑变成全局限制。
            EditorApplication.isPlaying=false;EditorApplication.delayCall+=FinishSavedHandoff;
            GameLog.Warn("本轮装配尚未收尾：保存后会恢复仅据点，请完成后再按Play。","CollectionSetup");
        }
        private static void FinishSavedHandoff()
        {
            if(!SessionState.GetBool(PendingHandoff,false)||EditorApplication.isPlayingOrWillChangePlaymode||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            for(int i=0;i<SceneManager.sceneCount;i++)
            {
                var scene=SceneManager.GetSceneAt(i);if(scene.isDirty)return;
                // 用户自行切换工作内容后不擅自关闭其它Scene；待显式收尾命令处理。
                if(scene.path!="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity"&&scene.path!="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity")return;
            }
            var hub=SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");if(!hub.isLoaded)return;
            var home=SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity");
            // Save All只是写磁盘，不卸载Additive Scene；保存后必须显式CloseScene，避免两套Runtime启动。
            if(home.isLoaded&&!EditorSceneManager.CloseScene(home,true))throw new InvalidOperationException("未能关闭已保存Home");
            SceneManager.SetActiveScene(hub);SessionState.SetBool(PendingHandoff,false);Audit("handoff-restored.txt");
        }
        private static void Poll()
        {
            if(!EditorApplication.isPlayingOrWillChangePlaymode)FinishSavedHandoff();
            if(File.Exists(Request)&&File.ReadAllText(Request).Trim()=="audit"&&!EditorApplication.isCompiling&&!EditorApplication.isUpdating)
            {File.Delete(Request);Audit(EditorApplication.isPlaying?"runtime-audit.txt":"editor-audit.txt");return;}
            if(File.Exists(Request)&&File.ReadAllText(Request).Trim()=="verify-movement"&&EditorApplication.isPlaying&&!EditorApplication.isCompiling)
            {File.Delete(Request);VerifyMovement();return;}
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating||EditorApplication.isPlayingOrWillChangePlaymode)return;
            var command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译失败，拒绝使用旧DLL");
                for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("有未保存Scene，不代保存/丢弃");
                if(command=="prepare")Prepare();else if(command=="apply-current")ApplyFormal();
                else if(command=="finish-saved"){SessionState.SetBool(PendingHandoff,true);FinishSavedHandoff();}
                else if(command=="verify-handoff")VerifySavedHandoff();
                else if(command=="enable-hub-camera")EnableHubCamera();
                else throw new ArgumentException(command);
            }
            catch(Exception e){Write("setup-error.txt",e.ToString());GameLog.Error(e.ToString(),"CollectionSetup");}
        }
        private static void EnableHubCamera()
        {
            var scene=SceneManager.GetActiveScene();if(SceneManager.sceneCount!=1||scene.path!="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity")throw new InvalidOperationException("只在单一正式据点恢复相机");
            foreach(var root in scene.GetRootGameObjects())if(root.name=="__Cameras")
            {
                bool main=false;foreach(var camera in root.GetComponentsInChildren<Camera>(true))if(camera.CompareTag("MainCamera")&&camera.enabled)main=true;
                if(!main)throw new InvalidOperationException("现有相机组缺少启用的MainCamera");
                Undo.RecordObject(root,"恢复据点游戏相机");root.SetActive(true);EditorSceneManager.MarkSceneDirty(scene);
                Write("camera-restored-undo.txt","CameraRootActive=true\nUndo=true\nFormalSceneSaved=false");return;
            }
            throw new InvalidOperationException("没有作者__Cameras节点");
        }
        private static void VerifyMovement()
        {
            var players=Object.FindObjectsByType<WizardController>(FindObjectsSortMode.None);
            if(SceneManager.sceneCount!=1||players.Length!=1||players[0].MainCamera==null)throw new InvalidOperationException("移动验证要求单Scene、单玩家和有效相机");
            // 单次Editor回归调用真实移动方向函数，暂存/恢复输入；不触碰Input System设备或移动玩家位置。
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var field=typeof(PlayerControllerBase).GetField("_moveInput",flags);var method=typeof(PlayerControllerBase).GetMethod("CalculateMoveDirection",flags);
            var before=(Vector2)field.GetValue(players[0]);Vector3 direction;
            try{field.SetValue(players[0],Vector2.up);direction=(Vector3)method.Invoke(players[0],null);}
            finally{field.SetValue(players[0],before);}
            if(!float.IsFinite(direction.x)||!float.IsFinite(direction.y)||!float.IsFinite(direction.z)||direction.sqrMagnitude<.9f)throw new InvalidOperationException("移动方向无效");
            Audit("runtime-camera-fixed-audit.txt");Write("movement-regression.txt","Passed=true\nNonZeroInputCalculatedWithoutException=true\nDirection="+direction+"\nInputRestored=true");
        }
        private static string SceneHash(string path)
        {using var hash=System.Security.Cryptography.SHA256.Create();return Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)));}
        private static void VerifySavedHandoff()
        {
            string hubPath="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity",homePath="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity";
            if(SceneManager.sceneCount!=1||SceneManager.GetActiveScene().path!=hubPath)throw new InvalidOperationException("显式回归仅在已保存的单一据点执行");
            string hubBefore=SceneHash(hubPath),homeBefore=SceneHash(homePath);
            EditorSceneManager.OpenScene(homePath,OpenSceneMode.Additive);Audit("handoff-pair-before.txt");
            SessionState.SetBool(PendingHandoff,true);FinishSavedHandoff();
            if(SceneManager.sceneCount!=1||SceneManager.GetActiveScene().path!=hubPath||SceneHash(hubPath)!=hubBefore||SceneHash(homePath)!=homeBefore)
                throw new InvalidOperationException("收尾没有恢复单据点，或改变了正式Scene文件");
            Write("handoff-regression.txt","Passed=true\nTwoLoadedScenesRestoredToOne=true\nHubFileUnchanged=true\nHomeFileUnchanged=true\nNoSaveOrDiscard=true");
        }
        private static void Audit(string file)
        {
            var text=new StringBuilder();text.AppendLine("Playing="+EditorApplication.isPlaying);text.AppendLine("SceneCount="+SceneManager.sceneCount);
            for(int i=0;i<SceneManager.sceneCount;i++){var scene=SceneManager.GetSceneAt(i);text.AppendLine(scene.path+" Loaded="+scene.isLoaded+" Dirty="+scene.isDirty+" Active="+(scene==SceneManager.GetActiveScene()));}
            var players=Object.FindObjectsByType<WizardController>(FindObjectsSortMode.None);var sessions=Object.FindObjectsByType<SinglePlayerInventorySession>(FindObjectsSortMode.None);
            text.AppendLine("Players="+players.Length);text.AppendLine("EventSystems="+Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length);text.AppendLine("InventorySessions="+sessions.Length);
            foreach(var player in players)text.AppendLine("Player="+player.name+" CameraValid="+(player.MainCamera!=null));
            foreach(var session in sessions)text.AppendLine("Inventory="+session.name+" Ready="+(session.ReadOnly!=null)+" Preview="+session.IsScenePreview+" Message="+session.InitializationMessage);
            Write(file,text.ToString());
        }
        private static void Write(string file,string value){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+file,DateTime.UtcNow.ToString("O")+"\n"+value);}
        private static void ResourcesSetup()
        {
            const string assets="Assets/_Project/ScriptableObjects/Items/Containers/";
            var definition=AssetDatabase.LoadAssetAtPath<ContainerUseDefinition>("Assets/_Project/ScriptableObjects/Items/Containers/Use/UseDefinition.asset");
            var data=new SerializedObject(definition);data.FindProperty("_collectionRate").floatValue=8000;
            data.FindProperty("_collectionRadius").floatValue=.5f;data.FindProperty("_gasPourMultiplier").floatValue=4;
            data.FindProperty("_gasDepositRadius").floatValue=.8f;data.FindProperty("_maximumPourBatchGmu").intValue=1024;data.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(definition);
            foreach(var name in new[]{"ice","dried"})
            {var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(assets+name+".asset");var itemData=new SerializedObject(item);itemData.FindProperty("_materialItemCapacityGmu").longValue=15000;itemData.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(item);}
            AssetDatabase.SaveAssets();
        }
        private static void Prepare()
        {
            ResourcesSetup();var original=SceneManager.GetActiveScene();var audit=new StringBuilder();
            foreach(string name in new[]{"Hub","Home"})
            {
                string path="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_"+name+".unity",review="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_"+name+"_CollectionReview.unity";
                if(SceneManager.GetSceneByPath(review).isLoaded)throw new InvalidOperationException("Review已打开");
                if(File.Exists(review)){File.Copy(path,review,true);AssetDatabase.ImportAsset(review,ImportAssetOptions.ForceUpdate);}
                else if(!AssetDatabase.CopyAsset(path,review))throw new IOException(review);
                var scene=EditorSceneManager.OpenScene(review,OpenSceneMode.Additive);
                try{SceneManager.SetActiveScene(scene);Apply(scene,false,audit);EditorSceneManager.SaveScene(scene);if(name=="Hub")SinglePlayerItemCarrySetup.Generate(scene);}
                finally{EditorSceneManager.CloseScene(scene,true);SceneManager.SetActiveScene(original);}
            }
            Write("prepared.txt",audit+"\nPracticePrefabUpdated=true\nFormalSceneSaved=false");
        }
        private static void ApplyFormal()
        {
            // 两个独立正式Scene拥有各自作者UI；暂时Additive供用户保存，保存后自动恢复仅据点。
            var hub=SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");
            if(!hub.isLoaded)hub=EditorSceneManager.OpenScene("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity",OpenSceneMode.Additive);
            var home=SceneManager.GetSceneByPath("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity");
            if(!home.isLoaded)home=EditorSceneManager.OpenScene("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Home.unity",OpenSceneMode.Additive);
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.SetCurrentGroupName("背包整理与全部拾取按钮");var audit=new StringBuilder();
            try{Apply(hub,true,audit);Apply(home,true,audit);Undo.FlushUndoRecordObjects();Undo.CollapseUndoOperations(group);EditorSceneManager.MarkSceneDirty(hub);EditorSceneManager.MarkSceneDirty(home);SceneManager.SetActiveScene(hub);SessionState.SetBool(PendingHandoff,true);Write("formal-applied.txt",audit+"\nUndo=true\nFormalSceneSaved=false\nRestoreHubAfterUserSave=true");}
            catch{Undo.RevertAllDownToGroup(group);throw;}
        }
        private static void Apply(Scene scene,bool undo,StringBuilder audit)
        {
            int count=0;
            foreach(var root in scene.GetRootGameObjects())foreach(var view in root.GetComponentsInChildren<BackpackWorkspaceView>(true))
            {
                if(!view.enabled||!view.TryGetComponent<SinglePlayerInventoryController>(out var ui)
                    ||new SerializedObject(ui).FindProperty("_backpackWorkspace").objectReferenceValue!=view)continue;
                var data=new SerializedObject(view);var wallet=data.FindProperty("_wallet").objectReferenceValue as Text;if(wallet==null)continue;
                var gold=wallet.transform.parent as RectTransform;var parent=gold.parent as RectTransform;
                if(gold.name!="GoldRegion")throw new InvalidOperationException("钱包父节点不是作者GoldRegion，停止而不猜布局："+gold.name);
                audit.AppendLine(scene.name+" WalletParent="+parent.name+" before="+gold.anchoredPosition+" size="+gold.sizeDelta);
                if(undo){Undo.RecordObject(gold,"钱包宽度");Undo.RecordObject(view,"背包按钮引用");}
                float yMin=gold.anchorMin.y,yMax=gold.anchorMax.y,lo=gold.offsetMin.y,hi=gold.offsetMax.y;
                SetHorizontal(gold,.02f,.39f);
                // Home与Hub的旧金币子节点有不同固定偏移；缩窄父框后将图标和金额约束在框内。
                var logo=gold.Find("Gold_Logo") as RectTransform;
                if(undo){Undo.RecordObject(wallet.rectTransform,"钱包金额区域");if(logo!=null)Undo.RecordObject(logo,"钱包图标区域");}
                if(logo!=null){logo.anchorMin=new Vector2(.03f,.16f);logo.anchorMax=new Vector2(.22f,.84f);logo.offsetMin=logo.offsetMax=Vector2.zero;var logoImage=logo.GetComponent<Image>();if(logoImage!=null){if(undo)Undo.RecordObject(logoImage,"钱包图标比例");logoImage.preserveAspect=true;}}
                wallet.rectTransform.anchorMin=new Vector2(.24f,.08f);wallet.rectTransform.anchorMax=new Vector2(.97f,.92f);wallet.rectTransform.offsetMin=wallet.rectTransform.offsetMax=Vector2.zero;
                if(undo)Undo.RecordObject(wallet,"钱包金额字体适配");wallet.resizeTextForBestFit=true;wallet.resizeTextMinSize=18;wallet.resizeTextMaxSize=36;
                var organize=Button(parent,gold,"OrganizeBackpack","整理背包",.415f,.69f,undo);
                var pickup=Button(parent,gold,"PickupAllNearby","全部拾取",.71f,.985f,undo);
                foreach(var button in new[]{organize,pickup})
                {var rect=(RectTransform)button.transform;rect.anchorMin=new Vector2(rect.anchorMin.x,yMin);rect.anchorMax=new Vector2(rect.anchorMax.x,yMax);rect.offsetMin=new Vector2(0,lo);rect.offsetMax=new Vector2(0,hi);}
                data.FindProperty("_organize").objectReferenceValue=organize;data.FindProperty("_pickupAll").objectReferenceValue=pickup;data.ApplyModifiedPropertiesWithoutUndo();count++;
            }
            if(count!=1)throw new InvalidOperationException("期望1个钱包背包，实际"+count+" @"+scene.path);audit.AppendLine("Buttons="+count*2);
        }
        private static void SetHorizontal(RectTransform rect,float left,float right)
        {rect.anchorMin=new Vector2(left,rect.anchorMin.y);rect.anchorMax=new Vector2(right,rect.anchorMax.y);rect.offsetMin=new Vector2(0,rect.offsetMin.y);rect.offsetMax=new Vector2(0,rect.offsetMax.y);}
        private static Button Button(RectTransform parent,RectTransform gold,string name,string title,float left,float right,bool undo)
        {
            var existing=parent.Find(name);GameObject go;
            if(existing!=null)go=existing.gameObject;
            else
            {
                go=new GameObject(name,typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(Button));SceneManager.MoveGameObjectToScene(go,parent.gameObject.scene);go.transform.SetParent(parent,false);go.layer=5;
                if(undo)Undo.RegisterCreatedObjectUndo(go,"背包便捷按钮");
                var image=go.GetComponent<Image>();var template=gold.GetComponent<Image>();image.sprite=template!=null?template.sprite:null;image.type=Image.Type.Sliced;image.color=Color.white;
                var label=new GameObject("Label",typeof(RectTransform),typeof(CanvasRenderer),typeof(Text));label.transform.SetParent(go.transform,false);label.layer=5;
                var text=label.GetComponent<Text>();var source=gold.GetComponentInChildren<Text>(true);text.font=source.font;text.fontSize=28;text.resizeTextForBestFit=true;text.resizeTextMinSize=18;text.resizeTextMaxSize=28;text.alignment=TextAnchor.MiddleCenter;text.color=Color.white;text.text=title;text.raycastTarget=false;
                var rect=text.rectTransform;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=new Vector2(6,4);rect.offsetMax=new Vector2(-6,-4);
                go.GetComponent<Button>().targetGraphic=image;
            }
            SetHorizontal((RectTransform)go.transform,left,right);return go.GetComponent<Button>();
        }
    }
}
