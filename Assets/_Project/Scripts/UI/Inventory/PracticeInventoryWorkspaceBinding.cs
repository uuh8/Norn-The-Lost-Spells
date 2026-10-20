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
        private void Awake()
        {
            // 场景只装配携带背包；旧法杖编辑器已在 Authoring 迁移时移除，不再运行时互相禁用。
            _inventory.ConfigurePracticeWorkspace(_workspace.Session,_workspace.Nearby,_workspace.Pause);
            foreach(var root in gameObject.scene.GetRootGameObjects())
            {var menu=root.GetComponentInChildren<DemoPauseMenuController>(true);if(menu!=null)menu.ConfigurePracticeInventory(_inventory);}
            if(_heldHud!=null)_heldHud.ConfigureVisibility(_inventory,null,null,_heldHud.GetComponentInParent<Canvas>(true));
            if(_eventSystem!=null)_eventSystem.SetActive(EventSystem.current==null);
        }
        private void Start()
        {
            var tools=_workspace.Session.ToolbeltReadOnly;
            // 此显示与角色同步均读取同一Run快照；UI不引用Character的采集控制器。
            if(_containerHud!=null && tools.SelectedSlot>=3 && tools.TryGetBinding(tools.SelectedSlot-3,out var item))_containerHud.Select(item);
        }
        private void Update()
        {if(_containerCanvas!=null && _containerCanvas.enabled==_inventory.IsOpen)_containerCanvas.enabled=!_inventory.IsOpen;}
    }
}
