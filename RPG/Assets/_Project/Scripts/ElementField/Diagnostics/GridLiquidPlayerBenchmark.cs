#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.ElementField
{
    /// <summary>
    /// Development Player 的一次性 1080p 基线。独立原型和 P7 Grid 对照场景才自动安装；
    /// 正式 Release 不编译，Editor 也不运行。Update 只写预分配数组。
    /// </summary>
    public sealed class GridLiquidPlayerBenchmark : MonoBehaviour
    {
        private static readonly Vector3 EmptyGroundPosition = new Vector3(-8f, 1f, -4f);
        private static readonly ProfilerMarker UpdateMarker = new ProfilerMarker("GridLiquid.Benchmark.Update");
        private const int MaximumPhaseSamples = 32768;
        [Serializable]
        private sealed class PhaseReport
        {
            public int Samples, TotalFrames, DroppedSamples, GpuSamples;
            public int ActiveMax, ResidentChunksMax, ExpandedSectorsMax, GcNonzeroFrames;
            public float P50FrameMs, P95FrameMs, MaxFrameMs;
            public float P95MainMs, P95RenderMs, P95GpuMs;
            public float MaxMainMs, MaxRenderMs, MaxGpuMs;
            public long MaxGcBytes;
        }

        private sealed class PhaseSamples
        {
            // Uncapped 的 15 秒窗口可能超过 2048 帧；四阶段总数组约 2 MiB，
            // 仅专用 Development 采样器预留。仍报告异常超高帧率下的覆盖量。
            private readonly float[] _frame = new float[MaximumPhaseSamples];
            private readonly float[] _main = new float[MaximumPhaseSamples];
            private readonly float[] _render = new float[MaximumPhaseSamples];
            private readonly float[] _gpu = new float[MaximumPhaseSamples];
            private int _cursor, _count, _total;
            private float _frameMax, _mainMax, _renderMax, _gpuMax;
            private long _gcMax;
            private int _gcNonzero, _activeMax, _residentChunksMax, _expandedSectorsMax;

            public void Record(float frameMs, long mainNs, long renderNs,
                double gpuMs, long gcBytes, int active, int residentChunks, int expandedSectors)
            {
                float mainMs = mainNs / 1000000f;
                float renderMs = renderNs / 1000000f;
                float validGpuMs = gpuMs > 0d ? (float)gpuMs : 0f;
                _frame[_cursor] = frameMs;
                _main[_cursor] = mainMs;
                _render[_cursor] = renderMs;
                _gpu[_cursor] = validGpuMs;
                _cursor = (_cursor + 1) % _frame.Length;
                if (_count < _frame.Length) _count++;
                _total++;
                _frameMax = Mathf.Max(_frameMax, frameMs);
                _mainMax = Mathf.Max(_mainMax, mainMs);
                _renderMax = Mathf.Max(_renderMax, renderMs);
                _gpuMax = Mathf.Max(_gpuMax, validGpuMs);
                _gcMax = Math.Max(_gcMax, gcBytes);
                if (gcBytes > 0) _gcNonzero++;
                _activeMax = Mathf.Max(_activeMax, active);
                _residentChunksMax = Mathf.Max(_residentChunksMax, residentChunks);
                _expandedSectorsMax = Mathf.Max(_expandedSectorsMax, expandedSectors);
            }

            public PhaseReport Finish()
            {
                // 报告只在采样结束创建；排序也只在退出前发生一次。
                float[] sorted = new float[_count];
                Array.Copy(_frame, sorted, _count);
                Array.Sort(sorted);
                float[] gpuValid = new float[_count];
                int gpuCount = 0;
                for (int i = 0; i < _count; i++)
                    if (_gpu[i] > 0f) gpuValid[gpuCount++] = _gpu[i];
                return new PhaseReport
                {
                    Samples = _count,
                    TotalFrames = _total,
                    DroppedSamples = Math.Max(0, _total - _frame.Length),
                    GpuSamples = gpuCount,
                    P50FrameMs = _count > 0 ? sorted[_count / 2] : 0f,
                    P95FrameMs = _count > 0 ? sorted[(int)((_count - 1) * .95f)] : 0f,
                    MaxFrameMs = _frameMax,
                    P95MainMs = Percentile95(_main, _count),
                    P95RenderMs = Percentile95(_render, _count),
                    P95GpuMs = Percentile95(gpuValid, gpuCount),
                    MaxMainMs = _mainMax,
                    MaxRenderMs = _renderMax,
                    MaxGpuMs = _gpuMax,
                    MaxGcBytes = _gcMax,
                    GcNonzeroFrames = _gcNonzero,
                    ActiveMax = _activeMax,
                    ResidentChunksMax = _residentChunksMax,
                    ExpandedSectorsMax = _expandedSectorsMax
                };
            }

            private static float Percentile95(float[] values, int count)
            {
                if (count == 0) return 0f;
                float[] sorted = new float[count];
                Array.Copy(values, sorted, count);
                Array.Sort(sorted);
                return sorted[(int)((count - 1) * .95f)];
            }
        }

        [Serializable]
        private sealed class Report
        {
            public string Scene, Error, Action, InitialTotal, FinalTotal, Pending;
            public string StableQuantity, CommittedGmu, Output;
            public string AutoSeedInspectionsAtAction, AutoSeedInspectionsFinal;
            public float FirstCommittedAfterDepositMs;
            public float FirstStateChangeAfterActionMs;
            public float FirstPartialCompressedMs, AllThreePartialCompressedMs;
            public int Width, Height, ResidentChunks, ActiveCells, StableRegions;
            public int ResidentExpandedSectors;
            public int InitialStableRegions;
            public int DenseTerrainOpenAccepted, DenseTerrainCloseAccepted, DenseExpectedAddedGmu;
            public bool DenseDepositAccepted, DenseQuantityAccounted;
            public uint Rejected, Overflow, ExpandedStableRegions, RejectedStableExpansions;
            public uint RecapturedStableRegions, RejectedStableRecaptures;
            public uint AutoPartialAtAction, AutoPartialFinal;
            public bool TotalPreserved;
            public bool MainRecorderValid, RenderRecorderValid, GcRecorderValid, GpuTimingValid;
            public string FrameRateMode, UnityVersion, Cpu, Gpu, GraphicsApi;
            public int RequestedTargetFrameRate, EffectiveTargetFrameRate, VSyncCount;
            public bool FrameRateConfigurationChanged;
            public int BenchmarkGcSampledFrames, BenchmarkGcNonzeroFrames;
            public long BenchmarkGcBytes, BenchmarkMaxGcBytes;
            public PhaseReport Startup, Baseline, AfterDeposit, Settled;
        }

        private readonly PhaseSamples _startup = new PhaseSamples();
        private readonly PhaseSamples _baseline = new PhaseSamples();
        private readonly PhaseSamples _afterDeposit = new PhaseSamples();
        private readonly PhaseSamples _settled = new PhaseSamples();
        private readonly FrameTiming[] _timings = new FrameTiming[1];
        private LiquidWorldRuntime _liquids;
        private LiquidPrototypeProbe _waterProbe;
        private StableLiquidStaticCellAuthoring _sectorFloor;
        private readonly StableLiquidStaticCellAuthoring[] _denseFloors =
            new StableLiquidStaticCellAuthoring[3];
        private ProfilerRecorder _main, _render, _gc;
        private float _elapsed;
        private bool _deposited, _finished, _gpuTimingValid;
        private bool _sectorCloseAttempted, _sectorCloseAccepted;
        private bool _sectorMode, _partialMode, _p7Mode, _denseMode;
        private int _denseOpenAccepted, _denseCloseAccepted, _denseExpectedAddedGmu;
        private bool _denseDepositAccepted;
        private bool _p7Ready;
        private float _p7WarmupSeconds, _warmupTotalSeconds;
        private ulong _initialQuantity;
        private ulong _autoSeedAtAction;
        private uint _autoPartialAtAction;
        private int _initialStableRegions;
        private bool _hasInitialQuantity, _firstCommitRecorded;
        private float _firstCommittedAfterDepositMs;
        private float _firstPartialCompressedMs = -1f;
        private float _allThreePartialCompressedMs = -1f;
        private bool _uncapped, _frameRateConfigurationChanged;
        private int _requestedTargetFrameRate;
        private int _benchmarkGcSampledFrames, _benchmarkGcNonzeroFrames;
        private long _benchmarkGcBytes, _benchmarkMaxGcBytes;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (Application.isEditor || (scene != "LiquidWorldPrototype"
                && scene != "LiquidSectorPrototype"
                && scene != "LiquidPartialSurfacePrototype"
                && scene != "LiquidDenseLakePrototype"
                && scene != "P7_DemoRun_GridLiquid"))
                return;
            // P7 对照也可能被开发者作为普通 Development Build 运行。只有显式
            // 命令行标志才接管分辨率、记录 30 秒并退出，不能干扰日常试玩。
            if ((scene == "P7_DemoRun_GridLiquid" || scene == "LiquidDenseLakePrototype")
                && !HasArgument("-grid-liquid-benchmark")) return;
            var host = new GameObject("Grid Liquid Player Benchmark");
            host.AddComponent<GridLiquidPlayerBenchmark>();
        }

        private static bool HasArgument(string flag)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
                if (arguments[i] == flag) return true;
            return false;
        }

        private void Start()
        {
            if (Application.isEditor) { enabled = false; return; }
            // Target60 检查节拍，Uncapped 检查余量。只在显式基准中覆盖设置，
            // 报告保留请求与实际值，不能把含 60 FPS 等待的 Main Thread 时间当工作成本。
            _uncapped = HasArgument("-grid-liquid-uncapped");
            _requestedTargetFrameRate = _uncapped ? -1 : 60;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = _requestedTargetFrameRate;
            // 独立 Player 采样会被外部工具读取报告；窗口暂时失焦时仍需继续
            // 推进 Gameplay，否则 30 秒时间窗会被系统后台停帧扭曲。
            Application.runInBackground = true;
            Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            _sectorMode = SceneManager.GetActiveScene().name == "LiquidSectorPrototype";
            _partialMode = SceneManager.GetActiveScene().name == "LiquidPartialSurfacePrototype";
            _p7Mode = SceneManager.GetActiveScene().name == "P7_DemoRun_GridLiquid";
            _denseMode = SceneManager.GetActiveScene().name == "LiquidDenseLakePrototype";
            _liquids = FindFirstObjectByType<LiquidWorldRuntime>(FindObjectsInactive.Include);
            if (_sectorMode || _denseMode)
            {
                var floors = FindObjectsByType<StableLiquidStaticCellAuthoring>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < floors.Length; i++)
                    if (floors[i].name == "Sector Lake Internal Floor - Toggle In Play Mode")
                        _sectorFloor = floors[i];
                if (_denseMode)
                    for (int i = 0; i < floors.Length; i++)
                    {
                        string name = floors[i].name;
                        if (name == "Water Dense Lake Internal Floor - Toggle In Play Mode")
                            _denseFloors[0] = floors[i];
                        else if (name == "Poison Dense Lake Internal Floor - Toggle In Play Mode")
                            _denseFloors[1] = floors[i];
                        else if (name == "Sticky Dense Lake Internal Floor - Toggle In Play Mode")
                            _denseFloors[2] = floors[i];
                    }
            }
            if (!_p7Mode && !_sectorMode)
            {
                var probes = FindObjectsByType<LiquidPrototypeProbe>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                for (int i = 0; i < probes.Length; i++)
                    if (probes[i].name == "Water Deposit Probe") _waterProbe = probes[i];
            }
            _main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
            _render = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread", 1);
            _gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 1);
            if (_liquids == null || (_denseMode && (_denseFloors[0] == null
                || _denseFloors[1] == null || _denseFloors[2] == null))
                || (!_p7Mode && (_sectorMode ? _sectorFloor == null : _waterProbe == null)))
                Finish("测量场景缺少 Grid Liquid Runtime 或对应的原型触发器。");
        }

        private void Update()
        {
            if (_finished || _liquids == null || !_liquids.IsFluidInitialized) return;
            if (Application.targetFrameRate != _requestedTargetFrameRate || QualitySettings.vSyncCount != 0)
                _frameRateConfigurationChanged = true;
            long before = GC.GetAllocatedBytesForCurrentThread();
            using (UpdateMarker.Auto()) AdvanceBenchmark();
            // Finish 的 JSON／排序是退出阶段的一次性分配，排除它才能定位逐帧工具开销。
            // Deposit/地形动作若在工具调用内分配，该离散帧仍如实计入，不扣减全局 GC。
            if (_finished) return;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            _benchmarkGcSampledFrames++;
            if (allocated > 0)
            {
                _benchmarkGcNonzeroFrames++;
                _benchmarkGcBytes += allocated;
                _benchmarkMaxGcBytes = Math.Max(_benchmarkMaxGcBytes, allocated);
            }
        }

        private void AdvanceBenchmark()
        {
            if ((_p7Mode || _denseMode) && !_p7Ready)
            {
                // P7 和大湖首次加载可能出现秒级长帧。必须先连续三秒有正常帧，
                // 再开始五秒静止基线；否则一帧就吃掉整个 Baseline 时间窗。
                float delta = Time.unscaledDeltaTime;
                _warmupTotalSeconds += delta;
                _p7WarmupSeconds = delta <= .1f ? _p7WarmupSeconds + delta : 0f;
                RecordFrame(_startup);
                if (_p7WarmupSeconds >= 3f)
                {
                    _p7Ready = true;
                    _elapsed = 3f;
                }
                else if (_warmupTotalSeconds >= 30f)
                    Finish("启动后 30 秒仍未得到连续正常帧，静水／操作采样尚未开始。");
                return;
            }
            _elapsed += Time.unscaledDeltaTime;
            if (_partialMode)
            {
                // 初次自动接管可能发生在 3 秒基线窗口之前，单独记下它的首次观测时间。
                uint compressed = _liquids.AutoPartialSurfaceCompressedRegions;
                if (compressed > 0 && _firstPartialCompressedMs < 0f)
                    _firstPartialCompressedMs = _elapsed * 1000f;
                if (compressed >= 3 && _allThreePartialCompressedMs < 0f)
                    _allThreePartialCompressedMs = _elapsed * 1000f;
            }
            if (!_hasInitialQuantity && _elapsed >= 3f)
            {
                _initialQuantity = _liquids.TotalQuantity;
                _initialStableRegions = _liquids.StableRegionCount;
                _hasInitialQuantity = true;
            }
            if (!_deposited && _elapsed >= 8f)
            {
                _autoPartialAtAction = _liquids.AutoPartialSurfaceCompressedRegions;
                _autoSeedAtAction = _liquids.AutoSeedInspections;
                if (_denseMode)
                {
                    // 只扰动三座大湖各一个 Sector；不逐格遍历湖底，也不提高 Solver 预算。
                    for (int i = 0; i < _denseFloors.Length; i++)
                    {
                        bool wasClosed = _denseFloors[i].IsClosed;
                        _denseFloors[i].Toggle();
                        if (wasClosed && !_denseFloors[i].IsClosed) _denseOpenAccepted++;
                    }
                    _denseExpectedAddedGmu = _waterProbe.Quantity;
                    _denseDepositAccepted = _waterProbe.TryDeposit();
                }
                else if (_sectorMode) _sectorFloor.Toggle();
                else if (!_p7Mode)
                {
                    // 部分水面基准把新增水投向原湖，检验已接管湖面能否继续收水；
                    // 普通水滩基准仍把 Probe 移到干燥区，保留既有首显延迟语义。
                    if (!_partialMode) _waterProbe.transform.position = EmptyGroundPosition;
                    _waterProbe.Deposit();
                }
                // P7 从第 8 秒开始留给玩家施法；不自动生成物质，以免掩盖真实操作成本。
                _deposited = true;
            }
            if ((_sectorMode || _denseMode) && _deposited
                && !_sectorCloseAttempted && _elapsed >= 13f)
            {
                // 只在单次原型基准中关回原地板；真实关卡仍由地形 Authoring
                // 先向液体世界申请，再改 Collider。记录接受与否，不能只凭截图推断。
                if (_denseMode)
                {
                    for (int i = 0; i < _denseFloors.Length; i++)
                    {
                        // 开口若被拒绝，不能第二次 Toggle 反而打开仍关闭的地板。
                        if (_denseFloors[i].IsClosed) continue;
                        _denseFloors[i].Toggle();
                        if (_denseFloors[i].IsClosed) _denseCloseAccepted++;
                    }
                }
                else
                {
                    _sectorFloor.Toggle();
                    _sectorCloseAccepted = _sectorFloor.IsClosed;
                }
                _sectorCloseAttempted = true;
            }
            if (_deposited && !_firstCommitRecorded
                && (_sectorMode || _denseMode
                    ? _liquids.ExpandedStableRegions > 0
                    : _liquids.TotalQuantity > _initialQuantity))
            {
                _firstCommittedAfterDepositMs = Mathf.Max(0f, (_elapsed - 8f) * 1000f);
                _firstCommitRecorded = true;
            }
            // 启动期单列，避免场景首显卡顿被 3～8 秒的静态基线掩盖。
            PhaseSamples phase = _elapsed < 3f ? _startup
                : _elapsed < 8f ? _baseline
                : _elapsed < (_p7Mode ? 23f : 13f) ? _afterDeposit : _settled;
            RecordFrame(phase);
            // 三材料顺序接管要经历各自的安静窗口；部分湖延长观察，
            // 避免在第三座湖刚到资格线前结束并误报永久无法压缩。
            if (_elapsed >= (_p7Mode ? 30f : _denseMode ? 35f : _partialMode || _sectorMode ? 25f : 18f))
                Finish(null);
        }

        private void RecordFrame(PhaseSamples phase)
        {
            FrameTimingManager.CaptureFrameTimings();
            uint timings = FrameTimingManager.GetLatestTimings(1, _timings);
            // 某些 Editor/驱动组合虽返回一条记录，GPU 时间却始终为 0；
            // 报告必须把这种情况标为无效，不能当作 GPU 已达 60 FPS。
            if (timings > 0 && _timings[0].gpuFrameTime > 0d)
                _gpuTimingValid = true;
            phase.Record(Time.unscaledDeltaTime * 1000f,
                _main.Valid ? _main.LastValue : 0,
                _render.Valid ? _render.LastValue : 0,
                timings > 0 ? _timings[0].gpuFrameTime : 0,
                _gc.Valid ? _gc.LastValue : 0,
                _liquids.ActiveCellCount, _liquids.ResidentChunkCount,
                _liquids.ResidentExpandedSectors);
        }

        private void Finish(string error)
        {
            if (_finished) return;
            _finished = true;
            var report = new Report
            {
                Scene = SceneManager.GetActiveScene().path,
                Error = error,
                Action = _denseMode ? "ThreeDenseLakesLocalTerrainAndWaterDeposit"
                    : _p7Mode ? "ManualP7LiquidGameplayFromSecond8"
                    : _sectorMode ? (_sectorCloseAccepted
                        ? "OpenThenCloseSectorFloor" : "OpenSectorFloorCloseRejected")
                    : _partialMode ? "DepositWaterIntoPartialLake" : "DepositWater",
                Width = Screen.width,
                Height = Screen.height,
                FrameRateMode = _uncapped ? "Uncapped" : "Target60",
                RequestedTargetFrameRate = _requestedTargetFrameRate,
                EffectiveTargetFrameRate = Application.targetFrameRate,
                VSyncCount = QualitySettings.vSyncCount,
                FrameRateConfigurationChanged = _frameRateConfigurationChanged,
                UnityVersion = Application.unityVersion,
                Cpu = SystemInfo.processorType,
                Gpu = SystemInfo.graphicsDeviceName,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                BenchmarkGcSampledFrames = _benchmarkGcSampledFrames,
                BenchmarkGcNonzeroFrames = _benchmarkGcNonzeroFrames,
                BenchmarkGcBytes = _benchmarkGcBytes,
                BenchmarkMaxGcBytes = _benchmarkMaxGcBytes,
                InitialTotal = _initialQuantity.ToString(),
                FinalTotal = _liquids != null ? _liquids.TotalQuantity.ToString() : "0",
                Pending = _liquids != null ? _liquids.PendingQuantity.ToString() : "0",
                StableQuantity = _liquids != null ? _liquids.StableQuantity.ToString() : "0",
                CommittedGmu = _liquids != null && _liquids.Transfers != null
                    ? _liquids.Transfers.CommittedGmu.ToString() : "0",
                ResidentChunks = _liquids != null ? _liquids.ResidentChunkCount : 0,
                ActiveCells = _liquids != null ? _liquids.ActiveCellCount : 0,
                StableRegions = _liquids != null ? _liquids.StableRegionCount : 0,
                ResidentExpandedSectors = _liquids != null
                    ? _liquids.ResidentExpandedSectors : 0,
                InitialStableRegions = _initialStableRegions,
                DenseTerrainOpenAccepted = _denseOpenAccepted,
                DenseTerrainCloseAccepted = _denseCloseAccepted,
                DenseExpectedAddedGmu = _denseExpectedAddedGmu,
                DenseDepositAccepted = _denseDepositAccepted,
                DenseQuantityAccounted = _denseMode && _denseDepositAccepted && _liquids != null
                    && _liquids.TotalQuantity == _initialQuantity + (ulong)_denseExpectedAddedGmu
                    && _liquids.PendingQuantity == 0,
                Rejected = _liquids != null ? _liquids.RejectedDepositCount : 0,
                Overflow = _liquids != null ? _liquids.ActivityOverflowCount : 0,
                ExpandedStableRegions = _liquids != null
                    ? _liquids.ExpandedStableRegions : 0,
                RejectedStableExpansions = _liquids != null
                    ? _liquids.RejectedStableExpansions : 0,
                RecapturedStableRegions = _liquids != null
                    ? _liquids.RecapturedStableRegions : 0,
                RejectedStableRecaptures = _liquids != null
                    ? _liquids.RejectedStableRecaptures : 0,
                AutoPartialAtAction = _autoPartialAtAction,
                AutoPartialFinal = _liquids != null
                    ? _liquids.AutoPartialSurfaceCompressedRegions : 0,
                AutoSeedInspectionsAtAction = _autoSeedAtAction.ToString(),
                AutoSeedInspectionsFinal = _liquids != null
                    ? _liquids.AutoSeedInspections.ToString() : "0",
                TotalPreserved = _sectorMode && _liquids != null
                    && _liquids.TotalQuantity == _initialQuantity
                    && _liquids.PendingQuantity == 0,
                MainRecorderValid = _main.Valid,
                RenderRecorderValid = _render.Valid,
                GcRecorderValid = _gc.Valid,
                GpuTimingValid = _gpuTimingValid,
                FirstCommittedAfterDepositMs = _firstCommitRecorded
                    && !_sectorMode && !_denseMode && !_p7Mode ? _firstCommittedAfterDepositMs : -1f,
                FirstStateChangeAfterActionMs = !_p7Mode && _firstCommitRecorded
                    ? _firstCommittedAfterDepositMs : -1f,
                FirstPartialCompressedMs = _firstPartialCompressedMs,
                AllThreePartialCompressedMs = _allThreePartialCompressedMs,
                Startup = _startup.Finish(),
                Baseline = _baseline.Finish(),
                AfterDeposit = _afterDeposit.Finish(),
                Settled = _settled.Finish()
            };
            string stem = _denseMode ? "grid-liquid-dense" : _p7Mode ? "grid-liquid-p7" : _sectorMode ? "grid-liquid-sector"
                : _partialMode ? "grid-liquid-partial" : "grid-liquid";
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..",
                stem + (_uncapped ? "-uncapped-result.json" : "-result.json")));
            report.Output = output;
            try { File.WriteAllText(output, JsonUtility.ToJson(report, true)); }
            catch (Exception exception) { Game.Core.GameLog.Error(exception.Message, "ElementField"); }
            Application.Quit(error == null ? 0 : 1);
        }

        private void OnDestroy()
        {
            _main.Dispose();
            _render.Dispose();
            _gc.Dispose();
        }
    }
}
#endif
