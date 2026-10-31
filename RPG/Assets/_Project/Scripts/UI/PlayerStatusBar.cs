using UnityEngine;
using Game.Core;
using Game.Combat;

namespace Game.UI
{
    public class PlayerStatusBar : MonoBehaviour, ICombatHudElement
    {
        public GameObject PresentationRoot => gameObject;
        [SerializeField] private HealthComponent _playerHealth;
        [SerializeField] private StatusIconView _burningView;
        [SerializeField] private StatusIconView _wetView;
        [SerializeField] private StatusIconView _poisonedView;
        [SerializeField] private StatusIconView _stickyView;

        // Tracker 只在组件创建时分配一次；StatusChangedEvent 的强度刷新热路径保持 Zero-GC。
        private readonly StatusIconAcquisitionOrder _acquisitionOrder =
            new StatusIconAcquisitionOrder();

        public void Bind(HealthComponent health)
        {
            ClearAll(); _playerHealth = health;
            if (health == null) return;
            var status = health.GetComponent<StatusController>();
            if (status == null) return;
            // 不等下一次强度变化事件：HUD 可能在首份网络快照之后才加载。
            for (int i = 0; i < 4; i++)
            {
                var kind = (StatusKind)i;
                if (!status.TryGetIntensity(kind, out float intensity)) continue;
                OnStatusChanged(new StatusChangedEvent { TargetId = health.Id, Kind = kind,
                    IsActive = true, Intensity = intensity, Icon = status.GetIcon(kind) });
            }
        }

        private void Awake()
        {
            // Awake 发生在 OnEnable 订阅之前：先清理场景序列化留下的占位显示，之后收到的
            // StatusChangedEvent 就是 Runtime Truth，不能再被较晚执行的 Start 覆盖。
            // 父 HUD 的 OnEnable 可能已绑定新角色；不能把先到的 Replica 状态又清成空白。
            Bind(_playerHealth);
        }

        private void OnEnable()
        {
            EventBus<StatusChangedEvent>.Subscribe(OnStatusChanged);
        }

        private void OnDisable()
        {
            EventBus<StatusChangedEvent>.Unsubscribe(OnStatusChanged);
        }

        private void OnStatusChanged(StatusChangedEvent e)
        {
            if (_playerHealth == null || e.TargetId != _playerHealth.Id)
                return;

            StatusIconView view = GetView(e.Kind);
            if (view == null)
                return;

            if (e.IsActive)
            {
                // 只有状态真正从 inactive 变成 active 才移动层级；同一状态的百分比刷新
                // 不会反复 SetAsLastSibling，因此 HorizontalLayoutGroup 不会左右抖动。
                if (_acquisitionOrder.Activate(e.Kind))
                    view.transform.SetAsLastSibling();

                view.Set(e.Icon, e.Intensity);
            }
            else
            {
                _acquisitionOrder.Deactivate(e.Kind);
                view.Clear();
            }
        }

        private void ClearAll()
        {
            _acquisitionOrder.Clear();

            if (_burningView != null)
                _burningView.Clear();
            if (_wetView != null)
                _wetView.Clear();
            if (_poisonedView != null)
                _poisonedView.Clear();
            if (_stickyView != null)
                _stickyView.Clear();
        }

        private StatusIconView GetView(StatusKind kind)
        {
            switch (kind)
            {
                case StatusKind.Burning:
                    return _burningView;
                case StatusKind.Wet:
                    return _wetView;
                case StatusKind.Poisoned:
                    return _poisonedView;
                case StatusKind.Sticky:
                    return _stickyView;
                default:
                    return null;
            }
        }
    }
}
