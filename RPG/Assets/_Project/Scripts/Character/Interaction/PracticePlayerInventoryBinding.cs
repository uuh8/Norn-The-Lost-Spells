using Game.Run;
using Game.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Character
{
    /// <summary>初始化桥接属于Character：Run只暴露工作区，不反向调用角色或UI。绑定是一次离散操作。</summary>
    public sealed class PracticePlayerInventoryBinding:MonoBehaviour
    {
        [SerializeField] private PracticeInventoryWorkspace _workspace;
        [SerializeField] private InputActionReference _select1,_select2,_select3;
        [SerializeField] private ContainerUseDefinition _containerDefinition;
        [SerializeField] private string _solidContentId;
        [SerializeField] private Material _materialWaitingMaterial;
        private void Awake()
        {
            var player=_workspace.Player; if(player==null)return;
            var held=GetOrAdd<HeldItemInputController>(player);held.ConfigurePractice(_workspace.Session);
            GetOrAdd<ContainerUseController>(player).ConfigurePractice(_workspace.Session,_containerDefinition);
            var collection=GetOrAdd<MaterialCollectionController>(player);collection.ConfigurePractice(_workspace.Session,_solidContentId,_containerDefinition!=null?_containerDefinition.CollectionRate:0);
            var owned=GetOrAdd<OwnedWandCastAdapter>(player);
            owned.ConfigurePractice(_workspace.Session,_select1,_select2,_select3,_materialWaitingMaterial);
            player.GetComponent<WizardController>()?.BindOwnedInventory(owned);
        }
        // Awake先完成来源绑定；Start补发可见性，覆盖同帧CrosshairUI.Awake/OnEnable的订阅顺序。
        private void Start()=>EventBus<CrosshairVisibilityEvent>.Publish(new CrosshairVisibilityEvent{Visible=true});
        private static T GetOrAdd<T>(Transform actor)where T:Component
        {var component=actor.GetComponent<T>();return component!=null?component:actor.gameObject.AddComponent<T>();}
    }
}
