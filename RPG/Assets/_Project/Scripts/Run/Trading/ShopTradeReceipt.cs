using System;
using Game.Core;

namespace Game.Run
{
    [Serializable]
    public sealed class ShopTradeReceipt
    {
        public string RequestId, Fingerprint;
        public long Currency, CurrencyDelta, ShopVersion, Generation;
        public ulong Quantity;
        // 旧档缺字段默认 0；新收据记录真实去向，重试不能重新放置或猜测反馈。
        public ulong WarehouseQuantity;
        public ShopTradeKind Kind;
        public MaterialAmount[] Materials;
        internal ShopTradeReceipt Copy()
        {var copy=(ShopTradeReceipt)MemberwiseClone();copy.Materials=Materials==null?null:(MaterialAmount[])Materials.Clone();return copy;}
        internal bool IsValid(long version, long generation)
        {
            if(string.IsNullOrWhiteSpace(RequestId) || string.IsNullOrEmpty(Fingerprint) || Currency<0 || Quantity==0 || ShopVersion<=0
                || ShopVersion>version || Generation<=0 || Generation>generation || Kind>ShopTradeKind.SellMaterial)return false;
            if (WarehouseQuantity > Quantity || Kind != ShopTradeKind.BuyItem && WarehouseQuantity != 0) return false;
            bool buy=Kind==ShopTradeKind.BuyItem || Kind==ShopTradeKind.BuyMaterial;
            if(buy?CurrencyDelta>=0:CurrencyDelta<=0)return false;
            bool material=Kind==ShopTradeKind.BuyMaterial || Kind==ShopTradeKind.SellMaterial;
            if(!material)return Materials==null || Materials.Length==0;
            if(Materials==null || Materials.Length==0 || Materials.Length>8)return false;
            ushort seen=0;ulong total=0;
            for(int i=0;i<Materials.Length;i++)
            {var amount=Materials[i];if(!MaterialAmount.IsKnown(amount.MaterialKey) || amount.Quantity==0 || (seen & 1<<amount.MaterialKey)!=0 || amount.Quantity>ulong.MaxValue-total)return false;
                seen|=(ushort)(1<<amount.MaterialKey);total+=amount.Quantity;}
            return total==Quantity;
        }
    }
    public readonly struct ShopTradeResult
    {
        public readonly ShopTradeFailure Failure;
        public readonly ShopTradeReceipt Receipt;
        public readonly bool Replayed;
        public bool Success => Failure == ShopTradeFailure.None;
        public ShopTradeResult(ShopTradeFailure failure, ShopTradeReceipt receipt = null, bool replayed = false)
        { Failure = failure; Receipt = receipt; Replayed = replayed; }
    }
}
