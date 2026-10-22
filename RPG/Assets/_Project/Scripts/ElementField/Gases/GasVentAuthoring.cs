using Game.Materials;
using UnityEngine;
namespace Game.ElementField
{
    /// <summary>真实排气口；仅移除落在入口 Bounds 内的气体并记账，封闭房间没有隐式衰减。</summary>
    public sealed class GasVentAuthoring : MonoBehaviour
    {
        [SerializeField] private GasWorldRuntime _world;
        [SerializeField] private Vector3 _size = Vector3.one;
        [SerializeField] private MaterialId _material = MaterialId.Empty;
        [Tooltip("入口内每格的排出速率 GMU/s；入口面积越大，总排出越多。按整数 Tick 扣量。")]
        [SerializeField, Min(0)] private float _gmuPerSecond = 100;
        public Bounds Bounds => new Bounds(transform.position, _size);
        public MaterialId Material => _material;
        public float Rate => float.IsFinite(_gmuPerSecond) ? Mathf.Max(0, _gmuPerSecond) : 0;
        private void OnEnable() { if (_world != null) _world.RegisterVent(this); }
        private void OnDisable() { if (_world != null) _world.UnregisterVent(this); }
        private void OnDrawGizmosSelected() { Gizmos.color = Color.cyan; Gizmos.DrawWireCube(transform.position, _size); }
    }
}
