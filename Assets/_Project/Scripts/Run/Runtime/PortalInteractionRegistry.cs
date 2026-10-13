using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;
using Game.Core;

namespace Game.Run
{
    /// <summary>门只提供空间资格和显式命令；Character采集输入、UI只读提示共享这份条件。</summary>
    public interface IPortalInteractionTarget
    {
        Transform PortalTransform { get; }
        string InteractionLabel { get; }
        bool CanInteract(Collider actor);
        bool Interact(Collider actor);
    }

    public static class PortalInteractionRegistry
    {
        private static readonly List<IPortalInteractionTarget> Targets = new List<IPortalInteractionTarget>(16);
        private static readonly ProfilerMarker QueryMarker = new ProfilerMarker("Norn.Portal.InteractionQuery");
        public static void Register(IPortalInteractionTarget target)
        { if (Targets.Contains(target)) return; if (Targets.Count == 0) EventBus<PortalInteractionRequestedEvent>.Subscribe(OnRequested); Targets.Add(target); }
        public static void Unregister(IPortalInteractionTarget target)
        { Targets.Remove(target); if (Targets.Count == 0) EventBus<PortalInteractionRequestedEvent>.Unsubscribe(OnRequested); }
        // 禁用Domain Reload的试玩也不能留下上一轮场景的门引用。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() { EventBus<PortalInteractionRequestedEvent>.Unsubscribe(OnRequested); Targets.Clear(); }
        private static void OnRequested(PortalInteractionRequestedEvent command)
        {
            for (int i = 0; i < Targets.Count; i++)
            {
                var target = Targets[i];
                if (target.PortalTransform != null && target.PortalTransform.gameObject.GetInstanceID() == command.PortalId)
                { if (target.CanInteract(command.Actor)) target.Interact(command.Actor); return; }
            }
        }
        public static bool TryGetNearest(Collider actor, out IPortalInteractionTarget target)
        {
            using var sample = QueryMarker.Auto(); target = null;
            if (actor == null || !actor.enabled || !actor.gameObject.activeInHierarchy) return false;
            float best = float.PositiveInfinity;
            for (int i = 0; i < Targets.Count; i++)
            {
                var candidate = Targets[i];
                if (candidate.PortalTransform == null || !candidate.CanInteract(actor)) continue;
                float distance = (candidate.PortalTransform.position - actor.transform.position).sqrMagnitude;
                if (distance >= best) continue;
                best = distance; target = candidate;
            }
            return target != null;
        }
        public static bool Contains(Collider trigger, Collider actor)
            => trigger != null && trigger.enabled && trigger.gameObject.activeInHierarchy && actor != null
                && (trigger.ClosestPoint(actor.bounds.center) - actor.bounds.center).sqrMagnitude < .04f;
    }
}
