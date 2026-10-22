using System.Collections;
using Game.Combat;
using Game.Core;
using Game.Run;
using UnityEngine;

namespace Game.UI
{
    /// <summary>
    /// Boss Teleport 的纯表现 Adapter。源点和目标点各预创建一套 Open/Idle/Close，
    /// Event 只驱动位置与显隐；目标合法性、Boss 位移和 Cooldown 全部仍由 Game.Character 决定。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BossTeleportVfxController : MonoBehaviour
    {
        [SerializeField] private HealthComponent _bossHealth;
        [SerializeField] private GameObject _openPrefab;
        [SerializeField] private GameObject _idlePrefab;
        [SerializeField] private GameObject _closePrefab;
        [Tooltip("Portal Prefab 的 Pivot 位于视觉圆心时，需要沿 World Up 抬高，避免下半圆进入地面。只影响表现，不修改 Gameplay Teleport 落点。")]
        [SerializeField] private float _visualHeightOffset = 1.5f;
        [SerializeField, Min(0f)] private float _openDuration = 0.35f;
        [SerializeField, Min(0f)] private float _closeDuration = 0.35f;

        private GameObject _sourceOpen;
        private GameObject _sourceIdle;
        private GameObject _sourceClose;
        private GameObject _destinationOpen;
        private GameObject _destinationIdle;
        private GameObject _destinationClose;
        private Coroutine _sourceRoutine;
        private Coroutine _destinationRoutine;

        public float VisualHeightOffset => _visualHeightOffset;

        private void Awake()
        {
            _sourceOpen = CreateVisual(_openPrefab, "Source Open");
            _sourceIdle = CreateVisual(_idlePrefab, "Source Idle");
            _sourceClose = CreateVisual(_closePrefab, "Source Close");
            _destinationOpen =
                CreateVisual(_openPrefab, "Destination Open");
            _destinationIdle =
                CreateVisual(_idlePrefab, "Destination Idle");
            _destinationClose =
                CreateVisual(_closePrefab, "Destination Close");
            ConfigureUnscaledParticles(_sourceOpen);
            ConfigureUnscaledParticles(_sourceIdle);
            ConfigureUnscaledParticles(_sourceClose);
            ConfigureUnscaledParticles(_destinationOpen);
            ConfigureUnscaledParticles(_destinationIdle);
            ConfigureUnscaledParticles(_destinationClose);
            HideAll();
        }

        private void OnEnable()
        {
            EventBus<BossTeleportEvent>.Subscribe(OnBossTeleport);
        }

        private void OnDisable()
        {
            EventBus<BossTeleportEvent>.Unsubscribe(OnBossTeleport);
            StopChannelRoutine(ref _sourceRoutine);
            StopChannelRoutine(ref _destinationRoutine);
            HideAll();
        }

        private void OnBossTeleport(BossTeleportEvent teleportEvent)
        {
            if (_bossHealth == null ||
                teleportEvent.BossId != _bossHealth.Id)
            {
                return;
            }

            // Event 中的位置是 NavMesh/Physics 验证过的 Gameplay Ground Point。
            // Portal 的 Pivot 属于美术空间，因此只在表现层添加 World Up 偏移，
            // 不能反向抬高 Boss Transform，否则会破坏 CharacterController 贴地关系。
            Vector3 visualOffset =
                Vector3.up * _visualHeightOffset;
            SetChannelPosition(
                _sourceOpen,
                _sourceIdle,
                _sourceClose,
                teleportEvent.SourcePosition + visualOffset);
            SetChannelPosition(
                _destinationOpen,
                _destinationIdle,
                _destinationClose,
                teleportEvent.DestinationPosition + visualOffset);

            switch (teleportEvent.Stage)
            {
                case BossTeleportStage.Telegraph:
                    BeginOpen(
                        source: true,
                        _sourceOpen,
                        _sourceIdle,
                        ref _sourceRoutine);
                    BeginOpen(
                        source: false,
                        _destinationOpen,
                        _destinationIdle,
                        ref _destinationRoutine);
                    break;
                case BossTeleportStage.Departed:
                    BeginClose(
                        source: true,
                        _sourceClose,
                        ref _sourceRoutine);
                    break;
                case BossTeleportStage.Arrived:
                    BeginClose(
                        source: false,
                        _destinationClose,
                        ref _destinationRoutine);
                    break;
                case BossTeleportStage.Cancelled:
                    BeginClose(
                        source: true,
                        _sourceClose,
                        ref _sourceRoutine);
                    BeginClose(
                        source: false,
                        _destinationClose,
                        ref _destinationRoutine);
                    break;
            }
        }

        private void BeginOpen(
            bool source,
            GameObject open,
            GameObject idle,
            ref Coroutine routine)
        {
            StopChannelRoutine(ref routine);
            ShowOnly(source, open);
            routine = StartCoroutine(
                OpenThenIdleRoutine(source, idle));
        }

        private void BeginClose(
            bool source,
            GameObject close,
            ref Coroutine routine)
        {
            StopChannelRoutine(ref routine);
            ShowOnly(source, close);
            routine = StartCoroutine(HideAfterCloseRoutine(source));
        }

        private IEnumerator OpenThenIdleRoutine(
            bool source,
            GameObject idle)
        {
            float elapsed = 0f;
            while (elapsed < _openDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            ShowOnly(source, idle);
            SetRoutine(source, null);
        }

        private IEnumerator HideAfterCloseRoutine(bool source)
        {
            float elapsed = 0f;
            while (elapsed < _closeDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            ShowOnly(source, null);
            SetRoutine(source, null);
        }

        private void SetRoutine(bool source, Coroutine routine)
        {
            if (source)
            {
                _sourceRoutine = routine;
            }
            else
            {
                _destinationRoutine = routine;
            }
        }

        private void StopChannelRoutine(ref Coroutine routine)
        {
            if (routine == null)
            {
                return;
            }

            StopCoroutine(routine);
            routine = null;
        }

        private GameObject CreateVisual(GameObject prefab, string objectName)
        {
            if (prefab == null)
            {
                return null;
            }

            GameObject instance = Instantiate(prefab, transform, false);
            instance.name = objectName;
            instance.SetActive(false);
            return instance;
        }

        private void ShowOnly(bool source, GameObject active)
        {
            if (source)
            {
                SetActive(_sourceOpen, ReferenceEquals(active, _sourceOpen));
                SetActive(_sourceIdle, ReferenceEquals(active, _sourceIdle));
                SetActive(_sourceClose, ReferenceEquals(active, _sourceClose));
                return;
            }

            SetActive(
                _destinationOpen,
                ReferenceEquals(active, _destinationOpen));
            SetActive(
                _destinationIdle,
                ReferenceEquals(active, _destinationIdle));
            SetActive(
                _destinationClose,
                ReferenceEquals(active, _destinationClose));
        }

        private void HideAll()
        {
            ShowOnly(source: true, null);
            ShowOnly(source: false, null);
        }

        private static void SetChannelPosition(
            GameObject open,
            GameObject idle,
            GameObject close,
            Vector3 position)
        {
            SetPosition(open, position);
            SetPosition(idle, position);
            SetPosition(close, position);
        }

        private static void SetPosition(GameObject target, Vector3 position)
        {
            if (target != null)
            {
                target.transform.position = position;
            }
        }

        private static void SetActive(GameObject target, bool active)
        {
            if (target != null)
            {
                target.SetActive(active);
            }
        }

        private static void ConfigureUnscaledParticles(GameObject visual)
        {
            if (visual == null)
            {
                return;
            }

            // 未来若 Terminal/Pause 与 Teleport 边界重叠，粒子仍应完成收尾；
            // 数组只在 Awake 获取，不在事件或逐帧路径重复分配。
            ParticleSystem[] particles =
                visual.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                ParticleSystem.MainModule main = particles[i].main;
                main.useUnscaledTime = true;
            }
        }
    }
}
