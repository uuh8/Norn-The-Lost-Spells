using Game.Core;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>独立原型的手动注入点；使用与法术相同的 Deposit 接口，不启动自动跑批。</summary>
    public sealed class LiquidPrototypeProbe : MonoBehaviour
    {
        [SerializeField] private LiquidWorldRuntime _liquids;
        [SerializeField] private MaterialId _material = MaterialId.Water;
        [SerializeField, Range(1, 65535)] private int _quantity = 1020;
        [SerializeField, Min(0f)] private float _radius = 1f;
        public int Quantity => _quantity;

        [ContextMenu("Deposit In Play Mode")]
        public void Deposit()
        {
            if (_liquids == null || !_liquids.IsFluidInitialized) return;
            if (!TryDeposit())
                GameLog.Warn("原型注入未被接受；查看 Grid Liquid Pending 及材料配置。",
                    "ElementField");
        }

        /// <summary>显式返回队列接收结果；接收不等于已入账，后续仍须检查 Pending 和总量。</summary>
        public bool TryDeposit()
        {
            if (_liquids == null || !_liquids.IsFluidInitialized) return false;
            var request = new ElementWriteRequest(transform.position, _material,
                (ushort)_quantity, _radius, false, Vector3.zero, Vector3.up);
            return _liquids.TryEnqueueDeposit(in request);
        }
    }
}
