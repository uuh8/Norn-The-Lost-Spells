using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using Game.Character;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Game.Diagnostics
{
    /// <summary>显式 CLI 单独启动的 Windows Player。克隆正式 Home 几何与材料配置，只在此实例使用单机权威；
    /// 不创建角色、库存、账户或副本会话。计时来自实际主相机每次结束渲染到下一次结束渲染的墙钟。</summary>
    [DefaultExecutionOrder(28000)]
    public sealed partial class CellularNativeFrameBenchmark : MonoBehaviour
    {
        private enum Phase { Prepare, Activate, Prefill, Warmup, Measure, Evidence, BottleView, Finished }
        private const string Option = "--norn-cellular-frame-benchmark";
        private const int MaxFrames = 65536, MaxTicks = 2048;
        private static readonly MaterialId[] Species = { MaterialId.Water, MaterialId.Sand, MaterialId.Steam };
        private readonly Report _report = new Report();
        private Phase _phase;
        private string _path;
        private GameObject _map;
        private Camera _camera;
        private BoxCollider _groundBox;
        private Bounds _ground;
        private ElementWorldRuntime _world;
        private CellularMaterialRenderer _renderer;
        private int _size, _layers, _feedPerType, _seedIndex, _feedIndex, _activateAt, _lastCameraFrame = -1;
        private long _lastFeedTick;
        private double _phaseAt, _previousFrameAt, _bootAt;
        private bool _capturingImage;
        private int _configuredLimit;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Application.isEditor) return;
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, Option);
            if (index < 0) return;
            if (index + 1 >= args.Length) { GameLog.Error("基准缺少 JSON 输出路径。", "CellularBenchmark"); Application.Quit(2); return; }
            var host = new GameObject("Native cellular frame benchmark");
            DontDestroyOnLoad(host);
            var probe = host.AddComponent<CellularNativeFrameBenchmark>();
            try { probe.Configure(args, args[index+1]); }
            catch (Exception ex) { probe.Finish(ex.ToString()); }
        }

        private void Configure(string[] args, string path)
        {
            _path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(_path));
            _bottleView = Array.IndexOf(args, "--norn-cellular-bottle-view") >= 0;
            _report.Tier = Argument(args,"--norn-cellular-tier","medium").ToLowerInvariant();
            switch (_report.Tier)
            {
                case "empty": _size=0; _layers=0; _feedPerType=0; break;
                case "low": _size=14; _layers=1; _feedPerType=4; break;
                case "medium": _size=28; _layers=2; _feedPerType=12; break;
                case "high": _size=40; _layers=3; _feedPerType=24; break;
                default: throw new ArgumentException("tier 取 empty、low、medium 或 high。");
            }
            _report.RequestedSeconds = Seconds(args,"--norn-cellular-seconds",12,3,60);
            _report.WarmupSeconds = Seconds(args,"--norn-cellular-warmup",3,1,20);
            _report.RequestedWidth=(int)Seconds(args,"-screen-width",0,0,16384);
            _report.RequestedHeight=(int)Seconds(args,"-screen-height",0,0,16384);
            _report.DetailedRejectionObservations=Array.IndexOf(args,"--norn-cellular-rejection-detail")>=0;
            _report.StartedUtc = DateTime.UtcNow.ToString("O"); _report.UnityVersion=Application.unityVersion;
            _report.GraphicsAPI=SystemInfo.graphicsDeviceType.ToString(); _report.Gpu=SystemInfo.graphicsDeviceName;
            _report.Vendor=SystemInfo.graphicsDeviceVendor; _report.VendorId=SystemInfo.graphicsDeviceVendorID;
            _report.BatchMode=Application.isBatchMode; _report.Cpu=SystemInfo.processorType;
            _report.RamMiB=SystemInfo.systemMemorySize; _report.VramMiB=SystemInfo.graphicsMemorySize;
            _report.FrameTimingEnabled=FrameTimingManager.IsFeatureEnabled();
            _report.SeedPlannedPerMaterial=_size*_size*_layers; _report.FeedAttemptsPerMaterialPerTick=_feedPerType;
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)
                throw new InvalidOperationException("基准需要正常可见的 Windows Player 窗口和图形设备。");
            Application.runInBackground=true; QualitySettings.vSyncCount=0; Application.targetFrameRate=-1;
            Time.timeScale=1f; _bootAt=Time.realtimeSinceStartupAsDouble;
            RenderPipelineManager.endCameraRendering+=CameraRendered;
            ElementWorldRuntime.FrameBenchmarkTickCompleted+=TickCompleted;
        }

        private static string Argument(string[] args, string name, string fallback)
        {
            int index=Array.IndexOf(args,name);
            if(index<0) return fallback;
            if(index+1>=args.Length) throw new ArgumentException(name + " 缺少参数。");
            return args[index+1];
        }

        private static double Seconds(string[] args,string name,double fallback,double low,double high)
        {
            string token=Argument(args,name,fallback.ToString(CultureInfo.InvariantCulture));
            if (!double.TryParse(token,NumberStyles.Float,CultureInfo.InvariantCulture,out double seconds) || !(seconds>=low && seconds<=high))
                throw new ArgumentOutOfRangeException(name);
            return seconds;
        }

        private void Update()
        {
            if (_phase==Phase.Finished || string.IsNullOrEmpty(_path)) return;
            try
            {
                double now=Time.realtimeSinceStartupAsDouble;
                if(now-_bootAt>_report.RequestedSeconds+_report.WarmupSeconds+160) throw new TimeoutException("真实相机或场景没有按时推进。");
                if(_world!=null && _world.IsSimulationFaulted) throw new InvalidOperationException("正式世界报告 Faulted。");
                switch (_phase)
                {
                    case Phase.Prepare: Prepare(); break;
                    case Phase.Activate: if (Time.frameCount>=_activateAt) Activate(); break;
                    case Phase.BottleView: UpdateBottleView(); break;
                    case Phase.Prefill:
                        int end=Math.Min(_report.SeedPlannedPerMaterial*3,_seedIndex+384);
                        for(;_seedIndex<end;_seedIndex++) Deposit(_seedIndex/3,_seedIndex%3,false);
                        if(_seedIndex==_report.SeedPlannedPerMaterial*3)
                        {
                            _world.SetSimulationSuspended(false); _phase=Phase.Warmup; _phaseAt=now;
                            GameLog.Info("正式地图预填完成，开始模拟预热。","CellularBenchmark");
                        }
                        break;
                    case Phase.Warmup:
                    case Phase.Measure:
                        if (_world.WorldTick!=_lastFeedTick) Feed();
                        break;
                    case Phase.Evidence:
                        if(!_capturingImage && (_renderer.NativeFrame.DirtyVisiblePages==0 || now-_phaseAt>12))
                        { _capturingImage=true; StartCoroutine(CaptureEvidence()); }
                        break;
                }
            }
            catch(Exception ex){ Finish(ex.ToString()); }
        }

        private void Prepare()
        {
            // 菜单只作为 Build 的正常启动入口。关停其根对象和显示层，在独立的内存 Scene 实例化地图，
            // 由游戏 Catalog 的真实引用带入 Mesh、Collider、材质和 Shader，运行中不写共享资产。
            var launch=SceneManager.GetActiveScene();
            var scene=SceneManager.CreateScene("NativeCellularMeasurement");
            SceneManager.SetActiveScene(scene);
            foreach(var root in launch.GetRootGameObjects()) root.SetActive(false);
            var catalog=Resources.Load<OnlineDungeonCatalog>(OnlineDungeonCatalog.ResourcePath);
            if(catalog==null || !catalog.TryGet("home",out var definition)) throw new InvalidOperationException("正式 Home 定义缺失。");
            _report.MapSource=definition.SourceScenePath; _report.MapSourceHash=definition.SourceHash;
            // 导出 World 的 root 固定为 inactive。隔一帧待联网绑定销毁后才激活，避免其 Awake 接管这次独立单机实例。
            if(definition.WorldPrefab.activeSelf) throw new InvalidOperationException("正式地图 prefab 需要能在 inactive 边界设置所有权。");
            _map=Instantiate(definition.WorldPrefab); _map.name=definition.WorldPrefab.name;
            _world=_map.GetComponentInChildren<ElementWorldRuntime>(true);
            if(_world==null || _world.CellularMapContentId!="home") throw new InvalidOperationException("没有取得正式 Home 材料世界。");
            var online=_map.GetComponent<OnlineMaterialWorldBinding>();
            if(online==null) throw new InvalidOperationException("缺少应隔离的在线世界绑定。");
            Destroy(online); _world.ConfigureExecutionMode(WorldExecutionMode.SinglePlayer); _world.enabled=true;
            foreach(var box in _map.GetComponentsInChildren<BoxCollider>(true))
                if(box.name.StartsWith("HomeGround_",StringComparison.Ordinal)){_groundBox=box;break;}
            if(_groundBox==null)throw new InvalidOperationException("找不到原地图 HomeGround 地板引用。");
            // 在线 prefab 正常清除单机角色引用。独立单机实例使用地板中心的兴趣点，保证整个 40m 地板的
            // 活动距离采用正式 Profile。此引用在 Awake 之前填入，只作用于本次 clone。
            var anchor=new GameObject("Map center material interest").transform; anchor.SetParent(_map.transform,false);
            anchor.position=_groundBox.transform.TransformPoint(_groundBox.center+Vector3.up*(_groundBox.size.y*.5f));
            var interest=typeof(ElementWorldRuntime).GetField("_interestPoint",BindingFlags.Instance|BindingFlags.NonPublic);
            if(interest==null || interest.FieldType!=typeof(Transform))throw new MissingFieldException("原单机 Interest Point 字段不存在。");
            interest.SetValue(_world,anchor);
            _renderer=_world.GetComponent<CellularMaterialRenderer>();
            if(_renderer==null) _renderer=_world.gameObject.AddComponent<CellularMaterialRenderer>();
            _renderer.enabled=true; _renderer.CaptureNativeFrames=true;
            _activateAt=Time.frameCount+2; _phase=Phase.Activate;
        }

        private void Activate()
        {
            _world.gameObject.SetActive(true);
            _map.SetActive(true);
            if(!_world.isActiveAndEnabled || !_world.IsInitialized || !_world.UsesCellularWorld || _world.CellularCellCapacity==0)
                throw new InvalidOperationException("正式材料权威初始化失败，无法开始原生帧采集。");
            _world.SetSimulationSuspended(true);
            _ground=_groundBox.bounds;
            if(_ground.size.x<30 || _ground.size.z<30) throw new InvalidOperationException("未取得正式 Home 开挖地板坐标。");
            foreach(var old in FindObjectsByType<Camera>(FindObjectsSortMode.None)) old.enabled=false;
            foreach(var canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None)) canvas.enabled=false;
            var node=new GameObject("Cellular measurement camera"); node.tag="MainCamera";
            _camera=node.AddComponent<Camera>(); _camera.fieldOfView=50; _camera.nearClipPlane=.2f; _camera.farClipPlane=180;
            _camera.allowDynamicResolution=false;
            var center=new Vector3(_ground.center.x,_ground.max.y,_ground.center.z);
            _camera.transform.position=center+new Vector3(0,44,-43); _camera.transform.LookAt(center+Vector3.up*5);
            if (_bottleView) ConfigureBottleView(center);
            if(_report.RequestedWidth>0 && _report.RequestedHeight>0 && (Screen.width!=_report.RequestedWidth || Screen.height!=_report.RequestedHeight))
                Screen.SetResolution(_report.RequestedWidth,_report.RequestedHeight,FullScreenMode.Windowed);
            _report.CameraPosition=_camera.transform.position; _report.CameraForward=_camera.transform.forward;
            _report.GroundMeters=_ground.size; _report.FieldOfView=_camera.fieldOfView; _report.CellSize=_world.CellSize;
            _report.Capacity=_world.CellularCellCapacity; _report.RequestedTickSeconds=_world.CellularTickDuration;
            var cap=typeof(ElementCellWorldProfile).GetProperty("MaximumCells",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            if(cap==null)throw new MissingMemberException("缺少正式驻留格上限。");
            _report.ConfiguredResidentCellLimit=_configuredLimit=(int)cap.GetValue(_world.CellularDefinition);
            _renderer.NativeQuadrantCenter=Vector3Int.FloorToInt((center-_world.Origin)/_world.CellSize);
            if(Camera.main!=_camera) throw new InvalidOperationException("目标相机未取得 MainCamera。");
            _phase=_bottleView ? Phase.BottleView : Phase.Prefill;
        }

        private void Deposit(int index,int type,bool feed)
        {
            int cover=_size*_size, column=index%cover, ix=column%_size, iz=column/_size;
            // 取地板长宽的 90%，横跨整张地图。三种材料各用不同高度/小偏移，静态障碍的拒绝数如实进入报告。
            float wx=_ground.center.x+((ix+.5f)/_size-.5f)*_ground.size.x*.9f;
            float wz=_ground.center.z+((iz+.5f)/_size-.5f)*_ground.size.z*.9f;
            int layer=index/cover;
            int y=(feed? 18:6) + (ix*3+iz*7)%7 + layer*5 + type*3;
            var p=Vector3Int.FloorToInt((new Vector3(wx,_ground.max.y,wz)-_world.Origin)/_world.CellSize);
            p+=new Vector3Int(type-1,y,type==1?1:0);
            uint amount=type==0 ? _world.CellularCellCapacity*3/4 : type==1 ? _world.CellularCellCapacity : _world.CellularCellCapacity/2;
            if(_world.TryDepositCellular(p,Species[type],Math.Max(1u,amount)))
            {
                if(feed) _report.FeedAccepted[type]++; else _report.SeedAccepted[type]++;
            }
            else
            {
                if(feed) _report.FeedRejected[type]++; else _report.SeedRejected[type]++;
                var row=_report.Materials[type];bool atLimit=_world.CellularOccupiedCount>=_configuredLimit;
                if(atLimit)row.RejectedAtResidentLimit++;else row.RejectedBelowResidentLimit++;
                if(_report.DetailedRejectionObservations)ObserveRejectedTarget(row,p,atLimit);
            }
        }

        private void ObserveRejectedTarget(SpeciesEvidence row,Vector3Int cell,bool atLimit)
        {
            // 失败时的同格读数可区分满表与该地址本身已占用，两者可以同时发生。只在单独显式请求
            // 这份明细时执行，便于同一 A/B 保持相同观测成本；不重新投放，也不尝试搬动邻格气体。
            bool hasDynamic=_world.TryGetCellularContents(cell,out _,out _,out _,out _);
            bool resting=_world.CellularRestingLiquids?.TryGetCell(cell,out _,out _)==true;
            bool terrain=_world.GetTerrainMaterial(cell)!=MaterialId.Empty;
            if(terrain)row.RejectedTargetTerrain++;
            else if(resting)row.RejectedTargetResting++;
            else if(hasDynamic){if(atLimit)row.RejectedExistingAtLimit++;else row.RejectedExistingBelowLimit++;}
            else if(atLimit)row.RejectedEmptyAtLimit++;else row.RejectedEmptyBelowLimit++;
        }

        private void Feed()
        {
            _lastFeedTick=_world.WorldTick;
            if(_feedPerType==0) return;
            bool measure=_phase==Phase.Measure;
            long start=measure?System.Diagnostics.Stopwatch.GetTimestamp():0;
            // 地图继续由正常 World.Update 推进。注入和观测分开，新增源本身不能被当作材料发生移动。
            _renderer.CaptureNativeMotion=false;
            int columns=_size*_size;
            for(int i=0;i<_feedPerType;i++)
            {
                int index=(int)(((long)_feedIndex++*197)%columns);
                for(int type=0;type<3;type++) Deposit(index,type,true);
            }
            _renderer.CaptureNativeMotion=_phase==Phase.Measure;
            if(measure)
            {
                if(_feeds==MaxTicks)throw new InvalidOperationException("Feed 样本超过固定容量。");
                double duration=(System.Diagnostics.Stopwatch.GetTimestamp()-start)*(1000d/System.Diagnostics.Stopwatch.Frequency);
                _feedMs[_feeds++]=duration;_feedInFrame+=duration;
            }
        }

        private void OnDestroy()
        {
            RenderPipelineManager.endCameraRendering-=CameraRendered;
            ElementWorldRuntime.FrameBenchmarkTickCompleted-=TickCompleted;
        }
    }
}
