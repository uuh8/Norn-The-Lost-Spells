using UnityEngine;

namespace Game.ElementField
{
    /// <summary>显式局部环境条件；没有 Temperature Field 时不伪称已经模拟全域热传导。</summary>
    [DisallowMultipleComponent]
    public sealed class MaterialEnvironmentAuthoring : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private Vector3 _size=new Vector3(4,4,4);
        [SerializeField] private float _temperature=20;
        [SerializeField, Range(0,1)] private float _dryness=.4f;
        [SerializeField] private bool _enableVolatilePoison;
        [SerializeField] private int _priority;
        public Bounds Bounds => new Bounds(transform.position,_size);
        public int Priority => _priority;
        public MaterialEnvironmentSample Sample => new MaterialEnvironmentSample(_temperature,_dryness,_enableVolatilePoison);
        private void OnEnable() => _world?.RegisterMaterialEnvironment(this);
        private void Start() => _world?.RegisterMaterialEnvironment(this);
        private void OnDisable() => _world?.UnregisterMaterialEnvironment(this);
        private void OnDrawGizmosSelected() { Gizmos.color=_temperature<0?Color.cyan:Color.yellow; Gizmos.DrawWireCube(transform.position,_size); }
        private void OnValidate()
        {
            if (!float.IsFinite(_temperature)) _temperature=20;
            _dryness=float.IsFinite(_dryness)?Mathf.Clamp01(_dryness):.4f;
            _size=new Vector3(Mathf.Clamp(_size.x,.1f,64),Mathf.Clamp(_size.y,.1f,64),Mathf.Clamp(_size.z,.1f,64));
        }
    }
}
