using Game.Run;
using UnityEngine;
using UnityEngine.UI;
using Unity.Profiling;

namespace Game.UI
{
    /// <summary>用户外框由Inspector负责；只刷新真实容量/钱包/七位工具，不重排父面板。</summary>
    public sealed class BackpackWorkspaceView : MonoBehaviour
    {
        [SerializeField] private Text _capacity,_wallet;
        [SerializeField] private ToolbeltView _tools;
        [SerializeField] private Button _organize,_pickupAll;
        private void OnEnable(){_organize?.onClick.AddListener(Organize);_pickupAll?.onClick.AddListener(PickupAll);ButtonText(_organize,"整理背包");ButtonText(_pickupAll,"全部拾取");}
        private void OnDisable(){_organize?.onClick.RemoveListener(Organize);_pickupAll?.onClick.RemoveListener(PickupAll);}
        private void Organize()=>_controller?.OrganizeBackpack();
        private void PickupAll()=>_controller?.PickupAllNearby();
        // 旧详情区已删除，反馈直接显示在刚点击的按钮内；只在离散结果时改字，不另占网格空间。
        public void ShowOrganizeResult(bool success)=>ButtonText(_organize,success?"整理完成":"整理失败");
        public void ShowPickupResult(int collected,int remaining)=>ButtonText(_pickupAll,"拾取 "+collected+" · 剩余 "+remaining);
        private static void ButtonText(Button button,string text)
        {if(button!=null){var label=button.GetComponentInChildren<Text>(true);if(label!=null)label.text=text;}}
        private SinglePlayerInventoryController _controller;
        private long _lastCurrency=long.MinValue;
        private uint _toolVersion;
        private static readonly ProfilerMarker UpdateMarker=new ProfilerMarker("Norn.Backpack.Update");
        public void Configure(Text capacity,Text wallet,ToolbeltView tools) { _capacity=capacity; _wallet=wallet; _tools=tools; }
        public void Refresh(SinglePlayerInventoryController controller,InventoryItemSnapshot[] items,int count,string bagId)
        {
            _controller=controller; int cells=0,used=0;
            for (int i=0;i<count;i++)
            {
                var item=items[i]; if (item.InstanceId==bagId) cells=item.Rules.BackpackWidth*item.Rules.BackpackHeight;
                if (item.Location.Kind!=ItemLocationKind.Backpack || item.Location.OwnerId!=bagId) continue;
                bool first=true; for (int j=0;j<i;j++) if (items[j].Location==item.Location) first=false;
                if (first) used+=item.Rules.Shape.CellCount;
            }
            if (_capacity!=null) { _capacity.font=controller.UiFont; _capacity.text=cells==0?"未装备背包":"已占 "+used+" / "+cells+" 格"; }
            _tools?.Refresh(controller); _toolVersion=controller.Session.ToolbeltReadOnly?.ConfigurationVersion??0; RefreshWallet();
        }
        private void Update()
        {
            if (_controller==null || !_controller.IsOpen) return;
            using var sample=UpdateMarker.Auto(); RefreshWallet();
            uint version=_controller.Session.ToolbeltReadOnly?.ConfigurationVersion??0;
            if (_toolVersion!=version) { _toolVersion=version; _tools?.Refresh(_controller); }
        }
        private void RefreshWallet()
        {
            long value=_controller.Session.WalletReadOnly.Currency;
            if (value==_lastCurrency || _wallet==null) return;
            // 钱包只在交易等离散变化时格式化；稳定帧不创建字符串。
            _lastCurrency=value; _wallet.font=_controller.UiFont; _wallet.text=value.ToString();
        }
    }
}
