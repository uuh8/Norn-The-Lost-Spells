using System;
using Game.Run;
using UnityEngine;
namespace Game.UI
{
    public sealed class NearbyGridLayoutResult
    {public readonly Vector2Int[] Positions;public readonly int Columns,Rows;internal NearbyGridLayoutResult(Vector2Int[] p,int columns,int rows){Positions=p;Columns=columns;Rows=rows;}}
    /// <summary>仅在附近集合变化时算临时排布；不伪造Backpack或改写WorldItem的真实Location。</summary>
    public static class NearbyItemGridLayout
    {
        public static NearbyGridLayoutResult Build(InventoryShape[] shapes,int columns)
        {
            if(shapes==null || columns<1)throw new ArgumentException("有效形状与列数不能为空。");
            int maxRows=0;foreach(var shape in shapes){columns=Mathf.Max(columns,shape.Width);maxRows+=shape.Height;}
            var occupied=new bool[Mathf.Max(1,maxRows)*columns];var positions=new Vector2Int[shapes.Length];int rows=0;
            for(int i=0;i<shapes.Length;i++)
            {
                var shape=shapes[i];bool placed=false;
                for(int y=0;y<=maxRows-shape.Height && !placed;y++)for(int x=0;x<=columns-shape.Width;x++)
                {
                    bool valid=true;for(int c=0;c<shape.CellCount;c++){var p=shape.GetCell(c,0);if(occupied[(y+p.y)*columns+x+p.x]){valid=false;break;}}
                    if(!valid)continue;positions[i]=new Vector2Int(x,y);for(int c=0;c<shape.CellCount;c++){var p=shape.GetCell(c,0);occupied[(y+p.y)*columns+x+p.x]=true;}
                    rows=Mathf.Max(rows,y+shape.Height);placed=true;break;
                }
            }
            return new NearbyGridLayoutResult(positions,columns,rows);
        }
    }
}
