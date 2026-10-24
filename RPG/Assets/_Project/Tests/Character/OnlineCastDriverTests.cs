using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Combat;
using Game.Core;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Character.Tests
{
    /// <summary>真实生产 Catalog / Owned事务 / SpellCaster 的组合回归；没有网络或本地存档捷径。</summary>
    public sealed class OnlineCastDriverTests
    {
        private const string Wand = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Card = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private GameObject _root;
        private readonly List<GameObject> _outputs = new List<GameObject>();
        private int _born;
        [SetUp] public void Setup() { _born = 0; EventBus<CombatOutputSpawnedEvent>.Subscribe(OnBorn); }
        private void OnBorn(CombatOutputSpawnedEvent fact) { _born++; _outputs.Add(fact.Output); }
        [TearDown] public void Cleanup()
        {
            EventBus<CombatOutputSpawnedEvent>.Unsubscribe(OnBorn);
            if (_root != null)
            {
                var adapter = _root.GetComponent<OwnedWandCastAdapter>();
                if (adapter != null) typeof(OwnedWandCastAdapter).GetMethod("OnDisable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
                var driver = _root.GetComponent(typeof(WizardController).Assembly.GetType("Game.Character.OnlineCastDriver"));
                if (driver != null) driver.GetType().GetMethod("OnDestroy",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(driver,null);
            }
            if (_root != null) Object.DestroyImmediate(_root);
            foreach (var output in _outputs) if (output != null) Object.DestroyImmediate(output);
            _outputs.Clear();
        }
        private object Create(out OnlineOwnedAssetSnapshot assets, bool insufficient = false)
        {
            var type = typeof(WizardController).Assembly.GetType("Game.Character.OnlineCastDriver");
            Assert.That(type, Is.Not.Null, "缺少正式角色的权威施法装配，不能解除单机 guard 代替。");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/WizardPlayer.prefab");
            _root = Object.Instantiate(prefab); _root.name = "N4 Cast Driver Fixture";
            _root.transform.position = new Vector3(23143, 34251, 12312);
            var wizard = _root.GetComponent<WizardController>(); wizard.ConfigureExecutionMode(PlayerExecutionMode.Authority);
            // EditMode 不保证自动发送 Awake；仅夹具补真实生命周期，不在生产代码手动触发 Unity 生命周期。
            if (wizard.SpellCaster == null) typeof(WizardController).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(wizard,null);
            var health = _root.GetComponent<HealthComponent>();
            if (health.CurrentHp <= 0) typeof(HealthComponent).GetMethod("Awake",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(health,null);
            var definition = AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset");
            assets = new OnlineOwnedAssetSnapshot { SchemaVersion = 1, ActorId = 1, PlayerId = "owner", RunId = "run", InventoryId = "online:test:" + Guid.NewGuid().ToString("N"),
                InventoryVersion = 1, SelectedWandInstanceId = Wand, GameplayConfigHash = definition.GameplayConfigHash,
                Items = new[] {
                    new OnlineOwnedItemSnapshot { InstanceId=Wand,ContentId="wandtest.wand.a",Revision=1,LocationKind=3,Slot=0,UseTemplateWandStats=!insufficient,
                        WandStats=insufficient ? new OnlineOwnedWandStatsSnapshot {MaxMana=10,ManaRegenPerSecond=0,BaseCastDelay=.8f,BaseReloadTime=.8f,ProgramCapacity=4,BaseDraws=1} : null },
                    new OnlineOwnedItemSnapshot {InstanceId=Card,ContentId="wandtest.fire",Revision=1,LocationKind=2,ParentInstanceId=Wand,Slot=0,RemainingCharges=30}
                }};
            var driver = _root.AddComponent(type);
            object[] init = { assets, definition, new CombatSimulationWorld(), null };
            Assert.That((bool)type.GetMethod("InitializeAuthority").Invoke(driver, init), Is.True, init[3] as string);
            var adapter = _root.GetComponent<OwnedWandCastAdapter>();
            // 和上面的 Awake 一样，EditMode 夹具必须建立正式 OnEnable 的同步收据订阅。
            typeof(OwnedWandCastAdapter).GetMethod("OnDisable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
            typeof(OwnedWandCastAdapter).GetMethod("OnEnable",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(adapter,null);
            type.GetMethod("SetConnection").Invoke(driver, new object[] { 1u, true });
            return driver;
        }
        private static OnlineCastIntent Intent(ulong command = 1) => new OnlineCastIntent { CommandId=command,ConnectionEpoch=1,ProgramRevision=1,WandInstanceId=Wand,
            ClaimedOrigin=new Vector3(23143,34251,12312),AimPoint=new Vector3(23143,34251,12332) };
        private static bool Queue(object driver, OnlineCastIntent intent)
        { object[] args = { intent, null }; return (bool)driver.GetType().GetMethod("TryQueue").Invoke(driver,args); }
        private static OnlineCastResourceSnapshot Resources(object driver)
        { object[] args = { null }; Assert.That((bool)driver.GetType().GetMethod("TryGetAuthorityResources").Invoke(driver,args), Is.True); return (OnlineCastResourceSnapshot)args[0]; }
        private static void Step(object driver, uint tick) => driver.GetType().GetMethod("StepAuthority").Invoke(driver,new object[] {1f/30f,tick});
        [Test] public void ForeignWandAndInvalidOrigin_AreRejectedWithoutOutputOrCharge()
        {
            var driver=Create(out _); var intent=Intent(); intent.WandInstanceId=new string('c',32);
            Assert.That(Queue(driver,intent),Is.False); intent=Intent(2);intent.ClaimedOrigin+=Vector3.right*5;
            Assert.That(Queue(driver,intent),Is.False); Assert.That(_born,Is.Zero); Assert.That(Resources(driver).CurrentMana,Is.EqualTo(100));
        }
        [Test] public void DuplicateRootCommand_OnlyCommitsOneRealOwnedCast()
        {
            var driver=Create(out _); Assert.That(Queue(driver,Intent()),Is.True);
            for(uint tick=1;tick<=15;tick++) Step(driver,tick);
            Assert.That(_born,Is.EqualTo(1)); Assert.That(Resources(driver).RemainingCharges,Is.EqualTo(29));
            Assert.That(Queue(driver,Intent()),Is.True);
            for(uint tick=16;tick<=45;tick++) Step(driver,tick);
            Assert.That(_born,Is.EqualTo(1)); Assert.That(Resources(driver).RemainingCharges,Is.EqualTo(29));
        }
        [Test] public void InsufficientMana_DoesNotSpawnOrConsumeFiniteCharge()
        {
            var driver=Create(out _,true); Queue(driver,Intent());
            for(uint tick=1;tick<=15;tick++) Step(driver,tick);
            Assert.That(_born,Is.Zero); Assert.That(Resources(driver).RemainingCharges,Is.EqualTo(30));
            Assert.That(Resources(driver).CurrentMana,Is.EqualTo(10));
        }
        [Test] public void ReinitializingSameRunAndNewEpoch_DoesNotRefillOwnedWorkspace()
        {
            var driver=Create(out var assets); Queue(driver,Intent()); for(uint tick=1;tick<=12;tick++) Step(driver,tick);
            var before=Resources(driver);
            object[] init={assets,AssetDatabase.LoadAssetAtPath<OnlineCombatDefinition>("Assets/_Project/Resources/Online/CombatDefinition.asset"),new CombatSimulationWorld(),null};
            Assert.That((bool)driver.GetType().GetMethod("InitializeAuthority").Invoke(driver,init),Is.True);
            driver.GetType().GetMethod("SetConnection").Invoke(driver,new object[]{2u,true});
            var after=Resources(driver); Assert.That(after.CurrentMana,Is.EqualTo(before.CurrentMana)); Assert.That(after.RemainingCharges,Is.EqualTo(29));
        }
        [Test] public void DeadActorCancelsDueSchedulerBeforeItCanRelease()
        {
            var driver = Create(out _);
            var caster = _root.GetComponent<SpellCaster>();
            var scheduler = (AuthorityCastScheduler)typeof(SpellCaster).GetField("_authorityScheduler", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(caster);
            int released = 0;
            Assert.That(scheduler.TrySchedule(.01f, 1, _ => released++, out _), Is.True);
            var health = _root.GetComponent<HealthComponent>();
            var hit = new DamageRequest(99, 2, 10000, DamageType.True, _root.transform.position, Vector3.forward);
            health.ReceiveHit(in hit);
            Assert.That(health.IsAlive, Is.False);
            Step(driver, 1);
            Assert.That(released, Is.Zero, "先检查死亡再推进 deadline；上一 Physics Tick 死亡不能留下最后一发。");
        }
    }
}
