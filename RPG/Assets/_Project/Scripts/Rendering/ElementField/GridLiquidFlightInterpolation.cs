using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 两端必须描述同一条有界轨迹。仅用v*t分别向前/回推，会把匀加速误差重复计入，
    /// 首个静止下落窗口甚至先上弹；补上同一重力的0.5*a*t²后，两个端点的路径重合。
    /// 单位显式分成World位移与Cell位移；正式12m/s终速一Tick走4.8格，6格有界Warp覆盖它。
    /// </summary>
    internal static class GridLiquidFlightInterpolation
    {
        internal const float MaximumWarpCells=6;
        internal static Vector3 WarpWorld(Vector3 velocity,float gravity,float seconds,float cellSize)
        {
            Vector3 displacement=velocity*seconds+Vector3.down*(.5f*Mathf.Max(0,gravity)*seconds*seconds);
            return Vector3.ClampMagnitude(displacement,cellSize*MaximumWarpCells);
        }
        internal static Vector3 WarpCells(Vector3 velocity,float gravity,float interval,float phase,float cellSize)
            =>WarpWorld(velocity,gravity,interval*phase,cellSize)/cellSize;
        internal static float Gravity(Vector4 terrain,bool supported)=>!supported && terrain.y<0?-terrain.y:0;
    }
}
