using System.Collections.Generic;
using Game.Run;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>点击整数占格画板配置形状；美术尺寸与库存占格是两份独立数据。</summary>
    [CustomEditor(typeof(ItemDefinition))]
    internal sealed class ItemDefinitionEditor:UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();serializedObject.Update();
            var width=serializedObject.FindProperty("_shapeWidth");var height=serializedObject.FindProperty("_shapeHeight");var cells=serializedObject.FindProperty("_occupiedCells");
            if(width.intValue<1 || height.intValue<1 || width.intValue>16 || height.intValue>16)return;
            EditorGUILayout.Space();EditorGUILayout.LabelField("占格画板（左上为0,0；点击切换，空洞可放其他物品）");
            var occupied=new HashSet<Vector2Int>();for(int i=0;i<cells.arraySize;i++)occupied.Add(cells.GetArrayElementAtIndex(i).vector2IntValue);
            bool changed=false;
            for(int y=0;y<height.intValue;y++)
            {EditorGUILayout.BeginHorizontal();for(int x=0;x<width.intValue;x++)
                {var cell=new Vector2Int(x,y);bool value=occupied.Contains(cell),next=GUILayout.Toggle(value,value?"■":"□","Button",GUILayout.Width(25),GUILayout.Height(25));
                    if(value!=next){changed=true;if(next)occupied.Add(cell);else occupied.Remove(cell);}}EditorGUILayout.EndHorizontal();}
            if(changed)
            {cells.arraySize=occupied.Count;int index=0;for(int y=0;y<height.intValue;y++)for(int x=0;x<width.intValue;x++)if(occupied.Contains(new Vector2Int(x,y)))cells.GetArrayElementAtIndex(index++).vector2IntValue=new Vector2Int(x,y);
                serializedObject.ApplyModifiedProperties();}
        }
    }
    [CustomEditor(typeof(WorldItemSpawnPoint)), CanEditMultipleObjects]
    internal sealed class WorldItemSpawnPointEditor:UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Scene Key与库存Session一致。扫描可一次补齐全部空SpawnId；已有非空编号保留。重复ID需选中复制品单独重新编号。编号关联存档，勿重新编号原物品或Apply回共享Prefab。",MessageType.Info);
            // Prefab Asset不拥有具体摆放身份；禁用按钮避免所有实例继承同一个编号。
            bool sceneInstances=true;foreach(var item in targets)if(EditorUtility.IsPersistent(item))sceneInstances=false;
            using(new EditorGUI.DisabledScope(!sceneInstances || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if(GUILayout.Button("扫描当前场景并补齐 SpawnId（Undo，不保存）")) WorldItemSpawnIdTools.ScanCurrentScene();
                if(GUILayout.Button("重新编号选中复制品（保留原点；Undo）"))
                {
                    var points=new WorldItemSpawnPoint[targets.Length];for(int i=0;i<targets.Length;i++)points[i]=(WorldItemSpawnPoint)targets[i];
                    WorldItemSpawnIdTools.RegenerateSelected(points);serializedObject.Update();
                }
            }
            if(targets.Length==1)WorldItemSpawnInspector.Draw((WorldItemSpawnPoint)target);
        }
    }
}
