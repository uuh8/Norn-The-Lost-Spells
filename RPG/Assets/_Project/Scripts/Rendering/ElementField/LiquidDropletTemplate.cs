using System.Collections.Generic;
using UnityEngine;

namespace Game.Rendering
{
    /// <summary>共享单位 Icosphere：孤立液包直接按真实量缩放，移动不改变拓扑/显示体积。</summary>
    internal static class LiquidDropletTemplate
    {
        internal static readonly Vector3[] Vertices;
        internal static readonly int[] Indices;

        static LiquidDropletTemplate()
        {
            // 两次 subdivision 的 162 个顶点只分配一次；每个实例复用球面方向作平滑法线。
            // 避免为不需要相互融合的孤立小滴扫描整个 SDF，也不会随采样相位闪没。
            float t=(1f+Mathf.Sqrt(5f))*.5f;
            var vertices=new List<Vector3>(162)
            {
                new Vector3(-1,t,0),new Vector3(1,t,0),new Vector3(-1,-t,0),new Vector3(1,-t,0),
                new Vector3(0,-1,t),new Vector3(0,1,t),new Vector3(0,-1,-t),new Vector3(0,1,-t),
                new Vector3(t,0,-1),new Vector3(t,0,1),new Vector3(-t,0,-1),new Vector3(-t,0,1)
            };
            for(int i=0;i<vertices.Count;i++)vertices[i]=vertices[i].normalized;
            int[] faces={0,11,5,0,5,1,0,1,7,0,7,10,0,10,11,1,5,9,5,11,4,11,10,2,
                10,7,6,7,1,8,3,9,4,3,4,2,3,2,6,3,6,8,3,8,9,4,9,5,2,4,11,6,2,10,8,6,7,9,8,1};
            var midpoints=new Dictionary<long,int>(480);
            for(int level=0;level<2;level++)
            {
                var next=new int[faces.Length*4];midpoints.Clear();
                for(int i=0;i<faces.Length;i+=3)
                {
                    int a=faces[i],b=faces[i+1],c=faces[i+2],at=i*4;
                    int ab=Midpoint(a,b,vertices,midpoints),bc=Midpoint(b,c,vertices,midpoints),ca=Midpoint(c,a,vertices,midpoints);
                    next[at]=a;next[at+1]=ab;next[at+2]=ca;
                    next[at+3]=b;next[at+4]=bc;next[at+5]=ab;
                    next[at+6]=c;next[at+7]=ca;next[at+8]=bc;
                    next[at+9]=ab;next[at+10]=bc;next[at+11]=ca;
                }
                faces=next;
            }
            Vertices=vertices.ToArray();Indices=faces;
        }
        private static int Midpoint(int a,int b,List<Vector3> vertices,Dictionary<long,int> midpoints)
        {
            long key=((long)Mathf.Min(a,b)<<32)|(uint)Mathf.Max(a,b);
            if(midpoints.TryGetValue(key,out int index))return index;
            index=vertices.Count;vertices.Add((vertices[a]+vertices[b]).normalized);midpoints.Add(key,index);return index;
        }
    }
}
