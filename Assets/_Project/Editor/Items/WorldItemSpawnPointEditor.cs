using Game.Run;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>解释作者模板、唯一实例和Runtime代理三者关系；只定位，不改资产或保存场景。</summary>
    internal static class WorldItemSpawnInspector
    {
        internal static void Draw(WorldItemSpawnPoint point)
        {
            EditorGUILayout.Space();EditorGUILayout.HelpBox("这是场景初始投放模板。SpawnId只决定初次发放；拾取后同一InstanceId进入背包数据，丢弃在WorldItems_Runtime重建模型。Test保留初始布局，Play不会反向保存.unity。",MessageType.Info);
            if(point.Definition!=null)
            {
                using(new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("可拾取Prefab默认来源",point.Definition.PickupPrefab,typeof(GameObject),false);
                    EditorGUILayout.Vector3Field("当前作者世界Scale",point.transform.lossyScale);
                }
                if(!Application.isPlaying)EditorGUILayout.HelpBox("Scene实例Override按Unity规则覆盖Prefab。想让所有同类更新，请改Pickup Prefab；如果实例已有Scale Override，先检查Overrides再决定是否Revert。不要为调整外观更换SpawnId。",MessageType.None);
            }
            if(!Application.isPlaying)return;
            EditorGUILayout.LabelField("Runtime InstanceId",point.RuntimeInstanceId??"尚未发放 / 配置被拒绝");
            if(point.TryGetRuntimeLocation(out var location))EditorGUILayout.LabelField("当前资产位置",location.Kind+" / "+location.OwnerId);
            else EditorGUILayout.LabelField("当前资产位置","未注册，或已出售/结算移除");
            EditorGUILayout.LabelField("作者模型隐藏",point.AuthorVisualHidden?"已隐藏，可拾取模型在Runtime层级":"保留（未注册或这是禁用的视觉副本）");
            if(GUILayout.Button("定位对应运行时拾取物"))
            {
                WorldItemPickup match=null;
                foreach(var root in point.gameObject.scene.GetRootGameObjects())foreach(var session in root.GetComponentsInChildren<SinglePlayerInventorySession>(true))
                {match=session.FindWorldProxy(point.RuntimeInstanceId);if(match!=null)break;}
                if(match!=null){Selection.activeGameObject=match.gameObject;EditorGUIUtility.PingObject(match.gameObject);}
                else EditorUtility.DisplayDialog("当前没有地面模型","背包/仓库内的物品保存为数据，不会额外藏一个3D模型；丢弃后才在WorldItems_Runtime显示。","知道了");
            }
        }
    }
    [CustomEditor(typeof(SinglePlayerInventorySession))]
    internal sealed class SinglePlayerInventorySessionEditor:Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();var session=(SinglePlayerInventorySession)target;EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Editor默认Scene Preview：每次Play使用独立临时资产，从Starting Inventory和当前场景布局开始。同轮场景往返保留状态。Saved Profile用于正式保存验证；Build始终使用存档。",MessageType.Info);
            if(!Application.isPlaying)return;
            EditorGUILayout.LabelField("当前运行模式",session.ProfileModeDescription);
            EditorGUILayout.LabelField(session.IsScenePreview?"内存键（不写磁盘）":"存档路径",session.ProfileStoragePath??"尚未初始化");
        }
    }
}
