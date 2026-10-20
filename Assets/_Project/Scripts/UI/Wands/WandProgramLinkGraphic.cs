using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>用一个UGUI Mesh画触发折线；RectMask2D负责裁剪，不创建每段LineRenderer或材质。</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class WandProgramLinkGraphic : MaskableGraphic
    {
        private struct Link { public Vector2 From,To;public Color Color; }
        private readonly List<Link> _links=new List<Link>();
        public void Begin(){_links.Clear();}
        public void Add(Vector2 from,Vector2 to,Color tint){_links.Add(new Link{From=from,To=to,Color=tint});}
        public void End(){SetVerticesDirty();}
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for(int i=0;i<_links.Count;i++)
            {
                var link=_links[i];var a=new Vector2(link.From.x,-link.From.y);var b=new Vector2(link.To.x,-link.To.y);
                var elbow=new Vector2(a.x,b.y);
                Line(vh,a,elbow,link.Color);Line(vh,elbow,b,link.Color);
                var d=(b-elbow).normalized;if(d.sqrMagnitude<.1f)d=Vector2.right;
                var p=new Vector2(-d.y,d.x);int start=vh.currentVertCount;
                vh.AddVert(b,link.Color,Vector2.zero);vh.AddVert(b-d*9+p*5,link.Color,Vector2.zero);
                vh.AddVert(b-d*9-p*5,link.Color,Vector2.zero);vh.AddTriangle(start,start+1,start+2);
            }
        }
        private static void Line(VertexHelper vh,Vector2 a,Vector2 b,Color tint)
        {
            var direction=b-a;if(direction.sqrMagnitude<.01f)return;
            var normal=new Vector2(-direction.y,direction.x).normalized*1.4f;int start=vh.currentVertCount;
            vh.AddVert(a+normal,tint,Vector2.zero);vh.AddVert(b+normal,tint,Vector2.zero);
            vh.AddVert(b-normal,tint,Vector2.zero);vh.AddVert(a-normal,tint,Vector2.zero);
            vh.AddTriangle(start,start+1,start+2);vh.AddTriangle(start,start+2,start+3);
        }
    }
}
