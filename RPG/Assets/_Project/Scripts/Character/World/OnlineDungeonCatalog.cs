using System;
using UnityEngine;

namespace Game.Character
{
    /// <summary>
    /// 共同Resources中的副本白名单。格式正确的ID也必须在此表唯一注册；找不到时拒绝，
    /// 不给未知地图回退到MovementWorld，也不接受网络传来的任意Scene/Prefab路径。
    /// </summary>
    [CreateAssetMenu(menuName = "Norn/Online/Dungeon Catalog")]
    public sealed class OnlineDungeonCatalog : ScriptableObject
    {
        public const string ResourcePath = "Online/DungeonCatalog";
        [SerializeField] private OnlineDungeonDefinition[] _dungeons = Array.Empty<OnlineDungeonDefinition>();
        public int Count => _dungeons != null ? _dungeons.Length : 0;
        public bool TryGet(string id, out OnlineDungeonDefinition definition)
        {
            definition = null;
            if (!OnlineDungeonDefinition.IsValidDungeonId(id) || _dungeons == null) return false;
            for (int i = 0; i < _dungeons.Length; i++)
            {
                var entry = _dungeons[i];
                if (entry == null || !string.Equals(entry.DungeonId, id, StringComparison.Ordinal)) continue;
                if (definition != null) { definition = null; return false; }
                definition = entry;
            }
            // 查找同样拒绝无效目标，避免某个组合调用者漏掉整表Validate时拿到半份配置。
            if (definition != null && definition.TryValidate(out _)) return true;
            definition = null; return false;
        }
        public bool TryValidate(out string error)
        {
            error = null;
            if (_dungeons == null || _dungeons.Length == 0) { error = "副本目录为空。"; return false; }
            for (int i = 0; i < _dungeons.Length; i++)
            {
                var entry = _dungeons[i];
                if (entry == null) { error = "副本目录有空引用。"; return false; }
                if (!entry.TryValidate(out error)) return false;
                for (int j = 0; j < i; j++) if (string.Equals(_dungeons[j].DungeonId, entry.DungeonId, StringComparison.Ordinal))
                { error = "副本目录ID重复，拒绝有歧义的地图选择。"; return false; }
            }
            return true;
        }
    }
}
