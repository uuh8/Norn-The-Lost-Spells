using System;
using System.Collections.Generic;
using System.IO;
using Game.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.EditorTools
{
    /// <summary>供应方地图兼容检查。只读检查当前Scene；装配阶段只保存新副本，不处理用户的dirty场景。</summary>
    [InitializeOnLoad]
    internal static class BeffioHubCompatibility
    {
        internal const string Source="Assets/ThirdParty/beffio/2.0 Update Medieval Kingdom Standard Shader/Scenes/Desktop & Mobile/MainScene.unity";
        internal const string Target="Assets/_Project/Scenes/Source/HubEnvironment_Beffio.unity";
        internal const string Candidate="Assets/_Project/Scenes/Tests/Hub/HubCandidate_Beffio_Map1.unity";
        private const string Art="Assets/_Project/Art/Environment/BeffioURP";
        private const string Request="Temp/SinglePlayer/beffio-hub.request";
        internal const string Evidence="TestResults/SinglePlayer/BeffioHub";
        private static string _restoreScene;
        private static int _verifyStage=-1,_warmFrames;
        static BeffioHubCompatibility(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(_verifyStage>=0){PollVerification();return;}
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(EditorUtility.scriptCompilationFailed)throw new InvalidOperationException("编译未通过，不能使用旧Assembly执行兼容处理。");
                if(command=="audit")Audit(SceneManager.GetActiveScene(),"before");
                else if(command=="render-before")Render(SceneManager.GetActiveScene(),"before");
                else if(command=="prepare")Prepare();
                else if(command=="verify")BeginVerification();
                else if(command=="repair-candidate-ground")RepairCandidateGround();
                else if(command=="repair-water-materials")RepairWaterMaterials();
                else if(command=="render-water-repair")Render(SceneManager.GetActiveScene(),"water-repaired");
                else throw new ArgumentException("未知兼容命令："+command);
            }
            catch(Exception e){Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/error.json",JsonUtility.ToJson(new Failure{Error=e.ToString()},true));GameLog.Error(e.ToString(),"BeffioHub");}
        }
        private static void RepairWaterMaterials()
        {
            var shader=Shader.Find("Game/Environment/BeffioDecorativeWaterURP");
            if(shader==null || !shader.isSupported)throw new InvalidOperationException("装饰水URP Shader不可用。");
            var changed=new List<string>();
            foreach(string guid in AssetDatabase.FindAssets("t:Material",new[]{"Assets/ThirdParty/beffio"}))
            {
                string path=AssetDatabase.GUIDToAssetPath(guid);var material=AssetDatabase.LoadAssetAtPath<Material>(path);
                if(material==null || material.shader==null)continue;
                string legacy=material.shader.name;
                if(legacy!="beffio/Medieval_Kingdom/Water" && legacy!="beffio/Medieval_Kingdom/Water_Mobile")continue;
                string backup="Temp/Spells/RuntimeRepairBackup/"+path;Directory.CreateDirectory(Path.GetDirectoryName(backup));
                if(!File.Exists(backup)){File.Copy(path,backup);File.Copy(path+".meta",backup+".meta");}
                // 保留材质GUID与同名Normal/颜色/UV参数，新扩建Prefab也直接得到兼容材质；不重写Scene。
                material.shader=shader;material.SetFloat("_Opacity",.78f);EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssetIfDirty(material);changed.Add(path);
            }
            File.WriteAllLines("Temp/Spells/water-material-repair.txt",changed);
        }

        internal static void Audit(Scene scene,string label)
        {
            var report=new AuditReport{Utc=DateTime.UtcNow.ToString("O"),Scene=scene.path,Dirty=scene.isDirty,ColorSpace=PlayerSettings.colorSpace.ToString()};
            var materials=new Dictionary<Material,MaterialReport>();
            foreach(var root in scene.GetRootGameObjects())
            {
                var rows=root.GetComponentsInChildren<Renderer>(true);var info=new RootReport{Name=root.name,Active=root.activeSelf,Renderers=rows.Length};
                foreach(var row in rows)
                {
                    if(row.gameObject.activeInHierarchy && row.enabled){info.ActiveRenderers++;report.ActiveRenderers++;}
                    foreach(var mat in row.sharedMaterials)
                    {
                        if(mat==null){report.NullMaterialSlots++;continue;}
                        if(!materials.TryGetValue(mat,out var data))
                        {
                            data=new MaterialReport{Path=AssetDatabase.GetAssetPath(mat),Shader=mat.shader!=null?mat.shader.name:"null",Supported=mat.shader!=null && mat.shader.isSupported,
                                Pipeline=mat.GetTag("RenderPipeline",false,"")};materials.Add(mat,data);
                            if(mat.shader!=null)foreach(var message in ShaderUtil.GetShaderMessages(mat.shader))if(message.severity.ToString()=="Error")data.Errors.Add(message.message);
                        }
                        data.Renderers++;if(row.gameObject.activeInHierarchy)data.HierarchyRenderers++;if(row.gameObject.activeInHierarchy && row.enabled)data.ActiveRenderers++;
                        if(data.Shader.StartsWith("beffio/",StringComparison.Ordinal))data.Example=Hierarchy(row.transform);
                    }
                    // Unity组件有特殊的null比较；?.只判断CLR null，会误访问缺失的Unity组件代理。
                    var meshFilter=row.GetComponent<MeshFilter>();var mesh=meshFilter!=null?meshFilter.sharedMesh:null;
                    var materialSlots=row.sharedMaterials;
                    for(int slot=0;slot<materialSlots.Length;slot++)if(materialSlots[slot]==null)
                        report.NullSlots.Add(Hierarchy(row.transform)+" slot="+slot+" active="+(row.enabled && row.gameObject.activeInHierarchy)+" subMeshes="+(mesh!=null?mesh.subMeshCount:0)+" indices="+(mesh!=null && slot<mesh.subMeshCount?mesh.GetIndexCount(slot):0));
                    if(scene.path==Candidate && mesh!=null && row.enabled && row.gameObject.activeInHierarchy && row.bounds.size.x>4 && row.bounds.size.z>4 && row.bounds.size.y<6)
                        report.GroundCandidates.Add(Hierarchy(row.transform)+" mesh="+AssetDatabase.GetAssetPath(mesh)+" bounds="+row.bounds+" ownCollider="+(row.GetComponent<Collider>()!=null));
                    if(row is MeshRenderer)
                    {
                        var filter=row.GetComponent<MeshFilter>();if(filter!=null && filter.sharedMesh!=null)
                        {report.MeshRenderers++;if(row.GetComponent<Collider>()==null)report.MeshRenderersWithoutOwnCollider++;}
                    }
                }
                foreach(var collider in root.GetComponentsInChildren<Collider>(true)){report.Colliders++;if(collider.enabled && collider.gameObject.activeInHierarchy)report.ActiveColliders++;}
                foreach(var transform in root.GetComponentsInChildren<Transform>(true))report.MissingScripts+=GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                foreach(var component in root.GetComponentsInChildren<MonoBehaviour>(true))if(component!=null){var path=AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(component));if(path.StartsWith("Assets/ThirdParty/beffio",StringComparison.Ordinal))report.DemoScripts.Add(Hierarchy(component.transform)+" : "+component.GetType().Name);}
                foreach(var camera in root.GetComponentsInChildren<Camera>(true))report.Cameras.Add(Hierarchy(camera.transform)+" active="+(camera.enabled && camera.gameObject.activeInHierarchy)+" position="+camera.transform.position+" far="+camera.farClipPlane);
                report.Roots.Add(info);
            }
            foreach(var pair in materials)report.Materials.Add(pair.Value);
            // 地面探针只证明这些离散位置存在静态碰撞，不能代替CharacterController走路/台阶/屋内验证。
            Camera review=null;
            foreach(var root in scene.GetRootGameObjects())if(root.name=="Environment Review Camera")review=root.GetComponent<Camera>();
            if(review!=null)
            {
                Physics.SyncTransforms();Vector3 direction=review.transform.forward;
                Vector3 center=review.transform.position+direction*(-review.transform.position.y/direction.y);
                for(int x=-6;x<=6;x++)for(int z=-6;z<=6;z++)
                {
                    Vector3 start=new Vector3(center.x+x*5,100,center.z+z*5);report.CollisionProbeSamples++;
                    var hits=Physics.RaycastAll(start,Vector3.down,200,~0,QueryTriggerInteraction.Ignore);bool found=false;
                    foreach(var hit in hits)if(hit.collider.gameObject.scene==scene){found=true;break;}
                    if(found)report.CollisionProbeHits++;else report.CollisionProbeMisses.Add(start);
                }
            }
            foreach(var pair in materials)
            {
                string path=pair.Value.Path;if(!path.StartsWith("Assets/_Project/Art/Environment/BeffioURP/Materials/",StringComparison.Ordinal))continue;
                int suffix=path.LastIndexOf('_');string guid=path.Substring(suffix+1,path.Length-suffix-5);
                var source=AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if(source==null){report.MaterialCopyIssues.Add(path+" : source missing");continue;}
                for(int i=0;i<source.shader.GetPropertyCount();i++)
                {
                    string property=source.shader.GetPropertyName(i);var copy=pair.Key;
                    if(!copy.HasProperty(property))
                    {
                        // 水面是明确降级的装饰实现；列出未移植功能，地面缺失属性则仍记为错误。
                        if(copy.shader.name=="Game/Environment/BeffioDecorativeWaterURP")report.UnsupportedWaterProperties.Add(property);
                        else report.MaterialCopyIssues.Add(path+" : missing property "+property);
                        continue;
                    }
                    bool equal;
                    switch(source.shader.GetPropertyType(i))
                    {
                        case ShaderPropertyType.Texture: equal=copy.GetTexture(property)==source.GetTexture(property) && copy.GetTextureScale(property)==source.GetTextureScale(property) && copy.GetTextureOffset(property)==source.GetTextureOffset(property);break;
                        case ShaderPropertyType.Color: equal=copy.GetColor(property)==source.GetColor(property);break;
                        case ShaderPropertyType.Vector: equal=copy.GetVector(property)==source.GetVector(property);break;
                        default: equal=copy.GetFloat(property)==source.GetFloat(property);break;
                    }
                    if(!equal)report.MaterialCopyIssues.Add(path+" : changed property "+property);
                }
            }
            Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/"+label+"-audit.json",JsonUtility.ToJson(report,true));
        }
        private static bool Legacy(Material material)=>material!=null && material.shader!=null && material.shader.name.StartsWith("beffio/",StringComparison.Ordinal);
        private static void Folder(string path)
        {if(AssetDatabase.IsValidFolder(path))return;int slash=path.LastIndexOf('/');Folder(path.Substring(0,slash));AssetDatabase.CreateFolder(path.Substring(0,slash),path.Substring(slash+1));}
        private static void Prepare()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("用户Scene未保存；未自动保存或丢弃。");
            if(File.Exists(Target) || File.Exists(Candidate))throw new InvalidOperationException("兼容副本已存在，拒绝覆盖。");
            var ground=Shader.Find("Game/Environment/BeffioGroundURP");var water=Shader.Find("Game/Environment/BeffioDecorativeWaterURP");
            foreach(var shader in new[]{ground,water})
            {if(shader==null)throw new InvalidOperationException("兼容Shader尚未导入。");foreach(var e in ShaderUtil.GetShaderMessages(shader))if(e.severity.ToString()=="Error")throw new InvalidOperationException(e.message);}
            Folder("Assets/_Project/Scenes/SinglePlayer");Folder("Assets/_Project/Art/Environment/BeffioURP/Materials");
            if(!AssetDatabase.CopyAsset(Source,Target))throw new IOException("无法创建Scene副本。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Target,OpenSceneMode.Additive);
            var converted=new Dictionary<Material,Material>();var changes=new Changes{Utc=DateTime.UtcNow.ToString("O")};
            try
            {
                SceneManager.SetActiveScene(scene);
                foreach(var root in scene.GetRootGameObjects())foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var slots=renderer.sharedMaterials;bool changed=false;
                    for(int i=0;i<slots.Length;i++)
                    {
                        var source=slots[i];if(!Legacy(source))continue;
                        Shader replacement=source.shader.name.EndsWith("/GroundBlending",StringComparison.Ordinal)?ground:source.shader.name.EndsWith("/Water",StringComparison.Ordinal)?water:null;
                        if(replacement==null)continue;
                        if(!converted.TryGetValue(source,out var material))
                        {
                            string output="Assets/_Project/Art/Environment/BeffioURP/Materials/"+source.name+"_"+AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source))+".mat";
                            if(File.Exists(output))throw new IOException("材料副本已存在，拒绝覆盖："+output);
                            material=new Material(source){shader=replacement,shaderKeywords=Array.Empty<string>(),renderQueue=-1};
                            if(replacement==water)material.SetFloat("_Opacity",.78f);
                            AssetDatabase.CreateAsset(material,output);converted.Add(source,material);
                            changes.Materials.Add(AssetDatabase.GetAssetPath(source)+" -> "+output);
                        }
                        slots[i]=material;changed=true;
                    }
                    if(changed)renderer.sharedMaterials=slots;
                }
                foreach(var root in scene.GetRootGameObjects())
                {
                    foreach(var group in root.GetComponentsInChildren<LODGroup>(true))
                    {
                        var levels=group.GetLODs();var valid=new List<LOD>();bool broken=false;
                        foreach(var level in levels)
                        {
                            bool compatible=true;
                            foreach(var renderer in level.renderers)if(renderer!=null)foreach(var material in renderer.sharedMaterials)if(Legacy(material))compatible=false;
                            if(compatible)valid.Add(level);else broken=true;
                        }
                        if(!broken || valid.Count==0)continue;
                        // 一些旧LOD2仍带旧模型/材质；保留已转换的低LOD直到原Cull阈值，不换错UV或让远景消失。
                        var last=valid[valid.Count-1];last.screenRelativeTransitionHeight=levels[levels.Length-1].screenRelativeTransitionHeight;valid[valid.Count-1]=last;
                        group.SetLODs(valid.ToArray());changes.LodRepairs.Add(Hierarchy(group.transform)+" "+levels.Length+" -> "+valid.Count);
                    }
                    foreach(var transform in root.GetComponentsInChildren<Transform>(true))
                    {int missing=GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);if(missing>0){changes.MissingScripts.Add(Hierarchy(transform)+" : "+missing);GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);}}
                    foreach(var script in root.GetComponentsInChildren<MonoBehaviour>(true))if(script!=null && script.GetType().Name=="Reflection_pass")
                    {changes.RemovedScripts.Add(Hierarchy(script.transform)+" : legacy Reflection_pass");UnityEngine.Object.DestroyImmediate(script);}
                }
                EditorSceneManager.SaveScene(scene,Target);Audit(scene,"after");
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
            if(!AssetDatabase.CopyAsset(Target,Candidate))throw new IOException("无法创建据点候选。");
            scene=EditorSceneManager.OpenScene(Candidate,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);Camera template=null;
                foreach(var root in scene.GetRootGameObjects())if(root.name=="Cameras_TopView")foreach(var camera in root.GetComponentsInChildren<Camera>(true))if(Hierarchy(camera.transform).Contains("/Map_1/CameraCenter_1/")){template=camera;break;}
                // 临时预览相机只用于新环境副本，不接玩家控制；正式角色/Input/Cinemachine沿原项目后续装配。
                var host=new GameObject("Environment Review Camera");SceneManager.MoveGameObjectToScene(host,scene);var preview=host.AddComponent<Camera>();
                if(template!=null){EditorUtility.CopySerialized(template,preview);preview.transform.SetPositionAndRotation(template.transform.position,template.transform.rotation);}
                else{preview.transform.position=new Vector3(-61,47,52);preview.transform.rotation=Quaternion.Euler(45,140,0);}
                preview.enabled=true;preview.farClipPlane=350;preview.tag="MainCamera";host.AddComponent<UniversalAdditionalCameraData>();
                foreach(var root in scene.GetRootGameObjects())if(root.name.StartsWith("Map_",StringComparison.Ordinal) && root.name!="Map_1" || root.name.StartsWith("Cameras_",StringComparison.Ordinal))UnityEngine.Object.DestroyImmediate(root);
                EditorSceneManager.SaveScene(scene,Candidate);Audit(scene,"candidate");
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
            foreach(var material in converted.Values)AssetDatabase.SaveAssetIfDirty(material);
            Directory.CreateDirectory(Evidence);File.WriteAllText(Evidence+"/changes.json",JsonUtility.ToJson(changes,true));
            // 保存与实际绘制分开，verify在后续Editor帧使用Single Scene，避免提交刚加载的场景。
            BeginVerification();
        }
        private static void BeginVerification()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("用户Scene未保存，拒绝切换。");
            if(SceneManager.sceneCount!=1 || string.IsNullOrEmpty(SceneManager.GetActiveScene().path))throw new InvalidOperationException("验证只支持单个已保存场景，避免改变用户多Scene布局。");
            _restoreScene=SceneManager.GetActiveScene().path;_verifyStage=0;_warmFrames=0;EditorSceneManager.OpenScene(Target,OpenSceneMode.Single);
        }
        private static void RepairCandidateGround()
        {
            for(int i=0;i<SceneManager.sceneCount;i++)if(SceneManager.GetSceneAt(i).isDirty)throw new InvalidOperationException("用户Scene未保存；不能修复副本。");
            if(SceneManager.sceneCount!=1 || SceneManager.GetActiveScene().path==Candidate)throw new InvalidOperationException("先离开候选副本，工具只修改独立加载的项目副本。");
            var original=SceneManager.GetActiveScene();var scene=EditorSceneManager.OpenScene(Candidate,OpenSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(scene);MeshFilter ground=null;
                foreach(var root in scene.GetRootGameObjects())foreach(var filter in root.GetComponentsInChildren<MeshFilter>(true))if(Hierarchy(filter.transform)=="Map_1/Ground_Map_1")ground=filter;
                if(ground==null || ground.sharedMesh==null || !AssetDatabase.GetAssetPath(ground.sharedMesh).EndsWith("/Maps/Ground_Map_1.fbx",StringComparison.Ordinal))throw new InvalidOperationException("候选地面身份不匹配，拒绝猜测或批量增加碰撞。");
                var collider=ground.GetComponent<MeshCollider>();bool added=collider==null;
                // 静态凹地形使用非Convex MeshCollider贴合原有地面；不是会填平地形的Box，也不附加动态Rigidbody。
                if(added){collider=ground.gameObject.AddComponent<MeshCollider>();collider.sharedMesh=ground.sharedMesh;collider.convex=false;collider.isTrigger=false;}
                EditorSceneManager.SaveScene(scene,Candidate);
                File.WriteAllText(Evidence+"/ground-repair.json",JsonUtility.ToJson(new GroundRepair{Utc=DateTime.UtcNow.ToString("O"),Scene=Candidate,Hierarchy=Hierarchy(ground.transform),Mesh=AssetDatabase.GetAssetPath(ground.sharedMesh),Added=added},true));
            }
            finally{EditorSceneManager.CloseScene(scene,true);if(original.IsValid() && original.isLoaded)SceneManager.SetActiveScene(original);}
            BeginVerification();
        }
        private static void PollVerification()
        {
            if(EditorApplication.isCompiling || EditorApplication.isUpdating || ShaderUtil.anythingCompiling)return;
            // 加载Scene和真正绘制隔开Editor帧，避免在OpenScene的同一调用栈中提交未就绪的RenderGraph资源。
            if(_warmFrames++<20)return;
            try
            {
                var scene=SceneManager.GetActiveScene();string label=_verifyStage==0?"after":"candidate";
                Render(scene,label);Audit(scene,label);
                if(_verifyStage==0){_verifyStage=1;_warmFrames=0;EditorSceneManager.OpenScene(Candidate,OpenSceneMode.Single);}
                else{_verifyStage=-1;EditorSceneManager.OpenScene(_restoreScene,OpenSceneMode.Single);}
            }
            catch(Exception e){_verifyStage=-1;File.WriteAllText(Evidence+"/verify-error.json",e.ToString());EditorSceneManager.OpenScene(_restoreScene,OpenSceneMode.Single);GameLog.Error(e.ToString(),"BeffioHub");}
        }
        private static void Render(Scene scene,string label)
        {
            var isolated=EditorSceneManager.NewPreviewScene();RenderTexture target=null;Texture2D image=null;
            var oldTarget=RenderTexture.active;bool oldAsync=ShaderUtil.allowAsyncCompilation;
            try
            {
                // Camera归临时PreviewScene，渲染Scene指向目标；不会先创建到用户Scene再删除而留下dirty。
                var host=new GameObject("Beffio Audit Camera");SceneManager.MoveGameObjectToScene(host,isolated);var camera=host.AddComponent<Camera>();host.AddComponent<UniversalAdditionalCameraData>();camera.enabled=false;camera.scene=scene;camera.overrideSceneCullingMask=EditorSceneManager.GetSceneCullingMask(scene);
                Bounds bounds=default;bool first=true;
                foreach(var root in scene.GetRootGameObjects())foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))if(renderer.gameObject.activeInHierarchy)
                {
                    bool include=root.name.StartsWith("Map_",StringComparison.Ordinal);
                    if(label=="water-repaired")
                    {
                        // 据点扩建可能改组根节点。水面验收按实际材质识别，不能依赖供应方Map_名字。
                        include=false;foreach(var material in renderer.sharedMaterials)
                            if(material!=null && material.shader!=null && material.shader.name=="Game/Environment/BeffioDecorativeWaterURP")include=true;
                    }
                    if(!include)continue;
                    if(first){bounds=renderer.bounds;first=false;}else bounds.Encapsulate(renderer.bounds);
                }
                if(first)throw new InvalidOperationException("地图没有可渲染几何。");
                float span=Mathf.Max(bounds.size.x,bounds.size.z);camera.transform.position=bounds.center+new Vector3(0,span*1.15f,-span*.6f);
                camera.transform.rotation=Quaternion.LookRotation(bounds.center-camera.transform.position);camera.orthographic=true;camera.orthographicSize=span*.60f;camera.nearClipPlane=.3f;camera.farClipPlane=span*4+100;
                // 总览包含数千米外围山体，其Bounds会让城镇缩成像素；使用包内Map1展示Camera推导城镇中心。
                // SceneView Camera在Domain Reload后首次Repaint前可能仍是原点，不能把未更新的编辑器Camera当比较证据。
                Camera reference=null;
                if(label!="candidate" && label!="water-repaired")foreach(var root in scene.GetRootGameObjects())if(root.name=="Cameras_TopView")foreach(var c in root.GetComponentsInChildren<Camera>(true))if(Hierarchy(c.transform).Contains("/Map_1/CameraCenter_1/")){reference=c;break;}
                if(label=="candidate")foreach(var root in scene.GetRootGameObjects())if(root.name=="Environment Review Camera")reference=root.GetComponent<Camera>();
                if(reference!=null)
                {
                    if(label=="candidate"){camera.transform.SetPositionAndRotation(reference.transform.position,reference.transform.rotation);camera.orthographic=reference.orthographic;camera.orthographicSize=reference.orthographicSize;camera.fieldOfView=reference.fieldOfView;camera.nearClipPlane=reference.nearClipPlane;camera.farClipPlane=reference.farClipPlane;}
                    else
                    {Vector3 direction=reference.transform.forward;Vector3 focus=reference.transform.position+direction*(-reference.transform.position.y/direction.y);camera.transform.position=focus+new Vector3(0,220,-150);camera.transform.rotation=Quaternion.LookRotation(focus-camera.transform.position);camera.orthographic=true;camera.orthographicSize=180;camera.nearClipPlane=.3f;camera.farClipPlane=1800;}
                }
                camera.clearFlags=CameraClearFlags.Skybox;camera.allowHDR=false;camera.allowMSAA=false;
                target=new RenderTexture(1280,960,24,RenderTextureFormat.ARGB32);target.Create();
                var request=new UniversalRenderPipeline.SingleCameraRequest{destination=target};
                if(!RenderPipeline.SupportsRenderRequest(camera,request))throw new InvalidOperationException("当前Pipeline不支持URP单相机渲染请求。");
                ShaderUtil.allowAsyncCompilation=false;
                RenderPipeline.SubmitRenderRequest(camera,request);RenderTexture.active=target;image=new Texture2D(1280,960,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,1280,960),0,0);image.Apply();
                int magenta=0,nonblack=0;foreach(var pixel in image.GetPixels32()){if(pixel.r>180 && pixel.b>180 && pixel.g<80)magenta++;if(pixel.r>12 || pixel.g>12 || pixel.b>12)nonblack++;}
                var report=new RenderReport{Utc=DateTime.UtcNow.ToString("O"),Scene=scene.path,Width=image.width,Height=image.height,MagentaPixels=magenta,NonBlackPixels=nonblack,Position=camera.transform.position,Rotation=camera.transform.rotation,Orthographic=camera.orthographic,Fov=camera.fieldOfView,Bounds=bounds};
                Directory.CreateDirectory(Evidence);File.WriteAllBytes(Evidence+"/"+label+".png",image.EncodeToPNG());File.WriteAllText(Evidence+"/"+label+"-render.json",JsonUtility.ToJson(report,true));
            }
            finally{ShaderUtil.allowAsyncCompilation=oldAsync;RenderTexture.active=oldTarget;if(image!=null)UnityEngine.Object.DestroyImmediate(image);if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);}EditorSceneManager.ClosePreviewScene(isolated);}
        }
        private static string Hierarchy(Transform t){string path=t.name;while(t.parent!=null){t=t.parent;path=t.name+"/"+path;}return path;}
        [Serializable]private sealed class Failure{public string Error;}
        [Serializable]private sealed class GroundRepair{public string Utc,Scene,Hierarchy,Mesh;public bool Added;}
        [Serializable]private sealed class AuditReport
        {public string Utc,Scene,ColorSpace;public bool Dirty;public int ActiveRenderers,NullMaterialSlots,MeshRenderers,MeshRenderersWithoutOwnCollider,Colliders,ActiveColliders,MissingScripts,CollisionProbeSamples,CollisionProbeHits;public List<RootReport> Roots=new List<RootReport>();public List<MaterialReport> Materials=new List<MaterialReport>();public List<string> DemoScripts=new List<string>(),Cameras=new List<string>(),NullSlots=new List<string>(),MaterialCopyIssues=new List<string>(),UnsupportedWaterProperties=new List<string>(),GroundCandidates=new List<string>();public List<Vector3> CollisionProbeMisses=new List<Vector3>();}
        [Serializable]private sealed class RootReport{public string Name;public bool Active;public int Renderers,ActiveRenderers;}
        [Serializable]private sealed class MaterialReport{public string Path,Shader,Pipeline,Example;public bool Supported;public int Renderers,HierarchyRenderers,ActiveRenderers;public List<string> Errors=new List<string>();}
        [Serializable]private sealed class Changes{public string Utc;public List<string> Materials=new List<string>(),LodRepairs=new List<string>(),MissingScripts=new List<string>(),RemovedScripts=new List<string>();}
        [Serializable]private sealed class RenderReport{public string Utc,Scene;public int Width,Height,MagentaPixels,NonBlackPixels;public Vector3 Position;public Quaternion Rotation;public bool Orthographic;public float Fov;public Bounds Bounds;}
    }
}
