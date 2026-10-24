using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using Game.Skills;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>施法音效是提交后的表现事实，不在 Dedicated Server 播放，也不能在回滚后留下一声“假成功”。</summary>
    public sealed class CombatCastSoundTests
    {
        private readonly List<Object> _owned = new List<Object>();
        private readonly List<object> _sounds = new List<object>();
        private Type _soundType;

        [SetUp]
        public void Setup()
        {
            _soundType = typeof(IGameEvent).Assembly.GetType("Game.Core.CombatCastSoundEvent");
            Assert.That(_soundType, Is.Not.Null, "Authority 的 CastSfx 需要提交后的表现事实，禁用服务器声音并不能替代客户端播放。");
            Subscription(true); EventBus<CombatOutputSpawnedEvent>.Subscribe(OnOutput);
        }
        [TearDown]
        public void Cleanup()
        {
            if (_soundType != null) Subscription(false);
            EventBus<CombatOutputSpawnedEvent>.Unsubscribe(OnOutput);
            for (int i = _owned.Count - 1; i >= 0; i--) if (_owned[i] != null) Object.DestroyImmediate(_owned[i]);
            _owned.Clear(); _sounds.Clear();
        }

        [Test]
        public void AuthorityProgram_PublishesOncePerClipWithOriginalCommandAndPosition()
        {
            SpellCaster caster = Caster(); AudioClip clip = Clip(); WandLoadout program = Program(clip, 2);
            caster.ConfigureAuthority(new CombatSimulationWorld(), 5); caster.SetAuthorityCommand(1234);
            Vector3 point = new Vector3(32123f, 19000f, 23123f);
            int audioSourcesBefore = Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            Assert.That(caster.CastProgram(program, point, point + Vector3.forward, 2, 90, null, SpellManaPolicy.IgnoreMana), Is.EqualTo(2));
            Assert.That(_sounds.Count, Is.EqualTo(1), "同一层 Multicast 的相同 Clip 只播放一次。");
            Assert.That(Read<ulong>(_sounds[0], "ActorId"), Is.EqualTo(5));
            Assert.That(Read<ulong>(_sounds[0], "CommandId"), Is.EqualTo(1234));
            Assert.That(Read<AudioClip>(_sounds[0], "Clip"), Is.SameAs(clip));
            Assert.That(Read<Vector3>(_sounds[0], "Position"), Is.EqualTo(point));
            Assert.That(Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                Is.EqualTo(audioSourcesBefore), "PlayClipAtPoint 会创建独立 GameObject；Authority 在整个场景都不应添加 AudioSource。");
        }

        [Test]
        public void SpawnTransaction_QueuesSoundUntilSuccessfulCommit()
        {
            object transaction = Transaction(); object sound = Sound(Clip());
            Publish(transaction, sound);
            Assert.That(_sounds, Is.Empty);
            TransactionMethod("Complete").Invoke(transaction, null);
            Assert.That(_sounds.Count, Is.EqualTo(1));
            Assert.That(Read<ulong>(_sounds[0], "CommandId"), Is.EqualTo(1234));
        }

        [Test]
        public void SpawnTransaction_RollbackSuppressesQueuedAndLaterSounds()
        {
            object transaction = Transaction(); object sound = Sound(Clip());
            Publish(transaction, sound);
            TransactionMethod("Rollback").Invoke(transaction, new object[] { null });
            Publish(transaction, sound);
            Assert.That(_sounds, Is.Empty);
        }

        [Test]
        public void Replica_CannotExecuteProgramOrPublishAuthoritySound()
        {
            SpellCaster caster = Caster(); caster.ConfigureReplica();
            Assert.That(caster.CastProgram(Program(Clip(), 1), Vector3.zero, Vector3.forward, 2, 90, null, SpellManaPolicy.IgnoreMana), Is.Zero);
            Assert.That(_sounds, Is.Empty);
        }

        private SpellCaster Caster()
        { var root = new GameObject("Cast sound authority fixture"); root.SetActive(false); _owned.Add(root); return root.AddComponent<SpellCaster>(); }
        private AudioClip Clip()
        { AudioClip clip = AudioClip.Create("N4 cast sound fixture", 32, 1, 8000, false); _owned.Add(clip); return clip; }
        private WandLoadout Program(AudioClip clip, int count)
        {
            var projectile = new GameObject("N4 cast sound projectile template"); projectile.AddComponent<SphereCollider>(); projectile.AddComponent<Arrow>(); _owned.Add(projectile);
            var emit = ScriptableObject.CreateInstance<SpellDefinition>(); _owned.Add(emit);
            emit.Kind = SpellKind.Emit; emit.ProjectilePrefab = projectile; emit.CastSfx = clip; emit.BaseSpeed = 10;
            var program = ScriptableObject.CreateInstance<WandLoadout>(); _owned.Add(program);
            program.Spells = new[] { emit, emit }; program.BaseDraws = count; return program;
        }
        private static object Transaction() => Activator.CreateInstance(typeof(SpellCaster).Assembly.GetType("Game.Character.CastSpawnTransaction"), true);
        private static MethodInfo TransactionMethod(string name) => typeof(SpellCaster).Assembly.GetType("Game.Character.CastSpawnTransaction").GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        private static void Publish(object transaction, object sound)
        { MethodInfo method = TransactionMethod("PublishSound"); Assert.That(method, Is.Not.Null); method.Invoke(transaction, new[] { sound }); }
        private object Sound(AudioClip clip)
        {
            object sound = Activator.CreateInstance(_soundType);
            _soundType.GetField("ActorId").SetValue(sound, 5UL); _soundType.GetField("CommandId").SetValue(sound, 1234UL);
            _soundType.GetField("Clip").SetValue(sound, clip); _soundType.GetField("Position").SetValue(sound, Vector3.zero);
            return sound;
        }
        private static T Read<T>(object sound, string field) => (T)sound.GetType().GetField(field).GetValue(sound);
        private void Subscription(bool subscribe) => GetType().GetMethod(nameof(Subscribe), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(_soundType).Invoke(this, new object[] { subscribe });
        private void Subscribe<T>(bool subscribe) where T : struct, IGameEvent
        { if (subscribe) EventBus<T>.Subscribe(OnSound); else EventBus<T>.Unsubscribe(OnSound); }
        private void OnSound<T>(T sound) where T : struct, IGameEvent => _sounds.Add(sound);
        private void OnOutput(CombatOutputSpawnedEvent fact) { if (fact.Output != null) _owned.Add(fact.Output); }
    }
}
