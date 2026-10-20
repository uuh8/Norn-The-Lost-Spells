using System.Collections.Generic;
using Game.Rendering;
using Game.Run;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>硬边模型的原法线决定光照，只有派生外壳Mesh才合并同位置法线。</summary>
    internal static class WorldItemOutlineBaker
    {
        internal static void Bake(WorldItemHighlightProfile profile)
        {
            const string folder="Assets/_Project/Art/Meshes/ItemOutlines";
            ItemIconBaker.EnsureFolder(folder);
            var meshes=new HashSet<Mesh>();var entries=new List<WorldItemHighlightProfile.OutlineMeshEntry>();
            foreach(string guid in AssetDatabase.FindAssets("t:ItemDefinition",new[]{"Assets/_Project/ScriptableObjects/"}))
            {
                var item=AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));if(item.WorldModel==null)continue;
                foreach(var filter in item.WorldModel.GetComponentsInChildren<MeshFilter>(true))
                {
                    var source=filter.sharedMesh;if(source==null || !meshes.Add(source))continue;
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source,out string meshGuid,out long localId);
                    string path="Assets/_Project/Art/Meshes/ItemOutlines/"+meshGuid+"_"+localId+".asset";
                    var shell=AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if(shell==null)
                    {
                        // 仅Editor访问模型CPU数据，输出持久Mesh供Runtime只读使用。
                        shell=Object.Instantiate(source);shell.name=source.name+"_Outline";
                        var vertices=source.vertices;var normals=source.normals;var sums=new Dictionary<Vector3,Vector3>();
                        for(int i=0;i<vertices.Length;i++){sums.TryGetValue(vertices[i],out var sum);sums[vertices[i]]=sum+normals[i];}
                        for(int i=0;i<vertices.Length;i++)normals[i]=sums[vertices[i]].normalized;
                        shell.normals=normals;AssetDatabase.CreateAsset(shell,path);
                    }
                    entries.Add(new WorldItemHighlightProfile.OutlineMeshEntry{Source=source,Shell=shell});
                }
            }
            profile.OutlineMeshes=entries.ToArray();profile.FallbackShader=Shader.Find("Norn/ItemPickupRim");profile.MaskShader=Shader.Find("Norn/ItemPickupMask");
            EditorUtility.SetDirty(profile);AssetDatabase.SaveAssetIfDirty(profile);
        }
    }
}
