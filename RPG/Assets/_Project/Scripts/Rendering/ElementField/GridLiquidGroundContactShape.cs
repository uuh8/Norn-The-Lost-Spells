using UnityEngine;

namespace Game.Rendering
{
    /// <summary>
    /// 同一个实例小滴模板的接地球冠形变，不创建第二套Renderer或材料数量。
    /// 接触角只影响形状；R按V^(1/3)求取。模板离散闭合体积预先校准，避免把理想球体
    /// 公式与162顶点实际Draw混为一谈，也不靠VolumeScale/Alpha制造厚度。
    /// </summary>
    internal static class GridLiquidGroundContactShape
    {
        private static readonly float[] UnitVolumes=CreateVolumes();
        internal static void Warm()=>System.GC.KeepAlive(UnitVolumes);
        // code=1..256，0保留给自由液体。整数Half无误差；负号标记接地球冠，正数仍是原空中stretch。
        internal static float HeightRatio(int code)=>.12f+.8f*(Mathf.Clamp(code,1,256)-1)/255f;
        internal static float Radius(float volume,int code)=>Mathf.Pow(Mathf.Max(0,volume)/UnitVolumes[Mathf.Clamp(code,1,256)-1],1f/3f);
        internal static Vector3 UnitPosition(Vector3 direction,int code)
        {
            float k=HeightRatio(code),cosine=1-k;
            if(direction.y<0)return new Vector3(direction.x*Mathf.Sqrt(k*(2-k)),0,direction.z*Mathf.Sqrt(k*(2-k)));
            float c=cosine+k*direction.y,radial=Mathf.Sqrt(Mathf.Max(0,1-c*c));
            float planar=Mathf.Sqrt(direction.x*direction.x+direction.z*direction.z);
            return new Vector3(planar>.000001f?direction.x/planar*radial:0,k*direction.y,planar>.000001f?direction.z/planar*radial:0);
        }
        // 与ElementLiquidGroundContact.hlsl逐式一致，用于CPU fallback相同模板及实际几何诊断。
        internal static Vector3 TransformPosition(Vector3 direction,GridLiquidSmallDrop drop)
        {
            float encoded=GridLiquidPackedCell.High(drop.Fourth);
            if(encoded<0)return drop.Position+UnitPosition(direction,Mathf.RoundToInt(-encoded))*drop.Radius;
            Vector3 velocity=new Vector3(GridLiquidPackedCell.Low(drop.Third),GridLiquidPackedCell.High(drop.Third),GridLiquidPackedCell.Low(drop.Fourth));
            float stretch=Mathf.Clamp(encoded,1,1.6f),cross=1/Mathf.Sqrt(stretch);
            Vector3 axis=velocity.sqrMagnitude>.00000001f?velocity.normalized:Vector3.up;
            return drop.Position+(direction*cross+axis*Vector3.Dot(direction,axis)*(stretch-cross))*drop.Radius;
        }
        private static float[] CreateVolumes()
        {
            var result=new float[256];
            for(int code=1;code<=256;code++)
            {
                double volume=0;
                for(int index=0;index<LiquidDropletTemplate.Indices.Length;index+=3)
                {
                    var a=UnitPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[index]],code);
                    var b=UnitPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[index+1]],code);
                    var c=UnitPosition(LiquidDropletTemplate.Vertices[LiquidDropletTemplate.Indices[index+2]],code);
                    volume+=Vector3.Dot(a,Vector3.Cross(b,c))/6d;
                }
                result[code-1]=(float)System.Math.Abs(volume);
            }
            return result;
        }
    }
}
