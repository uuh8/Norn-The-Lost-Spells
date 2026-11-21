using System;
using System.Collections;
using System.IO;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Diagnostics
{
    public sealed partial class CellularNativeFrameBenchmark
    {
        [Serializable] private sealed class Distribution
        { public int Count; public double Mean, P50, P95, P99, Maximum; }

        [Serializable] private sealed class SpeciesEvidence
        {
            public string Material;
            public int MovingTicks, GainCells, LossCells, DrawFrames, MaxDrawsPerFrame, MaxVisibleTrianglesOrGasCells;
            public int[] MovingTicksInQuarters = new int[4];
            public int[] MovingTicksInQuadrants = new int[4];
            public int RejectedAtResidentLimit, RejectedBelowResidentLimit;
            public int RejectedTargetTerrain, RejectedTargetResting, RejectedExistingAtLimit, RejectedExistingBelowLimit, RejectedEmptyAtLimit, RejectedEmptyBelowLimit;
            public float NearestDrawPageCenterMeters = -1;
            public int FinalExplicitCells, ChangedPixelsAboveNoise;
            public long FinalExplicitQuantity;
        }

        [Serializable] private sealed class TerrainEvidence
        {
            public int SampleCellY, WidthCells, DepthCells, Columns, Soil, Rock, Other, Empty;
            public Vector3Int ProbeCell;
            public string Before, AfterRemoval, AfterRestore;
            public bool FoundUnoccupiedEdge, Removed, Restored;
        }

        [Serializable] private sealed class Report
        {
            public string Status, Error, StartedUtc, FinishedUtc, Tier, MapSource, MapSourceHash, UnityVersion, GraphicsAPI, Gpu, Vendor, Cpu, Screenshot;
            public string Measurement="Rendered camera end-to-end wall interval, normal Player Update and URP. GPU timings by FrameTimingManager timestamp. Single-player material owner instantiated from the formal Home prefab; no combat actors, network or inventory session.";
            public string Evidence="Quantities and draws are explicit Water, Sand, Steam. Moving tick requires gains and losses during one real world tick, excluding benchmark deposits. Pixels compare the same frozen camera against hiding exactly one drawn material after timing; RGB threshold = repeat-capture noise + 2.";
            public double RequestedSeconds, WarmupSeconds, ActualSeconds, MeanFps, ObservedTicksPerSecond;
            public int Width, Height, VSync, TargetFrameRate, VendorId, RamMiB, VramMiB, SeedPlannedPerMaterial, FeedAttemptsPerMaterialPerTick;
            public int TargetCameraFrames, MissedRendererFrames, LostFocusFrames, AllThreeDrawFrames, SimultaneousMovingTicks;
            public int ConfiguredResidentCellLimit, MaxOldestVisibleDirtyFrames;
            public int RequestedWidth, RequestedHeight;
            public bool DetailedRejectionObservations;
            public int MaxResidentPages, MaxVisiblePages, MaxVisibleDirtyPages, MaxRebuiltPagesPerFrame, MaxOccupiedCells, MaxActiveCells, MaxProcessedLastTick;
            public int FramesOver16_67ms, FramesOver33_33ms, FramesOver100ms, PositiveGpuTimings, PositiveSyncIntervalTimings, PixelsCompared, RepeatImageChangedPixels;
            public int[] SeedAccepted = new int[3], SeedRejected = new int[3], FeedAccepted = new int[3], FeedRejected = new int[3];
            public SpeciesEvidence[] Materials = { new SpeciesEvidence {Material="Water"}, new SpeciesEvidence {Material="Sand"}, new SpeciesEvidence {Material="Steam"} };
            public TerrainEvidence Ground = new TerrainEvidence();
            public bool BatchMode, FrameTimingEnabled, HardwareGraphicsDevice, RealCameraRendered, PixelCapture, VerifiedThreeMaterials;
            public Vector3 CameraPosition, CameraForward, GroundMeters;
            public float FieldOfView, CellSize, RequestedTickSeconds;
            public uint Capacity;
            public long FirstWorldTick, LastWorldTick, DroppedDebtFrames;
            public Distribution FrameMs, TickMs, GpuFrameMs;
            public Distribution FeedMs, PresentationMs, RebuildMs, OldestVisibleDirtyMs;
            public double[] RawFrameMs, RawTickMs, RawGpuMs;
            public double[] RawFeedMs, RawFrameFeedMs, RawFrameWorldTickMs, RawOldestVisibleDirtyMs, RawVisibleDirtyPages;
        }

        // 固定容量，避免计时期间扩容；大量超快帧达到上限时明确结束，绝不丢掉慢帧继续算分位数。
        private readonly double[] _frameMs = new double[MaxFrames], _tickMs = new double[MaxTicks], _gpuMs = new double[MaxFrames];
        private readonly FrameTiming[] _timings = new FrameTiming[4];
        private readonly double[] _feedMs=new double[MaxTicks],_frameFeed=new double[MaxFrames],_frameTick=new double[MaxFrames],
            _presentation=new double[MaxFrames],_rebuild=new double[MaxFrames],_age=new double[MaxFrames],_dirty=new double[MaxFrames];
        private readonly int[] _quadrantGains=new int[12],_quadrantLosses=new int[12];
        private double _feedInFrame,_tickInFrame;
        private int _feeds;
        private int _frames, _ticks, _gpuCount, _gpuReceiptsSkipped;
        private ulong _gpuTimestamp, _beginDebt;

        private void CameraRendered(ScriptableRenderContext context, Camera camera)
        {
            if(camera!=_camera || camera==null || _lastCameraFrame==Time.frameCount || _phase!=Phase.Measure && _phase!=Phase.Warmup) return;
            _lastCameraFrame=Time.frameCount;
            try
            {
                double now=Time.realtimeSinceStartupAsDouble;
                ReadGpuTimings(_phase==Phase.Measure);
                if(_phase==Phase.Warmup)
                {
                    if(now-_phaseAt<_report.WarmupSeconds) return;
                    _report.Width=camera.pixelWidth; _report.Height=camera.pixelHeight;
                    _report.VSync=QualitySettings.vSyncCount; _report.TargetFrameRate=Application.targetFrameRate;
                    if(_report.Width<=0 || _report.Height<=0 || _report.VSync!=0 || _report.TargetFrameRate!=-1)
                        throw new InvalidOperationException("相机没有可见分辨率或正式帧仍有限帧设置。");
                    if(_report.RequestedWidth>0 && _report.Width!=_report.RequestedWidth || _report.RequestedHeight>0 && _report.Height!=_report.RequestedHeight)
                        throw new InvalidOperationException($"请求分辨率 {_report.RequestedWidth}×{_report.RequestedHeight}，真实相机 {_report.Width}×{_report.Height}。");
                    _report.FirstWorldTick=_world.WorldTick; _beginDebt=_world.DroppedDebtFrameCount;
                    _renderer.TakeNativeMotion();_renderer.TakeNativeQuadrants(_quadrantGains,_quadrantLosses); _renderer.CaptureNativeMotion=true;
                    _phaseAt=_previousFrameAt=now; _phase=Phase.Measure;
                    GameLog.Info("开始计时：正常 PlayerLoop、世界 Tick 和目标相机绘制。","CellularBenchmark");
                    return;
                }
                if(_frames==MaxFrames) throw new InvalidOperationException("帧样本缓冲已满。");
                double elapsed=(now-_previousFrameAt)*1000; _previousFrameAt=now;
                _frameMs[_frames++]=elapsed; _report.TargetCameraFrames++;
                int index=_frames-1;
                _frameFeed[index]=_feedInFrame;_frameTick[index]=_tickInFrame;_feedInFrame=_tickInFrame=0;
                if(elapsed>1000d/60) _report.FramesOver16_67ms++;
                if(elapsed>1000d/30) _report.FramesOver33_33ms++;
                if(elapsed>100) _report.FramesOver100ms++;
                if(!Application.isFocused) _report.LostFocusFrames++;
                _report.MaxOccupiedCells=Math.Max(_report.MaxOccupiedCells,_world.CellularOccupiedCount);
                _report.MaxActiveCells=Math.Max(_report.MaxActiveCells,_world.CellularActiveCount);
                var draw=_renderer.NativeFrame;
                if(draw.Frame!=Time.frameCount || draw.CameraId!=camera.GetInstanceID()) _report.MissedRendererFrames++;
                else
                {
                    _report.MaxResidentPages=Math.Max(_report.MaxResidentPages,draw.ResidentPages);
                    _report.MaxVisiblePages=Math.Max(_report.MaxVisiblePages,draw.VisiblePages);
                    _report.MaxVisibleDirtyPages=Math.Max(_report.MaxVisibleDirtyPages,draw.DirtyVisiblePages);
                    _report.MaxRebuiltPagesPerFrame=Math.Max(_report.MaxRebuiltPagesPerFrame,draw.RebuiltPages);
                    _age[index]=draw.OldestDirtyMs;_dirty[index]=draw.DirtyVisiblePages;_presentation[index]=draw.PresentationMs;_rebuild[index]=draw.RebuildMs;
                    _report.MaxOldestVisibleDirtyFrames=Math.Max(_report.MaxOldestVisibleDirtyFrames,draw.OldestDirtyFrames);
                    DrawEvidence(_report.Materials[0],draw.WaterDraws,draw.WaterTriangles,draw.WaterNearestSquared);
                    DrawEvidence(_report.Materials[1],draw.SandDraws,draw.SandTriangles,draw.SandNearestSquared);
                    DrawEvidence(_report.Materials[2],draw.GasDraws,draw.GasCells,draw.GasNearestSquared);
                    if(draw.WaterDraws>0 && draw.SandDraws>0 && draw.GasDraws>0) _report.AllThreeDrawFrames++;
                }
                if(now-_phaseAt<_report.RequestedSeconds) return;
                _report.ActualSeconds=now-_phaseAt; _report.LastWorldTick=_world.WorldTick;
                _report.DroppedDebtFrames=(long)(_world.DroppedDebtFrameCount-_beginDebt);
                _report.ObservedTicksPerSecond=_ticks/_report.ActualSeconds; _report.MeanFps=_frames/_report.ActualSeconds;
                _phase=Phase.Evidence; _phaseAt=now; _renderer.CaptureNativeMotion=false;
                _world.SetSimulationSuspended(true); Time.timeScale=0;
                _world.VisitCellularOccupiedCells(RecordFinalAmount);
                GameLog.Info("计时结束，冻结同一相机和世界做四组帧缓冲差分。","CellularBenchmark");
            }
            catch(Exception ex){Finish(ex.ToString());}
        }

        private static void DrawEvidence(SpeciesEvidence row,int draws,int geometry,float nearestSquared)
        {
            if(draws==0)return;
            row.DrawFrames++; row.MaxDrawsPerFrame=Math.Max(row.MaxDrawsPerFrame,draws);
            row.MaxVisibleTrianglesOrGasCells=Math.Max(row.MaxVisibleTrianglesOrGasCells,geometry);
            float nearest=Mathf.Sqrt(nearestSquared);
            if(row.NearestDrawPageCenterMeters<0 || nearest<row.NearestDrawPageCenterMeters)row.NearestDrawPageCenterMeters=nearest;
        }

        private void TickCompleted(ElementWorldRuntime world,double milliseconds)
        {
            if(world!=_world || _phase!=Phase.Measure) return;
            if(_ticks==MaxTicks){Finish("真实 Tick 样本超出固定容量。");return;}
            _tickMs[_ticks++]=milliseconds;_tickInFrame+=milliseconds;
            _report.MaxProcessedLastTick=Math.Max(_report.MaxProcessedLastTick,world.CellularCellsProcessedLastTick);
            var change=_renderer.TakeNativeMotion();
            _renderer.TakeNativeQuadrants(_quadrantGains,_quadrantLosses);
            for(int i=0;i<12;i++)if(_quadrantGains[i]>0 && _quadrantLosses[i]>0)_report.Materials[i/4].MovingTicksInQuadrants[i%4]++;
            int quarter=Mathf.Clamp((int)((Time.realtimeSinceStartupAsDouble-_phaseAt)*4/_report.RequestedSeconds),0,3);
            bool water=Movement(_report.Materials[0],change.WaterGains,change.WaterLosses,quarter);
            bool sand=Movement(_report.Materials[1],change.SandGains,change.SandLosses,quarter);
            bool gas=Movement(_report.Materials[2],change.GasGains,change.GasLosses,quarter);
            if(water && sand && gas) _report.SimultaneousMovingTicks++;
        }

        private static bool Movement(SpeciesEvidence row,int gains,int losses,int quarter)
        {
            row.GainCells+=gains; row.LossCells+=losses;
            if(gains==0 || losses==0)return false;
            row.MovingTicks++; row.MovingTicksInQuarters[quarter]++; return true;
        }

        private void ReadGpuTimings(bool measure)
        {
            if(!_report.FrameTimingEnabled)return;
            FrameTimingManager.CaptureFrameTimings();
            uint count=FrameTimingManager.GetLatestTimings((uint)_timings.Length,_timings);
            ulong maximum=_gpuTimestamp;
            for(int i=0;i<count;i++)
            {
                var timing=_timings[i]; ulong stamp=timing.frameStartTimestamp;
                if(stamp==0 || stamp<=_gpuTimestamp)continue;
                maximum=Math.Max(maximum,stamp);
                // GPU 结果异步到达。切换计时窗口后丢弃首批四个唯一时间戳，避免预热帧混入 GPU 百分位。
                if(!measure || _gpuReceiptsSkipped++<4 || !(timing.gpuFrameTime>0) || !double.IsFinite(timing.gpuFrameTime))continue;
                if(_gpuCount==MaxFrames)throw new InvalidOperationException("GPU 帧样本超出固定容量。");
                _gpuMs[_gpuCount++]=timing.gpuFrameTime;
                if(timing.syncInterval>0) _report.PositiveSyncIntervalTimings++;
            }
            _gpuTimestamp=maximum; _report.PositiveGpuTimings=_gpuCount;
        }

        private void RecordFinalAmount(Vector3Int cell)
        {
            if(!_world.TryGetCellularContents(cell,out var primary,out uint quantity,out uint steam,out _))return;
            if(primary==MaterialId.Water || primary==MaterialId.Sand)
            {var row=_report.Materials[primary==MaterialId.Water?0:1];row.FinalExplicitCells++;row.FinalExplicitQuantity+=quantity;}
            if(steam!=0){_report.Materials[2].FinalExplicitCells++;_report.Materials[2].FinalExplicitQuantity+=steam;}
        }

        private Color32[] _reference;
        private byte[] _pixelNoise;
        private IEnumerator CaptureEvidence()
        {
            var end=new WaitForEndOfFrame();
            for(int pass=0;pass<5;pass++)
            {
                _renderer.NativeHiddenMaterial=pass<2?MaterialId.Empty:Species[pass-2];
                // 先跨过一个完整 Update/LateUpdate，再在同帧最终屏幕绘制结束后读回 backbuffer。
                yield return null; yield return end;
                if(_phase==Phase.Finished)yield break;
                bool success=true;
                try{CapturePixels(pass);}catch(Exception ex){Finish(ex.ToString());success=false;}
                if(!success)yield break;
            }
            _renderer.NativeHiddenMaterial=MaterialId.Empty;
            _report.PixelCapture=true;
            try { InspectGround(); Finish(null); } catch(Exception ex) { Finish(ex.ToString()); }
        }

        private void InspectGround()
        {
            // 所有计时与像素对照都已结束。一个 XZ 行列只在真实地板顶面内侧取一个基础格；
            // 独立 clone 上做实际开挖/回填，权威读数、碰撞提交、回滚均经过地图正常公开命令。
            var ground=_report.Ground; float size=_world.CellSize; var origin=_world.Origin;
            int x0=Mathf.CeilToInt((_ground.min.x-origin.x)/size-.5f), x1=Mathf.CeilToInt((_ground.max.x-origin.x)/size-.5f);
            int z0=Mathf.CeilToInt((_ground.min.z-origin.z)/size-.5f), z1=Mathf.CeilToInt((_ground.max.z-origin.z)/size-.5f);
            ground.SampleCellY=Mathf.FloorToInt((_ground.max.y-origin.y)/size-.001f);
            ground.WidthCells=x1-x0;ground.DepthCells=z1-z0;ground.Columns=checked(ground.WidthCells*ground.DepthCells);
            if(ground.Columns<1 || ground.Columns>1000000)throw new InvalidOperationException("正式 Home 顶面超出基准取样范围。");
            MaterialId before=MaterialId.Empty;
            for(int z=z0;z<z1;z++)for(int x=x0;x<x1;x++)
            {
                var cell=new Vector3Int(x,ground.SampleCellY,z);var material=_world.GetTerrainMaterial(cell);
                if(material==MaterialId.Soil)ground.Soil++;else if(material==MaterialId.Rock)ground.Rock++;
                else if(material==MaterialId.Empty)ground.Empty++;else ground.Other++;
                bool edge=x==x0 || x==x1-1 || z==z0 || z==z1-1;
                if(!ground.FoundUnoccupiedEdge && edge && (material==MaterialId.Soil || material==MaterialId.Rock) && EmptyNeighborhood(cell))
                {ground.ProbeCell=cell;ground.FoundUnoccupiedEdge=true;before=material;}
            }
            if(!ground.FoundUnoccupiedEdge) return;
            ground.Before=before.ToString();
            try
            {
                ground.Removed=_world.TrySetTerrainMaterial(ground.ProbeCell,MaterialId.Empty);
                ground.AfterRemoval=_world.GetTerrainMaterial(ground.ProbeCell).ToString();
            }
            finally
            {
                if(ground.Removed)ground.Restored=_world.TrySetTerrainMaterial(ground.ProbeCell,before);
                ground.AfterRestore=_world.GetTerrainMaterial(ground.ProbeCell).ToString();
            }
        }

        private bool EmptyNeighborhood(Vector3Int cell)
        {
            for(int z=-1;z<=1;z++)for(int x=-1;x<=1;x++)for(int y=0;y<=1;y++)
            {
                var p=cell+new Vector3Int(x,y,z);
                if(_world.TryGetCellularContents(p,out _,out _,out _,out _) || _world.CellularRestingLiquids?.TryGetCell(p,out _,out _)==true)return false;
            }
            return true;
        }

        private void CapturePixels(int pass)
        {
            var image=ScreenCapture.CaptureScreenshotAsTexture(1);
            if(image==null)throw new InvalidOperationException("目标 Player backbuffer 读回失败。");
            try
            {
                if(image.width!=_report.Width || image.height!=_report.Height)throw new InvalidOperationException("采集期间分辨率变化。");
                var pixels=image.GetPixels32();
                if(pass==0)
                {
                    _reference=pixels; _pixelNoise=new byte[pixels.Length]; _report.PixelsCompared=pixels.Length;
                    _report.Screenshot=Path.ChangeExtension(_path,".png");
                    File.WriteAllBytes(_report.Screenshot,image.EncodeToPNG()); return;
                }
                int changed=0;
                for(int i=0;i<pixels.Length;i++)
                {
                    Color32 a=_reference[i],b=pixels[i];
                    int delta=Math.Max(Math.Abs(a.r-b.r),Math.Max(Math.Abs(a.g-b.g),Math.Abs(a.b-b.b)));
                    if(pass==1){_pixelNoise[i]=(byte)delta; if(delta>2)changed++;}
                    else if(delta>_pixelNoise[i]+2)changed++;
                }
                if(pass==1)_report.RepeatImageChangedPixels=changed;
                else _report.Materials[pass-2].ChangedPixelsAboveNoise=changed;
            }
            finally{Destroy(image);}
        }

        private void Finish(string error)
        {
            if(_phase==Phase.Finished)return;
            _phase=Phase.Finished; _report.FinishedUtc=DateTime.UtcNow.ToString("O");
            if (_bottleView) { FinishBottleView(error); return; }
            try
            {
                _report.RealCameraRendered=_frames>0 && _report.MissedRendererFrames<_frames && !_report.BatchMode;
                string device=(_report.Gpu??string.Empty).ToLowerInvariant();
                _report.HardwareGraphicsDevice=_report.RealCameraRendered && !string.IsNullOrEmpty(device) && _report.VendorId!=0x1414
                    && !device.Contains("swiftshader") && !device.Contains("llvmpipe") && !device.Contains("basic render") && !device.Contains("warp");
                bool mixed=_report.SimultaneousMovingTicks>0 && _report.AllThreeDrawFrames>0;
                foreach(var row in _report.Materials)
                {
                    int quarters=0; foreach(int ticks in row.MovingTicksInQuarters) if(ticks>0)quarters++;
                    mixed &= quarters>=3 && row.ChangedPixelsAboveNoise>0 && row.MaxVisibleTrianglesOrGasCells>0;
                    foreach(int ticks in row.MovingTicksInQuadrants)mixed &= ticks>0;
                }
                _report.VerifiedThreeMaterials=mixed;
                if(error==null && (!_report.HardwareGraphicsDevice || !_report.PixelCapture || _ticks==0 || _frames<3 || _report.Tier!="empty" && !mixed))
                    error="测量值已保留；未同时满足硬件可见帧、真实 Tick、三相持续移动/提交/像素证据。";
                if(error==null && (!_report.Ground.Removed || !_report.Ground.Restored || _report.Ground.AfterRemoval!="Empty" || _report.Ground.AfterRestore!=_report.Ground.Before))
                    error="计时数据已保留，未观察到完整的地图边缘空邻域挖掘及回填提交。";
                _report.Error=error??string.Empty; _report.Status=error==null?"Measured":"Incomplete";
                _report.FrameMs=Summarize(_frameMs,_frames,out _report.RawFrameMs);
                _report.TickMs=Summarize(_tickMs,_ticks,out _report.RawTickMs);
                _report.GpuFrameMs=Summarize(_gpuMs,_gpuCount,out _report.RawGpuMs);
                _report.FeedMs=Summarize(_feedMs,_feeds,out _report.RawFeedMs);
                _report.PresentationMs=Summarize(_presentation,_frames,out _);_report.RebuildMs=Summarize(_rebuild,_frames,out _);
                _report.OldestVisibleDirtyMs=Summarize(_age,_frames,out _report.RawOldestVisibleDirtyMs);
                _report.RawFrameFeedMs=Prefix(_frameFeed,_frames);_report.RawFrameWorldTickMs=Prefix(_frameTick,_frames);_report.RawVisibleDirtyPages=Prefix(_dirty,_frames);
                if(!string.IsNullOrEmpty(_path))File.WriteAllText(_path,JsonUtility.ToJson(_report,true));
                GameLog.Info($"帧基准 {_report.Status}，{_frames} 帧、{_ticks} Tick、{_gpuCount} GPU 帧；{_path}","CellularBenchmark");
            }
            catch(Exception ex){error=ex.ToString();GameLog.Error(error,"CellularBenchmark");}
            Application.Quit(error==null?0:2);
        }

        private static Distribution Summarize(double[] samples,int count,out double[] raw)
        {
            raw=Prefix(samples,count);
            var result=new Distribution {Count=count}; if(count==0)return result;
            var sorted=(double[])raw.Clone();Array.Sort(sorted);
            for(int i=0;i<count;i++) result.Mean+=sorted[i];
            result.Mean/=count; result.Maximum=sorted[count-1];
            result.P50=sorted[(int)Math.Ceiling(count*.50)-1];result.P95=sorted[(int)Math.Ceiling(count*.95)-1];result.P99=sorted[(int)Math.Ceiling(count*.99)-1];
            return result;
        }

        private static double[] Prefix(double[] samples,int count){var values=new double[count];Array.Copy(samples,values,count);return values;}
    }
}
