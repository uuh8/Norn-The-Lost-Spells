using System;
using System.Collections;
using Game.Core;
using Game.Run;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;
using UnityEngine.InputSystem;

namespace Game.UI
{
    /// <summary>近距离交互结果驱动面板；持续提示复用常量，残留量只在离散出发查询。</summary>
    public sealed class HubNavigationView : MonoBehaviour
    {
        [SerializeField]private SinglePlayerInventoryController _inventory;
        [SerializeField]private ShopController _shop;
        [SerializeField]private HubInteractionController _hub;
        [SerializeField]private ExperimentAreaController _experiment;
        [SerializeField]private Text _prompt,_residue;
        [SerializeField]private GameObject _departurePanel;
        [SerializeField]private Button _depart,_continue,_cancel;
        [SerializeField]private RunPauseCoordinator _pause;
        [SerializeField]private GameObject[] _worldHud;
        [SerializeField]private GameObject _shopOpenButton;
        private InputAction _escape;
        private void Awake(){_escape=new InputAction("CancelHubDeparture",InputActionType.Button,"<Keyboard>/escape");_escape.performed+=Escape;}
        private void Escape(InputAction.CallbackContext _){if(_departurePanel.activeSelf)Cancel();}
        private void OnDestroy(){_escape.performed-=Escape;_escape.Dispose();}
        private int _lastPrompt=-1;
        private static readonly ProfilerMarker UpdateMarker=new ProfilerMarker("Norn.Hub.NavigationUpdate");
        private static readonly string[] Prompts={"Tab 背包 · F 交互/拾取","F 仓库 · Tab 背包","F 商店 · Tab 背包","走入传送门进入箱庭","实验区 · 离开据点清空世界元素","Tab 背包 · 走入出口开始撤离","F 法术研究台 · Tab 背包"};
        private void OnEnable(){_escape.Enable();EventBus<HubInteractionResultEvent>.Subscribe(OnInteraction);EventBus<HubDepartureReviewResultEvent>.Subscribe(OnReview);_depart.onClick.AddListener(Depart);_continue.onClick.AddListener(Continue);_cancel.onClick.AddListener(Cancel);}
        private void OnDisable(){_escape.Disable();EventBus<HubInteractionResultEvent>.Unsubscribe(OnInteraction);EventBus<HubDepartureReviewResultEvent>.Unsubscribe(OnReview);_depart.onClick.RemoveListener(Depart);_continue.onClick.RemoveListener(Continue);_cancel.onClick.RemoveListener(Cancel);Cancel();}
        private IEnumerator Start()
        {while(_inventory.UiFont==null)yield return null;var texts=GetComponentsInChildren<Text>(true);foreach(var text in texts)text.font=_inventory.UiFont;_departurePanel.SetActive(false);}
        private void Update()
        {
            using(UpdateMarker.Auto())
            {
                // 独立高排序HUD不能盖住整屏面板；只在可见状态变化时切换，稳定帧不扫描层级。
                bool overlay=_inventory.IsOpen || _shop.IsOpen || WandResearchController.Current!=null && WandResearchController.Current.IsOpen;
                if(_prompt.gameObject.activeSelf==overlay)_prompt.gameObject.SetActive(!overlay);
                if(_shopOpenButton!=null && _shopOpenButton.activeSelf==overlay)_shopOpenButton.SetActive(!overlay);
                if(_worldHud!=null)for(int i=0;i<_worldHud.Length;i++)if(_worldHud[i]!=null && _worldHud[i].activeSelf==overlay)_worldHud[i].SetActive(!overlay);
                int prompt=0;var run=_inventory.Session.RunCoordinator;
                if(run!=null && run.State.Phase!=RaidPhase.HubReady)prompt=5;
                else if(_hub.TryGetStation(out var point))prompt=point.Kind==HubStationKind.Warehouse?1:
                    point.Kind==HubStationKind.Shop?2:point.Kind==HubStationKind.SpellResearch?6:3;
                else if(_experiment!=null && _experiment.IsInside)prompt=4;
                if(prompt==_lastPrompt)return;_lastPrompt=prompt;_prompt.text=Prompts[prompt];
            }
        }
        private void OnInteraction(HubInteractionResultEvent result)
        {
            if(!result.Success || result.InventoryId!=_inventory.Session.ReadOnly?.InventoryId)return;
            // 商店/仓库由各自常驻控制器处理，提示Canvas可以独立隐藏；避免一条成功事件打开两次面板。
            if(result.Kind==HubInteractionKind.Expedition){_shop.Close();_inventory.OpenBackpack();RequestReview(HubDepartureReviewOperation.Review);}
        }
        public void Depart()
        {
            RequestReview(HubDepartureReviewOperation.Depart);
        }
        private void RequestReview(HubDepartureReviewOperation operation)
        {EventBus<HubDepartureReviewRequestedEvent>.Publish(new HubDepartureReviewRequestedEvent{InventoryId=_inventory.Session.ReadOnly?.InventoryId,Operation=operation});}
        private void OnReview(HubDepartureReviewResultEvent result)
        {
            if(result.InventoryId!=_inventory.Session.ReadOnly?.InventoryId)return;
            if(result.Departing){_inventory.Close();_shop.Close();Cancel();return;}
            if(result.WorldReady && result.ResidentQuantity==0 && _experiment!=null && result.Message==_experiment.ResidueMessage){Cancel();return;}
            _residue.text=result.Message;
            _continue.interactable=result.WorldReady;
            _departurePanel.SetActive(true);_pause?.RequestPause(RunPauseReason.HubConfirmation);
        }
        public void Continue()=>RequestReview(HubDepartureReviewOperation.ConfirmAndDepart);
        public void Cancel(){if(_departurePanel!=null)_departurePanel.SetActive(false);_pause?.ReleasePause(RunPauseReason.HubConfirmation);}
    }
}
