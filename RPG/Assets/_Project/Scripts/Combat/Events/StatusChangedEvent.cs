using UnityEngine;
using Game.Core;

namespace Game.Combat
{
    public struct StatusChangedEvent : IGameEvent
    {
        public int TargetId;
        public StatusKind Kind;
        public float Intensity;
        public bool IsActive;
        public Sprite Icon;
        public string DisplayName;
    }
}
