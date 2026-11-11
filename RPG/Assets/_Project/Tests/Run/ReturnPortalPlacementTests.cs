using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>用真实Collider核原离散放门查询；不加载场景、不运行Encounter或保存任何资产。</summary>
    public sealed class ReturnPortalPlacementTests
    {
        private readonly List<GameObject> _objects=new List<GameObject>();
        [TearDown] public void DisposeOnlyPlacementFixture()
        { for (int i=_objects.Count-1;i>=0;i--) if (_objects[i]!=null) UnityEngine.Object.DestroyImmediate(_objects[i]); _objects.Clear(); Physics.SyncTransforms(); }
        private Transform Actor()
        {
            var value=new GameObject("OriginalReturnPlacementActor");_objects.Add(value);
            value.transform.SetPositionAndRotation(new Vector3(12123,14321,12567),Quaternion.Euler(0,73,0));
            return value.transform;
        }
        private void Floor(Vector3 at,float width)
        {
            var value=new GameObject("OriginalReturnPlacementGround");_objects.Add(value);
            value.transform.position=at-Vector3.up*.5f;value.AddComponent<BoxCollider>().size=new Vector3(width,1,width);Physics.SyncTransforms();
        }
        private static Pose Resolve(Transform actor)
        {
            var type=typeof(HubPracticeSession).Assembly.GetType("Game.Run.ReturnPortalPlacement");
            Assert.That(type,Is.Not.Null,"旧Session与DS必须共用原放门规则，不能各自写一份近似位置算法。");
            var method=type.GetMethod("Resolve",BindingFlags.Public|BindingFlags.Static,null,new[]{typeof(Transform)},null);
            Assert.That(method,Is.Not.Null);return (Pose)method.Invoke(null,new object[]{actor});
        }
        [Test] public void OriginalReturnPlacementFallsBackToTheActualActorPoseWithoutValidGround()
        {
            var actor=Actor();var pose=Resolve(actor);
            Assert.That(pose.position,Is.EqualTo(actor.position));Assert.That(Quaternion.Angle(pose.rotation,actor.rotation),Is.LessThan(.001f));
        }
        [Test] public void OriginalReturnPlacementUsesFirstValidFourMetreRightCandidateAndFacesActor()
        {
            var actor=Actor();var expected=actor.position+actor.right*4;Floor(expected,1.5f);
            var pose=Resolve(actor);Assert.That(Vector3.Distance(pose.position,expected),Is.LessThan(.002f));
            var facing=actor.position-expected;facing.y=0;
            Assert.That(Quaternion.Angle(pose.rotation,Quaternion.LookRotation(facing)),Is.LessThan(.01f));
            Assert.That(actor.position,Is.EqualTo(new Vector3(12123,14321,12567)),"查询不得移动真实玩家或生成门。");
        }
        [Test] public void OriginalReturnPlacementTriesTheNextFortyFiveDegreeCandidateWhenFirstHasNoGround()
        {
            var actor=Actor();var expected=actor.position+Quaternion.Euler(0,45,0)*actor.right*4;Floor(expected,1f);
            var pose=Resolve(actor);Assert.That(Vector3.Distance(pose.position,expected),Is.LessThan(.002f));
        }
    }
}
