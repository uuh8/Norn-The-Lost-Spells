using System;

namespace Game.Core
{
    /// <summary>一次实际产出的只读GMU快照；复制值不等于批准新的Owner，释放仍须验证唯一输出ID。</summary>
    public sealed class SpellMaterialBudget
    {
        public static readonly SpellMaterialBudget Empty = new SpellMaterialBudget(Array.Empty<MaterialAmount>(), 0);
        private readonly MaterialAmount[] _amounts;
        public int Count => _amounts.Length;
        public ulong Total { get; }
        public MaterialAmount this[int index] => _amounts[index];
        public SpellMaterialBudget(MaterialAmount[] amounts, int count)
        {
            if (amounts == null || count < 0 || count > 8 || count > amounts.Length) throw new ArgumentException("产出预算需要完整组分。");
            ushort seen = 0; int positive = 0; ulong total = 0;
            for (int i = 0; i < count; i++)
            {
                var a = amounts[i];
                if (!MaterialAmount.IsKnown(a.MaterialKey) || (seen & 1 << a.MaterialKey) != 0) throw new ArgumentException("产出组分未知/重复。");
                seen |= (ushort)(1 << a.MaterialKey); total = checked(total + a.Quantity); if (a.Quantity > 0) positive++;
            }
            _amounts = new MaterialAmount[positive]; Total = total; int output = 0;
            for (int i = 0; i < count; i++) if (amounts[i].Quantity > 0) _amounts[output++] = amounts[i];
        }
        public int CopyTo(MaterialAmount[] destination)
        {
            if (destination == null || destination.Length < Count) throw new ArgumentException("不能截断产出预算。");
            Array.Copy(_amounts, destination, Count); return Count;
        }
        public SpellMaterialBudget[] Split(int outputs)
        {
            if (outputs <= 0 || outputs > 64) throw new ArgumentOutOfRangeException(nameof(outputs));
            // 每份分配后从尚未分配量扣除；整数余数不会被每颗产物重新领取一次。
            var result = new SpellMaterialBudget[outputs]; var remaining = new MaterialAmount[8];
            var share = new MaterialAmount[8]; var fractions = new ulong[8]; CopyTo(remaining);
            for (int i = 0; i < outputs; i++)
            {
                ulong target = Total / (ulong)outputs + ((ulong)i < Total % (ulong)outputs ? 1UL : 0);
                MaterialAmountOps.SplitBudget(remaining, Count, target, share, fractions, i);
                result[i] = new SpellMaterialBudget(share, Count);
                for (int j = 0; j < Count; j++) remaining[j].Quantity -= share[j].Quantity;
            }
            return result;
        }
    }
}
