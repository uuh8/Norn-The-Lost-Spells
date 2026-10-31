using Game.Combat;
using Game.Core;
using UnityEngine;

namespace Game.UI
{
    /// <summary>给原单机 HUD 换只读角色来源；布局、美术和平滑继续由同一批 Player*Bar 负责。</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class LocalActorHudBinding : MonoBehaviour
    {
        [SerializeField] private PlayerHealthBar _healthBar;
        [SerializeField] private PlayerManaBar _manaBar;
        [SerializeField] private PlayerStatusBar _statusBar;

#if UNITY_SERVER && !UNITY_EDITOR
        // 同一 OnlineRoom 也用于 DS 构建；服务器不启动字体/Canvas 等 Client 表现生命周期。
        private void Awake() => gameObject.SetActive(false);
#endif

        private void OnEnable()
        {
            EventBus<LocalActorPresentationEvent>.Subscribe(OnActor);
            // 普通单机场景没有线上绑定，保留它自己的 Inspector 数据源。
            if (LocalActorPresentation.Actor != null) Bind(LocalActorPresentation.Actor);
        }
        private void OnDisable() => EventBus<LocalActorPresentationEvent>.Unsubscribe(OnActor);
        private void OnActor(LocalActorPresentationEvent state) => Bind(state.Actor);

        public void Bind(GameObject actor)
        {
            var health = actor != null ? actor.GetComponent<HealthComponent>() : null;
            if (_healthBar != null) _healthBar.Bind(health);
            if (_manaBar != null) _manaBar.Bind(actor != null ? actor.GetComponent<ManaComponent>() : null);
            if (_statusBar != null) _statusBar.Bind(health);
        }
    }
}
