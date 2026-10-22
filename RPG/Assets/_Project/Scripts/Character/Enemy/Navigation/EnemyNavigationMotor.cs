using Game.Combat;
using Game.Core;
using UnityEngine;
using UnityEngine.AI;

namespace Game.Character
{
    public enum EnemyNavigationResult : byte
    {
        Unavailable = 0,
        Pending = 1,
        Moving = 2,
        Reached = 3,
        Partial = 4,
        Invalid = 5
    }

    /// <summary>
    /// NavMesh 的 Runtime Adapter：Agent 只计算路径、Steering 与 Local Avoidance，不直接移动 Transform。
    /// EnemyControllerBase 读取 DesiredVelocity 后仍用 CharacterController.Move 执行真实位移，避免两个移动权威互相争抢。
    /// </summary>
    public sealed class EnemyNavigationMotor
    {
        private const float ReachedTolerance = 0.05f;
        private const float MinimumAttachRadius = 0.5f;
        private const float MinimumExpandedTargetSampleRadius = 4f;
        private const float MinimumStuckRecoveryDuration = 0.35f;

        private readonly NavMeshAgent _agent;
        private readonly Transform _owner;
        private readonly float _pathRefreshInterval;
        private readonly float _destinationMoveThreshold;
        private readonly float _destinationSampleRadius;
        private readonly float _retreatStepDistance;
        private readonly float _retreatSampleRadius;
        private readonly float _stuckCheckInterval;
        private readonly float _stuckProgressDistance;
        private readonly Vector3[] _retreatDirections = new Vector3[3];
        private readonly Vector3[] _targetSamplePositions = new Vector3[3];
        private readonly float[] _targetSampleRadii = new float[3];
        private readonly NavMeshPath _candidatePath = new NavMeshPath();
        private readonly EnemyNavigationFailureTracker _failureTracker = new EnemyNavigationFailureTracker();
        private readonly float _initialRefreshOffset;

        private Vector3 _lastRequestedTarget;
        private Vector3 _lastProgressPosition;
        private Vector3 _retreatDestination;
        private Vector3 _lastRetreatThreatPosition;
        private Vector3 _desiredVelocity;
        private float _pathRefreshElapsed;
        private float _progressCheckElapsed;
        private float _retreatSelectionElapsed;
        private float _stuckRecoveryTimeRemaining;
        private bool _hasPathRequestAttempt;
        private bool _hasRetreatDestination;
        private bool _hasRetreatSelectionAttempt;
        private bool _forceRefresh;
        private bool _navigationActive;
        private bool _initialOffsetConsumed;
        private bool _warnedAgentUnavailable;
        private bool _warnedTargetUnavailable;
        private EnemyNavigationFailureSignal _failureSignal;

        public EnemyNavigationMotor(NavMeshAgent agent, EnemyDefinition definition, Transform owner)
            : this(
                agent,
                owner,
                definition != null ? definition.PathRefreshInterval : 0.2f,
                definition != null ? definition.DestinationMoveThreshold : 0.5f,
                definition != null ? definition.DestinationSampleRadius : 2f,
                definition != null ? definition.RetreatStepDistance : 4f,
                definition != null ? definition.RetreatSampleRadius : 1.5f,
                definition != null ? definition.StuckCheckInterval : 0.5f,
                definition != null ? definition.StuckProgressDistance : 0.05f)
        {
        }

        /// <summary>
        /// Boss 与普通 Enemy 共用同一套路径刷新、目标投影和卡住恢复机制；
        /// 仅 Authoring 数据来源不同，避免再维护一份容易漂移的 NavMesh Adapter。
        /// </summary>
        public EnemyNavigationMotor(NavMeshAgent agent, BossDefinition definition, Transform owner)
            : this(
                agent,
                owner,
                definition != null ? definition.PathRefreshInterval : 0.2f,
                definition != null ? definition.DestinationMoveThreshold : 0.5f,
                definition != null ? definition.DestinationSampleRadius : 2f,
                definition != null ? definition.RetreatStepDistance : 3f,
                definition != null ? definition.RetreatSampleRadius : 1.5f,
                definition != null ? definition.StuckCheckInterval : 0.5f,
                definition != null ? definition.StuckProgressDistance : 0.05f)
        {
        }

        private EnemyNavigationMotor(
            NavMeshAgent agent,
            Transform owner,
            float pathRefreshInterval,
            float destinationMoveThreshold,
            float destinationSampleRadius,
            float retreatStepDistance,
            float retreatSampleRadius,
            float stuckCheckInterval,
            float stuckProgressDistance)
        {
            _agent = agent;
            _owner = owner;
            _pathRefreshInterval = Mathf.Max(0.05f, pathRefreshInterval);
            _destinationMoveThreshold = Mathf.Max(0f, destinationMoveThreshold);
            _destinationSampleRadius = Mathf.Max(0.05f, destinationSampleRadius);
            _retreatStepDistance = Mathf.Max(0.1f, retreatStepDistance);
            _retreatSampleRadius = Mathf.Max(0.05f, retreatSampleRadius);
            _stuckCheckInterval = Mathf.Max(0.05f, stuckCheckInterval);
            _stuckProgressDistance = Mathf.Max(0f, stuckProgressDistance);
            _lastProgressPosition = owner != null ? owner.position : Vector3.zero;

            int instanceId = owner != null ? owner.gameObject.GetInstanceID() : 0;
            float normalizedOffset = Mathf.Abs(instanceId % 10) * 0.1f;
            _initialRefreshOffset = _pathRefreshInterval * normalizedOffset;

            if (_agent == null)
                return;

            // 位置和旋转只允许 CharacterController/Enemy FSM 写入；Agent 保留导航模拟职责。
            _agent.updatePosition = false;
            _agent.updateRotation = false;
            _agent.autoRepath = true;
            // Prefab 中相同的 Priority 会让并排 Enemy 在狭窄绕行入口形成对称让行。
            // 稳定分散优先级后，同一批 Enemy 会确定谁先通过，不引入随机抖动。
            _agent.avoidancePriority = EnemyNavigationMath.ResolveAvoidancePriority(instanceId);
        }

        public bool PathPending => IsReady && _agent.pathPending;
        public bool HasCompletePath => IsReady && !_agent.pathPending && _agent.hasPath &&
                                       _agent.pathStatus == NavMeshPathStatus.PathComplete;
        public float RemainingDistance => IsReady && !_agent.pathPending && _agent.hasPath
            ? _agent.remainingDistance
            : float.PositiveInfinity;
        public Vector3 DesiredVelocity => _desiredVelocity;
        private bool IsReady => _agent != null && _agent.enabled && _agent.isActiveAndEnabled && _agent.isOnNavMesh;

        public void BeginNavigation()
        {
            _navigationActive = true;
            _failureSignal = EnemyNavigationFailureSignal.None;
            _failureTracker.Reset();
            _lastProgressPosition = _owner.position;
            _progressCheckElapsed = 0f;
            _retreatSelectionElapsed = 0f;
            _hasRetreatSelectionAttempt = false;
            _hasRetreatDestination = false;
            _stuckRecoveryTimeRemaining = 0f;
            _forceRefresh = true;

            if (!IsReady)
                TryAttachToNavMesh();

            if (IsReady)
                _agent.isStopped = false;
        }

        public void StopNavigation(bool clearPath)
        {
            _navigationActive = false;
            _desiredVelocity = Vector3.zero;
            _failureSignal = EnemyNavigationFailureSignal.None;
            _failureTracker.Reset();
            _hasRetreatDestination = false;
            _hasRetreatSelectionAttempt = false;
            _retreatSelectionElapsed = 0f;
            _stuckRecoveryTimeRemaining = 0f;

            if (!IsReady)
                return;

            _agent.isStopped = true;
            if (clearPath && _agent.hasPath)
                _agent.ResetPath();
            _agent.nextPosition = _owner.position;
        }

        public void DisableNavigation()
        {
            StopNavigation(true);
            if (_agent != null)
                _agent.enabled = false;
        }

        public EnemyNavigationResult NavigateTo(
            Vector3 worldTarget,
            float stoppingDistance,
            float moveSpeed,
            float deltaTime)
        {
            _hasRetreatDestination = false;
            return NavigateToInternal(worldTarget, stoppingDistance, moveSpeed, deltaTime, false);
        }

        public EnemyNavigationResult TryNavigateRetreat(
            Vector3 threatPosition,
            float moveSpeed,
            float deltaTime)
        {
            EnsureActive();
            _retreatSelectionElapsed += Mathf.Max(0f, deltaTime);

            bool shouldRefreshSelection = EnemyNavigationMath.ShouldRefreshRetreatSelection(
                _hasRetreatSelectionAttempt,
                _retreatSelectionElapsed,
                _lastRetreatThreatPosition,
                threatPosition,
                _pathRefreshInterval,
                _destinationMoveThreshold);

            if (shouldRefreshSelection)
            {
                _hasRetreatSelectionAttempt = true;
                _lastRetreatThreatPosition = threatPosition;
                _retreatSelectionElapsed = 0f;
                _hasRetreatDestination = TrySelectRetreatDestination(threatPosition, out _retreatDestination);
                _forceRefresh = true;
            }

            if (!_hasRetreatDestination)
                return RegisterUnavailablePath(deltaTime);

            return NavigateToInternal(_retreatDestination, 0.05f, moveSpeed, deltaTime, true);
        }

        public void SyncToCharacter()
        {
            if (IsReady)
                _agent.nextPosition = _owner.position;
        }

        public bool CanAttack(Vector3 worldTarget, float range)
        {
            if (!HasCompletePath)
                return false;

            float directDistance = Mathf.Sqrt(HorizontalSqrDistance(_owner.position, worldTarget));
            float detourTolerance = Mathf.Max(0.25f, _agent.radius);
            return EnemyNavigationMath.IsAttackPathReady(
                _agent.remainingDistance,
                directDistance,
                range,
                detourTolerance);
        }

        private EnemyNavigationResult NavigateToInternal(
            Vector3 worldTarget,
            float stoppingDistance,
            float moveSpeed,
            float deltaTime,
            bool preserveRetreatDestination)
        {
            EnsureActive();
            _failureSignal = EnemyNavigationFailureSignal.None;
            _pathRefreshElapsed += Mathf.Max(0f, deltaTime);
            _stuckRecoveryTimeRemaining = Mathf.Max(0f, _stuckRecoveryTimeRemaining - Mathf.Max(0f, deltaTime));

            // CharacterController 位移或出生点误差都可能让 Agent 临时脱离 NavMesh。
            // 追击期间持续尝试重新贴合，避免 BeginNavigation 首次失败后永久原地重试。
            if (!IsReady && !TryAttachToNavMesh())
                return RegisterUnavailablePath(deltaTime);

            _agent.speed = Mathf.Max(0f, moveSpeed);
            _agent.stoppingDistance = Mathf.Max(0f, stoppingDistance);
            _agent.isStopped = false;

            bool shouldRefresh = _forceRefresh || !_hasPathRequestAttempt ||
                                 EnemyNavigationMath.ShouldRefreshPath(
                                     _pathRefreshElapsed,
                                     _lastRequestedTarget,
                                     worldTarget,
                                     _pathRefreshInterval,
                                     _destinationMoveThreshold);

            if (shouldRefresh && !TryRequestPath(worldTarget) &&
                (!_agent.hasPath || _agent.pathStatus == NavMeshPathStatus.PathInvalid))
                return RegisterUnavailablePath(deltaTime);

            if (!preserveRetreatDestination)
                _hasRetreatDestination = false;

            if (_agent.pathPending)
            {
                _desiredVelocity = EnemyNavigationMath.ResolveSteeringVelocity(
                    _agent.desiredVelocity,
                    _desiredVelocity,
                    _agent.hasPath);
                return EnemyNavigationResult.Pending;
            }

            if (!_agent.hasPath || _agent.pathStatus == NavMeshPathStatus.PathInvalid)
                return RegisterUnavailablePath(deltaTime);

            _desiredVelocity = EnemyNavigationMath.ResolveSteeringVelocity(
                _agent.desiredVelocity,
                _desiredVelocity,
                false);

            float remaining = _agent.remainingDistance;
            bool wantsToMove = EnemyNavigationMath.ShouldMoveAlongPath(
                remaining,
                _agent.stoppingDistance,
                ReachedTolerance,
                _desiredVelocity);
            UpdateFailureTracking(deltaTime, _agent.pathStatus != NavMeshPathStatus.PathComplete, wantsToMove);

            if (_agent.pathStatus == NavMeshPathStatus.PathComplete && wantsToMove &&
                _stuckRecoveryTimeRemaining > 0f)
            {
                // Repath 只会得到同一条合法路径，无法解除 Local Avoidance 的对称死锁。
                // 短时间直接朝当前路径拐点推进，仍服从 NavMesh 拐角而非玩家直线方向。
                _desiredVelocity = EnemyNavigationMath.ResolveStuckRecoveryVelocity(
                    _owner.position,
                    _agent.steeringTarget,
                    _agent.speed);
            }

            if (_agent.pathStatus == NavMeshPathStatus.PathPartial)
                return EnemyNavigationResult.Partial;

            if (!wantsToMove)
            {
                _desiredVelocity = Vector3.zero;
                _failureTracker.Reset();
                return EnemyNavigationResult.Reached;
            }

            return EnemyNavigationResult.Moving;
        }

        private void EnsureActive()
        {
            if (_navigationActive)
                return;

            BeginNavigation();
        }

        private bool TryRequestPath(Vector3 worldTarget)
        {
            _hasPathRequestAttempt = true;
            _lastRequestedTarget = worldTarget;
            _forceRefresh = false;
            _pathRefreshElapsed = !_initialOffsetConsumed ? -_initialRefreshOffset : 0f;
            _initialOffsetConsumed = true;

            float sampleRadius = _destinationSampleRadius;
            float queryRadius = Mathf.Max(0.05f, sampleRadius);
            float expandedRadius = Mathf.Max(
                MinimumExpandedTargetSampleRadius,
                queryRadius * 2f,
                _agent.radius * 4f);
            int queryCount = EnemyNavigationMath.FillTargetSampleQueries(
                worldTarget,
                _owner.position.y,
                queryRadius,
                expandedRadius,
                _targetSamplePositions,
                _targetSampleRadii);

            bool hasCandidate = false;
            bool selectedPathComplete = false;
            float selectedTargetErrorSqr = float.PositiveInfinity;
            Vector3 selectedDestination = Vector3.zero;

            Vector3 navigationPlaneTarget = EnemyNavigationMath.ProjectToNavigationPlane(
                worldTarget,
                _owner.position.y);
            if (_agent.CalculatePath(navigationPlaneTarget, _candidatePath) &&
                _candidatePath.status != NavMeshPathStatus.PathInvalid)
            {
                // 玩家 XZ 本身位于 NavMesh 时，直接目标最能保持墙体两侧的拓扑语义；
                // 即使结果暂时为 Partial，也先保留它，让 Enemy 朝可达边界推进而非原地等待。
                hasCandidate = true;
                selectedPathComplete = _candidatePath.status == NavMeshPathStatus.PathComplete;
                selectedTargetErrorSqr = 0f;
                selectedDestination = navigationPlaneTarget;
            }

            for (int i = 0; i < queryCount; i++)
            {
                if (!NavMesh.SamplePosition(
                        _targetSamplePositions[i],
                        out NavMeshHit hit,
                        _targetSampleRadii[i],
                        _agent.areaMask))
                    continue;

                // SamplePosition 只回答“附近有没有 NavMesh”，不会保证该面与 Enemy 连通。
                // 先同步验证路径，避免障碍顶部/孤岛采样成功后直接提交一个会原地结束的 Partial Path。
                if (!_agent.CalculatePath(hit.position, _candidatePath) ||
                    _candidatePath.status == NavMeshPathStatus.PathInvalid)
                    continue;

                bool candidatePathComplete = _candidatePath.status == NavMeshPathStatus.PathComplete;
                float candidateTargetErrorSqr = HorizontalSqrDistance(hit.position, worldTarget);
                if (!EnemyNavigationMath.ShouldPreferTargetCandidate(
                        hasCandidate,
                        selectedPathComplete,
                        selectedTargetErrorSqr,
                        candidatePathComplete,
                        candidateTargetErrorSqr))
                    continue;

                hasCandidate = true;
                selectedPathComplete = candidatePathComplete;
                selectedTargetErrorSqr = candidateTargetErrorSqr;
                selectedDestination = hit.position;
            }

            if (!hasCandidate)
            {
                WarnTargetUnavailable("目标附近没有与当前 Enemy 连通的可用 NavMesh");
                return false;
            }

            bool accepted = _agent.SetDestination(selectedDestination);
            if (!accepted)
            {
                WarnTargetUnavailable("SetDestination 拒绝了本次路径请求");
                return false;
            }

            _warnedTargetUnavailable = false;
            return accepted;
        }

        private bool TrySelectRetreatDestination(Vector3 threatPosition, out Vector3 destination)
        {
            destination = Vector3.zero;
            if (!IsReady)
                return false;

            Vector3 away = _owner.position - threatPosition;
            away.y = 0f;
            int count = EnemyNavigationMath.FillRetreatDirections(away, _retreatDirections);
            float currentThreatDistanceSqr = HorizontalSqrDistance(_owner.position, threatPosition);
            float bestThreatDistanceSqr = currentThreatDistanceSqr;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                Vector3 rawCandidate = _owner.position + _retreatDirections[i] * _retreatStepDistance;
                if (!NavMesh.SamplePosition(
                        rawCandidate,
                        out NavMeshHit hit,
                        _retreatSampleRadius,
                        _agent.areaMask))
                    continue;

                float threatDistanceSqr = HorizontalSqrDistance(hit.position, threatPosition);
                if (threatDistanceSqr <= bestThreatDistanceSqr)
                    continue;

                if (!_agent.CalculatePath(hit.position, _candidatePath) ||
                    _candidatePath.status != NavMeshPathStatus.PathComplete)
                    continue;

                found = true;
                bestThreatDistanceSqr = threatDistanceSqr;
                destination = hit.position;
            }

            return found;
        }

        private EnemyNavigationResult RegisterUnavailablePath(float deltaTime)
        {
            _desiredVelocity = Vector3.zero;
            if (!IsReady && !_warnedAgentUnavailable)
            {
                string ownerName = _owner != null ? _owner.name : "<null>";
                GameLog.Warn($"Enemy '{ownerName}' 的 NavMeshAgent 未贴合已烘焙 NavMesh；请检查出生点和 Agent Type", "EnemyNavigation");
                _warnedAgentUnavailable = true;
            }

            _failureSignal = _failureTracker.Tick(
                Mathf.Max(0f, deltaTime),
                true,
                true,
                false,
                _pathRefreshInterval);

            if (_failureSignal == EnemyNavigationFailureSignal.Repath)
                _forceRefresh = true;

            return IsReady ? EnemyNavigationResult.Invalid : EnemyNavigationResult.Unavailable;
        }

        private bool TryAttachToNavMesh()
        {
            if (_agent == null || !_agent.enabled || !_agent.isActiveAndEnabled)
                return false;
            if (_agent.isOnNavMesh)
                return true;

            float configuredRadius = _destinationSampleRadius;
            float radius = Mathf.Max(MinimumAttachRadius, _agent.radius * 2f, configuredRadius);
            if (!NavMesh.SamplePosition(_owner.position, out NavMeshHit hit, radius, _agent.areaMask) ||
                !_agent.Warp(hit.position))
                return false;

            _warnedAgentUnavailable = false;
            return true;
        }

        private void WarnTargetUnavailable(string reason)
        {
            if (_warnedTargetUnavailable)
                return;

            string ownerName = _owner != null ? _owner.name : "<null>";
            float sampleRadius = _destinationSampleRadius;
            int areaMask = _agent != null ? _agent.areaMask : 0;
            Vector3 ownerPosition = _owner != null ? _owner.position : Vector3.zero;
            // 仅在 Editor/Development Build 的首次失败记录关键查询参数，便于区分资产漏配、Area Mask 与 Bake 覆盖问题。
            GameLog.Warn(
                $"Enemy '{ownerName}' 路径刷新失败：{reason}；目标={_lastRequestedTarget}，Enemy={ownerPosition}，" +
                $"采样半径={sampleRadius:F2}，AreaMask={areaMask}；保留旧路径并继续重试",
                "EnemyNavigation");
            _warnedTargetUnavailable = true;
        }

        private void UpdateFailureTracking(float deltaTime, bool pathUnreachable, bool wantsToMove)
        {
            _progressCheckElapsed += Mathf.Max(0f, deltaTime);
            float checkInterval = _stuckCheckInterval;
            if (_progressCheckElapsed < Mathf.Max(0.05f, checkInterval))
                return;

            bool madeProgress = EnemyNavigationMath.HasProgress(
                _lastProgressPosition,
                _owner.position,
                _stuckProgressDistance);

            float observedDuration = _progressCheckElapsed;
            _progressCheckElapsed = 0f;
            _lastProgressPosition = _owner.position;

            // Partial Path 在仍向边界前进时不触发 Repath；到达边界或完整路径卡住后才重算。
            bool observedFailure = (pathUnreachable && !wantsToMove) || (!pathUnreachable && wantsToMove && !madeProgress);
            _failureSignal = _failureTracker.Tick(
                observedDuration,
                observedFailure,
                observedFailure,
                !observedFailure,
                _pathRefreshInterval);

            if (_failureSignal == EnemyNavigationFailureSignal.Repath)
            {
                _forceRefresh = true;
                float refreshInterval = _pathRefreshInterval;
                _stuckRecoveryTimeRemaining = Mathf.Max(MinimumStuckRecoveryDuration, refreshInterval);
            }
        }

        private static float HorizontalSqrDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x;
            float z = a.z - b.z;
            return x * x + z * z;
        }
    }
}
