using System;
using Game.ElementField;
using Game.Materials;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>保留完整 26 邻 Halo；逐块定位代替每个 Voxel 的世界地址/Dictionary 查询。</summary>
    public static class GasDensitySnapshot
    {
        /// <summary>
        /// RG=过渡起点，BA=新浓度，两份双材料数据复用同一 RGBA Texture。
        /// 只在后端变化时打包；中断过渡先求当时可见值，新出生直接显示，均不改变 Gameplay 真量。
        /// </summary>
        public static void PackTemporalHistory(Color32[] pixels,Color32[] history,int offset,float blend,bool first)
        {
            blend=Mathf.Clamp01(blend);
            for(int i=0;i<pixels.Length;i++)
            {
                Color32 next=pixels[i],old=history[offset+i];
                byte steam=first?next.r:(byte)Mathf.RoundToInt(Mathf.Lerp(old.r,old.b,blend));
                byte toxic=first?next.g:(byte)Mathf.RoundToInt(Mathf.Lerp(old.g,old.a,blend));
                var packed=new Color32(steam,toxic,next.r,next.g);
                pixels[i]=packed; history[offset+i]=packed;
            }
        }
        public static bool Pack(IGasVolumeReadOnly source,ElementChunkKey key,Color32[] pixels,uint[] steam,uint[] toxic)
        {
            int size=source.ChunkSize,dimension=size+2,volume=dimension*dimension*dimension;
            if(pixels==null || steam==null || toxic==null || pixels.Length<volume || steam.Length<volume || toxic.Length<volume)
                throw new ArgumentException("Gas Snapshot 缓冲必须覆盖内部格与一格 Halo。");
            for(int z=-1;z<=1;z++) for(int y=-1;y<=1;y++) for(int x=-1;x<=1;x++)
            {
                var neighbor=new ElementChunkKey(key.X+x,key.Y+y,key.Z+z);
                var minimum=new Vector3Int(x<0?size-1:0,y<0?size-1:0,z<0?size-1:0);
                var extent=new Vector3Int(x==0?size:1,y==0?size:1,z==0?size:1);
                int offset=(x<0?0:x==0?1:size+1)+dimension*((y<0?0:y==0?1:size+1)
                    +dimension*(z<0?0:z==0?1:size+1));
                source.CopyMaterialBox(neighbor,minimum,extent,MaterialId.Steam,steam,offset,dimension,dimension*dimension);
                source.CopyMaterialBox(neighbor,minimum,extent,MaterialId.ToxicVapor,toxic,offset,dimension,dimension*dimension);
            }
            bool visible=false; uint capacity=source.CellCapacity;
            for(int i=0;i<volume;i++)
            {
                // 沿用原 RGBA32 量化，Shader、Alpha 与 GMU 真量均不变。
                pixels[i]=new Color32((byte)Mathf.Clamp(Mathf.RoundToInt(255f*steam[i]/capacity),0,255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt(255f*toxic[i]/capacity),0,255),0,255);
                visible|=pixels[i].r!=0 || pixels[i].g!=0;
            }
            return visible;
        }
    }
}
