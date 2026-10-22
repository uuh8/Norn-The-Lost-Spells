using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.UI
{
    /// <summary>侧栏只选择真实实例，成交仍由商店请求/Run事务完成；不通过点击修改库存。</summary>
    public sealed class ShopInventorySelection : MonoBehaviour,IPointerClickHandler
    {
        private ShopController _controller;
        private string _id;
        public void Bind(ShopController controller,string id){_controller=controller;_id=id;}
        public void OnPointerClick(PointerEventData data)
        {if(data.button==PointerEventData.InputButton.Left)_controller.SelectOwned(_id);}
    }
}
