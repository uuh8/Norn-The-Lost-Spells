using System;
using Game.Core;
using UnityEngine;

namespace Game.Combat
{
    internal enum CombatTickPhase : byte { BeforePhysics, AfterPhysics }
    internal interface ICombatSimulationParticipant
    {
        void StepAuthority(float deltaTime, uint serverTick);
    }

    /// <summary>
    /// Combat 自己拥有固定容量的参与者，不引用网络和施法解释器。Multiplayer 在唯一 Physics.Simulate
    /// 前后调用这两个入口；槽位不压缩，回调中的出生/注销不会移动尚未访问的对象。
    /// </summary>
    public sealed class CombatSimulationWorld
    {
        private struct Entry
        {
            internal MonoBehaviour Component;
            internal ICombatSimulationParticipant Participant;
            internal CombatTickPhase Phase;
            internal uint BirthTick;
            internal bool HasBirthTick;
        }

        private readonly Entry[] _entries;
        private readonly AreaDamage[] _immediateAreas;
        private int _immediateCount;
        private bool _hasTick, _hasBefore, _hasAfter, _stepping;
        private uint _tick, _beforeTick, _afterTick;
        private Func<bool> _authorityWriteGate;
        public int Capacity => _entries.Length;
        public uint CurrentTick => _tick;
        /// <summary>
        /// 每次写入都读取当前权限，不能缓存帧初结果：某个同步命中回调可能已使整个世界进入故障保护。
        /// 未接入在线世界时保持既有独立 Combat 行为；SinglePlayer 组件不消费这道 Authority 门。
        /// </summary>
        public bool AllowsAuthorityWrites => _authorityWriteGate == null || _authorityWriteGate();

        /// <summary>世界装配时配置一次中立谓词；Combat 不反向依赖网络状态，也不因关门清理既有对象。</summary>
        public void ConfigureAuthorityWriteGate(Func<bool> gate)
        { _authorityWriteGate = gate ?? throw new ArgumentNullException(nameof(gate)); }

        public CombatSimulationWorld() : this(512) { }
        public CombatSimulationWorld(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _entries = new Entry[capacity];
            _immediateAreas = new AreaDamage[capacity];
        }

        /// <summary>必须在本 Tick 的角色/动作生成之前调用，保证新生对象不消耗出生前已流逝的时间。</summary>
        public void BeginTick(uint serverTick)
        {
            if (!AllowsAuthorityWrites) return;
            if (_stepping) throw new InvalidOperationException("不能在 Combat 回调中切换当前 Tick。");
            if (_hasTick && !Newer(serverTick, _tick)) return;
            _tick = serverTick; _hasTick = true;
        }

        /// <summary>只在离散出生/装配时扫描；包括 inactive 子物体，保证 AreaDamage.Start 前已经撤销客户端权限。</summary>
        public void ConfigureHierarchy(GameObject root, CombatExecutionMode mode)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            var components = root.GetComponentsInChildren<MonoBehaviour>(true);
            if (mode == CombatExecutionMode.Authority)
            {
                // 组合事务在真正配置任何组件前核对整个层级预算，容量不足不能留下半个 Authority 层级。
                int required = 0, areas = 0, free = 0;
                for (int i = 0; i < _entries.Length; i++) if (_entries[i].Component == null) free++;
                for (int i = 0; i < components.Length; i++)
                {
                    if (!(components[i] is ICombatSimulationParticipant) || Contains(components[i])) continue;
                    required++; if (components[i] is AreaDamage) areas++;
                }
                if (required > free || _hasTick && areas > _immediateAreas.Length - _immediateCount)
                    throw new InvalidOperationException("Combat 层级超过固定容量，整批出生已拒绝。");
            }
            for (int i = 0; i < components.Length; i++)
            {
                switch (components[i])
                {
                    case ProjectileBase value: value.ConfigureExecutionMode(mode, this); break;
                    case ProjectileShield value: value.ConfigureExecutionMode(mode, this); break;
                    case ProjectileStatusOnImpact value: value.ConfigureExecutionMode(mode, this); break;
                    case ThrownContainerProjectile value: value.ConfigureExecutionMode(mode, this); break;
                    case AreaDamage value: value.ConfigureExecutionMode(mode, this); break;
                    case MeleeHitDetector value: value.ConfigureExecutionMode(mode, this); break;
                    case StatusController value: value.ConfigureExecutionMode(mode, this); break;
                    case StatusVolume value: value.ConfigureExecutionMode(mode, this); break;
                    case HealthComponent value: value.ConfigureExecutionMode(mode, this); break;
                    case ManaComponent value: value.ConfigureExecutionMode(mode, this); break;
                    case CharacterCombatFeedback value: value.ConfigureExecutionMode(mode); break;
                }
            }
        }

        private bool Contains(MonoBehaviour component)
        {
            for (int i = 0; i < _entries.Length; i++)
                if (ReferenceEquals(_entries[i].Component, component)) return true;
            return false;
        }

        internal void Register(MonoBehaviour owner, CombatTickPhase phase)
        {
            if (owner == null || !(owner is ICombatSimulationParticipant participant)) return;
            int free = -1;
            for (int i = 0; i < _entries.Length; i++)
            {
                if (ReferenceEquals(_entries[i].Component, owner)) return;
                if (_entries[i].Component == null && free < 0) free = i;
            }
            if (free < 0) throw new InvalidOperationException("CombatSimulationWorld 固定容量已满，拒绝未受控的额外模拟对象。");
            if (owner is AreaDamage && _hasTick && _immediateCount >= _immediateAreas.Length)
                throw new InvalidOperationException("Combat 首跳队列容量已满。");
            _entries[free] = new Entry { Component = owner, Participant = participant, Phase = phase,
                BirthTick = _tick, HasBirthTick = _hasTick };
            if (owner is AreaDamage area && _hasTick)
            {
                // 爆炸在本次 Physics 接触后必须首跳；它的持续时钟仍从下一 Tick 开始，避免既丢瞬时命中又偷跑寿命。
                _immediateAreas[_immediateCount++] = area;
            }
        }

        internal void Unregister(MonoBehaviour owner)
        {
            for (int i = 0; i < _entries.Length; i++)
                if (ReferenceEquals(_entries[i].Component, owner)) { _entries[i] = default; break; }
            // World 迁移/停用也撤销尚未执行的首跳；不能由旧世界越过当前世界的 Frozen 权限。
            // 留空槽而不移动数组，避免在首跳回调里注销自己时跳过下一个对象。
            for (int i = 0; i < _immediateCount; i++)
                if (ReferenceEquals(_immediateAreas[i], owner)) _immediateAreas[i] = null;
        }

        public void StepBeforePhysics(float deltaTime, uint serverTick)
        {
            if (!AllowsAuthorityWrites || !ValidDelta(deltaTime) || _hasTick && Newer(_tick, serverTick) || (_hasBefore && !Newer(serverTick, _beforeTick))) return;
            BeginTick(serverTick);
            if (!AllowsAuthorityWrites) return;
            _hasBefore = true; _beforeTick = serverTick;
            Step(CombatTickPhase.BeforePhysics, deltaTime, serverTick);
        }

        public void StepAfterPhysics(float deltaTime, uint serverTick)
        {
            if (!AllowsAuthorityWrites || !ValidDelta(deltaTime) || _hasTick && Newer(_tick, serverTick) || (_hasAfter && !Newer(serverTick, _afterTick))) return;
            BeginTick(serverTick);
            if (!AllowsAuthorityWrites) return;
            _hasAfter = true; _afterTick = serverTick;
            Step(CombatTickPhase.AfterPhysics, deltaTime, serverTick);
            // 首跳队列允许回调继续追加，但有固定总预算；它不重复扣减持续伤害计时器。
            // 关门只暂停尚未执行的首跳，不能把已有输出当成 Cancel。仅移走确实访问过的槽位。
            int processed = 0;
            try
            {
                while (processed < _immediateCount && AllowsAuthorityWrites)
                {
                    AreaDamage area = _immediateAreas[processed]; _immediateAreas[processed++] = null;
                    if (area != null && area.isActiveAndEnabled) area.ActivateAuthority(serverTick);
                }
            }
            finally
            {
                int remaining = _immediateCount - processed;
                if (processed > 0)
                {
                    Array.Copy(_immediateAreas, processed, _immediateAreas, 0, remaining);
                    Array.Clear(_immediateAreas, remaining, processed);
                }
                _immediateCount = remaining;
            }
        }

        private void Step(CombatTickPhase phase, float deltaTime, uint tick)
        {
            if (!AllowsAuthorityWrites) return;
            if (_stepping) throw new InvalidOperationException("Combat Tick 不允许在伤害回调中重入。");
            _tick = tick; _hasTick = true; _stepping = true;
            try
            {
                for (int i = 0; i < _entries.Length; i++)
                {
                    if (!AllowsAuthorityWrites) break;
                    Entry entry = _entries[i];
                    if (entry.Phase != phase || entry.Component == null || !entry.Component.isActiveAndEnabled) continue;
                    if (entry.HasBirthTick && !Newer(tick, entry.BirthTick)) continue;
                    entry.Participant.StepAuthority(deltaTime, tick);
                }
            }
            finally { _stepping = false; }
        }

        internal static bool ValidDelta(float deltaTime) => float.IsFinite(deltaTime) && deltaTime > 0f;
        internal static bool Newer(uint tick, uint previous) => unchecked((int)(tick - previous)) > 0;
    }

    /// <summary>值类型权限门复用于组件，不创建每帧 delegate；每个 Owner 独立记住最后一次权威 Tick。</summary>
    internal struct CombatExecutionState
    {
        internal CombatExecutionMode Mode;
        internal CombatSimulationWorld World;
        // 新玩法写入与离散装配分开：Configure/Init 可以在 Ready 前完成，真正扣血/扣费/推进必须过此门。
        internal bool AllowsGameplayWrites => Mode == CombatExecutionMode.SinglePlayer
            || Mode == CombatExecutionMode.Authority && (World == null || World.AllowsAuthorityWrites);
        private bool _hasTick;
        private uint _lastTick;

        internal void Configure(MonoBehaviour owner, CombatExecutionMode mode, CombatSimulationWorld world, CombatTickPhase phase)
        {
            if (mode != CombatExecutionMode.SinglePlayer && mode != CombatExecutionMode.Authority && mode != CombatExecutionMode.Replica)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (Mode == mode && ReferenceEquals(World, world)) return;
            // 先核对并取得新世界的槽，再撤销旧登记；Register 抛异常时原权限和时钟完整保留。
            if (mode == CombatExecutionMode.Authority) world?.Register(owner, phase);
            if (Mode == CombatExecutionMode.Authority && !(mode == CombatExecutionMode.Authority && ReferenceEquals(World, world)))
                World?.Unregister(owner);
            Mode = mode; World = world; _hasTick = false;
        }

        internal bool TryStep(float deltaTime, uint tick)
        {
            if (Mode != CombatExecutionMode.Authority || !AllowsGameplayWrites || !CombatSimulationWorld.ValidDelta(deltaTime)
                || (_hasTick && !CombatSimulationWorld.Newer(tick, _lastTick))) return false;
            _hasTick = true; _lastTick = tick; return true;
        }

        internal void Resume(MonoBehaviour owner, CombatTickPhase phase)
        { if (Mode == CombatExecutionMode.Authority) World?.Register(owner, phase); }
        internal void Suspend(MonoBehaviour owner) => World?.Unregister(owner);
    }
}
