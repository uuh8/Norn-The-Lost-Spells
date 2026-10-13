using System.Collections;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Game.Run.Tests
{
    /// <summary>
    /// 验证跨 Scene Coordinator 只保存轻量 Run Data，并区分 New Run 与 Boss Retry 的恢复语义。
    /// </summary>
    public sealed class RunSessionCheckpointTests
    {
        private bool _restoreBackground;
        private bool _previousBackground;
        private SpellDefinition _fire;
        private SpellDefinition _water;
        private SpellLibrary _startingLibrary;
        private WandLoadout _startingWand;
        private GameObject _sessionObject;
        private RunSpellSession _spellSession;
        private RunSessionCoordinator _coordinator;

        [SetUp]
        public void SetUp()
        {
            CreateFixture();
        }

        private void CreateFixture(bool activateSessionRoot = false)
        {
            _fire = ScriptableObject.CreateInstance<SpellDefinition>();
            _water = ScriptableObject.CreateInstance<SpellDefinition>();
            _startingLibrary = ScriptableObject.CreateInstance<SpellLibrary>();
            _startingLibrary.Available = new[] { _fire };
            _startingWand = ScriptableObject.CreateInstance<WandLoadout>();
            _startingWand.Spells = new[] { _fire };
            _startingWand.BaseDraws = 1;

            _sessionObject = new GameObject("RunSessionCheckpointTests");
            _sessionObject.SetActive(false);
            _spellSession = _sessionObject.AddComponent<RunSpellSession>();
            Assert.That(_spellSession.ConfigureAndInitialize(_startingLibrary, _startingWand), Is.True,
                "夹具必须取得RunSpellSession所有权；不能忽略失败后用空Runtime继续测试。");
            _coordinator = _sessionObject.AddComponent<RunSessionCoordinator>();
            _sessionObject.SetActive(activateSessionRoot);
        }

        [TearDown]
        public void TearDown()
        {
            DestroyFixture();
            if (_restoreBackground) Application.runInBackground = _previousBackground;
            _restoreBackground = false;
        }

        private void DestroyFixture()
        {
            // 从未active的MonoBehaviour不会执行OnDestroy；夹具通过显式注入创建了Clone，
            // 因此也要显式释放。ClearRuntime可重复调用，active对象后续OnDestroy仍安全。
            if (_spellSession != null)
                _spellSession.ClearRuntime();
            if (_sessionObject != null)
                Object.DestroyImmediate(_sessionObject);
            if (_startingLibrary != null)
                Object.DestroyImmediate(_startingLibrary);
            if (_startingWand != null)
                Object.DestroyImmediate(_startingWand);
            if (_fire != null)
                Object.DestroyImmediate(_fire);
            if (_water != null)
                Object.DestroyImmediate(_water);

            _sessionObject = null;
            _spellSession = null;
            _coordinator = null;
            _startingLibrary = null;
            _startingWand = null;
            _fire = null;
            _water = null;
        }

        [Test]
        public void InactiveFixtureCleanup_ReleasesRuntimeClonesAndCurrent()
        {
            SpellLibrary runtimeLibrary = _spellSession.RuntimeLibrary;
            WandLoadout runtimeWand = _spellSession.RuntimeWand;
            Assert.That(_sessionObject.activeSelf, Is.False);
            Assert.That(runtimeLibrary, Is.Not.Null);
            Assert.That(runtimeWand, Is.Not.Null);

            DestroyFixture();

            // 未进入Awake的夹具也必须释放自己主动创建的数据，不能留下DontSave Clone。
            Assert.That(runtimeLibrary == null, Is.True);
            Assert.That(runtimeWand == null, Is.True);
            Assert.That(ReferenceEquals(RunSpellSession.Current, null), Is.True,
                "Current必须清成CLR null，不能遗留指向已销毁组件的托管包装。");
        }

        [Test]
        public void BeginNewRun_ResetsTemplatesAndClearsOldCheckpoint()
        {
            _spellSession.RuntimeWand.Spells = new[] { _water };
            Assert.That(_coordinator.CaptureBossCheckpoint(), Is.True);

            _coordinator.BeginNewRun();

            Assert.That(_coordinator.Stage, Is.EqualTo(RunStage.MapOne));
            Assert.That(_coordinator.EntryMode, Is.EqualTo(RunEntryMode.NewRun));
            Assert.That(_coordinator.HasBossCheckpoint, Is.False);
            Assert.That(_spellSession.RuntimeWand.Spells, Is.EqualTo(new[] { _fire }));
        }

        [Test]
        public void RestoreBossCheckpoint_RestoresWandAndMarksRetryEntry()
        {
            _spellSession.RuntimeWand.Spells = new[] { _water, _fire };
            _spellSession.RuntimeWand.BaseDraws = 2;
            _coordinator.PrepareBossFieldEntry();
            Assert.That(_coordinator.CaptureBossCheckpoint(), Is.True);

            _spellSession.RuntimeWand.Spells[0] = _fire;
            _spellSession.RuntimeWand.BaseDraws = 7;

            Assert.That(_coordinator.RestoreBossCheckpoint(), Is.True);
            Assert.That(_coordinator.Stage, Is.EqualTo(RunStage.BossField));
            Assert.That(_coordinator.EntryMode, Is.EqualTo(RunEntryMode.RetryBoss));
            Assert.That(_spellSession.RuntimeWand.Spells, Is.EqualTo(new[] { _water, _fire }));
            Assert.That(_spellSession.RuntimeWand.BaseDraws, Is.EqualTo(2));
        }

        [Test]
        public void ClearSession_ReleasesRuntimeAndRejectsCheckpointRestore()
        {
            Assert.That(_coordinator.CaptureBossCheckpoint(), Is.True);

            _coordinator.ClearSession();

            Assert.That(_spellSession.IsInitialized, Is.False);
            Assert.That(RunSpellSession.Current, Is.Null);
            Assert.That(_coordinator.HasBossCheckpoint, Is.False);
            Assert.That(_coordinator.RestoreBossCheckpoint(), Is.False);
        }

        [UnityTest]
        public IEnumerator EndRun_ClearsRuntimeAndDestroysSessionAuthority()
        {
            // EndRun 的契约包含 DontDestroyOnLoad Authority 与 Destroy 的帧末语义，
            // 这两者只能在真实 Play Mode 生命周期中验证，不能用 inactive EditMode 夹具代替。
            DestroyFixture();
            yield return new EnterPlayMode();
            // 此EditMode IEnumerator由Editor更新驱动；窗口失焦时，null并不保证经过游戏帧。
            // 保持后台运行并等待真实native销毁，仍断言全部Clone与Authority释放，不降低契约。
            _previousBackground = Application.runInBackground;
            _restoreBackground = true;
            Application.runInBackground = true;

            // Test Framework在Domain Reload后重新运行SetUp，恢复出的inactive夹具仍占有
            // RunSpellSession.Current，却没有Awake过的Coordinator.Current。先释放本夹具，
            // 才能创建用于验证真实DDOL/帧末Destroy的active实例，不能只清Coordinator单例。
            DestroyFixture();

            // EnterPlayMode 会载入当前 Editor Scene；若场景本身带有 Session Root，
            // 先清掉该临时 Play Mode Authority，避免它污染当前测试的 Singleton 前置条件。
            if (RunSessionCoordinator.Current != null)
            {
                Object.Destroy(RunSessionCoordinator.Current.gameObject);
                yield return null;
            }

            CreateFixture(activateSessionRoot: true);
            Assert.That(_coordinator.CaptureBossCheckpoint(), Is.True);

            SpellLibrary runtimeLibrary = _spellSession.RuntimeLibrary;
            WandLoadout runtimeWand = _spellSession.RuntimeWand;
            Assert.That(runtimeLibrary, Is.Not.Null);
            Assert.That(runtimeWand, Is.Not.Null);
            Assert.That(_coordinator.EndRun(), Is.True);
            Assert.That(_spellSession.IsInitialized, Is.False);
            Assert.That(RunSpellSession.Current, Is.Null,
                "清理立即释放数据所有权，不能等帧末销毁后才让下一局获得所有权。");
            double deadline = EditorApplication.timeSinceStartup + 3;
            while ((runtimeLibrary != null || runtimeWand != null || _sessionObject != null)
                && EditorApplication.timeSinceStartup < deadline) yield return null;

            // Destroy销毁native对象，但托管包装引用仍存在；必须使用Unity重载的==判断，
            // NUnit的CLR Is.Null无法识别这种“已销毁但还有C#引用”的状态。
            Assert.That(runtimeLibrary == null, Is.True);
            Assert.That(runtimeWand == null, Is.True);
            Assert.That(RunSpellSession.Current, Is.Null);
            Assert.That(RunSessionCoordinator.Current, Is.Null);
            Assert.That(_sessionObject == null, Is.True,
                "Restart/Main Menu 必须销毁 DDOL Authority，不能让 P7 新 Root 被空 Session 拒绝。");

            yield return new ExitPlayMode();
        }
    }
}
