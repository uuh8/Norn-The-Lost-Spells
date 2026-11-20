using System.Reflection;
using Game.Core;
using NUnit.Framework;
using UnityEngine;

namespace Game.Character.Tests
{
    public sealed class LiquidPourOriginTests
    {
        private static MaterialTransferFailure Prepare(Transform actor,Vector3 aim,out Vector3 origin,out Vector3 velocity)
        {
            var type=typeof(OnlineCastDriver).Assembly.GetType("Game.Character.OnlineContainerTrajectory");
            var method=type.GetMethod("PrepareEmission",BindingFlags.Static|BindingFlags.NonPublic);
            Assert.That(method,Is.Not.Null,"SinglePlayer与DS应共用真实喷口计算。");
            object[] args={actor,aim,8f,2f,new RaycastHit[16],Vector3.zero,Vector3.zero};
            var result=(MaterialTransferFailure)method.Invoke(null,args);origin=(Vector3)args[5];velocity=(Vector3)args[6];return result;
        }
        [Test] public void EmissionOriginUsesActorAndFullAimWhilePreservingRotation()
        {
            var actor=new GameObject("Pour origin fixture");
            try
            {
                actor.transform.position=new Vector3(9000,7000,9000);actor.transform.rotation=Quaternion.Euler(0,30,0);
                var rotation=actor.transform.rotation;var direction=new Vector3(1,.5f,0).normalized;
                Assert.That(Prepare(actor.transform,direction,out var origin,out var velocity),Is.EqualTo(MaterialTransferFailure.None));
                Assert.That(Vector3.Distance(origin,actor.transform.position+Vector3.right*.8f+Vector3.up*1.2f),Is.LessThan(.001f));
                Assert.That(Vector3.Distance(velocity,direction*8+Vector3.up*2),Is.LessThan(.001f));
                Assert.That(actor.transform.rotation,Is.EqualTo(rotation));
            }
            finally{Object.DestroyImmediate(actor);}
        }
        [Test] public void WallBeforeNozzleRejectsInsteadOfSpawningThroughIt()
        {
            var actor=new GameObject("Pour blocked fixture");var wall=new GameObject("Nozzle wall");
            try
            {
                actor.transform.position=new Vector3(9000,7000,9000);
                wall.transform.position=actor.transform.position+Vector3.up*1.2f+Vector3.forward*.4f;
                wall.AddComponent<BoxCollider>().size=new Vector3(2,2,.1f);Physics.SyncTransforms();
                Assert.That(Prepare(actor.transform,Vector3.forward,out _,out _),Is.EqualTo(MaterialTransferFailure.GeometryRejected));
            }
            finally{Object.DestroyImmediate(wall);Object.DestroyImmediate(actor);}
        }
    }
}
