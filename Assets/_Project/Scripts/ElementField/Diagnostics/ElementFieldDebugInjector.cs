using Game.Materials;
using Game.Core;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 临时 Editor 操作面板：用 ContextMenu 构造与真实 Projectile 相同的 ElementWriteRequest。
    /// 不使用 legacy Input，也不直接改 Cell，因此验证通过的 Command Queue/固定 Tick 顺序不会被绕开。
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(ElementFieldRuntime))]
    public sealed class ElementFieldDebugInjector : MonoBehaviour
    {
        [SerializeField] private Transform _marker;
        [SerializeField, Range(1, ushort.MaxValue)] private int _totalAmount = 220;
        [SerializeField, Min(0f)] private float _radius = 0.75f;
        [SerializeField] private bool _useLinearFalloff = true;

        private ElementFieldRuntime _runtime;

        private void Awake()
        {
            _runtime = GetComponent<ElementFieldRuntime>();
        }

        [ContextMenu("Inject Water")]
        private void InjectWater()
        {
            Inject(MaterialId.Water);
        }

        [ContextMenu("Inject Fire")]
        private void InjectFire()
        {
            Inject(MaterialId.Fire);
        }

        [ContextMenu("Clear Field")]
        private void ClearField()
        {
            ResolveRuntime()?.ClearFieldForDebug();
        }

        [ContextMenu("Step One Tick")]
        private void StepOneTick()
        {
            ResolveRuntime()?.StepOnceForDebug();
        }

        [ContextMenu("Rebuild Solid Mask")]
        private void RebuildSolidMask()
        {
            ResolveRuntime()?.RebuildSolidMaskForDebug();
        }

        private void Inject(MaterialId materialKind)
        {
            ElementFieldRuntime runtime = ResolveRuntime();
            if (runtime == null || !runtime.IsInitialized)
                return;

            Vector3 position = _marker != null ? _marker.position : transform.position;
            var request = new ElementWriteRequest(
                position,
                materialKind,
                (ushort)Mathf.Clamp(_totalAmount, 1, ushort.MaxValue),
                Mathf.Max(0f, _radius),
                _useLinearFalloff);
            if (!runtime.TryEnqueueWrite(in request))
            {
                GameLog.Warn(
                    "ElementField debug deposit was rejected because the write queue is full.",
                    "ElementField");
            }
        }

        private ElementFieldRuntime ResolveRuntime()
        {
            if (_runtime == null)
                _runtime = GetComponent<ElementFieldRuntime>();
            return _runtime;
        }
    }
}
