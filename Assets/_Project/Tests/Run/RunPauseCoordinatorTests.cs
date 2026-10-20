using NUnit.Framework;
using UnityEngine;

namespace Game.Run.Tests
{
    /// <summary>
    /// 锁定多个 UI 共享暂停所有权时的核心规则：只要仍有任一 Pause Reason，Gameplay 就不能恢复。
    /// 测试直接观察 Time.timeScale，确保规则结果与 Unity 运行时副作用一致。
    /// </summary>
    public sealed class RunPauseCoordinatorTests
    {
        private GameObject _coordinatorObject;
        private RunPauseCoordinator _coordinator;
        private float _originalTimeScale;
        private CursorLockMode _originalCursorLockState;
        private bool _originalCursorVisible;

        [SetUp]
        public void SetUp()
        {
            _originalTimeScale = Time.timeScale;
            _originalCursorLockState = Cursor.lockState;
            _originalCursorVisible = Cursor.visible;
            Time.timeScale = 1f;
            // 模拟 Main Menu / Scene Transition 留给新 Gameplay Scene 的解锁状态。
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            _coordinatorObject = new GameObject("RunPauseCoordinatorTests");
            _coordinator = _coordinatorObject.AddComponent<RunPauseCoordinator>();

            // EditMode [Test] 创建组件后不会自动推进 MonoBehaviour 生命周期。
            // 这里显式执行生产 Awake，验证的仍是正式 Cursor 初始化路径，而不是在测试里复制实现。
            // Unity 6.3 的普通 EditMode Behaviour 通过 SendMessage 会触发 ShouldRunBehaviour Assert。
            // 只驱动正式 C# 边界；真实自动生命周期另由 PlayMode 验证。
            typeof(RunPauseCoordinator).GetMethod("Awake", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(_coordinator, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_coordinatorObject != null)
            {
                Object.DestroyImmediate(_coordinatorObject);
            }

            Time.timeScale = _originalTimeScale;
            Cursor.lockState = _originalCursorLockState;
            Cursor.visible = _originalCursorVisible;
        }

        [Test]
        public void Awake_InitializesGameplayCursorToLockedAndHidden()
        {
            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
            Assert.That(Cursor.visible, Is.False);
        }

        [Test]
        public void RequestAndReleasePause_OwnsCursorForUiAndGameplay()
        {
            _coordinator.RequestPause(RunPauseReason.GameplayGuide);

            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.None));
            Assert.That(Cursor.visible, Is.True);

            _coordinator.ReleasePause(RunPauseReason.GameplayGuide);

            Assert.That(Cursor.lockState, Is.EqualTo(CursorLockMode.Locked));
            Assert.That(Cursor.visible, Is.False);
        }

        [Test]
        public void RequestPause_FirstReason_PausesAndTracksOwner()
        {
            bool changed = _coordinator.RequestPause(RunPauseReason.Inventory);

            Assert.That(changed, Is.True);
            Assert.That(_coordinator.IsPaused, Is.True);
            Assert.That(_coordinator.ActiveReasons, Is.EqualTo(RunPauseReason.Inventory));
            Assert.That(Time.timeScale, Is.Zero);
        }

        [Test]
        public void ReleasePause_OneOfMultipleReasons_KeepsGameplayPaused()
        {
            _coordinator.RequestPause(RunPauseReason.Inventory);
            _coordinator.RequestPause(RunPauseReason.PauseMenu);

            bool changed = _coordinator.ReleasePause(RunPauseReason.Inventory);

            Assert.That(changed, Is.True);
            Assert.That(_coordinator.IsPaused, Is.True);
            Assert.That(_coordinator.ActiveReasons, Is.EqualTo(RunPauseReason.PauseMenu));
            Assert.That(Time.timeScale, Is.Zero,
                "Pause Menu 仍持有暂停原因时，关闭 Wand Editor 不能恢复 Gameplay。");
        }

        [Test]
        public void ReleasePause_LastReason_RestoresGameplay()
        {
            _coordinator.RequestPause(RunPauseReason.TerminalResult);

            bool changed = _coordinator.ReleasePause(RunPauseReason.TerminalResult);

            Assert.That(changed, Is.True);
            Assert.That(_coordinator.IsPaused, Is.False);
            Assert.That(_coordinator.ActiveReasons, Is.EqualTo(RunPauseReason.None));
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [Test]
        public void DuplicateRequestAndRelease_AreIdempotent()
        {
            Assert.That(_coordinator.RequestPause(RunPauseReason.PauseMenu), Is.True);
            Assert.That(_coordinator.RequestPause(RunPauseReason.PauseMenu), Is.False);
            Assert.That(_coordinator.ReleasePause(RunPauseReason.PauseMenu), Is.True);
            Assert.That(_coordinator.ReleasePause(RunPauseReason.PauseMenu), Is.False);

            Assert.That(_coordinator.ActiveReasons, Is.EqualTo(RunPauseReason.None));
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [Test]
        public void NoneReason_IsRejectedWithoutChangingTimeScale()
        {
            Assert.That(_coordinator.RequestPause(RunPauseReason.None), Is.False);
            Assert.That(_coordinator.ReleasePause(RunPauseReason.None), Is.False);
            Assert.That(_coordinator.IsPaused, Is.False);
            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        [Test]
        public void InventoryRelease_DoesNotReleasePauseMenuOrResearch()
        {
            _coordinator.RequestPause(RunPauseReason.Inventory);
            _coordinator.RequestPause(RunPauseReason.PauseMenu);
            _coordinator.RequestPause(RunPauseReason.SpellResearch);
            _coordinator.ReleasePause(RunPauseReason.Inventory);
            Assert.That(Time.timeScale, Is.Zero);
            Assert.That(_coordinator.HasReason(RunPauseReason.PauseMenu), Is.True);
            Assert.That(_coordinator.HasReason(RunPauseReason.SpellResearch), Is.True);
        }

        [Test]
        public void SceneTransitionReason_BlocksGameplayUntilLoadBoundaryPreparation()
        {
            Assert.That(
                _coordinator.RequestPause(RunPauseReason.SceneTransition),
                Is.True);
            Assert.That(Time.timeScale, Is.Zero,
                "Fade Canvas 只能挡住 UGUI Raycast；必须暂停 Gameplay 才能阻止移动与施法输入泄漏。");

            _coordinator.PrepareForSceneTransition();

            Assert.That(_coordinator.ActiveReasons, Is.EqualTo(RunPauseReason.None));
            Assert.That(Time.timeScale, Is.EqualTo(1f),
                "LoadSceneAsync 前必须恢复全局 Time Scale，避免新 Scene 启动后看似卡死。");
        }
        [Test]public void ResearchNinthBitDoesNotReleaseOtherWindowsOrTransitions()
        {
            _coordinator.RequestPause(RunPauseReason.SpellResearch);_coordinator.RequestPause(RunPauseReason.Inventory);
            _coordinator.RequestPause(RunPauseReason.Shop);_coordinator.RequestPause(RunPauseReason.SceneTransition);
            Assert.That((ushort)_coordinator.ActiveReasons & 256,Is.EqualTo(256));
            _coordinator.ReleasePause(RunPauseReason.SpellResearch);_coordinator.ReleasePause(RunPauseReason.SpellResearch);
            Assert.That(Time.timeScale,Is.Zero);Assert.That(_coordinator.HasReason(RunPauseReason.Inventory),Is.True);
            _coordinator.ReleasePause(RunPauseReason.Inventory);_coordinator.ReleasePause(RunPauseReason.Shop);Assert.That(Time.timeScale,Is.Zero);
            _coordinator.ReleasePause(RunPauseReason.SceneTransition);Assert.That(Time.timeScale,Is.EqualTo(1));
        }
    }
}
