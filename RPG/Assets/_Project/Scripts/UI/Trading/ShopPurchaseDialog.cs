using Game.Run;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>数量确认的表现状态。只持有报价快照和目标ID，实际资产写入由控制器经EventBus提交。</summary>
    public sealed class ShopPurchaseDialog : MonoBehaviour
    {
        [SerializeField] private ShopController _controller;
        [SerializeField] private Image _icon;
        [SerializeField] private Text _name, _description, _quantityLabel, _limits, _stock, _cost, _wallet, _target, _feedback;
        [SerializeField] private Slider _slider;
        [SerializeField] private Button _minus, _plus, _cancel, _confirm;
        private ShopOfferSnapshot _offer;
        private bool _pending;
        public string OfferId => _offer.OfferId;
        public string ContainerId { get; private set; }
        public ulong Quantity { get; private set; }
        public ulong Maximum { get; private set; }
        public bool IsOpen => gameObject.activeSelf;
        public bool Pending => _pending;
        private static readonly ProfilerMarker QuantityMarker = new ProfilerMarker("Norn.Shop.PurchaseQuantity");
        private void OnEnable()
        {
            _slider.onValueChanged.AddListener(OnSlider);
            _minus.onClick.AddListener(Minus); _plus.onClick.AddListener(Plus);
            _cancel.onClick.AddListener(Hide); _confirm.onClick.AddListener(Confirm);
        }
        private void OnDisable()
        {
            _slider.onValueChanged.RemoveListener(OnSlider);
            _minus.onClick.RemoveListener(Minus); _plus.onClick.RemoveListener(Plus);
            _cancel.onClick.RemoveListener(Hide); _confirm.onClick.RemoveListener(Confirm);
            _pending = false;
        }
        public void Show(in ShopOfferSnapshot offer, string containerId)
        {
            _offer = offer; ContainerId = containerId; Quantity = 1; _pending = false;
            gameObject.SetActive(true); transform.SetAsLastSibling();
            _name.text = offer.DisplayName;
            _icon.sprite = offer.Icon; _icon.enabled = offer.Icon != null;
            _description.text = offer.IsMaterial ? "连续元素 · 数量单位 GMU\n注入中列选中的兼容容器，保留瓶中已有材料。" :
                (offer.IsSpell ? "法术 · " + (offer.Unlimited ? "无限使用" : offer.InitialCharges + " 次") : offer.Kind == ItemKind.Wand ? "法杖" : offer.Kind == ItemKind.Container ? "容器 · 每份独立保存储量" : "普通物品")
                + "\n先放入背包，空间不足的份数自动放入仓库。";
        }
        public void Refresh(in ShopOfferSnapshot offer, ulong maximum, long money, in InventoryItemSnapshot target, string reason)
        {
            _offer = offer; Maximum = maximum;
            Quantity = maximum == 0 ? 0 : System.Math.Max(1UL, System.Math.Min(Quantity, maximum));
            _stock.text = "剩余供货：" + offer.Stock + (offer.IsMaterial ? " GMU" : " 份") + "    单价：" + offer.BuyPrice;
            _wallet.text = "当前金币：" + money.ToString("N0");
            _target.text = offer.IsMaterial ? "目标容器：" + target.DisplayName + "  " + target.ContainerTotal + "/" + target.ContainerCapacity + " GMU" : "购买后放入背包；空间不足自动入仓";
            _feedback.text = reason ?? ""; RenderQuantity();
        }
        public void Hide() => gameObject.SetActive(false);
        public void SetPending(bool value) { _pending = value; RenderQuantity(); }
        public void ShowFailure(string reason) { _pending = false; _feedback.text = reason; RenderQuantity(); }
        // 以整数为唯一数量状态，回写Slider用SetValueWithoutNotify，防止float反向舍入整数或递归回调。
        public void SetQuantity(ulong quantity)
        {
            if (_pending) return;
            Quantity = Maximum == 0 ? 0 : System.Math.Max(1UL, System.Math.Min(quantity, Maximum));
            RenderQuantity();
        }
        private void OnSlider(float value)
        {
            using var scope = QuantityMarker.Auto();
            SetQuantity(ShopPurchaseQuantity.FromProgress(value, Maximum));
        }
        private void Minus() { if (Quantity > 1) SetQuantity(Quantity - 1); }
        private void Plus() { if (Quantity < Maximum) SetQuantity(Quantity + 1); }
        private void Confirm() { if (!_pending && Quantity > 0) _controller.ConfirmPurchase(this); }
        private void RenderQuantity()
        {
            _quantityLabel.text = Quantity + (_offer.IsMaterial ? " GMU" : " 份");
            _limits.text = (Maximum == 0 ? "0" : "1") + "                                   " + Maximum;
            _slider.SetValueWithoutNotify(ShopPurchaseQuantity.ToProgress(Quantity, Maximum));
            _slider.interactable = !_pending && Maximum > 1;
            _minus.interactable = !_pending && Quantity > 1; _plus.interactable = !_pending && Quantity < Maximum;
            bool priced = ShopPurchaseQuantity.TryCost(_offer.BuyPrice, Quantity, out var total);
            _cost.text = "消耗金币：" + (priced ? total.ToString("N0") : "—");
            _confirm.interactable = !_pending && priced && Quantity <= Maximum;
            _cancel.interactable = !_pending;
        }
    }
}
