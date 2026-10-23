#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using Game.Character;
using Game.Core;
using Game.Multiplayer;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>真实在线Prefab/碰撞/NavMesh的隔离运行验证；仅输入Frame与Roster装配是测试夹具，不冒充网络准入。</summary>
    public sealed class OnlineMovementPhysicsPlayModeTests
    {
        private Scene _scene, _previous;
        private GameObject _root;
        private NetworkActorRegistry _registry;
        private PlayerControllerBase _player;
        private uint _sequence;
        private float _maxY, _maxAngle, _maxSide;
        [UnitySetUp] public IEnumerator SetUp()
        {
            _previous = SceneManager.GetActiveScene(); _scene = SceneManager.CreateScene("OnlineMovementPhysicsIsolation");
            SceneManager.SetActiveScene(_scene); _root = new GameObject("OwnedOnlinePhysicsFixture");
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile"); Assert.That(profile, Is.Not.Null);
            UnityEngine.Object.Instantiate(profile.WorldPrefab, _root.transform);
            _registry = new NetworkActorRegistry(profile, _root.transform);
            var slot = _registry.EnsurePlayer(0, PlayerExecutionMode.Authority); slot.Connected = true; slot.Epoch = 1;
            _player = slot.Player; _sequence = 0; _maxY = _maxAngle = _maxSide = 0;
            Physics.SyncTransforms(); yield return null;
        }
        private void Place(Vector3 position)
        {
            var pose = _player.CaptureMovementSnapshot(); pose.Position = position; pose.Rotation = Quaternion.identity;
            pose.TargetFacing = Vector3.forward; pose.VerticalVelocity = 0; pose.Locomotion = PlayerLocomotionKind.Grounded;
            pose.DashBufferCounter = pose.JumpBufferCounter = pose.DashCooldownCounter = 0;
            _player.RestoreMovementSnapshot(pose); Physics.SyncTransforms();
        }
        private IEnumerator Walk(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                _player.Simulate(new PlayerSimulationFrame(++_sequence, 1, Vector2.up, 0, false, false, false, false), FixedStepClock.DeltaTime);
                var pose = _player.CaptureMovementSnapshot(); _maxY = Mathf.Max(_maxY, pose.Position.y); _maxAngle = Mathf.Max(_maxAngle, pose.GroundAngle);
                yield return null; // Coroutine在Update后的PlayerLoop推进，绝不把CC.Move挪进FixedUpdate。
            }
        }
        [UnityTest] public IEnumerator ActualControllerClimbsStepsAndWalkableThirtyDegreeRamp()
        {
            Place(new Vector3(-10, .1f, -9)); yield return Walk(30);
            Save("steps"); Assert.That(_maxY, Is.GreaterThan(.7f), "原0.3米Step Offset应跨过连续0.2米台阶。");
            Place(new Vector3(-9, .1f, -1)); _maxY = _maxAngle = 0; yield return Walk(30);
            Save("walkable-ramp"); Assert.That(_player.transform.position.z, Is.GreaterThan(4));
            Assert.That(_maxY, Is.GreaterThan(2)); Assert.That(_maxAngle, Is.InRange(25, 35));
        }
        [UnityTest] public IEnumerator ActualControllerDoesNotWalkUpFiftyFiveDegreeRamp()
        {
            Place(new Vector3(9, .1f, -1)); yield return Walk(60); Save("steep-ramp");
            Assert.That(_player.transform.position.z, Is.LessThan(4), "超过Slope Limit不能按普通平地继续爬上陡坡。");
            Assert.That(_player.transform.position.y, Is.LessThan(1.5f));
        }
        [UnityTest] public IEnumerator AuthorityEnemyUsesNavMeshDetourInsteadOfCrossingCentralWall()
        {
            Place(new Vector3(0, .1f, 0)); var enemy = _registry.EnsureEnemy(true).Enemy;
            var agent = enemy.GetComponent<NavMeshAgent>(); var cc = enemy.GetComponent<CharacterController>();
            cc.enabled = false; enemy.transform.position = new Vector3(0, .1f, 6);
            Assert.True(agent.Warp(enemy.transform.position)); cc.enabled = true; Physics.SyncTransforms(); yield return null;
            bool crossedWall = false;
            for (uint tick = 1; tick <= 240; tick++)
            {
                enemy.StepAuthority(FixedStepClock.DeltaTime, tick); Vector3 p = enemy.transform.position;
                _maxSide = Mathf.Max(_maxSide, Mathf.Abs(p.x));
                crossedWall |= Mathf.Abs(p.x) < 3 && p.z > 1.6f && p.z < 2.4f && p.y < 1.8f;
                yield return null;
            }
            Save("enemy-detour", enemy.transform.position);
            Assert.False(crossedWall); Assert.That(_maxSide, Is.GreaterThan(3.1f));
            Assert.That(enemy.transform.position.z, Is.LessThan(1.6f)); Assert.That(enemy.Perception.TargetActorId, Is.EqualTo(1));
        }
        [Serializable] private sealed class Evidence { public string Case; public Vector3 End; public float MaximumY, MaximumAngle, MaximumSide; public uint Inputs; }
        private void Save(string name, Vector3? end = null)
        {
            Directory.CreateDirectory("Temp/OnlineMovement");
            File.WriteAllText("Temp/OnlineMovement/" + name + ".json", JsonUtility.ToJson(new Evidence
            { Case = name, End = end ?? _player.transform.position, MaximumY = _maxY, MaximumAngle = _maxAngle, MaximumSide = _maxSide, Inputs = _sequence }, true));
        }
        [UnityTearDown] public IEnumerator TearDown()
        {
            _registry?.Neutralize(); _registry?.Dispose(); if (_root != null) UnityEngine.Object.Destroy(_root); yield return null;
            if (_previous.IsValid() && _previous.isLoaded) SceneManager.SetActiveScene(_previous);
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
        }
    }
}
#endif
