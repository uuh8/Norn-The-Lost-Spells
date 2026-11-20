using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 只读流体表现的公共数学约定。Anisotropy 只改变同一GMU外壳的三个轴，
    /// 不回写数量、碰撞或Solver；CPU、GPU和Bounds必须使用同一有界公式。
    /// </summary>
    internal static class GridLiquidFlowPresentation
    {
        internal const float MaximumFlowSpeed=8;
        internal static Vector3 AirAxes(Vector3 velocity,float interval,float cellSize)
        {
            float speed=velocity.magnitude;
            if(speed<.00001f)return Vector3.one;
            Vector3 direction=velocity/speed;
            // 每Tick位移达到一格时已经显著拉长，最大1.6倍；QueryReach按冻结姿态
            // 有界扩展，不作无界搜索。慢速黏液自然比快速水流保持更圆的形态。
            float stretch=1+.6f*Mathf.Clamp01(speed*interval/Mathf.Max(.001f,cellSize));
            float cross=1/stretch,along=stretch*stretch-cross;
            var axes=new Vector3(Mathf.Sqrt(cross+along*direction.x*direction.x),
                Mathf.Sqrt(cross+along*direction.y*direction.y),Mathf.Sqrt(cross+along*direction.z*direction.z));
            // 对角协方差近似不保存旋转项，须重新归一化determinant。
            // 三轴乘积恒为1，拉长一轴时另外两轴收缩，避免用体积膨胀假装液体。
            axes/=Mathf.Pow(axes.x*axes.y*axes.z,1f/3f);
            return axes;
        }
        internal static uint Pack(float swell,Vector2 flow,float impact)
        {
            int x=Mathf.RoundToInt(Mathf.Clamp(flow.x/MaximumFlowSpeed,-1,1)*127);
            int z=Mathf.RoundToInt(Mathf.Clamp(flow.y/MaximumFlowSpeed,-1,1)*127);
            return (uint)Mathf.RoundToInt(Mathf.Clamp01(swell)*255)|((uint)x&255u)<<8|((uint)z&255u)<<16|
                (uint)Mathf.RoundToInt(Mathf.Clamp01(impact)*255)<<24;
        }
        internal static Vector4 Unpack(uint value)
        {
            int x=(int)(value>>8&255u),z=(int)(value>>16&255u);
            if(x>127)x-=256;if(z>127)z-=256;
            return new Vector4((value&255u)/255f,x*(MaximumFlowSpeed/127),z*(MaximumFlowSpeed/127),(value>>24)/255f);
        }
    }
}
