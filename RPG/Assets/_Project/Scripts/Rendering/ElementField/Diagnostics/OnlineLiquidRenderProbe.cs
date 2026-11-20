using System;
using System.IO;
using System.Runtime.InteropServices;
using Game.Core;
using Game.ElementField;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Game.Rendering
{
    /// <summary>
    /// 显式Native液体诊断的表现端观察者。原ElementField探针只能观察只读输入，不能反向引用Rendering；
    /// 因此实际Capture/回退/Dispatch计数与最后GPU结果由本层独立记录，通过PID、Frame和同一启动信号关联。
    /// 默认无组件、无Update、无Readback；只在Development Player且显式环境变量为绝对目录时创建。
    /// 性能采样与截图/Readback严格分段：后者有同步、分配与IO成本，不能混入前者再宣称日常帧耗时。
    /// </summary>
    [DefaultExecutionOrder(20)]
    public sealed class OnlineLiquidRenderProbe : MonoBehaviour
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private const int Capacity=2048;
        private const double Duration=10;
        private readonly Sample[] _samples=new Sample[Capacity];
        private readonly FrameTiming[] _timings=new FrameTiming[1];
        private GridLiquidFieldSurfaceRenderer _renderer;
        private string _directory,_path,_imagePath;
        private int _count,_dropped,_pid,_finishFrame,_waitingReadbacks;
        private long _processStartTicks;
        private double _nextPoll,_startedAt,_lastFrameAt,_finishedAt;
        private bool _started,_measured,_written,_captureRequested;
        private Report _report;
        private ProfilerRecorder _captureRecorder,_dispatchRecorder,_drawRecorder;
        private ProfilerRecorder _lateUpdateRecorder,_initializeRecorder,_gpuUploadRecorder;
        private int _gcMaximumGeneration,_lastGc0,_lastGc1,_lastGc2;
        private Camera _evidenceCamera;
        private RenderTexture _target;
        private bool _imageReadbackStarted,_imageDone,_frameTimingUsable;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallExplicitProbe()
        {
            if(Application.isEditor)return;
            string directory=Environment.GetEnvironmentVariable("NORN_LIQUID_MOTION_PROBE");
            if(string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)
                || SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)return;
            // 创建一次常驻观察者，跨入口场景等待实际Renderer就绪；它不持有任何Gameplay权威。
            var host=new GameObject("Explicit liquid renderer diagnostics");
            DontDestroyOnLoad(host);var probe=host.AddComponent<OnlineLiquidRenderProbe>();probe.Initialize(directory);
        }
        private void Initialize(string directory)
        {
            _directory=Path.GetFullPath(directory);
            using(var process=System.Diagnostics.Process.GetCurrentProcess())
            {_pid=process.Id;_processStartTicks=process.StartTime.ToUniversalTime().Ticks;}
            string stem="liquid-render-"+_pid+"-"+_processStartTicks;
            _path=Path.Combine(_directory,stem+".json");_imagePath=Path.Combine(_directory,stem+".png");
        }
        private void LateUpdate()
        {
            if(_written || string.IsNullOrEmpty(_directory))return;
            double now=Time.realtimeSinceStartupAsDouble;
            if(!_started)
            {
                if(now<_nextPoll)return;_nextPoll=now+.25;
                if(!File.Exists(Path.Combine(_directory,"start.request")))return;
                // 只在启动等待阶段低频查找；不能误绑仍在销毁过程中的Hub本地源。
                // 这次数组分配发生在计时窗口之前；开始后每帧只读取已绑定Renderer。
                var candidates=FindObjectsByType<GridLiquidFieldSurfaceRenderer>(FindObjectsSortMode.None);
                for(int i=0;i<candidates.Length;i++)if(candidates[i].IsReady && candidates[i].Source is WorldReplicaLiquidReadModel){_renderer=candidates[i];break;}
                if(_renderer==null)return;
                Begin(now);return;
            }
            if(_measured){CompleteEvidence(now);return;}
            if(_renderer==null || !_renderer.IsReady)
            {FinishMeasure(now,"RendererDetached");return;}
            double elapsed=(now-_lastFrameAt)*1000;_lastFrameAt=now;
            if(now-_startedAt>=Duration){FinishMeasure(now,"Completed");return;}
            // 本机URP Core的RuntimeProfilerTests也显式跳过batchmode FrameTiming。
            // 即使返回一份CPU数字，也不能据它认定实际相机GPU帧被测量；因此这里明确标为不可用。
            if(_frameTimingUsable)FrameTimingManager.CaptureFrameTimings();
            bool timingAvailable=_frameTimingUsable && FrameTimingManager.GetLatestTimings(1,_timings)>0;
            var source=_renderer.Source;var playback=source as ILiquidPresentationPlaybackSource;
            var replica=source as WorldReplicaLiquidReadModel;
            var frame=_renderer.Frame;
            // CollectionCount是进程级累计次数，不是本Renderer的分配量或GC停顿时间。
            // Unity的Boehm后端可能只有第0代；不存在的代记-1，不能把“不支持”当作零次回收。
            int gc0=GC.CollectionCount(0),gc1=_gcMaximumGeneration>=1?GC.CollectionCount(1):-1,gc2=_gcMaximumGeneration>=2?GC.CollectionCount(2):-1;
            if(_count<_samples.Length)
            {
                // Frame与各ProfilerRecorder取值时段同时保存。Recorder的LastValue来自最近已结束样本，
                // 不把它误称为当前整个Frame的准确归因；尤其GPU队列等待可能跨帧。
                _samples[_count++]=new Sample{Frame=Time.frameCount,ElapsedSeconds=now-_startedAt,WallMilliseconds=elapsed,
                    CaptureMilliseconds=_renderer.CaptureMilliseconds,LastCaptureFrame=_renderer.LastCaptureFrame,DisplayLagMilliseconds=_renderer.DisplayLagMilliseconds,
                    SurfacePages=_renderer.SurfacePages,RebuiltPages=_renderer.RebuiltSurfacePages,VisiblePages=_renderer.VisibleSurfacePages,
                    QueryReach=_renderer.Frame!=null?_renderer.Frame.QueryReach:Vector3Int.zero,
                    HybridPages=_renderer.HybridPages,SmallDrops=_renderer.SmallDropCount,Alpha=_renderer.InterpolationAlpha,
                    CpuFallback=_renderer.CpuFallback,CpuPending=_renderer.IsPresentationPending,HasHybridCache=_renderer.HasVisibleHybridCache,
                    CpuBuildFrames=_renderer.CpuBuildFrames,CpuBuildMilliseconds=_renderer.CpuBuildMilliseconds,
                    SurfaceVersion=source.SurfaceVersion,SourceWindow=playback!=null?playback.PresentationWindowId:0,
                    CapturedWindow=frame!=null?frame.PlaybackWindowId:0,
                    SourceHolding=playback!=null && playback.PresentationHolding,
                    SourceSnapped=replica!=null && replica.PresentationSnapped,
                    MaterialTick=replica!=null?replica.PresentationMaterialTick:0,
                    SourceSampleTime=replica!=null?replica.PresentationSampleTime:0,
                    // 时间槽起点可因timestamp抖动晚于raw SampleTime；分析Alpha必须用真实播放起点，
                    // 否则会把有意保留的旧尾段误判成新窗口卡顿或时钟错误。
                    SourcePlaybackStartTime=replica!=null?replica.PresentationPlaybackStartTime:0,
                    BufferedWindows=replica!=null?replica.BufferedPresentationWindows:0,
                    BufferedOverflows=replica!=null?replica.BufferedPresentationOverflows:0,
                    BufferedDiscontinuities=replica!=null?replica.BufferedPresentationDiscontinuities:0,
                    PendingSequence=replica!=null?replica.PendingPresentationSequence:0,
                    PresentationServerTime=replica!=null?replica.PresentationServerTime:0,
                    AttemptedSequence=replica!=null?replica.LastAttemptedMotionSequence:0,
                    AttemptedSampleTime=replica!=null?replica.LastAttemptedMotionSampleTime:0,
                    AttemptedAnchors=replica!=null?replica.LastAttemptedMotionAnchorCount:0,
                    AttemptedRows=replica!=null?replica.LastAttemptedMotionRowCount:0,
                    PublishFailure=replica!=null?replica.LastMotionPublishFailure:(byte)0,
                    Witness=replica!=null?replica.LastMotionWitnessDiagnostic:default,
                    Hold=replica!=null?replica.LastMotionHoldDiagnostic:default,
                    HoldChecks=replica!=null?replica.MotionHoldChecks:0,
                    CaptureRecorderNanoseconds=_captureRecorder.Valid?_captureRecorder.LastValue:0,
                    DispatchRecorderNanoseconds=_dispatchRecorder.Valid?_dispatchRecorder.LastValue:0,
                    DrawRecorderNanoseconds=_drawRecorder.Valid?_drawRecorder.LastValue:0,
                    LateUpdateRecorderNanoseconds=_lateUpdateRecorder.Valid?_lateUpdateRecorder.LastValue:0,
                    InitializeRecorderNanoseconds=_initializeRecorder.Valid?_initializeRecorder.LastValue:0,
                    GpuUploadRecorderNanoseconds=_gpuUploadRecorder.Valid?_gpuUploadRecorder.LastValue:0,
                    GcGeneration0Count=gc0,GcGeneration1Count=gc1,GcGeneration2Count=gc2,
                    GcGeneration0Delta=gc0-_lastGc0,GcGeneration1Delta=gc1>=0?gc1-_lastGc1:-1,GcGeneration2Delta=gc2>=0?gc2-_lastGc2:-1,
                    FrameTimingAvailable=timingAvailable,CpuFrameMilliseconds=timingAvailable?_timings[0].cpuFrameTime:0,
                    GpuFrameMilliseconds=timingAvailable?_timings[0].gpuFrameTime:0};
            }
            else _dropped++;
            _lastGc0=gc0;_lastGc1=gc1;_lastGc2=gc2;
        }
        private void Begin(double now)
        {
            _frameTimingUsable=!Application.isBatchMode && FrameTimingManager.IsFeatureEnabled();
            _captureRecorder=ProfilerRecorder.StartNew(ProfilerCategory.Scripts,"GridLiquid.Field.Capture",1);
            _dispatchRecorder=ProfilerRecorder.StartNew(ProfilerCategory.Scripts,"GridLiquid.Field.Dispatch",1);
            _drawRecorder=ProfilerRecorder.StartNew(ProfilerCategory.Scripts,"GridLiquid.Field.Draw",1);
            _lateUpdateRecorder=ProfilerRecorder.StartNew(ProfilerCategory.Scripts,"GridLiquid.Field.LateUpdate",1);
            _initializeRecorder=ProfilerRecorder.StartNew(ProfilerCategory.Scripts,"GridLiquid.Field.Initialize",1);
            _gpuUploadRecorder=ProfilerRecorder.StartNew(ProfilerCategory.Scripts,"GridLiquid.Field.GpuInputUpload",1);
            _gcMaximumGeneration=GC.MaxGeneration;
            _lastGc0=GC.CollectionCount(0);_lastGc1=_gcMaximumGeneration>=1?GC.CollectionCount(1):-1;_lastGc2=_gcMaximumGeneration>=2?GC.CollectionCount(2):-1;
            _report=new Report{ProcessId=_pid,ProcessStartUtcTicks=_processStartTicks,StartedUtc=DateTime.UtcNow.ToString("O"),
                Scene=_renderer.gameObject.scene.path,Device=SystemInfo.graphicsDeviceName,
                Scope="10 second renderer LateUpdate observations. Profiler LastValue can refer to the preceding ended sample. Initialize normally precedes IsReady/this recording window; zero is not proof of zero startup cost. GC counts/deltas are process-wide collection counts, not allocation bytes or pause milliseconds; unsupported generations are -1. Screenshot/readbacks occur after this interval; no Zero-GC claim.",
                CaptureRecorderValid=_captureRecorder.Valid,DispatchRecorderValid=_dispatchRecorder.Valid,DrawRecorderValid=_drawRecorder.Valid,
                LateUpdateRecorderValid=_lateUpdateRecorder.Valid,InitializeRecorderValid=_initializeRecorder.Valid,GpuUploadRecorderValid=_gpuUploadRecorder.Valid,
                GcMaximumGeneration=_gcMaximumGeneration,
                RequestedSeconds=Duration,ImagePath=_imagePath,ImageWorldTick=-1,
                ApplicationBatchMode=Application.isBatchMode,FrameTimingFeatureEnabled=FrameTimingManager.IsFeatureEnabled(),FrameTimingUsable=_frameTimingUsable};
            _startedAt=_lastFrameAt=now;_started=true;
        }
        private void FinishMeasure(double now,string state)
        {
            if(_measured)return;_measured=true;_finishedAt=now;_finishFrame=Time.frameCount;
            _report.State=state;_report.FinishedUtc=DateTime.UtcNow.ToString("O");_report.ActualSeconds=now-_startedAt;
            _report.Samples=new Sample[_count];Array.Copy(_samples,_report.Samples,_count);_report.DroppedSamples=_dropped;
            DisposeRecorders();
            // 原生帧数和GPU几何不是同一种证据：这里只在测量完成后显式读取当前的实际Buffer。
            // 请求排在本帧Renderer的Dispatch之后；保存请求Frame/Window，以免将下一窗口数据误配到性能采样。
            if(state=="Completed" && _renderer!=null && _renderer.IsReady && !_renderer.CpuFallback && SystemInfo.supportsAsyncGPUReadback
                && _renderer.DebugArguments!=null && _renderer.DebugVertices!=null && _renderer.SurfacePages>0)
            {
                _report.ReadbackFrame=Time.frameCount;_report.ReadbackWindow=_renderer.Frame!=null?_renderer.Frame.PlaybackWindowId:0;
                _report.ReadbackPages=_renderer.SurfacePages;_report.ReadbackStarted=true;_waitingReadbacks=2;
                AsyncGPUReadback.Request(_renderer.DebugArguments,ArgumentsReceived);
                int side=_renderer.Frame.ChunkEdge*_renderer.SurfaceSubdivision+6;
                // 只读本帧实际Owner前缀；池后部可能保留历史页面，不能算作当前可见几何。
                int count=Math.Min(_renderer.DebugVertices.count,_renderer.SurfacePages*side*side*side);
                AsyncGPUReadback.Request(_renderer.DebugVertices,count*_renderer.DebugVertices.stride,0,VerticesReceived);
            }
            else _report.ReadbackUnavailableReason="No active GPU surface, Renderer detached, CPU fallback, or AsyncGPUReadback unsupported.";
        }
        private void ArgumentsReceived(AsyncGPUReadbackRequest request)
        {
            _waitingReadbacks--;_report.ReadbackFailed|=request.hasError;if(request.hasError)return;
            var data=request.GetData<uint>();ulong indices=0;
            for(int i=0;i<_report.ReadbackPages && i*5+4<data.Length;i++)
            {indices+=data[i*5];if(data[i*5]>0)_report.NonemptyGpuPages++;}
            _report.GpuTriangleCount=indices/3;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex{public Vector3 Position;public float Valid;public Vector3 Normal;public uint A,B,C,D,Pad;}
        private void VerticesReceived(AsyncGPUReadbackRequest request)
        {
            _waitingReadbacks--;_report.ReadbackFailed|=request.hasError;if(request.hasError)return;
            var data=request.GetData<Vertex>();bool first=true;
            for(int i=0;i<data.Length;i++)
            {
                var vertex=data[i];if(vertex.Valid<=0)continue;
                if(!float.IsFinite(vertex.Position.x)||!float.IsFinite(vertex.Position.y)||!float.IsFinite(vertex.Position.z))
                {_report.InvalidGpuVertices++;continue;}
                _report.ValidGpuVertices++;
                if(first){_report.GpuMinimum=_report.GpuMaximum=vertex.Position;first=false;}
                else{_report.GpuMinimum=Vector3.Min(_report.GpuMinimum,vertex.Position);_report.GpuMaximum=Vector3.Max(_report.GpuMaximum,vertex.Position);}
            }
            // 有效缓冲顶点仍可能是旧槽：真正draw存在须结合Arguments的非空页，截图还要人工确认目标在视野内。
        }
        private void CompleteEvidence(double now)
        {
            if(!_captureRequested && Time.frameCount>_finishFrame+1)
            {
                _captureRequested=true;_report.ImageRequestedFrame=Time.frameCount;
                try
                {
                    Directory.CreateDirectory(_directory);
                    // Camera/RT只在性能窗口结束后创建。固定视角拍摄正式注水区，
                    // 不挪动玩家相机、不暂停世界，也不要求Renderer新增数量或重跑Solver。
                    var host=new GameObject("Explicit liquid evidence camera");host.transform.SetParent(transform,false);
                    _evidenceCamera=host.AddComponent<Camera>();_evidenceCamera.enabled=false;
                    Vector3 center=(_renderer!=null&&_renderer.Source!=null?_renderer.Source.Origin:Vector3.zero)+new Vector3(.125f,1.75f,.125f);
                    host.transform.position=center+new Vector3(-5,3.5f,-6);host.transform.LookAt(center);
                    _evidenceCamera.fieldOfView=45;_evidenceCamera.nearClipPlane=.05f;_evidenceCamera.farClipPlane=100;
                    // 不CopyFrom玩家相机，避免Viewport、CullingMask、投影/抖动矩阵或Target残留。
                    // 这个Camera只由下面一次显式URP请求驱动，batchmode也不依赖自动相机循环。
                    _evidenceCamera.rect=new Rect(0,0,1,1);_evidenceCamera.cullingMask=~0;
                    _evidenceCamera.clearFlags=CameraClearFlags.SolidColor;_evidenceCamera.backgroundColor=new Color(.025f,.035f,.06f,1);
                    _evidenceCamera.allowHDR=false;_evidenceCamera.allowMSAA=false;_evidenceCamera.aspect=1280f/720;
                    _evidenceCamera.ResetProjectionMatrix();_evidenceCamera.ResetWorldToCameraMatrix();_evidenceCamera.ResetCullingMatrix();
                    var additional=host.AddComponent<UniversalAdditionalCameraData>();
                    additional.requiresDepthTexture=true;additional.requiresColorTexture=true;additional.renderPostProcessing=false;additional.renderType=CameraRenderType.Base;
                    _target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);_target.Create();
                    _evidenceCamera.targetTexture=_target;
                    _report.CameraPosition=host.transform.position;_report.CameraForward=host.transform.forward;
                    _report.CameraActiveInHierarchy=host.activeInHierarchy;_report.CameraEnabled=_evidenceCamera.enabled;
                    _report.CameraRect=_evidenceCamera.rect;_report.CameraCullingMask=_evidenceCamera.cullingMask;
                    _report.ImageWidth=_target.width;_report.ImageHeight=_target.height;_report.ImageCameraStartedFrame=Time.frameCount;
                }
                catch(Exception error){_report.ImageError=error.Message;_imageDone=true;}
            }
            if(_target!=null && !_imageReadbackStarted && Time.frameCount>_report.ImageCameraStartedFrame+1)
            {
                _imageReadbackStarted=true;_report.ImageReadbackFrame=Time.frameCount;
                // 截图阶段仅查一次，按同一个只读源关联实际World，不用场景里的第一个Runtime猜Tick。
                var worlds=FindObjectsByType<ElementWorldRuntime>(FindObjectsSortMode.None);
                for(int i=0;i<worlds.Length;i++)if(_renderer!=null && ReferenceEquals(worlds[i].GridLiquids,_renderer.Source))
                {_report.ImageWorldTick=worlds[i].ExecutionMode==WorldExecutionMode.Replica&&worlds[i].ReplicaStore!=null?worlds[i].ReplicaStore.WorldTick:worlds[i].WorldTick;break;}
                _report.ImageSourceWindow=_renderer!=null&&_renderer.Frame!=null?_renderer.Frame.PlaybackWindowId:0;
                try
                {
                    // 已核对URP17本地ProcessRenderRequests与SingleCameraRequest实现：
                    // destination不为空会直接执行该Camera的RenderSingleCameraInternal并Submit到GPU。
                    // 此调用与Readback、PNG写入都发生在10秒性能区间之后，单独记时。
                    var request=new UniversalRenderPipeline.SingleCameraRequest{destination=_target,mipLevel=0,slice=0,face=CubemapFace.Unknown};
                    _report.RenderRequestSupported=RenderPipeline.SupportsRenderRequest(_evidenceCamera,request);
                    long began=System.Diagnostics.Stopwatch.GetTimestamp();
                    RenderPipeline.SubmitRenderRequest(_evidenceCamera,request);_report.RenderRequestSubmitted=true;
                    _report.RenderRequestMilliseconds=(System.Diagnostics.Stopwatch.GetTimestamp()-began)*1000.0/System.Diagnostics.Stopwatch.Frequency;
                    AsyncGPUReadback.Request(_target,0,TextureFormat.RGBA32,ImageReceived);
                }
                catch(Exception error){_report.ImageError=error.Message;_imageDone=true;}
            }
            if(!_captureRequested || (_waitingReadbacks>0 || !_imageDone) && now-_finishedAt<8)return;
            _report.ImageExists=File.Exists(_imagePath);
            _report.PixelEvidenceScope="Native fixed-camera 1280x720 RenderTexture from explicit URP SingleCameraRequest after sampling. Validity requires nonuniform/nonblack pixels, not merely ImageExists. Inspect PNG to confirm liquid and framing.";
            WriteReport();
        }
        private void ImageReceived(AsyncGPUReadbackRequest request)
        {
            _imageDone=true;if(request.hasError){_report.ImageError="RenderTexture AsyncGPUReadback failed";return;}
            var texture=new Texture2D(1280,720,TextureFormat.RGBA32,false);
            try
            {
                var bytes=request.GetData<byte>();byte r=bytes[0],g=bytes[1],b=bytes[2];int maximum=0,nonbackground=0;
                for(int i=0;i+3<bytes.Length;i+=4)
                {
                    maximum=Math.Max(maximum,Math.Max(bytes[i],Math.Max(bytes[i+1],bytes[i+2])));
                    if(Math.Abs(bytes[i]-r)+Math.Abs(bytes[i+1]-g)+Math.Abs(bytes[i+2]-b)>12)nonbackground++;
                }
                _report.ImageMaximumRgb=maximum;_report.ImageNonBackgroundPixels=nonbackground;_report.ImageAllBlack=maximum<=2;
                _report.ImagePixelsValid=_report.RenderRequestSubmitted && !_report.ImageAllBlack && nonbackground>16;
                if(!_report.ImagePixelsValid)_report.ImageError="No actual nonuniform visible pixels after explicit render request; PNG existence is insufficient.";
                texture.LoadRawTextureData(bytes);texture.Apply();File.WriteAllBytes(_imagePath,texture.EncodeToPNG());
            }
            catch(Exception error){_report.ImageError=error.Message;}
            finally{Destroy(texture);}
        }
        private void WriteReport()
        {
            if(_written || _report==null)return;_written=true;
            try
            {
                Directory.CreateDirectory(_directory);
                using(var stream=new FileStream(_path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
                using(var writer=new StreamWriter(stream))writer.Write(JsonUtility.ToJson(_report,true));
            }
            catch(Exception error){GameLog.Warn("显式液体Renderer诊断写入失败："+error.Message,"LiquidRenderProbe");}
            if(_evidenceCamera!=null)_evidenceCamera.enabled=false;
            enabled=false;
        }
        private void OnDestroy()
        {
            if(_started && !_measured)DisposeRecorders();
            if(_evidenceCamera!=null)_evidenceCamera.targetTexture=null;
            if(_target!=null){_target.Release();Destroy(_target);}
        }
        private void DisposeRecorders()
        {
            _captureRecorder.Dispose();_dispatchRecorder.Dispose();_drawRecorder.Dispose();
            _lateUpdateRecorder.Dispose();_initializeRecorder.Dispose();_gpuUploadRecorder.Dispose();
        }
        private void OnApplicationQuit()
        {
            if(!_started || _written)return;
            if(!_measured)FinishMeasure(Time.realtimeSinceStartupAsDouble,"ProcessQuit");
            // 退出不能等待GPU回调；明确报告未完成，不能阻塞正常退出或当作成功Readback。
            _report.ReadbackIncompleteOnQuit=_waitingReadbacks>0;WriteReport();
        }
        [Serializable] private struct Sample
        {
            public int Frame,LastCaptureFrame,SurfacePages,RebuiltPages,VisiblePages,HybridPages,SmallDrops,CpuBuildFrames;
            public Vector3Int QueryReach;
            public double ElapsedSeconds,WallMilliseconds,CpuBuildMilliseconds,CpuFrameMilliseconds,GpuFrameMilliseconds;
            public float CaptureMilliseconds,DisplayLagMilliseconds,Alpha;
            public uint SurfaceVersion,MaterialTick;public ulong SourceWindow,CapturedWindow,PendingSequence;
            public double SourceSampleTime,SourcePlaybackStartTime;
            public int BufferedWindows;public long BufferedOverflows,BufferedDiscontinuities;
            public double PresentationServerTime,AttemptedSampleTime;
            public ulong AttemptedSequence;public byte PublishFailure;
            public int AttemptedAnchors,AttemptedRows;
            public WorldLiquidMotionValidationDiagnostic Witness,Hold;
            public long HoldChecks;
            public long CaptureRecorderNanoseconds,DispatchRecorderNanoseconds,DrawRecorderNanoseconds;
            public long LateUpdateRecorderNanoseconds,InitializeRecorderNanoseconds,GpuUploadRecorderNanoseconds;
            public int GcGeneration0Count,GcGeneration1Count,GcGeneration2Count,GcGeneration0Delta,GcGeneration1Delta,GcGeneration2Delta;
            public bool CpuFallback,CpuPending,HasHybridCache,FrameTimingAvailable,SourceHolding,SourceSnapped;
        }
        [Serializable] private sealed class Report
        {
            public string State,StartedUtc,FinishedUtc,Scene,Device,Scope,ImagePath,ImageError,PixelEvidenceScope,ReadbackUnavailableReason;
            public int ProcessId,DroppedSamples,ReadbackFrame,ReadbackPages,NonemptyGpuPages,ValidGpuVertices,InvalidGpuVertices,ImageRequestedFrame,ImageCameraStartedFrame,ImageReadbackFrame,ImageWidth,ImageHeight,CameraCullingMask,ImageNonBackgroundPixels,ImageMaximumRgb;
            public long ImageWorldTick;public ulong ImageSourceWindow;
            public long ProcessStartUtcTicks;public ulong ReadbackWindow,GpuTriangleCount;
            public double RequestedSeconds,ActualSeconds,RenderRequestMilliseconds;
            public bool ApplicationBatchMode,FrameTimingFeatureEnabled,FrameTimingUsable,CameraActiveInHierarchy,CameraEnabled,RenderRequestSupported,RenderRequestSubmitted,ImagePixelsValid,ImageAllBlack;
            public Rect CameraRect;
            public bool CaptureRecorderValid,DispatchRecorderValid,DrawRecorderValid,ReadbackStarted,ReadbackFailed,ReadbackIncompleteOnQuit,ImageExists;
            public bool LateUpdateRecorderValid,InitializeRecorderValid,GpuUploadRecorderValid;
            public int GcMaximumGeneration;
            public Vector3 GpuMinimum,GpuMaximum,CameraPosition,CameraForward;
            public Sample[] Samples;
        }
#endif
    }
}
