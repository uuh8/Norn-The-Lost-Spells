using System.Collections.Generic;
using Game.UI;
using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class NearbyItemGridLayoutTests
    {
        private static InventoryShape Rect(int w,int h){var cells=new Vector2Int[w*h];for(int i=0;i<cells.Length;i++)cells[i]=new Vector2Int(i%w,i/w);return new InventoryShape(w,h,cells);}
        [Test]public void EmptyListHasNoRows(){var result=NearbyItemGridLayout.Build(new InventoryShape[0],4);Assert.That(result.Rows,Is.Zero);}
        [Test]public void OversizeItemExpandsColumnsInsteadOfShrinkingShape(){var result=NearbyItemGridLayout.Build(new[]{Rect(7,2)},3);Assert.That(result.Columns,Is.EqualTo(7));Assert.That(result.Rows,Is.EqualTo(2));}
        [Test]public void LShapeHoleCanHoldAnotherItem(){var shape=new InventoryShape(2,2,new[]{Vector2Int.zero,Vector2Int.right,Vector2Int.up});var result=NearbyItemGridLayout.Build(new[]{shape,Rect(1,1)},2);Assert.That(result.Positions[1],Is.EqualTo(new Vector2Int(1,1)));Assert.That(result.Rows,Is.EqualTo(2));}
        [Test]public void MixedShapesNeverOverlapAndRowsGrow()
        {
            var shapes=new[]{Rect(2,3),Rect(3,3),Rect(1,3),Rect(2,1),Rect(1,1)};var result=NearbyItemGridLayout.Build(shapes,4);var occupied=new HashSet<Vector2Int>();
            for(int i=0;i<shapes.Length;i++)for(int c=0;c<shapes[i].CellCount;c++)Assert.That(occupied.Add(result.Positions[i]+shapes[i].GetCell(c,0)),Is.True);
            Assert.That(result.Rows,Is.GreaterThan(3));
        }
        [Test]public void IconRegionNeverCoversAnUnoccupiedHole()
        {
            var shape=new InventoryShape(2,2,new[]{Vector2Int.zero,Vector2Int.right,Vector2Int.up});
            for(int turn=0;turn<4;turn++)
            {var area=InventoryItemView.IconRegion(shape,turn);for(int y=area.y;y<area.yMax;y++)for(int x=area.x;x<area.xMax;x++)
                {bool present=false;for(int i=0;i<shape.CellCount;i++)present|=shape.GetCell(i,turn)==new Vector2Int(x,y);Assert.That(present,Is.True);}}
        }
    }
}
