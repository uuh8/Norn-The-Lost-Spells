using Game.UI;
using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class NearbyItemPlacementCacheTests
    {
        private static InventoryShape Shape(int w,int h)
        {var cells=new Vector2Int[w*h];for(int i=0;i<cells.Length;i++)cells[i]=new Vector2Int(i%w,i/w);return new InventoryShape(w,h,cells);}
        [Test]public void MovingRetainsChosenPositionAfterNewItemsAndCancel()
        {
            var cache=new NearbyItemPlacementCache();var shape=Shape(2,3);
            cache.Ensure("a",shape,8);Assert.That(cache.Move("a",shape,new GridPlacement(5,7,1),8),Is.True);
            cache.Ensure("b",shape,8);cache.TryGet("a",out var retained);Assert.That(retained.X,Is.EqualTo(5));Assert.That(retained.Y,Is.EqualTo(7));
            Assert.That(cache.Move("b",shape,new GridPlacement(5,7,0),8),Is.False);Assert.That(cache.IsInvalid("b"),Is.True);
            cache.Cancel();cache.TryGet("b",out var rollback);Assert.That(rollback.X,Is.Zero);Assert.That(rollback.Y,Is.Zero);
        }
        [Test]public void InvalidConsecutiveMovesRestoreLastLegalAndLegalMoveCommits()
        {
            var cache=new NearbyItemPlacementCache();var shape=Shape(4,5);cache.Ensure("a",shape,3);
            Assert.That(cache.Move("a",shape,new GridPlacement(9,12,1),3),Is.True);
            cache.Move("a",shape,new GridPlacement(-1,2,2),3);cache.Move("a",shape,new GridPlacement(-4,-8,3),3);cache.Cancel();
            cache.TryGet("a",out var rollback);Assert.That(rollback.X,Is.EqualTo(9));Assert.That(rollback.Y,Is.EqualTo(12));Assert.That(rollback.QuarterTurns,Is.EqualTo(1));
            cache.Move("a",shape,new GridPlacement(-1,0,2),3);Assert.That(cache.Move("a",shape,new GridPlacement(20,25,3),3),Is.True);cache.Cancel();
            cache.TryGet("a",out rollback);Assert.That(rollback.X,Is.EqualTo(20));cache.Bounds(out var cols,out var rows);Assert.That(cols,Is.GreaterThan(20));Assert.That(rows,Is.GreaterThan(25));
        }
    }
}
