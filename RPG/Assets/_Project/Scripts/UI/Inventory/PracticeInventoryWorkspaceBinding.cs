using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.UI
{
    /// <summary>UI单向读取Run工作区上下文；Awake先于库存OnEnable，跨Scene引用不会存进Prefab。</summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class PracticeInventoryWorkspaceBinding : MonoBehaviour
    {
        [SerializeField] private PracticeInventoryWorkspace _workspace;
        [SerializeField] private SinglePlayerInventoryController _inventory;
        [SerializeField] private GameObject _eventSystem;
        [SerializeField] private HeldItemHud _heldHud;
        [SerializeField] private ContainerCompositionView _containerHud;
        [SerializeField] private Canvas _containerCanvas;
        private CombatPresentationScope _combatPresentation;
        private bool _configured;
        private bool _accountPresentationDirty;
        private void Awake()
        {
            // 场景只装配携带背包；旧法杖编辑器已在 Authoring 迁移时移除，不再运行时互相禁用。
            _inventory.ConfigurePracticeWorkspace(_workspace.Session,_workspace.Nearby,_workspace.Pause);
            foreach(var root in gameObject.scene.GetRootGameObjects())
            {var menu=root.GetComponentInChildren<DemoPauseMenuController>(true);if(menu!=null)menu.ConfigurePracticeInventory(_inventory);}
            if(_heldHud!=null)_heldHud.ConfigureVisibility(_inventory,null,null,_heldHud.GetComponentInParent<Canvas>(true));
            if(_eventSystem!=null)_eventSystem.SetActive(EventSystem.current==null);
            _configured = true;
        }
        private void OnEnable()
        {
            EventBus<OnlineAccountInventoryHubChangedEvent>.Subscribe(OnAccountInventoryChanged);
            if (_configured) BindCombatPresentation();
        }
        private void OnDisable()
        {
            EventBus<OnlineAccountInventoryHubChangedEvent>.Unsubscribe(OnAccountInventoryChanged);
            if (_combatPresentation != null) _combatPresentation.Clear();
        }
        private void OnAccountInventoryChanged(OnlineAccountInventoryHubChangedEvent change)
        { if (change.SceneHandle == gameObject.scene.handle) _accountPresentationDirty = true; }

        /// <summary>
        /// 所有使用共同练习工作区的地图都经过这里，不按 P7/P8 名字分支。Scope 留在场景中，
        /// 只有 Host 的纯 HUD 常驻；不把 Session/Player reparent 到 DontDestroyOnLoad 树。
        /// </summary>
        private void BindCombatPresentation()
        {
            if (_workspace == null || _workspace.Player == null || _workspace.Session == null) return;
            if (_combatPresentation == null)
                _combatPresentation = GetComponent<CombatPresentationScope>() ?? gameObject.AddComponent<CombatPresentationScope>();
            var actor = _workspace.Player.gameObject;
            var ability = actor.GetComponent<ICombatActorPresentation>();
            _combatPresentation.Bind(new CombatPresentationContext
            {
                Actor = actor, WorldScene = actor.scene, InventorySource = _inventory.PresentationSource,
                InventoryController = _inventory, CrosshairVisible = ability != null && ability.DefaultCrosshairVisible
            });
        }
        private void Start()
        {
            // Start 是另一条保险：工作区 Configure 可能晚于组件首次启用；重复 Context 由 Host 幂等处理。
            BindCombatPresentation();
            var tools=_inventory.PresentationSource?.ToolbeltReadOnly;
            // 此显示与角色同步均读取同一Run快照；UI不引用Character的采集控制器。
            if(_containerHud!=null && tools!=null && tools.SelectedSlot>=3 && tools.TryGetBinding(tools.SelectedSlot-3,out var item))_containerHud.Select(item);
        }
        private void Update()
        {
            // 等所有消费者处理完同一离散事件再重绑HUD，不依赖EventBus订阅先后，也不每帧创建Context。
            if (_accountPresentationDirty) { _accountPresentationDirty = false; BindCombatPresentation(); }
            if(_containerCanvas!=null && _containerCanvas.enabled==_inventory.IsOpen)_containerCanvas.enabled=!_inventory.IsOpen;
        }
    }
}
