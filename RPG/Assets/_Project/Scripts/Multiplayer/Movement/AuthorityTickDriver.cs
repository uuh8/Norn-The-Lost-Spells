using System;
using Game.Networking;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>在Update中推进固定逻辑步；只有Authority接管全局Physics，Client不回滚整场物理。</summary>
    [DefaultExecutionOrder(-50)]
    public sealed class AuthorityTickDriver : MonoBehaviour
    {
        private static AuthorityTickDriver _physicsOwner;
        private readonly FixedStepClock _clock = new FixedStepClock();
        private OnlineWorldBootstrap _world;
        private bool _authority, _ownsPhysics;
        private SimulationMode _previousMode;
        private float _previousFixedDelta;
        public uint Tick { get; private set; }
        public long DroppedSteps => _clock.DroppedSteps;
        private CodecAllocationMeter _meter;
        public int MeasuredTicks { get; private set; }
        public long TickAllocationEvents { get; private set; }
        public bool TickCounterValid { get; private set; }
        public void StartDiagnosticMeasurement()
        {
            _meter?.Dispose(); _meter = new CodecAllocationMeter(1024);
            MeasuredTicks = 0; TickAllocationEvents = 0; TickCounterValid = _meter.IsValid;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic() => _physicsOwner = null;
        public void Initialize(OnlineWorldBootstrap world, bool authority)
        {
            _world = world; _authority = authority;
            if (!authority) return;
            if (_physicsOwner != null && _physicsOwner != this) throw new InvalidOperationException("已有在线Physics Owner，拒绝双重模拟。");
            _physicsOwner = this; _previousMode = Physics.simulationMode; _previousFixedDelta = Time.fixedDeltaTime;
            Physics.simulationMode = SimulationMode.Script; Time.fixedDeltaTime = FixedStepClock.DeltaTime; _ownsPhysics = true;
        }
        private void Update()
        {
            if (_world == null) return;
            int steps = _clock.Advance(Time.unscaledDeltaTime, _world.CanAdvance);
            for (int i = 0; i < steps; i++)
            {
                // steps 只是帧开始时的预算；上一个 Tick 的出生/命中回调可能使世界 Faulted 或停止。
                // 必须重新取得推进资格，不能把旧预算当成继续运行 Gameplay 的授权。
                if (_world == null || !_world.CanAdvance) break;
                bool sample = _meter != null && MeasuredTicks < 120;
                if (sample) _meter.Begin();
                Tick++; _world.Step(FixedStepClock.DeltaTime, Tick);
                if (_authority && _world != null && _world.CanAdvance)
                {
                    // BeforePhysics 内也可能拒绝产出；这种失败必须连本 Tick 的物理推进一起撤销。
                    Physics.SyncTransforms(); Physics.Simulate(FixedStepClock.DeltaTime);
                    if (_world != null && _world.CanAdvance) _world.AfterPhysics(FixedStepClock.DeltaTime, Tick);
                }
                if (sample)
                {
                    long count = _meter.End(); MeasuredTicks++;
                    TickCounterValid &= count >= 0 && _meter.IsValid;
                    if (count >= 0) TickAllocationEvents += count;
                }
            }
            if (_world != null) _world.PumpSnapshots(Time.realtimeSinceStartupAsDouble);
        }
        public void StopDriver()
        {
            _world = null; _clock.Reset();
            _meter?.Dispose(); _meter = null;
            if (_ownsPhysics)
            {
                Physics.simulationMode = _previousMode; Time.fixedDeltaTime = _previousFixedDelta;
                _ownsPhysics = false; if (_physicsOwner == this) _physicsOwner = null;
            }
        }
        private void OnDisable() => StopDriver();
    }
}
