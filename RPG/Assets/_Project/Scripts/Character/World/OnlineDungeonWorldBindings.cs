using System;
using Game.Run;
using UnityEngine;

namespace Game.Character
{
    /// <summary>每个原遭遇区的Scene引用；顺序对应共同Definition，不保存进度、玩家身份或奖励工作区。</summary>
    [Serializable]
    public sealed class OnlineDungeonAreaBindings
    {
        public Collider[] EntryVolumes = Array.Empty<Collider>();
        public ArenaGate[] Gates = Array.Empty<ArenaGate>();
        public GameObject SafeZoneVisualRoot;
    }

    /// <summary>
    /// 受控地图Prefab里的空间/门表现接线。只复用原Collider、ArenaGate和安全区VFX，
    /// 不挂回单机EntryTrigger/Encounter Owner；是否开始、何时开门由OnlineDungeonRuntime唯一裁定。
    /// </summary>
    public sealed class OnlineDungeonWorldBindings : MonoBehaviour
    {
        [SerializeField] private OnlineDungeonAreaBindings[] _areas = Array.Empty<OnlineDungeonAreaBindings>();
        private ParticleSystem[][] _particles;
        private bool[] _draining, _finished;
        private bool _hasDraining;
        public int AreaCount => _areas != null ? _areas.Length : 0;

        /// <summary>
        /// 一次性缓存粒子数组。Prefab在inactive层级完成装配后才启用，Awake不是任意Editor测试必定运行的前置，
        /// 所以Runtime构造也会幂等调用Prepare；GetComponentsInChildren的数组分配只发生在此离散阶段。
        /// </summary>
        public void Prepare()
        {
            if (_particles != null) return;
            int count = AreaCount;
            _particles = new ParticleSystem[count][]; _draining = new bool[count]; _finished = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var root = _areas[i] != null ? _areas[i].SafeZoneVisualRoot : null;
                _particles[i] = root != null ? root.GetComponentsInChildren<ParticleSystem>(true) : Array.Empty<ParticleSystem>();
            }
        }
        private void Awake() => Prepare();

        /// <summary>
        /// 查询原入口真实形状。Collider.bounds是世界AABB，旋转薄Box或P8偏心Capsule会在AABB角落误触发；
        /// ClosestPoint返回该形状距查询点最近的世界点，内部点返回自身。radius允许调用者表达玩家横向体积。
        /// 禁用Collider不会提供有效空间权限，不能利用其ClosestPoint退化行为将所有位置判定为inside。
        /// </summary>
        public bool ContainsEntry(int areaIndex, Vector3 position, float radius = 0)
        {
            if (areaIndex < 0 || areaIndex >= AreaCount || !Finite(position) || !float.IsFinite(radius) || radius < 0) return false;
            var area = _areas[areaIndex]; if (area == null || area.EntryVolumes == null) return false;
            float tolerance = radius + .0001f; float squared = tolerance * tolerance;
            for (int i = 0; i < area.EntryVolumes.Length; i++)
            {
                var collider = area.EntryVolumes[i];
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy || !Supported(collider)) continue;
                Vector3 closest = collider.ClosestPoint(position);
                if ((closest - position).sqrMagnitude <= squared) return true;
            }
            return false;
        }
        public bool TryValidate(int expectedAreaCount, out string error)
        {
            error = null;
            // 原Home显式是零遭遇地图；是否允许零区域由已验证Definition决定，不能伪造空战斗区。
            if (expectedAreaCount < 0 || _areas == null || AreaCount != expectedAreaCount) { error = "地图区域接线数量与Definition不一致。"; return false; }
            for (int i = 0; i < AreaCount; i++)
            {
                var area = _areas[i];
                if (area == null || area.EntryVolumes == null || area.EntryVolumes.Length == 0 || area.Gates == null)
                { error = "区域缺少原入口Collider或门数组。"; return false; }
                for (int c = 0; c < area.EntryVolumes.Length; c++)
                    if (area.EntryVolumes[c] == null || !Supported(area.EntryVolumes[c]) || !area.EntryVolumes[c].transform.IsChildOf(transform))
                    { error = "入口必须是本地图的Box/Sphere/Capsule或convex Mesh Collider。"; return false; }
                for (int g = 0; g < area.Gates.Length; g++)
                    if (area.Gates[g] == null || !area.Gates[g].transform.IsChildOf(transform))
                    { error = "门引用缺失或指向其他地图。"; return false; }
                if (area.SafeZoneVisualRoot != null && !area.SafeZoneVisualRoot.transform.IsChildOf(transform))
                { error = "安全区表现不能引用其他地图对象。"; return false; }
            }
            return true;
        }
        private static bool Supported(Collider collider) => collider is BoxCollider || collider is SphereCollider ||
            collider is CapsuleCollider || collider is MeshCollider mesh && mesh.convex;
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        public void SetClosed(int areaIndex, bool closed)
        {
            if (areaIndex < 0 || areaIndex >= AreaCount || _areas[areaIndex]?.Gates == null) return;
            var gates = _areas[areaIndex].Gates;
            for (int i = 0; i < gates.Length; i++) if (gates[i] != null) gates[i].SetClosed(closed);
        }
        public void OpenAll() { for (int i = 0; i < AreaCount; i++) SetClosed(i, false); }

        /// <summary>
        /// 保留原P8安全区收尾：StopEmitting不清已出生粒子，等待自然寿命结束后再隐藏根。
        /// 不为这次收尾创建Coroutine；Update只在至少一个区域正在消散时读取已缓存的粒子，不分配数组。
        /// Dedicated Server无需渲染VFX，直接隐藏但不参与任何Encounter裁决。
        /// </summary>
        public void FinishSafeZone(int areaIndex)
        {
            if (areaIndex < 0 || areaIndex >= AreaCount) return;
            Prepare(); if (_finished[areaIndex]) return;
            _finished[areaIndex] = true;
            var root = _areas[areaIndex]?.SafeZoneVisualRoot; if (root == null) return;
#if UNITY_SERVER && !UNITY_EDITOR
            root.SetActive(false);
#else
            var particles = _particles[areaIndex];
            for (int i = 0; i < particles.Length; i++) if (particles[i] != null) particles[i].Stop(false, ParticleSystemStopBehavior.StopEmitting);
            _draining[areaIndex] = true; _hasDraining = true; PollDrain(areaIndex);
#endif
        }
        private void Update()
        {
            if (!_hasDraining) return;
            bool remains = false;
            for (int i = 0; i < _draining.Length; i++) if (_draining[i]) { PollDrain(i); remains |= _draining[i]; }
            _hasDraining = remains;
        }
        private void PollDrain(int index)
        {
            var particles = _particles[index];
            for (int p = 0; p < particles.Length; p++) if (particles[p] != null && particles[p].IsAlive(false)) return;
            _draining[index] = false; var root = _areas[index]?.SafeZoneVisualRoot; if (root != null) root.SetActive(false);
        }
    }
}
