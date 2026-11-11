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
            var currentWorld = _world;
            if (currentWorld == null) return;
            try
            {
                currentWorld.SynchronizeMaterialLifecycle();
                if (_world == null) return; // 明确StopWorld仍可释放当前Driver，故障挂起保留同一World。
                int steps = _clock.Advance(Time.unscaledDeltaTime, _world.CanAdvance && !_world.ResearchPaused);
                for (int i = 0; i < steps; i++)
                {
                    // steps只是帧开始时的预算；上一目标/上一Tick回调故障后，不能继续消费旧物理预算。
                    if (_world == null || !_world.CanAdvance || _world.ResearchPaused) break;
                    var meter = _meter;
                    bool sample = meter != null && MeasuredTicks < 120;
                    bool completed = false;
                    if (sample) meter.Begin();
                    try
                    {
                        Tick++; _world.Step(FixedStepClock.DeltaTime, Tick);
                        if (_authority && _world != null && _world.CanAdvance)
                        {
                            // BeforePhysics失败连本Tick物理也不再推进；Physics回调与After的异常由同一门锁存。
                            Physics.SyncTransforms(); Physics.Simulate(FixedStepClock.DeltaTime);
                            if (_world != null && _world.CanAdvance) _world.AfterPhysics(FixedStepClock.DeltaTime, Tick);
                        }
                        completed = true;
                    }
                    finally
                    {
                        // 异常同样结束已开始的Recorder；失败样本不计成功Tick，显式Stop已Dispose的Owner不再访问。
                        if (sample && ReferenceEquals(_meter, meter))
                        {
                            long count = meter.End();
                            if (completed)
                            {
                                MeasuredTicks++; TickCounterValid &= count >= 0 && meter.IsValid;
                                if (count >= 0) TickAllocationEvents += count;
                            }
                            else TickCounterValid = false;
                        }
                    }
                }
                if (_world != null) _world.PumpSnapshots(Time.realtimeSinceStartupAsDouble);
                // 同一Render轮先让控制/战斗/运动进入Mirror，再有界追加材料页；暂停仍可发布Frozen事实。
                if (_world != null) _world.PumpMaterialReplication(Time.realtimeSinceStartupAsDouble);
            }
            catch (Exception error)
            {
                if (_authority) currentWorld.PauseAuthorityAfterException(error);
                throw;
            }
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
