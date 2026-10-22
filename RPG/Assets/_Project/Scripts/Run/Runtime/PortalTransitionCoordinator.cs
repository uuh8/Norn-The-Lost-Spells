using System.Collections;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    public interface IPortalVisualSource { int PortalId { get; } }
    public interface IPortalTransitRestorer { void RestorePortalVisual(); }
    /// <summary>先完整播放表现再加载；画黑后再让加载占用主线程，避免卡顿发生在可见场景中。</summary>
    public static class PortalTransitionCoordinator
    {
        public static IEnumerator CloseAndFade(Transform player, Transform portal, int id)
        {
            if (player != null && portal != null) player.GetComponentInChildren<IPortalTraveler>(true)?.BeginPortalTransit(TransitDestination(portal), .6f);
            yield return new WaitForSecondsRealtime(.6f);
            if (id != 0) EventBus<PortalStateChangedEvent>.Publish(new PortalStateChangedEvent { PortalId = id, State = PortalVfxState.Closing });
            yield return new WaitForSecondsRealtime(1);
            EventBus<PortalFadeRequestedEvent>.Publish(new PortalFadeRequestedEvent { Black = true, Duration = .5f });
            yield return new WaitForSecondsRealtime(.55f);
            yield return null; // 至少一个完整黑屏渲染帧后才能启动Async Scene加载。
        }
        public static void Reveal(Transform player)
        {
            if (player != null) player.GetComponentInChildren<IPortalTransitRestorer>(true)?.RestorePortalVisual();
            EventBus<PortalFadeRequestedEvent>.Publish(new PortalFadeRequestedEvent { Black = false, Duration = .5f });
        }
        /// <summary>逻辑根定义地面触发范围；角色视觉应缩入门的可见中心，不能随Collider脚底走。</summary>
        public static Transform TransitDestination(Transform portal, Transform fallback = null)
        {
            if (portal == null) return fallback;
            var center = portal.Find("UnifiedPortalVisual");
            return center != null ? center : fallback != null ? fallback : portal;
        }
        public static void EnsureVisual(Transform portal)
        {
            var existing = portal.Find("UnifiedPortalVisual");
            if (existing != null) { existing.GetComponent<PortalVisualPlacement>()?.Place(portal); return; }
            var prefab = Resources.Load<GameObject>("SinglePlayer/UnifiedPortalVisual"); if (prefab == null) return;
            foreach (var renderer in portal.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            // 尚未迁移的旧Scene仅保留安全兼容；正式Editor迁移会删除这些已替代的节点/组件。
            foreach (var label in portal.GetComponentsInChildren<TextMesh>(true)) label.gameObject.SetActive(false);
            foreach (var behaviour in portal.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour.GetType().FullName == "Game.UI.PortalVfxController") behaviour.enabled = false;
            var visual = Object.Instantiate(prefab, portal, false); visual.name = "UnifiedPortalVisual";
            visual.GetComponent<PortalVisualPlacement>()?.Place(portal);
        }
        public static Vector3 Outside(Transform portal, Transform player)
        {
            Vector3 direction = player.position - portal.position; direction.y = 0;
            if (direction.sqrMagnitude < .01f) direction = -portal.forward;
            Vector3 position = portal.position + direction.normalized * 3;
            if (Physics.Raycast(position + Vector3.up * 5, Vector3.down, out var hit, 15, ~0, QueryTriggerInteraction.Ignore)) position.y = hit.point.y;
            return position;
        }
    }
}
