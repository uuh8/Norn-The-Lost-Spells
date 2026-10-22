using Game.Materials;
using System;

namespace Game.ElementField
{
    /// <summary>
    /// 一个 Cell 的紧凑 Gameplay 数据。它不是 Cube、GameObject 或 Collider，
    /// 只是描述空间小区域中“主要是什么、数量有多少”的值类型。
    /// </summary>
    [Serializable]
    public struct ElementCell
    {
        public MaterialId MaterialKind;
        public byte Amount;

        public ElementCell(MaterialId materialKind, byte amount)
        {
            MaterialKind = materialKind;
            Amount = amount;
        }

        /// <summary>
        /// MaterialKind 与 Amount 共同决定空状态。保留这个容错语义，能让模拟阶段安全处理
        /// “种类已清空但数量尚未归零”或“数量归零但种类稍后统一清理”的中间数据。
        /// </summary>
        public bool IsEmpty => MaterialKind == MaterialId.Empty || Amount == 0;
    }
}
