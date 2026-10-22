using System.Collections;
using Game.Core;
using UnityEngine;

namespace Game.Run
{
    /// <summary>
    /// Safe Zone 与 Boss Arena 之间的一次性权限门：进入只布防，完整离开后才启动战斗。
    /// Checkpoint 必须先成功捕获，再允许 Encounter 发布 Start，避免 Retry 没有可靠恢复点。
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public sealed class BossEntryTrigger : MonoBehaviour
    {
        [SerializeField] private BossEncounterController _encounter;
        [SerializeField] private LayerMask _playerLayers;
        [SerializeField] private Transform _safeZoneVisualRoot;

        private Collider _trigger;
        private ParticleSystem[] _safeZoneParticles;
        private bool _armed;
        private bool _activated;

        private void Awake()
        {
            _trigger = GetComponent<Collider>();
            if (_trigger != null)
            {
                _trigger.isTrigger = true;
            }

            // 只在初始化时缓存粒子数组；出圈瞬间和等待消散期间不反复遍历 Hierarchy，
            // 避免把 GetComponentsInChildren 的数组分配带进运行时触发路径。
            _safeZoneParticles = _safeZoneVisualRoot != null
                ? _safeZoneVisualRoot.GetComponentsInChildren<ParticleSystem>(true)
                : System.Array.Empty<ParticleSystem>();
        }

        private void OnTriggerEnter(Collider other)
        {
            TryArm(other);
        }

        private void OnTriggerExit(Collider other)
        {
            TryActivate(other);
        }

        public bool TryArm(Collider other)
        {
            if (_activated || !IsPlayerCollider(other))
            {
                return false;
            }

            _armed = true;
            return true;
        }

        public bool TryActivate(Collider other)
        {
            if (!_armed || _activated || !IsPlayerCollider(other))
            {
                return false;
            }

            // Checkpoint 必须包含当前真实物品实例，不能用另克隆的开场法杖代替。
            var practice = HubPracticeSession.Current;
            bool captured = practice != null && practice.CaptureBossCheckpoint(other);
            if (!captured)
            {
                GameLog.Warn(
                    "Boss Entry 未能捕获 Run Checkpoint，已拒绝启动 Encounter。",
                    "Run");
                return false;
            }

            if (_encounter == null || !_encounter.TryBegin())
            {
                return false;
            }

            _activated = true;
            if (_trigger != null)
            {
                // Encounter 已获得唯一启动权后立即关闭物理边界，防止多个 Collider
                // 在相邻 Physics Step 中再次产生 Exit 回调。
                _trigger.enabled = false;
            }

            StopSafeZoneEmission();
            return true;
        }

        private bool IsPlayerCollider(Collider other)
        {
            return other != null &&
                   (_playerLayers.value & (1 << other.gameObject.layer)) != 0;
        }

        private void StopSafeZoneEmission()
        {
            for (int i = 0; i < _safeZoneParticles.Length; i++)
            {
                ParticleSystem particles = _safeZoneParticles[i];
                if (particles != null)
                {
                    // StopEmitting 保留已生成粒子的剩余 Lifetime；
                    // StopEmittingAndClear 会让圆环瞬间消失，不符合离场收尾表现。
                    particles.Stop(
                        false,
                        ParticleSystemStopBehavior.StopEmitting);
                }
            }

            if (_safeZoneVisualRoot != null)
            {
                StartCoroutine(HideVisualAfterParticlesExpire());
            }
        }

        private IEnumerator HideVisualAfterParticlesExpire()
        {
            while (HasAliveSafeZoneParticles())
            {
                yield return null;
            }

            if (_safeZoneVisualRoot != null)
            {
                _safeZoneVisualRoot.gameObject.SetActive(false);
            }
        }

        private bool HasAliveSafeZoneParticles()
        {
            for (int i = 0; i < _safeZoneParticles.Length; i++)
            {
                ParticleSystem particles = _safeZoneParticles[i];
                if (particles != null && particles.IsAlive(false))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
