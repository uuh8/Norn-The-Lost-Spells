using System;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>门Scene路径只作为本机查表键；业务服只接收白名单DungeonId，不接收任意Scene。</summary>
    [CreateAssetMenu(menuName = "Game/Online/Room Profile")]
    public sealed class OnlineRoomProfile : ScriptableObject
    {
        [Serializable] public struct PortalBinding { public string PortalScenePath, DungeonId; }
        [SerializeField] private PortalBinding[] _portals;
        [SerializeField] private GameObject _connectionPrefab;
        [SerializeField] private string _connectionScene, _hubScene;
        public GameObject ConnectionPrefab => _connectionPrefab;
        public string ConnectionScene => _connectionScene;
        public string HubScene => _hubScene;
        public bool TryDungeon(string scene, out string dungeon)
        {
            if (_portals != null) for (int i = 0; i < _portals.Length; i++)
                if (_portals[i].PortalScenePath == scene) { dungeon = _portals[i].DungeonId; return !string.IsNullOrEmpty(dungeon); }
            dungeon = null; return false;
        }
    }
}
