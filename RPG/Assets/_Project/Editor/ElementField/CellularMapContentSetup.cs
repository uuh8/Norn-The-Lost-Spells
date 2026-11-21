#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Game.ElementField;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>正式单机修复工具与在线导出共用地图清单。只允许指定地板、天然岩体和原 Terrain；
    /// 地图摘要包含源 Scene 与实际 Mesh/TerrainData 字节及导入设置，导出对象的临时 InstanceID 不参与比较。</summary>
    internal static class CellularMapContentSetup
    {
        private const string ProfilePath = "Assets/_Project/ScriptableObjects/ElementField/CellularWorld.asset";
        private static readonly string[] RockPaths = { "__Level/Arena_01_Basics/Obstacles/P_Rock_05_Summer",
            "__Level/Arena_01_Basics/Obstacles/P_Rock_09_Summer", "__Level/Arena_03_Programming/Obstacles/P_Rock_06_Summer",
            "__Level/Arena_03_Programming/Obstacles/P_Rock_10_Summer" };
        private static readonly Regex ContentFields = new Regex(
            @"^  _cellular(ContentProfile|MapContentId|GeometryDigest|GroundBox|HeightTerrain|RockPaths):[^\n]*\n(?:  - __Level/[^\n]*\n)*",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        internal static bool Apply(ElementWorldRuntime world, string id, string scenePath, GameObject exportRoot = null)
        {
            var settings = new SerializedObject(world);
            var profile = AssetDatabase.LoadAssetAtPath<ElementCellWorldProfile>(ProfilePath);
            if (profile == null) throw new InvalidOperationException("缺少正式材料基底 Profile：" + ProfilePath);
            GameObject[] roots = exportRoot != null ? new[] { exportRoot } : world.gameObject.scene.GetRootGameObjects();
            BoxCollider ground = null; Terrain terrain = null;
            var dependencies = new SortedDictionary<string,string>(StringComparer.Ordinal);
            if (id == "home")
            {
                foreach (var root in roots) foreach (var box in root.GetComponentsInChildren<BoxCollider>(true))
                    if (box.name.StartsWith("HomeGround_",StringComparison.Ordinal))
                    {
                        if (ground != null || box.isTrigger) throw new InvalidOperationException("Home 自然平地不唯一或不是实体。");
                        ground = box;
                    }
                if (ground == null) throw new InvalidOperationException("Home 缺少原地板。");
            }
            else if (id == "p8")
            {
                foreach (var root in roots) foreach (var item in root.GetComponentsInChildren<Terrain>(true))
                {
                    if (terrain != null) throw new InvalidOperationException("P8 Terrain 不唯一。");
                    terrain = item;
                }
                if (terrain == null || terrain.terrainData == null) throw new InvalidOperationException("P8 缺少原 TerrainData。");
                Add(dependencies, terrain.terrainData);
            }
            else if (id == "p7")
            {
                foreach (var path in RockPaths)
                {
                    Transform stone = null;
                    foreach (var root in roots)
                    {
                        stone = root.transform.Find(path);
                        if (stone == null && path.StartsWith(root.name + "/", StringComparison.Ordinal))
                            stone = root.transform.Find(path.Substring(root.name.Length + 1));
                        if (stone != null) break;
                    }
                    var shape = stone != null ? stone.GetComponent<MeshCollider>() : null;
                    if (shape == null || shape.sharedMesh == null) throw new InvalidOperationException("P7 原生岩体缺少 MeshCollider：" + path);
                    Add(dependencies, shape.sharedMesh);
                }
            }
            else throw new ArgumentOutOfRangeException(nameof(id));

            string source = ContentFields.Replace(File.ReadAllText(scenePath).Replace("\r\n","\n"), "");
            var body = new StringBuilder("NornCellularMap2\n").Append(id).Append('\n').Append(Hash(Encoding.UTF8.GetBytes(source))).Append('\n');
            foreach (var pair in dependencies) body.Append(pair.Key).Append('\n').Append(Hash(File.ReadAllBytes(pair.Value))).Append('\n')
                .Append(Hash(File.ReadAllBytes(pair.Value+".meta"))).Append('\n');
            settings.FindProperty("_cellularContentProfile").objectReferenceValue = profile;
            settings.FindProperty("_cellularMapContentId").stringValue = id;
            settings.FindProperty("_cellularGeometryDigest").stringValue = Hash(Encoding.UTF8.GetBytes(body.ToString()));
            settings.FindProperty("_cellularGroundBox").objectReferenceValue = ground;
            settings.FindProperty("_cellularHeightTerrain").objectReferenceValue = terrain;
            var paths = settings.FindProperty("_cellularRockPaths"); paths.arraySize = id == "p7" ? RockPaths.Length : 0;
            for (int i = 0; i < paths.arraySize; i++) paths.GetArrayElementAtIndex(i).stringValue = RockPaths[i];
            return settings.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void SaveAuthoredScene(Scene scene, ElementWorldRuntime world, string id)
        {
            // Layout、碰撞和玩家引用可能在作者工具的同一次操作中改变。磁盘上的上一个版本不能代表
            // 导出器下一次会读取的 Scene。先完成该工具原本就要执行的保存，再对已写出的字节定稿。
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("地图保存失败：" + scene.path);
            if (world == null || !Apply(world, id, scene.path)) return;
            // 摘要的输入已排除所有 _cellular 配置字段，因此第二次保存不会让摘要自身递归变化。
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("地图材料摘要保存失败：" + scene.path);
        }

        private static void Add(SortedDictionary<string,string> inputs, UnityEngine.Object source)
        {
            string path = AssetDatabase.GetAssetPath(source);
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !File.Exists(path+".meta"))
                throw new InvalidOperationException("无法确定自然几何的源文件。");
            inputs[AssetDatabase.AssetPathToGUID(path)] = path;
        }
        private static string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();
        }
    }
}
#endif
