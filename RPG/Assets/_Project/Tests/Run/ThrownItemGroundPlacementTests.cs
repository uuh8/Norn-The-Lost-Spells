using NUnit.Framework;
using UnityEngine;
namespace Game.Run.Tests
{
    public sealed class ThrownItemGroundPlacementTests:InventoryTestFixture
    {
        [Test] public void WallEdgeCanSearchNearbyButOrdinaryDropStillRequiresOriginalSupport()
        {
            var floor=Track(GameObject.CreatePrimitive(PrimitiveType.Cube));floor.transform.position=new Vector3(10000,-.5f,10000);floor.transform.localScale=new Vector3(2,1,2);
            var item=Track(GameObject.CreatePrimitive(PrimitiveType.Cube));item.transform.position=new Vector3(10001.1f,.1f,10000);item.GetComponent<Collider>().enabled=false;
            Physics.SyncTransforms();var placement=new WorldItemGroundPlacement();var position=item.transform.position;
            Assert.That(placement.TryPlace(item,item.scene,null,~0,4,80,.5f,.01f),Is.False,"常规Drop不能擅自移动用户落点");
            Assert.That(item.transform.position,Is.EqualTo(position));
            Assert.That(placement.TryPlace(item,item.scene,null,~0,4,80,.5f,.01f,1.5f),Is.True,"墙边投掷只在附近找完整支撑");
            var delta=item.transform.position-position;delta.y=0;Assert.That(delta.magnitude,Is.LessThanOrEqualTo(1.501f));
            Assert.That(item.transform.position.x,Is.LessThan(10000.51f));Assert.That(item.GetComponent<Renderer>().bounds.min.y,Is.EqualTo(.01f).Within(.001f));
        }
        [Test] public void UnsupportedFlightCannotTeleportToDistantGroundOrLoseOriginalPosition()
        {
            var item=Track(GameObject.CreatePrimitive(PrimitiveType.Cube));item.transform.position=new Vector3(20000,2,20000);item.GetComponent<Collider>().enabled=false;
            Physics.SyncTransforms();var before=item.transform.position;
            Assert.That(new WorldItemGroundPlacement().TryPlace(item,item.scene,null,~0,4,80,.5f,.01f,1.5f),Is.False);
            Assert.That(item.transform.position,Is.EqualTo(before));
        }
    }
}
