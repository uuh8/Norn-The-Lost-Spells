using UnityEngine;

namespace Game.Run
{
    /// <summary>直接Play与传送进入共用上下文；Start在sceneLoaded之后，已有工作区时绝不再发初始资产。</summary>
    public sealed class PracticeInventorySceneBootstrap:MonoBehaviour
    {
        [SerializeField] private GameObject _workspacePrefab,_returnPortalPrefab;
        [SerializeField] private ItemCatalog _catalog;
        [SerializeField] private SinglePlayerStartingInventory _startingInventory;
        [SerializeField] private string _hubPath="Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity";
        private void Start()
        {
            if(HubPracticeSession.Current!=null)return;
            HubPracticeSession.BeginDirectPractice(gameObject.scene,_catalog,_startingInventory,_workspacePrefab,_returnPortalPrefab,_hubPath);
        }
    }
}
