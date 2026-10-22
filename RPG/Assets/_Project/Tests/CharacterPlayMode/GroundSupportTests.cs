using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Character.Tests
{
    /// <summary>真实Physics复现胶囊脚底的细支撑；不以手写布尔模拟Grounded。</summary>
    public sealed class GroundSupportTests
    {
        private GameObject _actor, _support;
        private GroundChecker _checker;
        private CharacterController _controller;
        [SetUp] public void Setup()
        {
            _actor=new GameObject("Ground probe test");_actor.transform.position=new Vector3(1000,10,1000);
            _controller=_actor.AddComponent<CharacterController>();_controller.radius=.3f;
            _controller.height=2;_controller.center=Vector3.up;_controller.skinWidth=.03f;
            _checker=_actor.AddComponent<GroundChecker>();
            typeof(GroundChecker).GetField("_groundLayer",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_checker,(LayerMask)~0);
            _support=GameObject.CreatePrimitive(PrimitiveType.Cube);_support.name="Narrow physical support";
        }
        [TearDown] public void Cleanup(){Object.DestroyImmediate(_actor);Object.DestroyImmediate(_support);}
        private void Check()
        {Physics.SyncTransforms();typeof(GroundChecker).GetMethod("CheckGround",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(_checker,null);}
        [UnityTest] public IEnumerator ThinPostSupportsControllerDespiteSkinOverlap()
        {
            _support.transform.localScale=new Vector3(.09f,2,.09f);
            _support.transform.position=_actor.transform.position-Vector3.up*.985f;
            yield return null;_controller.Move(Vector3.down*.02f);Check();
            Assert.That(_checker.IsGrounded,Is.True,"脚底与细柱接触不能因SphereCast起始重叠漏掉地面");
            Assert.That(_checker.GroundAngle,Is.LessThan(20));
        }
        [UnityTest] public IEnumerator OffsetCapsuleFootUsesActualControllerGeometry()
        {
            _controller.center=Vector3.up*1.4f;
            _support.transform.localScale=new Vector3(3,1,3);
            _support.transform.position=_actor.transform.position-Vector3.up*.09f;
            yield return null;Check();Assert.That(_checker.IsGrounded,Is.True);
        }
        [UnityTest] public IEnumerator WizardSizeStandingOnThinPostEdgeIsGrounded()
        {
            _controller.radius=.35f;_controller.height=1.8f;_controller.center=Vector3.up*.9f;_controller.skinWidth=.08f;
            typeof(GroundChecker).GetField("_sphereRadius",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_checker,.27f);
            _support.transform.localScale=new Vector3(.09f,2,.09f);
            _support.transform.position=new Vector3(1000,9,1000);
            _actor.transform.position=new Vector3(1000.2f,10.4f,1000);
            yield return null;
            for(int i=0;i<30;i++)_controller.Move(Vector3.down*.02f);
            Assert.That(_controller.isGrounded,Is.True,"真实胶囊必须先被柱顶支撑，此后才比较GroundChecker");
            Check();Assert.That(_checker.IsGrounded,Is.True,"正式Wizard半径大于探地球，柱边缘支撑不应漏检");
        }
        [UnityTest] public IEnumerator ThinSupportWithinSkinIsNotLostToInitialSphereOverlap()
        {
            _controller.radius=.35f;_controller.skinWidth=.08f;
            _support.transform.localScale=new Vector3(.09f,2,.09f);
            _support.transform.position=new Vector3(1000,9,1000);
            _actor.transform.position=new Vector3(1000,9.96f,1000);
            yield return null;Physics.SyncTransforms();
            Assert.That(Physics.SphereCast(_actor.transform.position+Vector3.up*.27f,.27f,Vector3.down,out _,.17f,~0,QueryTriggerInteraction.Ignore),Is.False,"原起始球重叠查询必须实际漏检，才能证明本案例覆盖旧问题");
            Check();Assert.That(_checker.IsGrounded,Is.True,"脚底在skinWidth接触容差内，不应漏掉细支撑");
        }
        [UnityTest] public IEnumerator AirSideWallAndTriggerAreNotGround()
        {
            _support.transform.localScale=new Vector3(.1f,4,2);
            _support.transform.position=_actor.transform.position+new Vector3(.36f,1,0);
            yield return null;Check();Assert.That(_checker.IsGrounded,Is.False,"侧墙不能成为脚底支撑");
            _support.transform.localScale=new Vector3(3,1,3);
            _support.transform.position=_actor.transform.position-Vector3.up*.5f;
            _support.GetComponent<Collider>().isTrigger=true;Check();Assert.That(_checker.IsGrounded,Is.False);
            _support.GetComponent<Collider>().isTrigger=false;
            _support.transform.position-=Vector3.up*2;Check();Assert.That(_checker.IsGrounded,Is.False);
        }
#if UNITY_EDITOR
        [UnityTest] public IEnumerator ActualHubFenceControllerSupportMatchesProbe()
        {
            Object.DestroyImmediate(_support);
            var path=UnityEditor.AssetDatabase.FindAssets("WoofFence_07 t:Prefab");
            Assert.That(path.Length,Is.GreaterThan(0));
            _support=Object.Instantiate(UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(UnityEditor.AssetDatabase.GUIDToAssetPath(path[0])));
            _support.transform.position=new Vector3(1000,8,1000);_support.transform.rotation=Quaternion.identity;
            _controller.radius=.35f;_controller.height=1.8f;_controller.center=Vector3.up*.9f;_controller.skinWidth=.08f;
            typeof(GroundChecker).GetField("_sphereRadius",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(_checker,.27f);
            yield return null;Physics.SyncTransforms();var colliders=_support.GetComponentsInChildren<Collider>();Assert.That(colliders.Length,Is.GreaterThan(0));
            Bounds bounds=colliders[0].bounds;foreach(var collider in colliders)bounds.Encapsulate(collider.bounds);
            int supported=0;
            for(int i=0;i<20;i++)
            {
                Vector3 point=new Vector3(Mathf.Lerp(bounds.min.x+.02f,bounds.max.x-.02f,i/19f),bounds.max.y+1,bounds.center.z);
                if(!Physics.Raycast(point,Vector3.down,out var hit,bounds.size.y+2,~0,QueryTriggerInteraction.Ignore))continue;
                _controller.enabled=false;_actor.transform.position=hit.point+Vector3.up*.5f;_controller.enabled=true;
                Physics.SyncTransforms();for(int j=0;j<35;j++)_controller.Move(Vector3.down*.02f);
                if(!_controller.isGrounded)continue;supported++;Check();
                Assert.That(_checker.IsGrounded,Is.True,"Fence top support at "+_actor.transform.position);
                Assert.That(_checker.GroundAngle,Is.LessThanOrEqualTo(_controller.slopeLimit),"平栏杆顶不能因球扫边缘Normal被切入Sliding");
            }
            Assert.That(supported,Is.GreaterThan(0),"至少命中一处真实栏杆支撑");
        }
#endif
    }
}
