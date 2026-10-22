using System;
using System.Globalization;
using System.IO;
using Game.Core;
using Game.Materials;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.ElementField
{
    /// <summary>真实 Player 的材料世界探针；只观察公开接口，不替世界推进 Tick 或直接修改 Store。</summary>
    public sealed class HeadlessGridProbe : MonoBehaviour
    {
        [SerializeField] private ElementWorldRuntime _world;
        [SerializeField] private LiquidWorldRuntime _liquids;
        [SerializeField] private HeadlessGridProbeProfile _profile;
        private ProfilerRecorder _frameGc;
        private double _startedAt, _measuringAt;
        private long _startTick, _measuredStartTick, _maxGcBytes;
        private int _measuredFrames, _previousFrameRate;
        private bool _running, _measuring, _finished;
        private float _expectedHz;

        private static bool IsServerBuild
        {
            get
            {
#if UNITY_SERVER
                return true;
#else
                return false;
#endif
            }
        }

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool requested = false;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "-nornHeadlessGridProbe") requested = true;
            // 场景可在 Editor 中审阅；没有显式参数时不启动实验，也不退出 Editor/普通游戏。
            if (!requested) { enabled = false; return; }
            if (!IsServerBuild || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            { Finish("RequiresDedicatedServerAndNullGraphics"); return; }
            if (_world == null || _liquids == null || _profile == null || !_profile.IsValid)
            { Finish("InvalidProbeConfiguration"); return; }
            _expectedHz = _profile.ExpectedTickRate;
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "-nornHeadlessGridExpectedHz" &&
                    (!float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _expectedHz) ||
                     !float.IsFinite(_expectedHz) || _expectedHz <= 0))
                { Finish("InvalidExpectedTickRate"); return; }
            if (_world.ConfiguredWaterSimulationMode != WaterSimulationMode.GridLiquid)
            { Finish("RequiresExplicitGridProfile"); return; }
            if (Mathf.Abs(_world.ConfiguredTickRate - _expectedHz) > .001f)
            { Finish("ConfiguredTickRateMismatch"); return; }

            _previousFrameRate = Application.targetFrameRate;
            Application.targetFrameRate = _profile.ProcessFrameRate;
            // 在所有配置已反序列化且 Collider 已同步后激活世界；避免 Awake 读到半装配状态。
            Physics.SyncTransforms();
            _world.gameObject.SetActive(true);
            if (!_world.IsInitialized || _world.EffectiveWaterSimulationMode != WaterSimulationMode.GridLiquid ||
                !_liquids.IsFluidInitialized)
            { Finish("WorldInitializationFailed"); return; }
            var request = new ElementWriteRequest(_profile.WaterPosition, MaterialId.Water,
                _profile.WaterQuantity, _profile.WaterRadius, false);
            if (!_world.TryEnqueueWrite(in request)) { Finish("WaterWriteRejected"); return; }
            _startedAt = Time.realtimeSinceStartupAsDouble;
            _startTick = _world.WorldTick;
            _frameGc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            _running = true;
        }

        private void LateUpdate()
        {
            if (!_running || _finished) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!_measuring)
            {
                if (now - _startedAt < _profile.WarmupSeconds) return;
                _measuring = true;
                _measuringAt = now;
                _measuredStartTick = _world.WorldTick;
                return;
            }
            // 只读计数器与值类型；不在每帧拼字符串、扫描世界或分配采样数组。
            _measuredFrames++;
            if (_frameGc.Valid && _frameGc.Count > 0)
                _maxGcBytes = Math.Max(_maxGcBytes, _frameGc.LastValue);
            if (now - _measuringAt >= _profile.MeasureSeconds) Finish(null);
        }

        private void Finish(string error)
        {
            if (_finished) return;
            _finished = true;
            double elapsed = _measuring ? Time.realtimeSinceStartupAsDouble - _measuringAt : 0;
            long ticks = _world != null && _measuring ? _world.WorldTick - _measuredStartTick : 0;
            double hz = elapsed > 0 ? ticks / elapsed : 0;
            bool initialized = _world != null && _world.IsInitialized;
            bool waterMovedDown = false;
            int sourceLiquidCellY = 0, observedLowerLiquidCellY = 0;
            if (error == null && initialized)
            {
                // 结束时的一次有界扫描只证明下方存在水。粗/细格可能映射到同一库存，不能相加当总量。
                Vector3Int originCell = Vector3Int.FloorToInt(
                    (_profile.WaterPosition - _world.Origin) / _world.CellSize);
                sourceLiquidCellY = _liquids.WorldCellToLiquidCell(originCell).y;
                observedLowerLiquidCellY = sourceLiquidCellY;
                Vector3Int min = _profile.ScanMinimum, size = _profile.ScanSize;
                for (int z = 0; z < size.z && !waterMovedDown; z++)
                    for (int y = 0; y < size.y && !waterMovedDown; y++)
                        for (int x = 0; x < size.x; x++)
                        {
                            var cell = min + new Vector3Int(x, y, z);
                            // 比较液体粗格坐标：注入格下半部的细格仍属原库存，不能误判为已经下落。
                            int liquidY = _liquids.WorldCellToLiquidCell(cell).y;
                            if (liquidY < sourceLiquidCellY && _world.TryGetQuantity(cell, MaterialId.Water, out uint q) && q > 0)
                            { waterMovedDown = true; observedLowerLiquidCellY = liquidY; break; }
                        }
            }
            ulong total = _liquids != null ? _liquids.TotalQuantity : 0;
            ulong pending = _liquids != null ? _liquids.PendingQuantity : 0;
            bool conserved = _profile != null && _profile.IsValid && total == _profile.WaterQuantity && pending == 0;
            bool rateMatches = _profile != null && elapsed > 0 && Math.Abs(hz - _expectedHz) <= _profile.TickRateTolerance;
            bool passed = error == null && initialized && IsServerBuild &&
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null && conserved && waterMovedDown && rateMatches;
            if (!passed && error == null) error = "GridProgressAcceptanceFailed";
            var report = new Report
            {
                Passed = passed, Error = error, UnityVersion = Application.unityVersion,
                DedicatedServer = IsServerBuild, GraphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                WorldInitialized = initialized, ConfiguredBackend = _world != null ? _world.ConfiguredWaterSimulationMode.ToString() : null,
                EffectiveBackend = initialized ? _world.EffectiveWaterSimulationMode.ToString() : null,
                ConfiguredHz = _world != null ? _world.ConfiguredTickRate : 0,
                ExpectedHz = _expectedHz, MeasuredSeconds = elapsed, MeasuredTicks = ticks, MeasuredHz = hz,
                TotalTicks = initialized ? _world.WorldTick - _startTick : 0, MeasuredFrames = _measuredFrames,
                InjectedGmu = _profile != null && _profile.IsValid ? _profile.WaterQuantity : 0, LiquidGmu = total, PendingGmu = pending,
                WaterMovedDown = waterMovedDown, QuantityConserved = conserved,
                InjectionLiquidCellY = sourceLiquidCellY, ObservedLowerLiquidCellY = observedLowerLiquidCellY,
                FrameGcRecorderValid = _frameGc.Valid, MaximumFrameGcBytes = _maxGcBytes,
                RecordedAtUtc = DateTime.UtcNow.ToString("O")
            };
            // 文件/JSON 仅在终止边界分配；全进程 GC 计数不等于材料 Tick 或 KCP 的单独分配结论。
            string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ProbeReports"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "headless-grid.json"), JsonUtility.ToJson(report, true));
            if (_frameGc.Valid) _frameGc.Dispose();
            GameLog.Info(passed ? "Headless Grid 验证通过。" : "Headless Grid 验证失败：" + error, "OnlineLab");
            Application.Quit(passed ? 0 : 1);
        }

        private void OnDestroy()
        {
            if (_frameGc.Valid) _frameGc.Dispose();
            if (_running) Application.targetFrameRate = _previousFrameRate;
        }

        [Serializable]
        private sealed class Report
        {
            public bool Passed, DedicatedServer, WorldInitialized, WaterMovedDown, QuantityConserved, FrameGcRecorderValid;
            public string Error, UnityVersion, GraphicsDevice, ConfiguredBackend, EffectiveBackend, RecordedAtUtc;
            public float ConfiguredHz, ExpectedHz;
            public double MeasuredSeconds, MeasuredHz;
            public long MeasuredTicks, TotalTicks, MaximumFrameGcBytes;
            public int MeasuredFrames, InjectedGmu, InjectionLiquidCellY, ObservedLowerLiquidCellY;
            public ulong LiquidGmu, PendingGmu;
        }
    }
}
