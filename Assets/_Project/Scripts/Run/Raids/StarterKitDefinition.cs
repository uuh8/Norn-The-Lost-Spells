using System;
using UnityEngine;
namespace Game.Run
{
    [CreateAssetMenu(menuName = "Norn/Run/Starter Kit")]
    public sealed class StarterKitDefinition : ScriptableObject
    {
        public Entry[] Entries = Array.Empty<Entry>();
        [Serializable] public struct Entry { public string ContentId; public int Charges; public bool Unlimited; }
    }
}
