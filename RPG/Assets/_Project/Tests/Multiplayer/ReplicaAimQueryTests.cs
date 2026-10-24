using System;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Multiplayer.Tests
{
    /// <summary>真实 Registry/Prefab/Physics Query/准星射线；不模拟网络、不生成伤害、不修改正式资源。</summary>
    public sealed class ReplicaAimQueryTests
    {
        private GameObject _root;
        private NetworkActorRegistry _registry;
        private WizardController _local;
        private readonly RaycastHit[] _hits = new RaycastHit[64];
        private readonly Vector3 _anchor = new Vector3(27000f, 1000f, 19000f);

        [SetUp]
        public void SetUp()
        {
            var profile = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            var definition = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            Assert.That(profile, Is.Not.Null); Assert.That(definition, Is.Not.Null);
            _root = new GameObject("N4 replica aim query fixture");
            _registry = new NetworkActorRegistry(profile, _root.transform, definition, new CombatSimulationWorld());
            _local = (WizardController)_registry.EnsurePlayer(0, PlayerExecutionMode.Prediction).Player;
            InitializePlayer(_local); _local.transform.position = _anchor + Vector3.right * 10;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root == null) return;
            // EditMode 补过 Awake 的本机包装器也要显式释放，避免把 InputAction 对象留给下一轮测试。
            var players = _root.GetComponentsInChildren<PlayerControllerBase>(true);
            for (int i = 0; i < players.Length; i++)
            {
                InvokeLifecycle(players[i], "OnDisable");
                // 生成包装器的 Dispose 使用运行时 Destroy；EditMode 由 fixture 销毁自己 FromJson 创建的 asset。
                // Reflection 只用于清理第三方类型，测试程序集无需为清理引入 InputSystem 额外依赖。
                var inputField = typeof(PlayerControllerBase).GetField("_inputActions", BindingFlags.Instance | BindingFlags.NonPublic);
                object actions = inputField.GetValue(players[i]);
                if (actions != null)
                {
                    var asset = (Object)actions.GetType().GetProperty("asset").GetValue(actions);
                    if (asset != null) Object.DestroyImmediate(asset);
                    inputField.SetValue(players[i], null);
                }
                InvokeLifecycle(players[i], "OnDestroy");
            }
            Object.DestroyImmediate(_root);
        }

        [TestCase(2)]
        [TestCase(5)]
        [TestCase(6)]
        [TestCase(7)]
        public void ActualReplicaBody_RemainsQueryableAndCrosshairChoosesNearTarget(int actorId)
        {
            var slot = actorId < 5 ? _registry.EnsurePlayer(actorId - 1, PlayerExecutionMode.Replica)
                : _registry.EnsureEnemy((ulong)actorId, false);
            if (slot.Player != null) InitializePlayer(slot.Player);
            slot.Body.SetPositionAndRotation(_anchor, Quaternion.identity);
            var controller = slot.Body.GetComponent<CharacterController>();
            Assert.That(controller, Is.Not.Null);
            // disabled Collider 的 bounds 为空，因此相机锚点取 authoring 的 local center，而不是错误的空 bounds。
            Vector3 center = slot.Body.TransformPoint(controller.center);
            var cameraRoot = new GameObject("N4 controlled crosshair camera"); cameraRoot.transform.SetParent(_root.transform);
            var camera = cameraRoot.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(center + Vector3.back * 6, Quaternion.identity);
            _local.BindOnlineCamera(camera);
            Physics.SyncTransforms();

            Ray ray = camera.ViewportPointToRay(new Vector3(.5f, .5f, 0));
            int count = Physics.RaycastNonAlloc(ray, _hits, 100, ~0, QueryTriggerInteraction.Ignore);
            bool hitBody = false;
            for (int i = 0; i < count; i++) if (_hits[i].collider == controller) hitBody = true;
            Assert.That(hitBody, Is.True, "远端人体胶囊必须进入真实 Physics Query，不能只剩武器/背景参与瞄准。");
            Assert.That(Vector3.Distance(camera.transform.position, _local.CaptureOnlineCastAim()), Is.LessThan(7f),
                "准星正对近处角色时应取近命中，不能回退到约100米的远点。");
        }

        [TestCase(PlayerExecutionMode.Prediction)]
        [TestCase(PlayerExecutionMode.Replica)]
        public void OnlineSnapshotRestore_PreservesExistingActorCollisionIgnore(PlayerExecutionMode restoredMode)
        {
            var remote = _registry.EnsurePlayer(1, PlayerExecutionMode.Replica).Player;
            InitializePlayer(remote); remote.transform.position = _anchor;
            var localController = _local.CharacterController;
            var remoteController = remote.CharacterController;
            // 独立验证快照恢复契约；先建立启用的真实胶囊与互穿关系，不让旧的“禁用Replica”掩盖恢复时丢规则。
            localController.enabled = remoteController.enabled = true;
            Physics.IgnoreCollision(localController, remoteController, true);
            Assert.That(Physics.GetIgnoreCollision(localController, remoteController), Is.True);
            PlayerControllerBase restored = restoredMode == PlayerExecutionMode.Prediction ? _local : remote;
            var snapshot = restored.CaptureMovementSnapshot(); snapshot.Position += Vector3.right * .2f;
            restored.RestoreMovementSnapshot(snapshot);
            Assert.That(Physics.GetIgnoreCollision(localController, remoteController), Is.True,
                "每15Hz的预测校正/远端插值不能撤销合作角色互穿规则。");
            Assert.That(restored.transform.position, Is.EqualTo(snapshot.Position));
            Assert.That(restored.CharacterController.enabled, Is.True);
        }

        [Test]
        public void QueryableReplica_StillHasNoLocalMovementAuthority()
        {
            var remote = _registry.EnsurePlayer(1, PlayerExecutionMode.Replica).Player;
            InitializePlayer(remote); remote.CharacterController.enabled = true;
            Vector3 before = remote.transform.position;
            var input = new PlayerSimulationFrame(1, 1, Vector2.up, 0, true, true, true, false);
            remote.Simulate(input, .1f); remote.MoveCharacter(Vector3.forward * 10);
            Assert.That(remote.transform.position, Is.EqualTo(before), "启用查询胶囊不会授予 Replica 的 Move/Simulate 权限。");
        }

        [Test]
        public void CollisionIgnoredCoopActor_DoesNotBecomeGroundThroughSphereQuery()
        {
            var remote = _registry.EnsurePlayer(1, PlayerExecutionMode.Replica).Player;
            InitializePlayer(remote); remote.transform.position = _anchor;
            remote.CharacterController.enabled = true;
            var localController = _local.CharacterController;
            var other = remote.CharacterController;
            // 正式 Prefab 的 Root 可能有 authoring scale，CC.center/height 是 local 单位；先统一到 world。
            Vector3 top = remote.transform.TransformPoint(other.center)
                + Vector3.up * (other.height * Mathf.Abs(remote.transform.lossyScale.y) * .5f);
            Vector3 localFootOffset = _local.transform.TransformVector(localController.center)
                - Vector3.up * (localController.height * Mathf.Abs(_local.transform.lossyScale.y) * .5f);
            Physics.IgnoreCollision(localController, other, false);
            Physics.SyncTransforms();
            // CCT 的查询胶囊可能略小于 authoring 外壳；先从该真实 Collider 射线取得顶部，再安排支撑间隙。
            Assert.That(other.Raycast(new Ray(top + Vector3.up * 2, Vector3.down), out var surface, 4), Is.True,
                "正控前提：必须先确认远端胶囊真实顶部确实可被查询。");
            _local.transform.position = surface.point + Vector3.up * .15f - localFootOffset;
            Physics.SyncTransforms();
            string rawDiagnostic = GroundProbeDiagnostic(_local.GroundChecker, localController, other, out bool rawHitOther);
            Assert.That(rawHitOther, Is.True, "正控前提：真实SphereCast应命中远端胶囊。" + rawDiagnostic);
            _local.GroundChecker.Refresh();
            Assert.That(_local.GroundChecker.IsGrounded, Is.True,
                "正控：相同真实几何必须能把未忽略的角色顶部检测为支撑，否则后续false只是空查询。" + rawDiagnostic);
            Physics.IgnoreCollision(localController, other, true);
            _local.GroundChecker.Refresh();
            Assert.That(_local.GroundChecker.IsGrounded, Is.False,
                "互穿角色不是支撑物；SphereCast不会自动应用IgnoreCollision，GroundChecker也需尊重同一关系。");
        }

        private static string GroundProbeDiagnostic(GroundChecker checker, CharacterController self, CharacterController other,
            out bool rawHitOther)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(GroundChecker);
            Vector3 foot = (Vector3)type.GetMethod("Foot", flags).Invoke(checker, null);
            float radius = (float)type.GetMethod("Radius", flags).Invoke(checker, null);
            float distance = (float)type.GetField("_groundCheckDistance", flags).GetValue(checker);
            LayerMask mask = (LayerMask)type.GetField("_groundLayer", flags).GetValue(checker);
            float lift = Mathf.Max(.06f, self.skinWidth * Mathf.Abs(self.transform.lossyScale.y) + .02f);
            Vector3 origin = foot + Vector3.up * (radius + lift);
            var hits = new RaycastHit[16];
            int count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, hits, distance + lift, mask,
                QueryTriggerInteraction.Ignore);
            rawHitOther = false;
            string result = $" origin={origin:R},foot={foot:R},radius={radius:R},range={(distance + lift):R},self={self.bounds},other={other.bounds},count={count};";
            for (int i = 0; i < count; i++)
            {
                if (hits[i].collider == other) rawHitOther = true;
                result += $" hit[{i}]={hits[i].collider.name}/{hits[i].collider.GetType().Name},distance={hits[i].distance:R},normal={hits[i].normal:R};";
            }
            return result;
        }

        private static void InitializePlayer(PlayerControllerBase player)
        {
            if (player.CharacterController == null) InvokeLifecycle(player, "Awake");
            // EditMode 不自动发送 GroundChecker.Awake；手动接通正式组件缓存，避免用退化脚底算法假通过。
            InvokeLifecycle(player.GroundChecker, "Awake");
        }
        private static void InvokeLifecycle(object target, string name)
        {
            Type type = target.GetType(); MethodInfo method = null;
            while (type != null && method == null)
            {
                method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                type = type.BaseType;
            }
            Assert.That(method, Is.Not.Null, name); method.Invoke(target, null);
        }
    }
}
