using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    public struct GridLiquidSmallDrop
    {
        public Vector3 Position;
        public float Radius;
        // 前8字节保存8个UNorm8材质显示权重，后8字节保存Half速度与形状。
        // Fourth高Half正值仍是空中stretch；负整数编码接触角code，不伪造速度或增加stride。
        // 仍是32B实例；数量与Radius不量化，不为细节增加GPU显存预算。
        public uint First,Second,Third,Fourth;
    }
    /// <summary>完整可见快照的少量落水姿态；独立于随后 GPU/CPU 的页面划分，避免两种路径边界变形。</summary>
    internal struct GridLiquidFallingMotion
    {
        internal Vector3 PreviousCenter,CurrentCenter,PreviousVelocity,CurrentVelocity;
        internal float PreviousGravity,CurrentGravity;
        // 极小滴每格总量 ≤8，8 个材料各用一个 byte 无损保存，共 16 Bytes，不创建每滴数组。
        internal uint Previous0,Previous1,Current0,Current1;
        // 仅接地聚合体使用无损Total和UNorm8配色；自由<=8GMU仍沿原byte数量。
        // 每条CPU运动记录增加16B，现65536固定池增加1MiB；GPU实例仍32B，无热路径扩容。
        internal uint PreviousTotal,CurrentTotal,PreviousContactCode,CurrentContactCode;
        internal uint Quantity(bool previous,int slot)
        {uint packed=slot<4?(previous?Previous0:Current0):(previous?Previous1:Current1);return packed>>(slot%4*8)&255u;}
        internal GridLiquidSmallDrop Sample(float alpha,float interval,float cellSize,float volumeScaleOverCapacity)
        {
            alpha=Mathf.Clamp01(alpha);float quantity=0;uint old=0,now=0;
            if(PreviousContactCode>0)old=PreviousTotal;else for(int i=0;i<8;i++)old+=Quantity(true,i);
            if(CurrentContactCode>0)now=CurrentTotal;else for(int i=0;i<8;i++)now+=Quantity(false,i);
            quantity=Mathf.Lerp(old,now,alpha);if(quantity<=0)return default;
            Vector3 a=PreviousCenter+GridLiquidFlightInterpolation.WarpWorld(PreviousVelocity,PreviousGravity,interval*alpha,cellSize);
            Vector3 b=CurrentCenter+GridLiquidFlightInterpolation.WarpWorld(CurrentVelocity,CurrentGravity,interval*(alpha-1),cellSize);
            if(PreviousContactCode>0)a.y=PreviousCenter.y;
            if(CurrentContactCode>0)b.y=CurrentCenter.y;
            // scale/capacity 把 GMU 数量换成占格体积；艺术倍率与连续液体一致，不能使用固定最小半径。
            float volume=quantity*cellSize*cellSize*cellSize*volumeScaleOverCapacity;
            Vector3 velocity=old>0 && now>0?Vector3.Lerp(PreviousVelocity,CurrentVelocity,alpha):old>0?PreviousVelocity:CurrentVelocity;
            float stretch=1+.6f*Mathf.Clamp01(velocity.magnitude*interval/Mathf.Max(.001f,cellSize));
            bool contact=(old==0 || PreviousContactCode>0) && (now==0 || CurrentContactCode>0)
                && (PreviousContactCode>0 || CurrentContactCode>0);
            int code=contact?Mathf.RoundToInt(old>0 && now>0?Mathf.Lerp(PreviousContactCode,CurrentContactCode,alpha):old>0?PreviousContactCode:CurrentContactCode):0;
            return new GridLiquidSmallDrop{Position=old>0 && now>0?Vector3.Lerp(a,b,alpha):old>0?a:b,
                Radius=contact?GridLiquidGroundContactShape.Radius(volume,code):Mathf.Pow(3*volume/(4*Mathf.PI),1f/3f),First=Weights(0,alpha,quantity),Second=Weights(4,alpha,quantity),Third=GridLiquidPackedCell.HalfPair(velocity.x,velocity.y),Fourth=GridLiquidPackedCell.HalfPair(velocity.z,contact?-code:stretch)};
        }
        private float MaterialQuantity(bool previous,int slot)
        {uint code=previous?PreviousContactCode:CurrentContactCode,total=previous?PreviousTotal:CurrentTotal;return code>0?Quantity(previous,slot)*(total/255f):Quantity(previous,slot);}
        private uint Weights(int slot,float alpha,float quantity)
        {
            uint value=0;for(int i=0;i<4;i++)value|=(uint)Mathf.RoundToInt(Mathf.Lerp(MaterialQuantity(true,slot+i),MaterialQuantity(false,slot+i),alpha)/quantity*255)<<(i*8);
            return value;
        }
    }
}
