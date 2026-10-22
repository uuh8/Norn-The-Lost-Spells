using Game.Core;
using Game.Combat;
using UnityEngine;
using Unity.Profiling;

namespace Game.Character
{
    /// <summary>批准材料的可见生命周期代理；独立于投射物销毁，真实量始终由Run的OutputId持有。</summary>
    public sealed class MaterialPayloadRuntimeAdapter : MonoBehaviour
    {
        private string _inventoryId, _outputId;
        private ProjectileBase _projectile;
        private Renderer _renderer;
        private Material _ownedMaterial;
        private Vector3 _position;
        private float _retry, _waitForProjectile;
        private bool _committed, _waiting, _resolved;
        private static readonly ProfilerMarker Marker = new ProfilerMarker("Norn.SpellMaterials.Proxy");
        public bool IsWaiting => _waiting && !_resolved;
        public string OutputId => _outputId;
        internal static MaterialPayloadRuntimeAdapter Create(MonoBehaviour owner, string inventory, string output, Vector3 position, float delay, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "SpellMaterial_" + output;
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, owner.gameObject.scene);
            go.transform.position = position; go.transform.localScale = Vector3.one * .15f;
            var collider = go.GetComponent<Collider>(); collider.enabled = false; Destroy(collider);
            var adapter = go.AddComponent<MaterialPayloadRuntimeAdapter>(); adapter._inventoryId = inventory; adapter._outputId = output;
            adapter._position = position; adapter._waitForProjectile = Mathf.Max(0, delay) + 1;
            adapter._renderer = go.GetComponent<Renderer>(); adapter._renderer.enabled = false;
            if (material == null)
            {
                // 测试资产可复用材质；未配置时创建独立URP材质，OnDestroy负责释放，不改sharedMaterial资产。
                adapter._ownedMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")); material = adapter._ownedMaterial;
            }
            adapter._renderer.sharedMaterial = material;
            var properties = new MaterialPropertyBlock(); properties.SetColor("_BaseColor", new Color(1, .55f, .08f)); adapter._renderer.SetPropertyBlock(properties);
            return adapter;
        }
        internal void Activate() { _committed = true; if (_waiting) Send(); }
        internal void Link(ProjectileBase projectile)
        {
            if (_resolved || _waiting || projectile == null) return;
            Unlink(); _projectile = projectile; _position = projectile.transform.position;
            projectile.SurfaceImpacted += OnImpact; projectile.Finished += OnFinished;
        }
        internal void ReleaseAt(Vector3 point) { _position = point; BeginWaiting(); }
        internal void SetFallback(Vector3 point, float delay) { _position = point; _waitForProjectile = Mathf.Max(0, delay) + 1; }
        private void OnImpact(ProjectileImpactContext contact)
        { _position = contact.Point + contact.SurfaceNormal * .12f; BeginWaiting(); }
        private void OnFinished()
        {
            if (_waiting || _resolved) return;
            if (_projectile != null) _position = _projectile.transform.position;
            BeginWaiting();
        }
        private void BeginWaiting()
        {
            if (_resolved || _waiting) return;
            _waiting = true; Unlink(); transform.position = _position; _renderer.enabled = true;
            if (_committed) Send(); _retry = 0;
        }
        private void Send() => EventBus<SpellMaterialOutputEvent>.Publish(new SpellMaterialOutputEvent
        { InventoryId = _inventoryId, OutputId = _outputId, Position = _position });
        private void OnEnable() => EventBus<SpellMaterialOutputResultEvent>.Subscribe(OnResult);
        private void OnDisable()
        {
            EventBus<SpellMaterialOutputResultEvent>.Unsubscribe(OnResult);
            // Disable同样停止Update与结果接收，已提交代理不能留下不可见且无法重试的Owner。
            // 准备阶段尚未扣量，Rollback销毁这种代理时不发送结束请求。
            if (!_committed || _resolved) return;
            _resolved = true; Unlink(); if (_renderer != null) _renderer.enabled = false;
            EventBus<SpellMaterialOutputEvent>.Publish(new SpellMaterialOutputEvent
            { InventoryId = _inventoryId, OutputId = _outputId, Ended = true });
            Destroy(gameObject);
        }
        private void OnResult(SpellMaterialOutputResultEvent result)
        {
            if (result.InventoryId != _inventoryId || result.OutputId != _outputId || !result.Resolved) return;
            _resolved = true; _renderer.enabled = false; Unlink(); Destroy(gameObject);
        }
        private void Update()
        {
            using var marker = Marker.Auto();
            if (!_committed || _resolved || Time.timeScale <= 0) return;
            if (_waiting) { _retry += Time.deltaTime; if (_retry >= .2f) { _retry = 0; Send(); } return; }
            if (_projectile != null) { _position = _projectile.transform.position; transform.position = _position; return; }
            // Skyfall调度已提交但实际对象未能建立时，保留落点处可见预算；不能丢料或再扣一份。
            _waitForProjectile -= Time.deltaTime; if (_waitForProjectile <= 0) BeginWaiting();
        }
        private void Unlink()
        { if (_projectile != null) { _projectile.SurfaceImpacted -= OnImpact; _projectile.Finished -= OnFinished; } _projectile = null; }
        private void OnDestroy()
        {
            Unlink(); if (_ownedMaterial != null) Destroy(_ownedMaterial);
            if (_committed && !_resolved) EventBus<SpellMaterialOutputEvent>.Publish(new SpellMaterialOutputEvent
            { InventoryId = _inventoryId, OutputId = _outputId, Ended = true });
        }
    }
}
