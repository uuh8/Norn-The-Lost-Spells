using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;
using Game.Core;
using Game.Combat;
using Game.Run;
using Game.Skills;

namespace Game.Character
{
    /// <summary>
    /// 法术施放器（Runtime Adapter）：把 Game.Skills 的纯数据求值结果转换成 Unity 场景对象。
    /// CastEvaluator 只把有序法术序列解释为 List&lt;EmitCommand&gt;；这里才负责 Instantiate、
    /// ProjectileBase.Init、音效播放以及 Payload 的命中/定时递归，因此纯逻辑层不需要依赖场景和 Physics。
    ///
    /// 可以把完整职责链理解为：
    /// 有序法术配置 → CastEvaluator 纯求值 → EmitCommand 值快照 → SpellCaster 创建/配置对象
    /// → ProjectileBase 接管 Rigidbody、碰撞与伤害入口。
    /// SpellCaster 只连接这些模块，不在这里实现法术排列规则，也不直接修改目标 HP。
    /// </summary>
    public class SpellCaster : MonoBehaviour
    {
        // Runtime Trigger 可能在投射物命中后的未来帧再次调用 RunCast。
        // 这里与纯解释器使用同一深度上限，形成第二道防线，防止错误数据或未来规则扩展造成无界递归。
        private const int MaxRuntimeTriggerDepth = CastEvaluator.MaxActionDepth;

        [Header("Run Data Source")]
        [SerializeField]
        [Tooltip("P7 单局模式的数据源；配置后优先使用它的 Runtime Wand，旧场景仍可回退到下方 Wand。")]
        // 单局 Session 持有玩家本局可编辑的 Runtime 配置，避免直接修改 Project 中的模板资产。
        private RunSpellSession _runSpellSession;

        // Inspector 模板/旧实验场景的回退数据源。正式单局中优先读取 _runSpellSession.RuntimeWand。
        [SerializeField] private WandLoadout _wand;
        [Tooltip("可用法力值")]
        // ManaComponent 持有当前角色独立的可变法力值；SpellDefinition/WandLoadout 不保存本次运行资源。
        [SerializeField] private ManaComponent _mana;

        [Header("开发诊断")]
        // 诊断等级只控制 Editor/Development Build 日志，不改变求值、扣费或生成结果。
        [SerializeField] private CastTraceLevel _traceLevel = CastTraceLevel.Off;
        // ProfilerMarker 会在 Unity Profiler 中形成可独立观察的采样区间，用于定位“求值”之后的生成成本。
        private static readonly ProfilerMarker s_runtimeSpawnMarker = new ProfilerMarker("Spell.RuntimeSpawn");

        // Runtime Diagnostics 隔离条件编译与日志格式化，让本类只保留 Gameplay Adapter 主流程。
        private readonly SpellCastRuntimeDiagnostics _diagnostics = new SpellCastRuntimeDiagnostics();
        private readonly List<EmitCommand> _emits = new List<EmitCommand>(16);  // 复用容器
        private readonly HashSet<AudioClip> _playedSfx = new HashSet<AudioClip>();
        // RaycastNonAlloc 由调用方提供数组接收结果，避免每次落点检测创建新数组。
        private readonly RaycastHit[] _landingHits = new RaycastHit[8];
        /// <summary>
        /// 返回当前真正参与求值的法杖。P7 使用 Runtime Clone，旧实验场景仍兼容 Inspector 中的模板 Wand。
        /// 这里按需解析而不在 Awake 缓存，避免依赖不同 GameObject 之间不可保证的 Awake 执行顺序。
        /// 表达式体属性只把读取转发给 ResolveWand；外部不能通过该属性替换内部数据源。
        /// </summary>
        public WandLoadout Wand => ResolveWand();

        /// <summary>
        /// 对外暴露当前有效的诊断等级。Release Build 直接返回 Off，调用方不能误以为详细 Trace 仍会运行。
        /// </summary>
        public CastTraceLevel TraceLevel => _diagnostics.ResolveLevel(_traceLevel);

        private void Awake()
        {
            // Awake 在该 MonoBehaviour 实例启用前由 Unity 调用一次，适合缓存同一 GameObject 上的组件引用。
            // GetComponent 只在当前 GameObject 上查找指定组件；Inspector 未手动绑定时才自动补齐引用。
            if (_mana == null)
                _mana = GetComponent<ManaComponent>();
        }
        private void OnDestroy()
        {
            // 组件单独销毁也会停止其 Coroutine；必须主动取消仍在 Session 中的未来预算。
            OwnedWandCastAdapter adapter = GetComponent<OwnedWandCastAdapter>();
            if (adapter != null) adapter.CancelOutstanding();
        }

        /// <summary>
        /// 运行当前有序法术序列：从 spawnPos 朝 aimPoint 施放，返回解释器产出的命令数量。
        /// 这里的“运行”不是逐条直接生成对象，而是先完整求值得到命令，再统一进入 Runtime Spawn 阶段。
        /// </summary>
        /// <param name="spawnPos">本次攻击的世界空间发射点，来自角色身上的发射 Transform。</param>
        /// <param name="aimPoint">玩家按下攻击时锁定的世界空间目标点，不是一个方向向量。</param>
        /// <param name="team">攻击者阵营快照，供投射物过滤自身和友方。</param>
        /// <param name="attackerId">攻击者实例 Id，这次伤害归属于哪个 Runtime 对象，用于伤害归属和事件过滤。</param>
        /// <param name="casterCollider">施法者碰撞体；投射物出生后应该忽略施法者 Collider，防止出生即撞到自己。</param>
        /// <returns>解释器产出的 EmitCommand 数量；它不等同于最终成功 Instantiate 的对象数量。</returns>
        public int CastWand(
            Vector3 spawnPos,           // 攻击对象从哪个世界坐标生成。
            Vector3 aimPoint,           // 玩家点击时锁存的世界坐标目标点。
            byte team,                  // 攻击者阵营，用于过滤自身和友军。
            int attackerId,             // 这次伤害归属于哪个 Runtime 对象。
            Collider casterCollider)    // 投射物出生后应该忽略哪个施法者 Collider。
        {
            // 1. 读取法术配置
            WandLoadout activeWand = ResolveWand();
            if (activeWand == null || activeWand.Spells == null || activeWand.Spells.Length == 0)
            {
                GameLog.Warn("SpellCaster 未配置 WandLoadout 或法杖为空，无法施放", "Skills");
                return 0;
            }

            // 目标点减发射点得到朝向向量；sqrMagnitude 不开平方，比 magnitude 更适合只做“是否接近零”的比较。
            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f) baseDir = transform.forward;
            baseDir.Normalize();    // Normalize 只保留方向、把长度变成 1，之后再乘速度即可得到初始 velocity。

            int castId = _diagnostics.AllocateCastId();
            return RunCast(activeWand.Spells, activeWand.BaseDraws, CastModifierState.Default,
                           spawnPos, aimPoint, baseDir, SpellAimMode.Direct,
                           team, attackerId, casterCollider, castId, 0,
                           SpellManaPolicy.SpendCasterMana);
        }

        /// <summary>
        /// SpellCaster 的核心管线：先对一层法术 Action 做 Mana 预检和纯求值，再把输出命令翻译成 Unity 对象。
        /// spells 是只读访问接口；baseDraws 是本层初始产出预算；incomingMods 是进入本层前已经形成的修正快照。
        /// spawnPos 对普通 Emit 是发射点，对 SkyfallAtPoint 是目标落点；Trigger 会把命中点/定时点作为下一层 spawnPos。
        /// team、attackerId、casterCollider 是攻击归属与碰撞过滤上下文；castId/depth 只服务诊断和递归护栏。
        /// manaPolicy 决定是否操作玩家的 ManaComponent，但不会改变解释器的法术排列语义。
        /// </summary>
        private int RunCast(
            IReadOnlyList<SpellDefinition> spells,
            int baseDraws,
            CastModifierState incomingMods,
            Vector3 spawnPos,
            Vector3 aimPoint,
            Vector3 baseDir,
            SpellAimMode aimMode,
            byte team,
            int attackerId,
            Collider casterCollider,
            int castId,
            int depth,
            SpellManaPolicy manaPolicy,
            OwnedWandCastContext ownedSource = default)
        {
            if (ownedSource.Enabled)
                return RunOwnedCast(ownedSource, spawnPos, aimPoint, baseDir, aimMode, team, attackerId, casterCollider, castId, depth, manaPolicy);
            if (depth >= MaxRuntimeTriggerDepth)
            {
                // 每层 Payload 都是父序列后面的一个更短 Action 切片，但运行时仍设置硬上限：
                // 即使未来 Authoring 语义扩展，也不会让命中回调形成无界递归。
                GameLog.Warn(
                    $"Cast #{castId} 的 Trigger 深度达到 {MaxRuntimeTriggerDepth}，已停止继续展开 Payload。",
                    "Skills");
                return 0;
            }

            // 1. 计算消耗
            // EstimateManaCost 镜像解释器的读取边界，但不 Instantiate、也不修改 ManaComponent。
            // 当前层采用“全有或全无”的整笔支付：资源不足时不生成任何对象，避免只释放出一半组合。
            // Trigger 捕获的 Payload 属于未来的下一层，此时不预付；真正触发时会再次进入 RunCast 并单独扣费。
            float requiredMana = CastEvaluator.EstimateManaCost(spells, baseDraws, incomingMods);

            float availableMana = _mana != null ? _mana.CurrentMana : float.PositiveInfinity;
            if (!ownedSource.Enabled && !TrySpendMana(requiredMana, manaPolicy))
            {
                // 负责法力预检失败时记录原因（调试函数）
                _diagnostics.LogManaPreflightFailure(
                    spells,
                    baseDraws,
                    incomingMods,
                    _emits,
                    _traceLevel,
                    castId,
                    depth,
                    requiredMana,
                    availableMana);
                _emits.Clear();
                return 0;
            }

            // 2. 解释法术
            // 负责运行解释器并把解释结果写进发射订单列表
            // 返回一个轻量的求值摘要（CastSummary），里面只有“解释器认为消耗了多少法力”和“是否中途熄火”
            // 只读法术序列 + 初始产出预算 + 修正状态快照 → 法术解释器 → 可变数量的发射订单列表
            _diagnostics.EvaluateCommands(
                spells,
                baseDraws,
                incomingMods,
                _emits,
                _traceLevel,
                castId,
                depth,
                requiredMana);

            // 音效去重的作用域是“当前求值层”。未来 Payload 触发会作为新层重新 Clear，可以播放自己的施法音效。
            _playedSfx.Clear();

            // 先保存 Count，明确本轮只消费当前 Evaluate 的结果；事件回调要到未来命中/计时帧才可能再次复用 _emits。
            int count = _emits.Count;
            // Auto() 返回一个可释放的采样作用域；离开 using 块时自动结束 Profiler 采样。
            using (s_runtimeSpawnMarker.Auto())
            {
                for (int i = 0; i < count; i++)
                {
                    EmitCommand cmd = _emits[i];

                    // 如果音效为空 ➡️ 不播放。
                    // 如果音效不为空，且是第一次遇到这个音效 ➡️ Add 返回 true，条件成立 ➡️ 播放音效。
                    // 如果音效不为空，但已经播放过了 ➡️ Add 返回 false，条件不成立 ➡️ 不播放
                    if (cmd.CastSfx != null && _playedSfx.Add(cmd.CastSfx))
                        AudioSource.PlayClipAtPoint(cmd.CastSfx, spawnPos);

                    if (cmd.ProjectilePrefab == null)
                    {
                        GameLog.Warn("[SpellCaster] EmitCommand.ProjectilePrefab 为空，跳过该法术产出", "Skills");
                        continue;
                    }

                    switch (cmd.SpawnMode)
                    {
                        case SpellSpawnMode.SkyfallAtPoint:
                            SpawnSkyfallProjectile(
                                cmd, spawnPos, baseDir, i, count, team, attackerId,
                                casterCollider, castId, depth, manaPolicy, ownedSource);
                            break;

                        case SpellSpawnMode.StaticAtPoint:
                            SpawnStaticProjectileAtPoint(cmd, spawnPos, baseDir, team, attackerId, casterCollider);
                            break;

                        default:
                            SpawnForwardProjectile(
                                cmd, spawnPos, aimPoint, baseDir, aimMode, i, count, team, attackerId,
                                casterCollider, castId, depth, manaPolicy, ownedSource);
                            break;
                    }
                }
            }

            // 返回的是求值命令数。若某条命令 Prefab 缺失或组件不合法，实际生成数可能更少。
            return count;
        }

        private int RunOwnedCast(in OwnedWandCastContext source, Vector3 spawn, Vector3 aim, Vector3 direction,
            SpellAimMode aimMode, byte team, int attacker, Collider collider, int castId, int depth, SpellManaPolicy policy)
        {
            if (depth >= MaxRuntimeTriggerDepth || !source.TryPrepare(out OwnedWandCastContext prepared)) return 0;
            CastExecutionLayer layer = prepared.Layer;
            try
            {
                prepared = prepared.PrepareMaterials(this, spawn);
                using (s_runtimeSpawnMarker.Auto())
                    for (int i = 0; i < layer.CommandCount; i++)
                    {
                        EmitCommand cmd = layer.GetCommand(i);
                        switch (cmd.SpawnMode)
                        {
                            case SpellSpawnMode.SkyfallAtPoint:
                                SpawnSkyfallProjectile(cmd, spawn, direction, i, layer.CommandCount, team, attacker, collider, castId, depth, policy, prepared); break;
                            case SpellSpawnMode.StaticAtPoint:
                                SpawnStaticProjectileAtPoint(cmd, spawn, direction, team, attacker, collider, prepared);
                                prepared.ReleaseMaterial(i, spawn + Vector3.up * .25f); break;
                            default:
                                SpawnForwardProjectile(cmd, spawn, aim, direction, aimMode, i, layer.CommandCount, team, attacker, collider, castId, depth, policy, prepared); break;
                        }
                    }
                if (!prepared.Finish(true))
                { prepared.SpawnTransaction.Rollback(this); prepared.Finish(false); return 0; }
                prepared.SpawnTransaction.Complete();
            }
            catch (System.Exception exception)
            {
                prepared.SpawnTransaction.Rollback(this); prepared.Finish(false);
                if (prepared.Adapter != null) prepared.Adapter.ReportSpawnFailure(prepared);
                GameLog.Warn("施法生成失败，未提交本层资源：" + exception.Message, "Skills"); return 0;
            }
            // 提交后才播放音效，失败事务不留下 AudioSource。
            // 材料代理在资源提交后激活，接触/失败重试不能绕过根施法的回滚边界。
            prepared.ActivateMaterials();
            _playedSfx.Clear();
            for (int i = 0; i < layer.CommandCount; i++)
            { AudioClip clip = layer.GetCommand(i).CastSfx; if (clip != null && _playedSfx.Add(clip)) AudioSource.PlayClipAtPoint(clip, spawn); }
            return layer.CommandCount;
        }

        /// <summary>
        /// 运行调用方显式提供的 Wand Program。Boss 使用 IgnoreMana，但仍复用同一个
        /// CastEvaluator、EmitCommand、Projectile 与 Payload 递归链路。
        /// </summary>
        /// <remarks>
        /// 与 CastWand 的区别只在“配置来源”和 Mana Policy；方向计算、求值、生成与 Payload 行为完全复用 RunCast。
        /// </remarks>
        public int CastProgram(
            WandLoadout program,
            Vector3 spawnPos,
            Vector3 aimPoint,
            byte team,
            int attackerId,
            Collider casterCollider,
            SpellManaPolicy manaPolicy,
            SpellAimMode aimMode = SpellAimMode.Direct)
        {
            if (program == null ||
                program.Spells == null ||
                program.Spells.Length == 0)
            {
                GameLog.Warn(
                    "SpellCaster 收到空的显式 Wand Program，无法施放",
                    "Skills");
                return 0;
            }

            // CastProgram 也接收“点”而不是“方向”，因此必须执行与玩家入口相同的世界空间方向换算。
            Vector3 baseDir = aimPoint - spawnPos;
            if (baseDir.sqrMagnitude < 1e-6f)
                baseDir = transform.forward;
            baseDir.Normalize();

            int castId = _diagnostics.AllocateCastId();
            return RunCast(
                program.Spells,
                program.BaseDraws,
                CastModifierState.Default,
                spawnPos,
                aimPoint,
                baseDir,
                aimMode,
                team,
                attackerId,
                casterCollider,
                castId,
                0,
                manaPolicy);
        }
        public int CastOwnedProgram(in WandProgramSnapshot program, in OwnedWandCastContext source,
            Vector3 spawn, Vector3 aim, byte team, int attacker, Collider collider)
        {
            if (!source.Enabled || program.Program == null || source.SourceWandId != program.SourceWandId
                || source.ProgramRevision != program.ProgramRevision) return 0;
            Vector3 direction = aim - spawn; if (direction.sqrMagnitude < 1e-6f) direction = transform.forward;
            direction.Normalize();
            return RunCast(program.Program, program.BaseDraws, CastModifierState.Default, spawn, aim, direction,
                SpellAimMode.Direct, team, attacker, collider, _diagnostics.AllocateCastId(), 0, SpellManaPolicy.SpendCasterMana, source);
        }

        # region 内部功能函数
        /// <summary>
        /// 解析当前权威的有序法术配置：优先使用单局 Runtime Clone，无法绑定时才回退到 Inspector 模板。
        /// 返回的是 ScriptableObject 引用，不会在这里复制数组或重新创建资产。
        /// </summary>
        private WandLoadout ResolveWand()
        {
            if (TryBindCurrentRunSession())
                return _runSpellSession.RuntimeWand;

            return _wand;
        }

        /// <summary>
        /// 跨 Scene 后重新绑定由 DontDestroyOnLoad 保留的单局数据。
        /// Scene Asset 不能序列化指向运行时对象的引用，因此后续关卡必须通过 Current 找回同一局 Session。
        /// </summary>
        /// <returns>找到且已初始化、同时拥有 RuntimeWand 的 Session 时返回 true。</returns>
        public bool TryBindCurrentRunSession()
        {
            // 已持有可用引用时直接复用，避免每次 Cast 都重新访问静态 Current。
            if (IsUsableRunSession(_runSpellSession))
                return true;

            // Current 是跨 Scene 的 Runtime 入口；它不是 Project Asset 中可序列化的对象引用。
            RunSpellSession current = RunSpellSession.Current;
            if (!IsUsableRunSession(current))
                return false;

            _runSpellSession = current;
            return true;
        }

        /// <summary>
        /// 集中定义 Session 可用条件，避免 Resolve、重绑和将来的调用方分别维护不一致的 null/初始化判断。
        /// </summary>
        private static bool IsUsableRunSession(RunSpellSession session)
        {
            return session != null &&
                   session.IsInitialized &&
                   session.RuntimeWand != null;
        }

        private bool TrySpendMana(
            float requiredMana,
            SpellManaPolicy manaPolicy)
        {
            if (manaPolicy == SpellManaPolicy.IgnoreMana)
                return true;
            if (requiredMana <= 0f)
                return true;
            if (_mana == null)
            {
                GameLog.Warn("SpellCaster 未配置 ManaComponent，本次施法按无限法力处理", "Skills");
                return true;
            }

            // CanSpend 是无副作用查询，Spend 才真正修改 Runtime State；二者都采用整笔支付语义。
            if (!_mana.CanSpend(requiredMana))
            {
                GameLog.Info($"法力不足：需要 {requiredMana:0.#}，当前 {_mana.CurrentMana:0.#}", "Skills");
                return false;
            }

            return _mana.Spend(requiredMana);
        }

        /// <summary>
        /// 把一条 ForwardProjectile 命令落地为真实 Prefab，并在启动 Rigidbody 生命周期前注入全部快照配置。
        /// index/count 用于同一层多个产出的散射、轨道相位等确定性排布。
        /// </summary>
        private void SpawnForwardProjectile(
            EmitCommand cmd,
            Vector3 spawnPos,
            Vector3 aimPoint,
            Vector3 baseDir,
            SpellAimMode aimMode,
            int index,
            int count,
            byte team,
            int attackerId,
            Collider casterCollider,
            int castId,
            int depth,
            SpellManaPolicy manaPolicy,
            OwnedWandCastContext ownedSource = default)
        {
            // 计算散射角和最终发射方向
            float yaw = SpellAiming.SpreadOffsetDegrees(index, count, cmd.SpreadDegrees);
            Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * baseDir;  // 将基准方向向量 baseDir 绕世界空间的 Y 轴（Vector3.up）旋转 yaw 度，得到一个新的方向向量 dir

            // 默认保持既有直线语义。只有显式 BallisticToPoint 且本条 Emit 打开重力时才改为反解初速度，
            // 所以 Fireball、玩家法术和不受重力的特殊 Projectile 都不会被 Arrow 的规则意外影响。
            Vector3 launchVelocity = dir * cmd.Speed;
            if (aimMode == SpellAimMode.BallisticToPoint && cmd.UseGravity)
            {
                float targetDistance = (aimPoint - spawnPos).magnitude;
                Vector3 spreadAimPoint = spawnPos + dir * targetDistance;
                if (!BallisticTrajectory.TrySolveVelocity(
                        spawnPos,
                        spreadAimPoint,
                        cmd.Speed,
                        Physics.gravity,
                        0.1f,
                        out Vector3 ballisticVelocity,
                        out _))
                {
                    GameLog.Warn("弹道投射物无法求得有效初速度，已降级为直线发射", "Skills");
                }
                else
                {
                    launchVelocity = ballisticVelocity;
                }
            }

            Vector3 launchForward = launchVelocity.sqrMagnitude > 1e-6f
                ? launchVelocity.normalized
                : dir;

            // Instantiate 根据 Prefab 在运行时创建一个独立 GameObject；LookRotation 让其 forward 朝向发射方向。
            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, Quaternion.LookRotation(launchForward));
            ownedSource.Track(go);
            ProjectileBase proj = go.GetComponent<ProjectileBase>();
            if (proj == null)
            {
                if (ownedSource.Enabled) throw new System.InvalidOperationException("生成的 Prefab 缺少 ProjectileBase。");
                GameLog.Warn($"法术预制体 {cmd.ProjectilePrefab.name} 上没有 ProjectileBase 组件", "Skills");
                Object.Destroy(go);
                return;
            }   // 安全检查

            // 把当前投射物和它未来要触发的 Payload 绑定起来。
            WirePayload(
                proj,
                cmd,
                team,
                attackerId,
                casterCollider,
                castId,
                depth,
                manaPolicy, ownedSource.Enabled ? ownedSource.Child(index) : default);

            ConfigureCompositeDamage(proj, in cmd);
            ConfigureProjectileMotion(proj, cmd, index, count);
            // ShieldProjectile 是仍沿 ProjectileBase 飞行链路工作的特殊产出，反射次数同样来自本次 EmitCommand 快照。
            if (proj is ShieldProjectile shieldProjectile)
                shieldProjectile.ConfigureShield(cmd.ShieldReflectCount);

            // Init 是投射物生命周期的统一启动点：保存伤害/阵营，忽略施法者碰撞，并写入 Rigidbody.linearVelocity。
            // BallisticToPoint 已把目标点与重力换算成 launchVelocity；Direct 仍是 dir * cmd.Speed。
            proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, launchVelocity, casterCollider, useGravity: cmd.UseGravity);
            ownedSource.LinkMaterial(index, proj);
        }

        /// <summary>
        /// 在给定世界坐标创建不依靠 Rigidbody 飞行的静态攻击对象。当前 StaticAtPoint Runtime 合同由 ProjectileShield 实现。
        /// </summary>
        private void SpawnStaticProjectileAtPoint(
            EmitCommand cmd,
            Vector3 spawnPos,
            Vector3 baseDir,
            byte team,
            int attackerId,
            Collider casterCollider, OwnedWandCastContext ownedSource = default)
        {
            // 先保留 Prefab 作者设置的基础旋转，再把它组合到瞄准方向旋转后，避免模型本地前轴补偿丢失。
            Quaternion rotation = cmd.ProjectilePrefab.transform.rotation;
            if (baseDir.sqrMagnitude > 1e-6f)
                rotation = Quaternion.LookRotation(baseDir.normalized) * rotation;

            // 静态对象依然通过 Instantiate 创建独立场景实例，但不会调用 ProjectileBase.Init。
            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, rotation);
            ownedSource.Track(go);
            ProjectileShield shield = go.GetComponent<ProjectileShield>();
            if (shield == null)
                // GetComponentInChildren 会继续检查子节点，兼容碰撞/表现组件不在 Prefab 根节点的静态对象。
                shield = go.GetComponentInChildren<ProjectileShield>(true);
            if (shield != null)
            {
                // 静态护盾自己持有阵营、归属、剩余反射次数与 Collider 忽略规则，因此走专用 Init。
                shield.Init(team, attackerId, cmd.ShieldReflectCount, casterCollider);
                return;
            }

            // 只报告 Authoring 错误；这里不猜测未知 StaticAtPoint Prefab 应该由哪个组件接管。
            if (ownedSource.Enabled) throw new System.InvalidOperationException("生成的静态对象缺少 ProjectileShield。");
            GameLog.Warn($"StaticAtPoint prefab {cmd.ProjectilePrefab.name} has no ProjectileShield component", "Skills");
        }

        /// <summary>
        /// 执行 SkyfallAtPoint 的“两阶段生成”：先解析真实落点并显示预警，再按配置延迟从上空生成投射物。
        /// landingPoint 是目标位置而不是实际出生位置，真正 spawnPos 在 SpawnSkyfallProjectileNow 中计算。
        /// </summary>
        private void SpawnSkyfallProjectile(
            EmitCommand cmd,
            Vector3 landingPoint,
            Vector3 baseDir,
            int index,
            int count,
            byte team,
            int attackerId,
            Collider casterCollider,
            int castId,
            int depth,
            SpellManaPolicy manaPolicy,
            OwnedWandCastContext ownedSource = default)
        {
            // 屏幕瞄准点可能位于空中，先向下做 Physics 查询，把它吸附到最近的真实碰撞表面。
            landingPoint = ResolveSkyfallLandingPoint(landingPoint, casterCollider);
            ownedSource.DelayMaterial(index, landingPoint + Vector3.up * .25f, cmd.LandingSiteDuration);

            if (cmd.LandingSitePrefab != null)
            {
                // 预警 Prefab 使用作者保存的旋转，不强行朝向施法方向，避免地面贴花被竖起来。
                Quaternion markerRotation = cmd.LandingSitePrefab.transform.rotation;
                GameObject marker = Object.Instantiate(cmd.LandingSitePrefab, landingPoint, markerRotation);
                ownedSource.Track(marker);
                // Destroy(Object, delay) 延迟销毁落点提示；额外 0.5 秒给结束动画留出时间。
                Object.Destroy(marker, Mathf.Max(0.05f, cmd.LandingSiteDuration + 0.5f));
            }

            if (cmd.LandingSiteDuration > 0f)
            {
                // Coroutine 可跨多帧暂停并恢复该流程，不会阻塞主线程；StartCoroutine 负责交给 Unity 调度。
                ownedSource.Track(StartCoroutine(SpawnSkyfallProjectileAfterDelay(
                    cmd, landingPoint, baseDir, index, count, team, attackerId,
                    casterCollider, castId, depth, manaPolicy, ownedSource)));
            }
            else
            {
                SpawnSkyfallProjectileNow(
                    cmd, landingPoint, baseDir, index, count, team, attackerId,
                    casterCollider, castId, depth, manaPolicy, ownedSource);
            }
        }

        //————————————————————————————————————————————————————————————————————————————————————————————

        /// <summary>
        /// 把 EmitCommand 中的 Payload 数据与某一个 ProjectileBase 实例的生命周期事件连接起来。
        /// 这里使用实例 C# event，而不是全局 EventBus：回调只属于这枚投射物，不需要额外用 Projectile Id 做匹配。
        /// </summary>
        private void WirePayload(
            ProjectileBase proj,
            EmitCommand cmd,
            byte team,
            int attackerId,
            Collider casterCollider,
            int castId,
            int depth,
            SpellManaPolicy manaPolicy,
            OwnedWandCastContext ownedSource = default)
        {
            // 如果这枚投射物根本没有携带后续法术，那么什么都不用绑定
            if (!cmd.HasPayload)
                return;
            // Combat 只通知生命周期；Character 再通过 Core 命令释放未触发子树。
            if (ownedSource.Enabled) proj.Finished += ownedSource.Abandon;

            // 把当前命令捕获的“后续一个完整 Action”与 Modifier 快照保存到闭包中。
            // 投射物未来命中或计时结束时，回调仍能用本次施法的上下文运行下一层，而不再读取已变化的序列。
            // 闭包会随 delegate 一起被投射物事件持有；该分配发生在离散施法，不位于每帧 Hot Path。
            IReadOnlyList<SpellDefinition> payload = cmd.Payload;   // payload 表示这枚投射物未来触发的时候，需要执行的那一小段法术程序
            CastModifierState payloadMods = cmd.PayloadMods;        //

            switch (cmd.PayloadTrigger)
            {
                case PayloadTriggerMode.OnImpact:
                    // += 向 C# event 订阅回调；ProjectileBase 在完成碰撞处理后 Invoke，命中点成为下一层发射点。
                    // Payload 表示一个 Action，因此新层的基础 Draw Budget 重置为 1；depth + 1 记录递归层级。
                    proj.Impacted += (hitPoint, hitDir) =>
                        RunCast(
                            payload,
                            1,
                            payloadMods,
                            hitPoint,
                            hitPoint + hitDir,
                            hitDir,
                            SpellAimMode.Direct,
                            team,
                            attackerId,
                            casterCollider,
                            castId,
                            depth + 1,
                            manaPolicy, ownedSource);
                    break;

                case PayloadTriggerMode.AfterDelay:
                    // 定时模式订阅另一个实例事件，再由 ArmTimedTrigger 开始投射物内部倒计时。
                    // position/direction 取触发瞬间的投射物状态，不重新读取角色或 Camera，因此转镜头不会改变后续方向。
                    proj.TimedTriggerElapsed += (position, direction) =>
                        RunCast(
                            payload,
                            1,
                            payloadMods,
                            position,
                            position + direction,
                            direction,
                            SpellAimMode.Direct,
                            team,
                            attackerId,
                            casterCollider,
                            castId,
                            depth + 1,
                            manaPolicy, ownedSource);
                    proj.ArmTimedTrigger(cmd.PayloadDelaySeconds);
                    break;
            }
        }

        /// <summary>
        /// 把命令中的运动快照写入投射物。Bounce 可与主运动并存；Homing 与 Orbit 都会接管主要方向，因此互斥。
        /// 每个分支都会显式关闭另一模式，避免 Prefab 上的序列化默认值污染本次命令语义。
        /// </summary>
        private void ConfigureProjectileMotion(ProjectileBase proj, EmitCommand cmd, int index, int count)
        {
            // 所有运动参数都是 CastEvaluator 已经烘焙好的快照；投射物不再回查 SpellDefinition。
            proj.ConfigureBounce(cmd.BounceCount);

            switch (cmd.MotionMode)
            {
                case ProjectileMotionMode.Homing:
                    proj.ConfigureHoming(cmd.HomingRadius, cmd.HomingDuration, cmd.HomingTurnRateDegrees);
                    proj.ConfigureOrbit(0f, 0f, 0f, 0f);
                    break;

                case ProjectileMotionMode.Orbit:
                    proj.ConfigureHoming(0f, 0f, 0f);
                    // 同层多个投射物均匀错开轨道初相位，并让轨道平面倾角正负交替，避免完全重叠。
                    float orbitPhase = cmd.OrbitPhaseOffsetDegrees + SpellAiming.PhaseOffsetDegrees(index, count);
                    float orbitPlaneTilt = SpellAiming.PlaneTiltDegrees(index, count, cmd.OrbitPlaneTiltDegrees);
                    proj.ConfigureOrbit(cmd.OrbitRadius, cmd.OrbitAngularSpeedDegrees, orbitPhase, orbitPlaneTilt);
                    break;

                default:
                    proj.ConfigureHoming(0f, 0f, 0f);
                    proj.ConfigureOrbit(0f, 0f, 0f, 0f);
                    break;
            }
        }


        /// <summary>
        /// 把解释器生成的复合伤害纯数据交给具体 Combat Runtime。
        /// SpellCaster 只做 Assembly 边界上的 Adapter，不在这里查询范围或直接扣血。
        /// in 以只读引用传递较大的 EmitCommand struct，避免复制并禁止方法修改调用方快照。
        /// </summary>
        private static void ConfigureCompositeDamage(
            ProjectileBase projectile,
            in EmitCommand command)
        {
            if (projectile is NovaFireball meteor)
            {
                meteor.ConfigureImpactDamage(
                    command.ExplosionDamage,
                    command.FireFieldDamagePerTick,
                    command.FireFieldTickInterval,
                    command.FireFieldDuration);
            }
        }

        /// <summary>
        /// Skyfall 预警阶段的 Coroutine。IEnumerator 由 Unity 保存并跨帧恢复；这里只在一次离散施法时创建，
        /// 不属于 Update/FixedUpdate 的逐帧分配 Hot Path。
        /// </summary>
        private IEnumerator SpawnSkyfallProjectileAfterDelay(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir,
                                                             int index, int count,
                                                             byte team, int attackerId, Collider casterCollider,
                                                             int castId, int depth,
                                                             SpellManaPolicy manaPolicy,
                                                             OwnedWandCastContext ownedSource = default)
        {
            // WaitForSeconds 使用受 Time.timeScale 影响的游戏时间；yield 时当前方法暂停，但其他 Update/渲染继续运行。
            yield return new WaitForSeconds(cmd.LandingSiteDuration);
            SpawnSkyfallProjectileNow(
                cmd, landingPoint, baseDir, index, count, team, attackerId,
                casterCollider, castId, depth, manaPolicy, ownedSource);
        }

        /// <summary>
        /// 预警结束后计算天空出生点与实际下落方向，并复用普通投射物相同的 Payload、运动、伤害和 Init 链路。
        /// </summary>
        private void SpawnSkyfallProjectileNow(EmitCommand cmd, Vector3 landingPoint, Vector3 baseDir, int index, int count,
                                               byte team, int attackerId, Collider casterCollider, int castId, int depth,
                                               SpellManaPolicy manaPolicy,
                                               OwnedWandCastContext ownedSource = default)
        {
            // 去掉 Y 分量得到地面平面上的瞄准方向，用它把天空生成点向后偏移。
            Vector3 horizontalDir = baseDir;
            horizontalDir.y = 0f;
            if (horizontalDir.sqrMagnitude < 1e-6f)
                horizontalDir = transform.forward;
            horizontalDir.Normalize();

            float height = Mathf.Max(0f, cmd.SkyfallHeight);
            Vector3 spawnPos = landingPoint + Vector3.up * height - horizontalDir * cmd.SkyfallBackOffset;
            // 从生成点指向落点，得到真正的下落方向，而不是简单使用 Vector3.down。
            Vector3 fallDir = landingPoint - spawnPos;
            if (fallDir.sqrMagnitude < 1e-6f)
                fallDir = Vector3.down;
            fallDir.Normalize();

            // LookRotation 让 Prefab 的 forward 指向“出生点 → 落点”，后续 Rigidbody 初速度沿同一方向。
            GameObject go = Object.Instantiate(cmd.ProjectilePrefab, spawnPos, Quaternion.LookRotation(fallDir));
            ownedSource.Track(go);
            ProjectileBase proj = go.GetComponent<ProjectileBase>();
            if (proj == null)
            {
                if (ownedSource.Enabled) throw new System.InvalidOperationException("生成的 Skyfall Prefab 缺少 ProjectileBase。");
                GameLog.Warn($"静态投射物预制体 {cmd.ProjectilePrefab.name} 上没有 ProjectileBase 组件", "Skills");
                Object.Destroy(go);
                return;
            }

            WirePayload(
                proj,
                cmd,
                team,
                attackerId,
                casterCollider,
                castId,
                depth,
                manaPolicy, ownedSource.Enabled ? ownedSource.Child(index) : default);
            ConfigureCompositeDamage(proj, in cmd);
            ConfigureProjectileMotion(proj, cmd, index, count);
            proj.Init(team, attackerId, cmd.Damage, cmd.DamageType, fallDir * cmd.Speed, casterCollider, useGravity: cmd.UseGravity);
            ownedSource.LinkMaterial(index, proj);
        }

        /// <summary>
        /// 把屏幕射线得到的候选世界点向下投影到最近实体表面。
        /// 查询失败时保留 sourcePoint，使调用方仍有确定的降级位置，而不是返回 Vector3.zero。
        /// </summary>
        private Vector3 ResolveSkyfallLandingPoint(Vector3 sourcePoint, Collider casterCollider)
        {
            Vector3 origin = sourcePoint + Vector3.up * 2f;
            // RaycastNonAlloc 沿 Vector3.down 最多检测 50 米，把命中写入容量为 8 的复用数组；~0 表示所有 Layer。
            // 若同一条射线超过 8 个命中，只能检查数组实际接收到的结果；这是用固定内存换取可控 GC 的取舍。
            // QueryTriggerInteraction.Ignore 排除 Trigger，避免把仅用于范围检测的体积误当作地面。
            int hitCount = Physics.RaycastNonAlloc(origin, Vector3.down, _landingHits, 50f, ~0, QueryTriggerInteraction.Ignore);
            if (hitCount <= 0)
                return sourcePoint;

            float bestDistance = float.MaxValue;
            Vector3 bestPoint = sourcePoint;
            bool found = false;

            // NonAlloc 版本不承诺按距离排序，因此手动选择最近且不是施法者自己的命中点。
            for (int i = 0; i < hitCount; i++)
            {
                Collider hitCollider = _landingHits[i].collider;
                if (hitCollider == null || hitCollider == casterCollider)
                    continue;

                if (_landingHits[i].distance >= bestDistance)
                    continue;

                bestDistance = _landingHits[i].distance;
                bestPoint = _landingHits[i].point;
                found = true;
            }

            return found ? bestPoint : sourcePoint;
        }

        # endregion
    }
}
