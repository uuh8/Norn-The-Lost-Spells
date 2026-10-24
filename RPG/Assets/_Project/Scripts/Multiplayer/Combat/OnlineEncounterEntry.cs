using Game.Core;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>首次接近战斗区才激活 Boss；水平距离忽略胶囊/地面高度差，避免加载认证直接开战。</summary>
    public static class OnlineEncounterEntry
    {
        public static bool CanActivate(ICombatActorRosterReadOnly roster, Vector3 center, float radius)
        {
            if (roster == null || !float.IsFinite(radius) || radius <= 0 ||
                !float.IsFinite(center.x) || !float.IsFinite(center.z)) return false;
            float squared = radius * radius;
            for (int i = 0; i < roster.Count; i++)
            {
                if (!roster.TryGetActor(i, out var actor) || !actor.IsAlive || !actor.IsConnected || !actor.Targetable) continue;
                float x = actor.Position.x - center.x, z = actor.Position.z - center.z;
                if (x * x + z * z <= squared) return true;
            }
            return false;
        }
    }
}
