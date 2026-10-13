using Game.Run;
using UnityEngine;
using UnityEngine.UI;
namespace Game.UI
{
    /// <summary>只画实际占格的外边，不把L形空洞包进矩形边框；UGUI几何由缓存VertexHelper生成。</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class InventoryShapeOutlineGraphic:MaskableGraphic
    {
        private InventoryShape _shape;private int _turns;private float _cell;
        public void Configure(InventoryShape shape,int turns,float cell){_shape=shape;_turns=turns;_cell=cell;raycastTarget=false;SetVerticesDirty();}
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();if(_shape==null)return;float thick=2;
            for(int i=0;i<_shape.CellCount;i++)
            {
                var p=_shape.GetCell(i,_turns);float x=rectTransform.rect.xMin+p.x*_cell,y=rectTransform.rect.yMax-p.y*_cell;
                if(!Contains(p.x,p.y-1))Quad(vh,x,y-thick,_cell,thick);
                if(!Contains(p.x,p.y+1))Quad(vh,x,y-_cell,_cell,thick);
                if(!Contains(p.x-1,p.y))Quad(vh,x,y-_cell,thick,_cell);
                if(!Contains(p.x+1,p.y))Quad(vh,x+_cell-thick,y-_cell,thick,_cell);
            }
        }
        private bool Contains(int x,int y){for(int i=0;i<_shape.CellCount;i++){var p=_shape.GetCell(i,_turns);if(p.x==x && p.y==y)return true;}return false;}
        private void Quad(VertexHelper vh,float x,float y,float width,float height)
        {int n=vh.currentVertCount;var vertex=UIVertex.simpleVert;vertex.color=color;vertex.position=new Vector3(x,y);vh.AddVert(vertex);vertex.position=new Vector3(x+width,y);vh.AddVert(vertex);
            vertex.position=new Vector3(x+width,y+height);vh.AddVert(vertex);vertex.position=new Vector3(x,y+height);vh.AddVert(vertex);vh.AddTriangle(n,n+1,n+2);vh.AddTriangle(n,n+2,n+3);}
    }
}
