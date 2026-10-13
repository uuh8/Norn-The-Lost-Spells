using System;
using System.IO;
using Game.Run;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.EditorTools
{
    /// <summary>离散Editor拍摄，不在游戏中为每件物品创建Camera；所有临时对象归属Preview Scene。</summary>
    public static class ItemIconBaker
    {
        public const string OutputRoot="Assets/_Project/Art/UI/ItemIcons";
        [MenuItem("Tools/Norn/物品/生成缺失的物品图标（模型拍摄）")]
        public static void BakeMissing() => BakeAll(false);
        public static int BakeAll(bool rebuildGenerated=false,Vector3? viewAngle=null,float padding=1.22f,bool wandsOnly=false)
        {
            int count=0;
            foreach(string guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/SinglePlayer"}))
            {
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (wandsOnly && item.Kind != ItemKind.Wand) continue;
                if(item.WorldModel==null || item.Icon!=null && (!rebuildGenerated || !AssetDatabase.GetAssetPath(item.Icon).StartsWith(OutputRoot+"/",StringComparison.Ordinal)))continue;
                string path=OutputRoot+"/"+item.Category+"/"+item.name+".png";EnsureFolder(Path.GetDirectoryName(path).Replace('\\','/'));
                var pixels=Render(item.WorldModel,512,viewAngle??(item.Kind==ItemKind.Wand?new Vector3(18,135,90):new Vector3(18,135,0)),padding);
                Texture2D cropped=null;
                try { if(item.Kind==ItemKind.Wand)cropped=CropTransparentBorder(pixels);File.WriteAllBytes(path,(cropped!=null?cropped:pixels).EncodeToPNG()); }
                finally { UnityEngine.Object.DestroyImmediate(pixels);if(cropped!=null)UnityEngine.Object.DestroyImmediate(cropped); }
                AssetDatabase.ImportAsset(path,ImportAssetOptions.ForceSynchronousImport);
                var importer=(TextureImporter)AssetImporter.GetAtPath(path);importer.textureType=TextureImporterType.Sprite;importer.spriteImportMode=SpriteImportMode.Single;
                importer.sRGBTexture=true;importer.alphaIsTransparency=true;importer.mipmapEnabled=false;importer.filterMode=FilterMode.Bilinear;
                importer.maxTextureSize=512;importer.textureCompression=TextureImporterCompression.Uncompressed;importer.SaveAndReimport();
                Undo.RecordObject(item,"绑定物品拍摄图标");var so=new SerializedObject(item);so.FindProperty("_icon").objectReferenceValue=AssetDatabase.LoadAssetAtPath<Sprite>(path);
                so.ApplyModifiedPropertiesWithoutUndo();AssetDatabase.SaveAssetIfDirty(item);count++;
            }
            return count;
        }
        public static Texture2D Render(GameObject model,int resolution)
            =>Render(model,resolution,new Vector3(18,135,0),1.22f);
        private static Texture2D CropTransparentBorder(Texture2D source)
        {
            // 正交拍摄保留模型比例，之后按Alpha裁边；长条图不再携带大块正方形透明留白。
            // GetPixels32和新Texture只发生在Editor烘焙，不属于Runtime热路径。
            var pixels=source.GetPixels32();int minX=source.width,minY=source.height,maxX=-1,maxY=-1;
            for(int y=0;y<source.height;y++)for(int x=0;x<source.width;x++)
                if(pixels[y*source.width+x].a>8){minX=Mathf.Min(minX,x);minY=Mathf.Min(minY,y);maxX=Mathf.Max(maxX,x);maxY=Mathf.Max(maxY,y);}
            if(maxX<minX)throw new InvalidOperationException("法杖拍摄没有可见像素，保留原绑定并拒绝生成空图。");
            int padding=Mathf.Max(3,Mathf.CeilToInt((maxX-minX+1)*.025f));
            minX=Mathf.Max(0,minX-padding);minY=Mathf.Max(0,minY-padding);maxX=Mathf.Min(source.width-1,maxX+padding);maxY=Mathf.Min(source.height-1,maxY+padding);
            var output=new Texture2D(maxX-minX+1,maxY-minY+1,TextureFormat.RGBA32,false);
            output.SetPixels(source.GetPixels(minX,minY,output.width,output.height));output.Apply();return output;
        }
        public static Texture2D Render(GameObject model,int resolution,Vector3 viewAngle,float padding)
        {
            if(model==null)throw new ArgumentNullException(nameof(model));
            var scene=EditorSceneManager.NewPreviewScene();var old=RenderTexture.active;RenderTexture target=null;
            try
            {
                var visual=(GameObject)PrefabUtility.InstantiatePrefab(model,scene);visual.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                foreach(var behaviour in visual.GetComponentsInChildren<MonoBehaviour>(true))behaviour.enabled=false;
                foreach(var animator in visual.GetComponentsInChildren<Animator>(true))animator.enabled=false;
                foreach(var particles in visual.GetComponentsInChildren<ParticleSystem>(true))particles.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                var renderers=visual.GetComponentsInChildren<Renderer>();if(renderers.Length==0)throw new InvalidOperationException(model.name+"没有可见Renderer。");
                Bounds bounds=renderers[0].bounds;foreach(var renderer in renderers)bounds.Encapsulate(renderer.bounds);
                var cameraObject=new GameObject("Icon camera",typeof(Camera));UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject,scene);
                var camera=cameraObject.GetComponent<Camera>();camera.enabled=false;camera.scene=scene;
                // Preview Scene有独立的Editor可见性位；只设camera.scene不足以让URP RenderRequest看见其Renderer。
                camera.overrideSceneCullingMask=EditorSceneManager.GetSceneCullingMask(scene);
                camera.clearFlags=CameraClearFlags.SolidColor;
                camera.backgroundColor=Color.clear;camera.orthographic=true;camera.aspect=1;camera.nearClipPlane=.001f;
                var rotation=Quaternion.Euler(viewAngle);float distance=Mathf.Max(bounds.size.magnitude*3,1);
                camera.transform.SetPositionAndRotation(bounds.center-rotation*Vector3.forward*distance,rotation);camera.farClipPlane=distance*3;
                // 世界Bounds的角点转入Camera局部坐标，正交取景按XY投影而非世界最大边裁定。
                float extent=0;for(int i=0;i<8;i++)
                {var corner=bounds.center+Vector3.Scale(bounds.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));
                    var local=camera.transform.InverseTransformPoint(corner);extent=Mathf.Max(extent,Mathf.Max(Mathf.Abs(local.x),Mathf.Abs(local.y)));}
                camera.orthographicSize=Mathf.Max(.01f,extent*Mathf.Max(1.01f,padding));
                var data=camera.GetUniversalAdditionalCameraData();data.renderPostProcessing=false;data.renderShadows=false;data.volumeLayerMask=0;
                Light(scene,rotation*Quaternion.Euler(15,-25,0),1.8f);Light(scene,rotation*Quaternion.Euler(-15,45,0),.85f);
                target=new RenderTexture(resolution,resolution,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);target.Create();
                var request=new UniversalRenderPipeline.SingleCameraRequest{destination=target};
                if(!RenderPipeline.SupportsRenderRequest(camera,request))throw new InvalidOperationException("当前URP不支持图标单相机渲染请求。");
                RenderPipeline.SubmitRenderRequest(camera,request);RenderTexture.active=target;
                var image=new Texture2D(resolution,resolution,TextureFormat.RGBA32,false);image.ReadPixels(new Rect(0,0,resolution,resolution),0,0);image.Apply();return image;
            }
            finally {RenderTexture.active=old;if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);}EditorSceneManager.ClosePreviewScene(scene);}
        }
        private static void Light(UnityEngine.SceneManagement.Scene scene,Quaternion rotation,float intensity)
        {var go=new GameObject("Icon light",typeof(Light));UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go,scene);go.transform.rotation=rotation;var light=go.GetComponent<Light>();light.type=LightType.Directional;light.intensity=intensity;light.shadows=LightShadows.None;}
        internal static void EnsureFolder(string path)
        {if(AssetDatabase.IsValidFolder(path))return;EnsureFolder(Path.GetDirectoryName(path).Replace('\\','/'));AssetDatabase.CreateFolder(Path.GetDirectoryName(path).Replace('\\','/'),Path.GetFileName(path));}
    }
}
