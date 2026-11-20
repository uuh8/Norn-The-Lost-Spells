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
        private float _lastPixelScale = 1f;

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

        private float PixelScale
        {
            get
            {
                var root = canvas != null ? canvas.rootCanvas : null;
                // 本控件用于 Screen Space 库存。World Space 的像素密度还受相机距离影响，
                // 不能误把它的 dynamicPixelsPerUnit 当成屏幕缩放；沿用其原设计单位。
                return root != null && root.renderMode != RenderMode.WorldSpace
                    ? Mathf.Max(.0001f, root.scaleFactor) : 1f;
            }
        }

        private void LateUpdate()
        {
            float scale = PixelScale;
            if (Mathf.Approximately(scale, _lastPixelScale)) return;
            _lastPixelScale = scale;
            // 同宽高比缩放时，Canvas 的参考面积、CellSize 和 Content rect 可能完全没变，
            // Configure 的缓存也不会失效。仅在实际缩放改变时重建现有 mesh，不重排/扩池格子。
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();if(_columns<=0 || _rows<=0 || _cell<=0)return;
            var rect=rectTransform.rect;float left=rect.xMin,top=rect.yMax-_firstRow*_rowHeight;
            float width=_columns*_cell,height=_rows*_rowHeight;
            _lastPixelScale = PixelScale;
            // 原先固定 1 Canvas 单位，1920 设计画面缩到 1280 时仅有 2/3 屏幕像素宽。
            // 无 MSAA 的 UI rasterization 可能整条漏采样，视觉便把相邻正方格合成长格。
            // 只给线宽设 1 屏幕像素下限；格子位置、拖放命中、容量和作者外框保持原合同。
            // 大于设计分辨率时仍保留原 1 参考单位，不把原美术边线反向缩细。
            float thickness=Mathf.Max(1f,1f/_lastPixelScale);
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
