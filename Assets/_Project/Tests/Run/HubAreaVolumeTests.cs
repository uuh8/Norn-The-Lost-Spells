using Game.Run;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    public sealed class HubAreaVolumeTests
    {
        private GameObject _host;
        private HubAreaVolume _area;
        private BoxCollider _box;
        [SetUp] public void Setup()
        { _host=new GameObject("Area geometry");_area=_host.AddComponent<HubAreaVolume>();_box=_area.Volume;_box.size=new Vector3(12,6,14);_box.center=new Vector3(0,3,0); }
        [TearDown] public void Cleanup()=>Object.DestroyImmediate(_host);
        [Test] public void BroadAreaIncludesPointsBeyondOldInteractionRadius()
        {Assert.That(_area.Contains(new Vector3(5,1,0)),Is.True);Assert.That(_area.Contains(new Vector3(7,1,0)),Is.False);}
        [TestCase(-6,0,true)] [TestCase(6,0,false)] [TestCase(0,-7,true)] [TestCase(0,7,false)]
        public void AdjacentAreasHaveHalfOpenBoundary(float x,float z,bool expected)
        {Assert.That(_area.Contains(new Vector3(x,1,z)),Is.EqualTo(expected));}
        [Test] public void RotatedScaledOffsetBoxUsesLocalGeometry()
        {
            _host.transform.position=new Vector3(7,2,-9);_host.transform.rotation=Quaternion.Euler(0,37,0);_host.transform.localScale=new Vector3(2,.3f,4);
            Assert.That(_area.Contains(_host.transform.TransformPoint(_box.center+new Vector3(5.9f,0,6.9f))),Is.True);
            Assert.That(_area.Contains(_host.transform.TransformPoint(_box.center+new Vector3(6.1f,0,0))),Is.False);
        }
        [Test] public void DisabledVolumeCannotGrantInteraction()
        {Assert.That(_area.Contains(Vector3.up),Is.True);_box.enabled=false;Assert.That(_area.Contains(Vector3.up),Is.False);_box.enabled=true;_area.enabled=false;Assert.That(_area.Contains(Vector3.up),Is.False);_area.enabled=true;_host.SetActive(false);Assert.That(_area.Contains(Vector3.up),Is.False);}
        [Test] public void InvalidPointZeroSizeAndZeroScaleFailClosed()
        {Assert.That(_area.Contains(new Vector3(float.NaN,1,0)),Is.False);Assert.That(_area.Contains(new Vector3(0,1,float.PositiveInfinity)),Is.False);_box.size=new Vector3(0,6,14);Assert.That(_area.Contains(Vector3.up),Is.False);_box.size=Vector3.one*4;_host.transform.localScale=new Vector3(0,1,1);Assert.That(_area.Contains(Vector3.up),Is.False);}
        [Test] public void VolumeFollowsMovedAndResizedPlatformParent()
        {
            var platform=new GameObject("Actual platform");try
            {
                _host.transform.SetParent(platform.transform,false);platform.transform.position=new Vector3(-32,.17f,6.8f);platform.transform.localScale=new Vector3(9.45f,.3f,10.58f);
                _box.center=new Vector3(0,10.5f,0);_box.size=new Vector3(1,20,1);
                Assert.That(_area.Contains(new Vector3(-35,.37f,6.8f)),Is.True);
                platform.transform.position+=Vector3.right*20;Assert.That(_area.Contains(new Vector3(-35,.37f,6.8f)),Is.False);
                Assert.That(_area.Contains(new Vector3(-15,.37f,6.8f)),Is.True);
            }
            finally {_host.transform.SetParent(null,true);Object.DestroyImmediate(platform);}
        }
    }
}
