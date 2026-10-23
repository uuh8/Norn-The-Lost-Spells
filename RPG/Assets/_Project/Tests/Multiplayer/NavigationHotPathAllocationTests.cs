using System;
using System.Collections;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Networking;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Game.Multiplayer.Tests
{
    public sealed class NavigationHotPathAllocationTests
    {
        [UnityTest] public IEnumerator ActualNavMeshPathRefreshDoesNotAllocateAfterPositiveCalibration()
        {
            // NavMeshAgent的运行注册依赖PlayerLoop；普通EditMode不能用Warp=false冒充分配回归RED。
            yield return new EnterPlayMode();
            var data = AssetDatabase.LoadAssetAtPath<NavMeshData>("Assets/_Project/ScriptableObjects/World/Online/MovementNavigation.asset");
            Assert.That(data, Is.Not.Null); var navigation = NavMesh.AddNavMeshData(data);
            var host = new GameObject("Online actual navigation allocation");
            try
            {
                host.transform.position = new Vector3(-6, .1f, -8);
                var agent = host.AddComponent<NavMeshAgent>(); yield return null; Assert.True(agent.Warp(host.transform.position));
                var motor = new EnemyNavigationMotor(agent, (EnemyDefinition)null, host.transform);
                var method = typeof(EnemyNavigationMotor).GetMethod("TryRequestPath", BindingFlags.Instance | BindingFlags.NonPublic);
                // 反射仅在装配阶段创建一次Delegate；采样时不能用MethodInfo.Invoke把反射分配混进结果。
                var calculate = (Func<Vector3, bool>)method.CreateDelegate(typeof(Func<Vector3, bool>), motor);
                Vector3 destination = new Vector3(-5, .1f, -8);
                for (int i = 0; i < 8; i++) Assert.True(calculate(destination));
                using var meter = new CodecAllocationMeter(128); Assert.True(meter.IsValid);
                bool pathsValid = true; meter.Begin();
                for (int i = 0; i < 16; i++) pathsValid &= calculate(destination);
                long allocations = meter.End();
                Assert.True(pathsValid); Assert.True(meter.IsValid);
                Assert.That(allocations, Is.Zero, "真实路径刷新不应由params数组或临时对象产生稳态GC。");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); navigation.Remove(); }
        }
        [UnityTearDown] public IEnumerator LeaveRuntime()
        { if (Application.isPlaying) yield return new ExitPlayMode(); }
    }
}
