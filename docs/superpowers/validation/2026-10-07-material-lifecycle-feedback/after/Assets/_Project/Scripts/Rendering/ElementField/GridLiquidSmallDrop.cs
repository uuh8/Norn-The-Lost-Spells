using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GridLiquidSmallDrop
    {
        public Vector3 Position;
        public float Radius;
        public uint First,Second,Third,Fourth;
    }
    /// <summary>完整可见快照的少量落水姿态；独立于随后 GPU/CPU 的页面划分，避免两种路径边界变形。</summary>
    internal struct GridLiquidFallingMotion
    {
        internal Vector3 PreviousCenter,CurrentCenter,PreviousVelocity,CurrentVelocity;
        // 极小滴每格总量 ≤8，8 个材料各用一个 byte 无损保存，共 16 Bytes，不创建每滴数组。
        internal uint Previous0,Previous1,Current0,Current1;
        internal uint Quantity(bool previous,int slot)
        {uint packed=slot<4?(previous?Previous0:Current0):(previous?Previous1:Current1);return packed>>(slot%4*8)&255u;}
        internal GridLiquidSmallDrop Sample(float alpha,float interval,float cellSize,float volumeScaleOverCapacity)
        {
            alpha=Mathf.Clamp01(alpha);float quantity=0;uint old=0,now=0;
            for(int i=0;i<8;i++){old+=Quantity(true,i);now+=Quantity(false,i);}
            quantity=Mathf.Lerp(old,now,alpha);if(quantity<=0)return default;
            Vector3 a=PreviousCenter+Vector3.ClampMagnitude(PreviousVelocity*(interval*alpha),cellSize*3);
            Vector3 b=CurrentCenter+Vector3.ClampMagnitude(CurrentVelocity*(interval*(alpha-1)),cellSize*3);
            // scale/capacity 把 GMU 数量换成占格体积；艺术倍率与连续液体一致，不能使用固定最小半径。
            float volume=quantity*cellSize*cellSize*cellSize*volumeScaleOverCapacity;
            return new GridLiquidSmallDrop{Position=old>0 && now>0?Vector3.Lerp(a,b,alpha):old>0?a:b,
                Radius=Mathf.Pow(3*volume/(4*Mathf.PI),1f/3f),First=Weights(0,alpha,quantity),Second=Weights(2,alpha,quantity),Third=Weights(4,alpha,quantity),Fourth=Weights(6,alpha,quantity)};
        }
        private uint Weights(int slot,float alpha,float quantity)=>GridLiquidPackedCell.HalfPair(
            Mathf.Lerp(Quantity(true,slot),Quantity(false,slot),alpha)/quantity,
            Mathf.Lerp(Quantity(true,slot+1),Quantity(false,slot+1),alpha)/quantity);
    }
}
