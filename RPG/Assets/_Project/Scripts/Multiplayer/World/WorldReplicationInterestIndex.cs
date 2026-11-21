using System;
using System.Collections.Generic;
using Game.Core;
using UnityEngine;

namespace Game.Multiplayer
{
    /// <summary>
    /// 以 8³ 个原生 Chunk 为一桶，只登记已经出现在权威目录内的地址。玩家跨过网络参考块时，
    /// 先提取新窗口附近的桶，再用权威 Interest 的半开 Bounds 作最终判交；发送顺序仍服从 Owner 的槽序。
    /// </summary>
    internal sealed class WorldReplicationInterestIndex
    {
        private readonly Dictionary<WorldChunkKey,HashSet<WorldChunkKey>> _buckets = new Dictionary<WorldChunkKey,HashSet<WorldChunkKey>>();
        private readonly Stack<HashSet<WorldChunkKey>> _spare = new Stack<HashSet<WorldChunkKey>>();
        private static WorldChunkKey Bucket(WorldChunkKey key) => new WorldChunkKey(key.Domain,key.X>>3,key.Y>>3,key.Z>>3);

        internal void Add(WorldChunkKey key)
        {
            var id=Bucket(key);
            if(!_buckets.TryGetValue(id,out var entries))
            {
                entries=_spare.Count>0?_spare.Pop():new HashSet<WorldChunkKey>();
                _buckets.Add(id,entries);
            }
            entries.Add(key);
        }
        internal void Remove(WorldChunkKey key)
        {
            var id=Bucket(key);
            if(!_buckets.TryGetValue(id,out var entries) || !entries.Remove(key) || entries.Count!=0)return;
            _buckets.Remove(id);
            // 活动区移动时复用少量空桶，历史探索规模不永久保留集合分配。
            if(_spare.Count<32)_spare.Push(entries);
        }

        internal void Collect(in WorldGridGeometry geometry,Bounds interest,List<WorldChunkKey> result)
        {
            if(_buckets.Count==0)return;
            float edge=geometry.ChunkSize*geometry.CellSize;
            if(!float.IsFinite(edge)||edge<=0)return;
            Vector3 min=interest.min,max=interest.max,origin=geometry.Origin;
            if(!Range(min.x,max.x,origin.x,edge,out int x0,out int x1) ||
               !Range(min.y,max.y,origin.y,edge,out int y0,out int y1) ||
               !Range(min.z,max.z,origin.z,edge,out int z0,out int z1))return;
            // 异常巨大而稀疏的合法窗口不枚举空坐标；真实世界的普通小窗口只查其覆盖的哈希桶。
            double covered=((double)x1-x0+1)*((double)y1-y0+1)*((double)z1-z0+1);
            if(covered>_buckets.Count)
            {
                foreach(var entry in _buckets)
                {
                    var k=entry.Key;
                    if(k.Domain==geometry.Domain && k.X>=x0 && k.X<=x1 && k.Y>=y0 && k.Y<=y1 && k.Z>=z0 && k.Z<=z1)
                        Copy(entry.Value,result);
                }
                return;
            }
            for(int x=x0;x<=x1;x++)for(int y=y0;y<=y1;y++)for(int z=z0;z<=z1;z++)
                if(_buckets.TryGetValue(new WorldChunkKey(geometry.Domain,x,y,z),out var entries))Copy(entries,result);
        }

        private static void Copy(HashSet<WorldChunkKey> entries,List<WorldChunkKey> result)
        { foreach(var key in entries)result.Add(key); }

        private static bool Range(float min,float max,float origin,float edge,out int first,out int last)
        {
            long start=Boundary(min,origin,edge,true),end=Boundary(max,origin,edge,false);
            first=last=0;
            if(start>=end)return false;
            first=(int)start>>3;last=(int)(end-1)>>3;return true;
        }
        // 与生产 Bounds 的 float 计算顺序一致。单调二分定位第一块 max>min / min>=max，
        // 对大坐标处 float 合并相邻整数、负坐标或边界恰好相切同样保守；至多 32 次，只在窗口移动时执行。
        private static long Boundary(float target,float origin,float edge,bool upper)
        {
            long low=int.MinValue,high=(long)int.MaxValue+1;
            float half=edge*.5f;
            while(low<high)
            {
                long middle=low+((high-low)>>1);
                float center=(origin+(float)middle*edge)+half;
                bool reached=upper?center+half>target:center-half>=target;
                if(reached)high=middle;else low=middle+1;
            }
            return low;
        }
    }
}
