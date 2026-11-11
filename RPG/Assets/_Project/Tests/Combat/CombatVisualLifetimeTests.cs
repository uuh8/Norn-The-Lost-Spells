using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Game.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Game.Combat.Tests
{
    public sealed class ThrownModelVisualTestBehaviour : MonoBehaviour { }

    /// <summary>落点预警的寿命属于服务器逻辑时间；不会把 Render frame 或墙钟时间当成已经模拟过的时间。</summary>
    public sealed class CombatVisualLifetimeTests
    {
        private readonly List<GameObject> _owned = new List<GameObject>();
        [TearDown] public void Cleanup()
        {
            foreach (GameObject root in _owned) if (root != null) Object.DestroyImmediate(root);
            _owned.Clear();
        }

        [Test]
        public void BirthTick_DoesNotConsumeTimeBeforeMarkerExisted()
        {
            var world = new CombatSimulationWorld(); world.BeginTick(10);
            Component timer = Create(world, 1f);
            world.StepBeforePhysics(.25f, 10);
            Assert.That(Remaining(timer), Is.EqualTo(1f));
            world.StepBeforePhysics(.25f, 11);
            Assert.That(Remaining(timer), Is.EqualTo(.75f).Within(.00001f));
        }

        [Test]
        public void DuplicateTick_DoesNotConsumeMarkerLifetimeAgain()
        {
            var world = new CombatSimulationWorld(); Component timer = Create(world, 1f);
            world.StepBeforePhysics(.25f, 1);
            world.StepBeforePhysics(.25f, 1);
            timer.GetType().GetMethod("StepAuthority").Invoke(timer, new object[] { .25f, 1u });
            Assert.That(Remaining(timer), Is.EqualTo(.75f).Within(.00001f));
            world.StepBeforePhysics(.25f, 2);
            Assert.That(Remaining(timer), Is.EqualTo(.5f).Within(.00001f));
        }

        [Test]
        public void NoSimulationDuringFreeze_LeavesRemainingTimeAndMarkerIntact()
        {
            var world = new CombatSimulationWorld(); Component timer = Create(world, 1f);
            world.StepBeforePhysics(.25f, 1);
            // 推进外部 Tick 标识并不等于推进 Gameplay；Frozen 恢复时只扣随后真正模拟的 dt。
            world.BeginTick(1000); world.StepBeforePhysics(0f, 1000);
            Assert.That(Remaining(timer), Is.EqualTo(.75f).Within(.00001f));
            Assert.That(timer.gameObject.activeSelf, Is.True);
            world.StepBeforePhysics(.25f, 1001);
            Assert.That(Remaining(timer), Is.EqualTo(.5f).Within(.00001f));
        }

        [Test]
        public void Expiry_DeactivatesBeforeDestroyAndHappensExactlyOnce()
        {
            var world = new CombatSimulationWorld(1); Component timer = Create(world, .5f);
            world.StepBeforePhysics(.25f, 1);
            LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));
            world.StepBeforePhysics(.25f, 2);
            Assert.That(timer.gameObject.activeSelf, Is.False, "Unity 帧尾 Destroy 前先停用，复制层才能在同帧观察到终止。");
            Assert.That((bool)timer.GetType().GetProperty("Expired").GetValue(timer), Is.True);
            timer.GetType().GetMethod("StepAuthority").Invoke(timer, new object[] { 1f, 3u });
            world.StepBeforePhysics(1f, 3);
            LogAssert.NoUnexpectedReceived();
            Assert.That(Remaining(timer), Is.Zero);
            Assert.DoesNotThrow(() => Create(world, 1f), "到期必须归还固定 World 槽位，不能逐次施法累积占用。");
        }

        [Test]
        public void ThrownWorldModelUsesOriginalNormalizationAndCannotOwnGameplayPhysics()
        {
            var projectile = CreateThrownVisual(out var oldRenderer);
            projectile.transform.SetPositionAndRotation(new Vector3(231, 342, 123), Quaternion.Euler(0, 37, 0));
            projectile.transform.localScale = new Vector3(1.4f, .8f, 1.2f);
            var position = projectile.transform.position; var rotation = projectile.transform.rotation;
            var scale = projectile.transform.localScale; var rootBody = projectile.GetComponent<Rigidbody>();
            var model = CreateThrownModel();
            Assert.That(InstallThrownModel(projectile, model), Is.True);
            Assert.That(oldRenderer.enabled, Is.False, "原ThrowPrefab球体只保留物理职责，不与真实Catalog模型重叠显示。");
            Assert.That(projectile.transform.childCount, Is.EqualTo(1));
            var installed = projectile.transform.GetChild(0).gameObject;
            Assert.That(installed.activeSelf, Is.True);
            var renderers = installed.GetComponentsInChildren<Renderer>(true);
            Assert.That(renderers.Length, Is.EqualTo(2));
            Bounds bounds = renderers[0].bounds; bounds.Encapsulate(renderers[1].bounds);
            Assert.That(Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)), Is.EqualTo(.45f).Within(.001f));
            Assert.That(Vector3.Distance(bounds.center, position), Is.LessThan(.001f), "使用Bounds修正偏心导入pivot，不能移动物理根。");
            foreach (var renderer in renderers) Assert.That(renderer.enabled, Is.True);
            var behaviours = installed.GetComponentsInChildren<MonoBehaviour>(true);
            Assert.That(behaviours.Length, Is.GreaterThan(0));
            foreach (var behaviour in behaviours) Assert.That(behaviour.enabled, Is.False);
            foreach (var collider in installed.GetComponentsInChildren<Collider>(true)) Assert.That(collider.enabled, Is.False);
            foreach (var body in installed.GetComponentsInChildren<Rigidbody>(true))
            { Assert.That(body.isKinematic, Is.True); Assert.That(body.detectCollisions, Is.False); }
            var sphere = projectile.GetComponent<SphereCollider>();
            Assert.That(sphere.center, Is.EqualTo(Vector3.zero)); Assert.That(sphere.radius, Is.EqualTo(.18f));
            Assert.That(projectile.transform.position, Is.EqualTo(position)); Assert.That(projectile.transform.rotation, Is.EqualTo(rotation));
            Assert.That(projectile.transform.localScale, Is.EqualTo(scale), "模型归一不允许缩放原物理根。");
            Assert.That(rootBody.isKinematic, Is.True); Assert.That(rootBody.detectCollisions, Is.True);
            Assert.That(model.GetComponent<ThrownModelVisualTestBehaviour>().enabled, Is.True, "只净化克隆，不修改Catalog来源模型。");
        }

        [Test]
        public void ThrownWorldModelInstallationRejectsBadRepeatedAndAlreadyLaunchedChanges()
        {
            var projectile = CreateThrownVisual(out var oldRenderer);
            var invalid = new GameObject("Temporary model without renderers"); _owned.Add(invalid);
            Assert.That(InstallThrownModel(projectile, null), Is.False);
            Assert.That(InstallThrownModel(projectile, invalid), Is.False);
            Assert.That(oldRenderer.enabled, Is.True, "准备失败要保留旧视觉，不能交付一只不可见的代理。");
            Assert.That(projectile.transform.childCount, Is.Zero);
            var model = CreateThrownModel();
            Assert.That(InstallThrownModel(projectile, model), Is.True);
            var installed = projectile.transform.GetChild(0);
            Assert.That(InstallThrownModel(projectile, model), Is.False, "一只代理仅安装一次受控模型。");
            Assert.That(projectile.transform.childCount, Is.EqualTo(1)); Assert.That(projectile.transform.GetChild(0), Is.SameAs(installed));

            var launched = CreateThrownVisual(out var launchedRenderer);
            try
            {
                launched.Prepare("visual-inventory", "visual-world", "visual-flight", Vector3.forward, 8, 80, null);
                launched.Launch();
                Assert.That(InstallThrownModel(launched, model), Is.False, "Launch后禁止替换输出模型/碰撞体。");
                Assert.That(launchedRenderer.enabled, Is.True); Assert.That(launched.transform.childCount, Is.Zero);
            }
            finally
            {
                // 本例只测安装窗口，不把夹具销毁误报成真实Run的飞行损失。
                typeof(ThrownContainerProjectile).GetField("_resolved", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(launched, true);
            }
        }

        [Test]
        public void ReplicaWaitingIsOrangeOneWayAndNeverPublishesMaterialImpact()
        {
            var model = CreateThrownModel(); var replica = CreateThrownVisual(out _);
            replica.ConfigureExecutionMode(CombatExecutionMode.Replica);
            Assert.That(InstallThrownModel(replica, model), Is.True);
            var position = replica.transform.position; var rotation = replica.transform.rotation;
            int impacts = 0; Action<ContainerImpactEvent> listener = _ => impacts++;
            EventBus<ContainerImpactEvent>.Subscribe(listener);
            try
            {
                ApplyReplicaWaiting(replica, true);
                Assert.That(replica.IsWaiting, Is.True);
                var properties = new MaterialPropertyBlock();
                // Linear项目的SetColor/GetColor会经过Native sRGB往返；独立块校准同一原常量的精确观察值。
                // 比较仍是exact，不复制生产输出作为期望，也不通过容差吞掉真实错色。
                var sourceColor = new Color(1, .55f, .08f);
                var referenceBlock = new MaterialPropertyBlock();
                int colorId = Shader.PropertyToID("_BaseColor");
                referenceBlock.SetColor(colorId, sourceColor);
                var nativeReference = referenceBlock.GetColor(colorId);
                var wrongBlock = new MaterialPropertyBlock();
                wrongBlock.SetColor(colorId, new Color(.08f, .55f, 1));
                var wrongColor = wrongBlock.GetColor(colorId);
                Assert.That(wrongColor, Is.Not.EqualTo(nativeReference), "负校准：同Native往返后的明显错色必须仍被精确比较拒绝。");
                string Precision(Color color) =>
                    $"r={color.r:R}/0x{BitConverter.SingleToInt32Bits(color.r):X8}, " +
                    $"g={color.g:R}/0x{BitConverter.SingleToInt32Bits(color.g):X8}, " +
                    $"b={color.b:R}/0x{BitConverter.SingleToInt32Bits(color.b):X8}, " +
                    $"a={color.a:R}/0x{BitConverter.SingleToInt32Bits(color.a):X8}";
                foreach (var renderer in replica.transform.GetChild(0).GetComponentsInChildren<Renderer>(true))
                {
                    renderer.GetPropertyBlock(properties);
                    var actualColor = properties.GetColor(colorId);
                    TestContext.WriteLine($"Waiting color precision; space={QualitySettings.activeColorSpace}; " +
                        $"source=({Precision(sourceColor)}); actual=({Precision(actualColor)}); " +
                        $"nativeReference=({Precision(nativeReference)}); matchesReference={actualColor.Equals(nativeReference)}");
                    Assert.That(actualColor, Is.EqualTo(nativeReference));
                }
                ApplyReplicaWaiting(replica, false); ApplyReplicaWaiting(replica, true);
                replica.StepAuthority(1, 1);
                typeof(ThrownContainerProjectile).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(replica, null);
                Assert.That(replica.IsWaiting, Is.True, "迟到Flying pose不得把Waiting退回飞行。");
                Assert.That(impacts, Is.Zero, "客户端只更新视觉状态，不能发世界投放/重试意图。");
                Assert.That(replica.GetComponent<Collider>().enabled, Is.False);
                Assert.That(replica.GetComponent<Rigidbody>().isKinematic, Is.True);
                Assert.That(replica.transform.position, Is.EqualTo(position)); Assert.That(replica.transform.rotation, Is.EqualTo(rotation));

                var local = CreateThrownVisual(out _);
                Assert.That(InstallThrownModel(local, model), Is.True);
                ApplyReplicaWaiting(local, true); Assert.That(local.IsWaiting, Is.False, "单机真实影响仍只由碰撞/寿命产生。");
                local.ConfigureExecutionMode(CombatExecutionMode.Authority, new CombatSimulationWorld());
                ApplyReplicaWaiting(local, true); Assert.That(local.IsWaiting, Is.False, "Authority不得消费Replica视觉状态来跳过真实Impact。");
            }
            finally { EventBus<ContainerImpactEvent>.Unsubscribe(listener); }
        }

        private ThrownContainerProjectile CreateThrownVisual(out Renderer renderer)
        {
            var root = GameObject.CreatePrimitive(PrimitiveType.Sphere); root.name = "Temporary original throw physics root"; _owned.Add(root);
            root.transform.position = new Vector3(231, 342, 123);
            renderer = root.GetComponent<Renderer>();
            var sphere = root.GetComponent<SphereCollider>(); sphere.center = Vector3.one; sphere.radius = .7f;
            var body = root.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false;
            return root.AddComponent<ThrownContainerProjectile>();
        }

        private GameObject CreateThrownModel()
        {
            var model = new GameObject("Temporary off-center Catalog model"); _owned.Add(model);
            model.AddComponent<ThrownModelVisualTestBehaviour>();
            for (int i = 0; i < 2; i++)
            {
                var mesh = GameObject.CreatePrimitive(PrimitiveType.Cube); mesh.transform.SetParent(model.transform, false);
                mesh.transform.localPosition = new Vector3(i, 3 + i * 2, 0); mesh.transform.localScale = new Vector3(2, 3, 1);
                var body = mesh.AddComponent<Rigidbody>(); body.isKinematic = true; body.detectCollisions = true;
            }
            return model;
        }

        private static bool InstallThrownModel(ThrownContainerProjectile projectile, GameObject model)
        {
            var method = typeof(ThrownContainerProjectile).GetMethod("InstallWorldModel", new[] { typeof(GameObject) });
            Assert.That(method, Is.Not.Null, "正式飞瓶与Replica需要复用原Catalog模型净化/归一方法。");
            return (bool)method.Invoke(projectile, new object[] { model });
        }

        private static void ApplyReplicaWaiting(ThrownContainerProjectile projectile, bool waiting)
        {
            var method = typeof(ThrownContainerProjectile).GetMethod("ApplyReplicaWaiting", new[] { typeof(bool) });
            Assert.That(method, Is.Not.Null, "可靠Flying/Waiting事实必须有不发Impact的Replica视觉入口。");
            method.Invoke(projectile, new object[] { waiting });
        }

        private Component Create(CombatSimulationWorld world, float seconds)
        {
            Type type = typeof(CombatSimulationWorld).Assembly.GetType("Game.Combat.CombatVisualLifetime");
            Assert.That(type, Is.Not.Null, "预警不能再用真实时间 Destroy，而应登记到已有固定容量的 Combat World。");
            var root = new GameObject("N4 visual lifetime fixture"); _owned.Add(root);
            Component timer = root.AddComponent(type);
            MethodInfo configure = type.GetMethod("ConfigureAuthority", new[] { typeof(CombatSimulationWorld), typeof(float) });
            Assert.That(configure, Is.Not.Null);
            configure.Invoke(timer, new object[] { world, seconds }); return timer;
        }
        private static float Remaining(Component timer) => (float)timer.GetType().GetProperty("RemainingSeconds").GetValue(timer);
    }
}
