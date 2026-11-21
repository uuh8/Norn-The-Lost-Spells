using Game.Materials;
using System;
using System.Diagnostics;
using UnityEngine;

namespace Game.Rendering
{
    public sealed partial class CellularMaterialRenderer
    {
        // Native 基准仅在专用进程开启。Draw 计数位于真正的 Graphics.DrawMesh 提交之后，
        // 每种材质保留几何/气格和距相机最近页中心，避免把一个空页进入视锥写成液体/沙/气都可见。
        public struct NativeDrawSample
        {
            public int Frame, CameraId, ResidentPages, VisiblePages, RebuiltPages, DirtyVisiblePages;
            public int WaterDraws, SandDraws, GasDraws, WaterTriangles, SandTriangles, GasCells;
            public float WaterNearestSquared, SandNearestSquared, GasNearestSquared;
            public int OldestDirtyFrames;
            public double OldestDirtyMs, RebuildMs, PresentationMs;
        }

        public struct NativeMotionSample
        {
            public int WaterGains, WaterLosses, SandGains, SandLosses, GasGains, GasLosses;
        }

        public bool CaptureNativeFrames { get; set; }
        public bool CaptureNativeMotion { get; set; }
        public Vector3Int NativeQuadrantCenter { get; set; }
        // 只在帧采集结束后的截图校准暂时隐藏一类显式材质，真实世界、Mesh/Texture 与相机均保留。
        public MaterialId NativeHiddenMaterial { get; set; }
        public NativeDrawSample NativeFrame => _nativeDraw;
        private NativeDrawSample _nativeDraw;
        private NativeMotionSample _nativeMotion;
        private readonly int[] _nativeGains = new int[12], _nativeLosses = new int[12];
        private double _nativeFrameNow;
        private long _nativeRebuildStart;

        public void TakeNativeQuadrants(int[] gains,int[] losses)
        {
            Array.Copy(_nativeGains,gains,12);Array.Copy(_nativeLosses,losses,12);
            Array.Clear(_nativeGains,0,12);Array.Clear(_nativeLosses,0,12);
        }

        public NativeMotionSample TakeNativeMotion()
        {
            var value = _nativeMotion; _nativeMotion = default; return value;
        }

        private void RecordNativeMotion(Vector3Int cell,in Sample before, in Sample after)
        {
            int q=(cell.x>=NativeQuadrantCenter.x?1:0)|(cell.z>=NativeQuadrantCenter.z?2:0);
            RecordDelta(before.Material == MaterialId.Water ? before.Quantity : 0, after.Material == MaterialId.Water ? after.Quantity : 0,
                ref _nativeMotion.WaterGains, ref _nativeMotion.WaterLosses,q);
            RecordDelta(before.Material == MaterialId.Sand ? before.Quantity : 0, after.Material == MaterialId.Sand ? after.Quantity : 0,
                ref _nativeMotion.SandGains, ref _nativeMotion.SandLosses,4+q);
            RecordDelta(before.Steam, after.Steam, ref _nativeMotion.GasGains, ref _nativeMotion.GasLosses,8+q);
        }

        private void RecordDelta(uint before, uint after, ref int gains, ref int losses,int bucket)
        { if (after > before){gains++;_nativeGains[bucket]++;}else if (after < before){losses++;_nativeLosses[bucket]++;} }

        private void BeginNativeDraw()
        {
            _nativeFrameNow=Time.realtimeSinceStartupAsDouble;_nativeRebuildStart=Stopwatch.GetTimestamp();
            _nativeDraw = new NativeDrawSample { Frame = Time.frameCount, CameraId = _camera.GetInstanceID(),
                ResidentPages = _pages.Count, VisiblePages = _visiblePages.Count, WaterNearestSquared = float.MaxValue,
                SandNearestSquared = float.MaxValue, GasNearestSquared = float.MaxValue };
        }

        private static double NativeMilliseconds(long start) => (Stopwatch.GetTimestamp()-start)*(1000d/Stopwatch.Frequency);

        private void RecordNativeAge(Page page)
        {
            int frame=_nativeDraw.Frame;
            if(!(page.SurfaceDirty||page.GasDirty))page.NativeWaiting=false;
            else
            {
                // 离开视锥期间不算等待。第一帧看到 dirty 即计时，持续多次改量也不能抹掉旧等待。
                if(!page.NativeWaiting || page.NativeSeenFrame!=frame-1)
                {page.NativeWaiting=true;page.NativeDirtyFrame=frame;page.NativeDirtyAt=_nativeFrameNow;}
                _nativeDraw.OldestDirtyFrames=Math.Max(_nativeDraw.OldestDirtyFrames,frame-page.NativeDirtyFrame);
                _nativeDraw.OldestDirtyMs=Math.Max(_nativeDraw.OldestDirtyMs,(_nativeFrameNow-page.NativeDirtyAt)*1000);
            }
            page.NativeSeenFrame=frame;
        }

        private void RecordNativeSurface(int slot, uint indices, float distanceSquared)
        {
            if (slot == CellularMaterialSurfaceMesh.Slot(MaterialId.Water))
            { _nativeDraw.WaterDraws++; _nativeDraw.WaterTriangles += (int)(indices / 3); _nativeDraw.WaterNearestSquared = Mathf.Min(_nativeDraw.WaterNearestSquared,distanceSquared); }
            else if (slot == CellularMaterialSurfaceMesh.Slot(MaterialId.Sand))
            { _nativeDraw.SandDraws++; _nativeDraw.SandTriangles += (int)(indices / 3); _nativeDraw.SandNearestSquared = Mathf.Min(_nativeDraw.SandNearestSquared,distanceSquared); }
        }
    }
}
