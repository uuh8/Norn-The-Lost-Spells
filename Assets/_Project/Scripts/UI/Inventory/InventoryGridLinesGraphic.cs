using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>连续底格上叠一份细线mesh；不以缩小格子露出底板模拟边线，视觉与真实CellSize一致。</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class InventoryGridLinesGraphic : MaskableGraphic
    {
        private int _columns, _rows, _firstRow;
        private float _cell, _rowHeight;

        internal static InventoryGridLinesGraphic Ensure(Transform parent)
        {
            var child=parent.Find("Grid lines");
            if(child!=null)return child.GetComponent<InventoryGridLinesGraphic>();
            // 只在初始化/离散扩池调用；不在Update/滚动稳定帧创建GameObject或Material。
            var root=new GameObject("Grid lines",typeof(RectTransform),typeof(InventoryGridLinesGraphic));
            root.transform.SetParent(parent,false);
            var graphic=root.GetComponent<InventoryGridLinesGraphic>();
            graphic.raycastTarget=false;graphic.color=new Color(.34f,.43f,.45f,.55f);
            var rect=graphic.rectTransform;rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;
            rect.offsetMin=rect.offsetMax=Vector2.zero;
            return graphic;
        }

        internal void Configure(int columns,int rows,float cell,int firstRow=0,float rowHeight=0)
        {
            float pitch=rowHeight>0?rowHeight:cell;
            if(_columns==columns && _rows==rows && _cell==cell && _firstRow==firstRow && _rowHeight==pitch)return;
            _columns=columns;_rows=rows;_cell=cell;_rowHeight=pitch;_firstRow=firstRow;SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();if(_columns<=0 || _rows<=0 || _cell<=0)return;
            var rect=rectTransform.rect;float left=rect.xMin,top=rect.yMax-_firstRow*_rowHeight;
            float width=_columns*_cell,height=_rows*_rowHeight;
            // 线宽以CanvasScaler参考单位计。仓库只提交可见行，mesh数量不随无限Content行数增长。
            const float thickness=1;
            for(int x=0;x<=_columns;x++)
                Quad(vh,Mathf.Clamp(left+x*_cell-thickness*.5f,left,left+width-thickness),top-height,thickness,height);
            for(int y=0;y<=_rows;y++)
                Quad(vh,left,Mathf.Clamp(top-y*_rowHeight-thickness*.5f,top-height,top-thickness),width,thickness);
        }
        private void Quad(VertexHelper vh,float x,float y,float width,float height)
        {
            int first=vh.currentVertCount;var vertex=UIVertex.simpleVert;vertex.color=color;
            vertex.position=new Vector3(x,y);vh.AddVert(vertex);
            vertex.position=new Vector3(x+width,y);vh.AddVert(vertex);
            vertex.position=new Vector3(x+width,y+height);vh.AddVert(vertex);
            vertex.position=new Vector3(x,y+height);vh.AddVert(vertex);
            vh.AddTriangle(first,first+1,first+2);vh.AddTriangle(first,first+2,first+3);
        }
    }
}
