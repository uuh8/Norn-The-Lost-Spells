using System;
using UnityEngine;
namespace Game.Run
{
    [CreateAssetMenu(menuName = "Norn/Run/Spell Loot")]
    public sealed class SpellLootTable : ScriptableObject
    {
        [Range(0, 1)] public float DropChance = .35f;
        public Entry[] Entries = Array.Empty<Entry>();
        [Serializable] public struct Entry { public string ContentId; [Min(1)] public int Weight; public int Charges; public bool Unlimited; }
    }
}
