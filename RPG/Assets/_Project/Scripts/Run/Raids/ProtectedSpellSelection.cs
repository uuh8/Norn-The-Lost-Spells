using System;
using System.Collections.Generic;
namespace Game.Run
{
    /// <summary>出发前按具体实例选名额；同名定义和新搜获副本都不会自动继承保护。</summary>
    public sealed class ProtectedSpellSelection
    {
        private readonly HashSet<string> _ids = new HashSet<string>(StringComparer.Ordinal);
        public int Count => _ids.Count;
        public bool Contains(string id) => id != null && _ids.Contains(id);
        public bool Select(SinglePlayerProfile profile, string id, bool selected, int slots = 2)
        {
            if (profile == null || profile.PendingRaid != null || !profile.Items.TryGet(id, out var item)
                || !profile.Items.Catalog.TryGet(item.ContentId, out var definition) || definition.Kind != ItemKind.Spell) return false;
            if (!selected) return _ids.Remove(id);
            if (_ids.Contains(id)) return true;
            if (_ids.Count >= slots) return false;
            return _ids.Add(id);
        }
        public string[] CopyIds() { var result = new string[_ids.Count]; _ids.CopyTo(result); return result; }
        public void Clear() => _ids.Clear();
    }
}
