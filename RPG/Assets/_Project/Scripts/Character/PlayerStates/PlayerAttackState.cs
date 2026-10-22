using UnityEngine;
using Game.Combat;
using Game.Core;

namespace Game.Character
{
    /// <summary>
    /// 攻击状态 —— 连段驱动。同一个实例承载整套连段：
    /// 1. Enter 起手段 0：CrossFade 到该段动画 + 把该段 AttackDefinition 推给命中判定器
    /// 2. Update 每帧：按段 ActiveStart/End 开/关命中窗口（及剑刃拖尾窗口）；用 ComboResolver 判定维持/推进/结束
    ///    - Advance：comboIndex++，消耗缓冲输入，CrossFade 下一段并换段
    ///    - End：动画播完未接 → 切回移动状态
    /// 3. Exit：comboIndex 归零 + 清攻击缓冲（防泄漏）+ 关命中窗口 + 关拖尾
    ///    —— 任何离开攻击态的路径（自然结束/未来受击打断）都经 Exit，是连段归零的唯一责任点。
    /// </summary>
    public class PlayerAttackState : PlayerStateBase
    {
        private const float EndThreshold = 0.85f; // 动画进度达到此值且本段未推进 → 结束连段
        private const float CrossFadeDuration = 0.1f; // 段切换 CrossFade 固定时长（秒）
        private int _comboIndex; // 当前打到第几段（0 起）。仅在本状态内维护，唯一归零点是 Exit()。

        // [新增] 仅供 HandleTrailWindow() 在"过渡期"使用：Advance 切段那一刻，SetAttack 已经把
        // MeleeHitDetector.Attack 同帧指向了新段，但 CrossFade 过渡这几帧 normalizedTime 读到的还是
        // 旧段的进度（见 HandleAttackWindow 同款注释）。过渡期要拿"旧段的窗口"去比"旧段的进度"，
        // 让旧段的拖尾能按它自己的 TrailActiveEnd 正常收尾，而不是被新段的 CrossFade 直接腰斩。
        // Enter() 起手第一段时显式置 null：那一刻的"旧状态"是 Locomotion 混合树，不是某个攻击段，
        // 用它的 normalizedTime 去比任何 AttackDefinition 的窗口都没有意义，必须强制当作"没有旧段"处理。
        private AttackDefinition _prevTrailDef;

        // _player（基类引用）只能拿到共享成员；Warrior 专属的 Combo / MeleeHitDetector / 连段 hash / 刀光
        // 需要具体子类引用，这里另存一份 typed 的 _warrior（与 _player 指向同一对象，只是类型更具体）。
        private readonly WarriorController _warrior;

        public PlayerAttackState(WarriorController player) : base(player)
        {
            _warrior = player;
        }

        #region 状态机函数

        public override void Enter()
        {
            _comboIndex = 0;
            _player.AttackBufferCounter = 0f; // 消耗起手输入，防重复触发
            _prevTrailDef = null; // [新增] 见字段声明处注释：起手第一段没有"旧攻击段"可言，强制清空

            // _combo 未配置或无段落时安全退出，避免 StartSegment→Combo.Segments[0] 直接 NRE
            if (_warrior.Combo == null || _warrior.Combo.SegmentCount == 0)
            {
                GameLog.Warn("ComboDefinition 未配置或无段落，无法攻击", "Combat");
                TransitionToMovement();
                return;
            }

            // [改动] 不再在这里把 emitting 直接拉到 true。
            // "进入攻击状态"≠"剑已经在挥动"——起手蓄力的那几帧动画进度还没到 HitActiveStart，
            // emitting 现在交给每帧的 HandleTrailWindow() 按动画进度判定开关（同 HandleAttackWindow 的窗口模式）。
            // Clear() 仍然留在这里：新一次攻击起手时清掉上次残留的尾点，防止上次尾点和这次新点连成一条突兀的直线
            // ——这条防御跟 emitting 怎么开没关系，单独保留，用 ?. 是因为 Clear() 是方法调用而非属性赋值，可以这样写。
            _warrior.BladeTrail?.Clear();

            StartSegment(0);
        }

        public override void Update()
        {
            // 攻击时施加向下压力，保持与地面接触（防止飘起来）
            HandleGravity();
            // 攻击时锁定水平移动，只保留垂直速度（重力）
            HandleMovement();
            // 每帧按当前段进度开/关命中窗口
            HandleAttackWindow();
            // 每帧按当前段进度开/关剑刃拖尾——用独立的 TrailActiveStart/TrailActiveEnd（路 2），
            // 调用顺序放在 HandleAttackWindow 之后、CheckCombo 之前，纯粹是阅读顺序，两者本帧内互不依赖。
            HandleTrailWindow();
            // 连段判定：维持/推进/结束
            CheckCombo();
        }

        public override void Exit()
        {
            _comboIndex = 0; // 归零（唯一责任点）
            _player.AttackBufferCounter = 0f; // 清残留，防连段结束后误触发新普攻
            _warrior.MeleeHitDetector?.CloseHitWindow(); // 关窗，防残留

            // [注释更新，逻辑不变] 关闭剑刃拖尾，刻意不 Clear——已生成的尾点按 Trail Renderer 的 Time 自然淡出。
            // HandleTrailWindow() 平时就会在窗口外把它关掉；这里是 Exit 作为"唯一退出口"的最后一道保险——
            // 不管 Update 那一帧算到什么结果，离开攻击态这一刻必须强制关闭，不依赖 Update 是否还会再跑一次。
            if (_warrior.BladeTrail != null)
                _warrior.BladeTrail.emitting = false;

            _prevTrailDef = null; // [新增] 兜底清空，避免残留引用悬挂到下一次完全不相关的攻击序列
        }

        #endregion


        #region 处理流程函数

        /// <summary>切到第 index 段：换命中数据 + 关窗（让新段在 ActiveStart 重新开窗清去重）+ CrossFade 动画。</summary>
        private void StartSegment(int index)
        {
            // 从玩家（_player）的连击配置（Combo）中，根据索引取出对应的攻击定义（AttackDefinition）。
            AttackDefinition seg = _warrior.Combo.Segments[index];

            if (_warrior.MeleeHitDetector != null)
            {
                _warrior.MeleeHitDetector.SetAttack(seg); // 武器现在"知道"自己这一下打多少伤害、命中盒多大
                _warrior.MeleeHitDetector.CloseHitWindow(); // 先确保命中窗口是关着的（防止上一次攻击的窗口状态残留）
            }

            int hash = _warrior.GetComboStateHash(index); // 获取该连击段落对应的动画状态机的哈希值
            // CrossFadeInFixedTime 保证过渡时间严格固定为你指定的秒数，不受动画播放速度影响。
            // 在连击系统中，这非常重要，因为它确保了无论当前攻击动画的播放速度如何，连招的衔接手感（过渡时间）始终稳定一致
            _player.Animator.CrossFadeInFixedTime(hash, CrossFadeDuration, 0);
        }

        private void HandleGravity()
        {
            if (_player.VerticalVelocity < 0f)
                _player.VerticalVelocity = -2f;
        }

        private void HandleMovement()
        {
            // 锁定水平移动；CC 每帧必须被 Move，否则 isGrounded 检测失效
            Vector3 velocity = Vector3.up * _player.VerticalVelocity;
            _player.MoveCharacter(velocity * Time.deltaTime);
        }

        /// <summary>
        /// 处理攻击窗口的开启和关闭，根据动画的状态和时间来控制攻击判定窗口
        /// </summary>
        private void HandleAttackWindow()
        {
            if (_warrior.MeleeHitDetector == null) return;
            AttackDefinition def = _warrior.MeleeHitDetector.Attack;
            if (def == null) return;

            // 过渡期：normalizedTime 读到的是源状态的值，不代表当前段进度 → 强制关窗
            // 检查动画是否处于过渡状态，如果是则关闭攻击窗口
            if (_player.Animator.IsInTransition(0))
            {
                _warrior.MeleeHitDetector.CloseHitWindow();
                return;
            }

            // 获取当前动画状态的标准化时间（0-1之间）
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;

            // 检查当前时间是否在攻击判定窗口的激活范围内
            if (t >= def.HitActiveStart && t <= def.HitActiveEnd)
                _warrior.MeleeHitDetector.OpenHitWindow();
            else
                _warrior.MeleeHitDetector.CloseHitWindow();
        }

        /// <summary>
        /// [改动：路 2 + 过渡期双轨修复] 每帧按动画进度开/关剑刃拖尾，而不是在 Enter/Exit 做一次性二元开关——
        /// 否则攻击状态刚进入的那几帧（动画还在起手蓄力，剑还没真正挥动）也会被记录成拖尾点，
        /// 出现"刀还没动、拖尾已经在了"的问题。
        ///
        /// 窗口边界用 AttackDefinition.TrailActiveStart/TrailActiveEnd——一对独立于命中判定的字段
        /// （不借用 HitActiveStart/HitActiveEnd）。
        ///
        /// 过渡期（CrossFade 切段那几帧）是"源状态(旧段)和目标状态(新段)同时在播、互相混权重"的一段时间，
        /// 不是旧段消失/新段从零开始这么简单——所以过渡期要同时查两条轨：
        ///   - GetCurrentAnimatorStateInfo：源状态(旧段)的进度，配 _prevTrailDef 的窗口，让旧段能收尾
        ///   - GetNextAnimatorStateInfo：目标状态(新段)的进度，配当前 Attack(已经是新段)的窗口，让新段能提前起手
        /// 只查旧段（上一版的修法）会在"旧段收尾"和"过渡期彻底结束才开始查新段"之间空出一段死区，
        /// 表现为两段拖尾中间有一道明显的断缝——这正是这次要修的问题。
        ///
        /// 注意：这个方法因此已经不再和 HandleAttackWindow() 完全同构——HandleAttackWindow() 在过渡期
        /// 仍然无条件关窗，命中窗口理论上有同样的风险，只是不可见、还没被观察到实际漏判。
        /// </summary>
        private void HandleTrailWindow()
        {
            TrailRenderer trail = _warrior.BladeTrail;
            if (trail == null) return;

            if (_player.Animator.IsInTransition(0))
            {
                // 旧段：用 _prevTrailDef 的窗口去比源状态的进度，让旧段能正常收尾（上一版已修）
                float oldT = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
                bool oldStillActive = _prevTrailDef != null && InTrailWindow(oldT, _prevTrailDef);

                // [新增] 新段：Attack 在 Advance 那一刻就已经被 SetAttack 切过来了，过渡期里直接拿来用即可；
                // 用 GetNextAnimatorStateInfo 查目标状态自己的进度——新段在过渡期内也在悄悄往前播，
                // 哪怕这几帧 IsInTransition 还是 true，新段自己的 TrailActiveStart 也可能已经被越过了。
                AttackDefinition newDef = _warrior.MeleeHitDetector?.Attack;
                float newT = _player.Animator.GetNextAnimatorStateInfo(0).normalizedTime % 1f;
                bool newAlreadyActive = newDef != null && InTrailWindow(newT, newDef);

                // 任一边满足就该亮：消除"旧段收尾"和"新段起手"之间原本空出来的那段死区
                trail.emitting = oldStillActive || newAlreadyActive;
                return;
            }

            if (_warrior.MeleeHitDetector == null) return;
            AttackDefinition def = _warrior.MeleeHitDetector.Attack;
            if (def == null) return;

            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            trail.emitting = InTrailWindow(t, def);
        }

        /// <summary>[新增] 抽出来的小工具：t 是否落在某段的拖尾窗口内，避免过渡期/非过渡期两处各写一遍同样的比较。</summary>
        private static bool InTrailWindow(float t, AttackDefinition def) =>
            t >= def.TrailActiveStart && t <= def.TrailActiveEnd;

        /// <summary>
        /// 检查连段状态，根据当前动画状态和输入决定是否进入下一连段或结束连段
        /// </summary>
        private void CheckCombo()
        {
            // 过渡期不做连段判定：normalizedTime 还是上一段的值（也防同帧切段后立刻又判一次）
            if (_player.Animator.IsInTransition(0)) return;

            AttackDefinition seg = _warrior.Combo.Segments[_comboIndex];
            // Inspector 里 Segments 该格未赋值时 seg 为 null，安全退出而不是每帧 NRE
            if (seg == null)
            {
                GameLog.Warn($"连段第 {_comboIndex} 段 AttackDefinition 未赋值，连段中断", "Combat");
                TransitionToMovement();
                return;
            }

            // 获取当前动画状态的标准化时间（0-1之间）
            float t = _player.Animator.GetCurrentAnimatorStateInfo(0).normalizedTime % 1f;
            // 检查是否有缓冲输入
            bool hasBuffer = _player.AttackBufferCounter > 0f;

            // 使用连段解析器决定连段状态
            ComboDecision decision = ComboResolver.Resolve(
                _comboIndex,
                _warrior.Combo.SegmentCount,
                t,
                hasBuffer,
                seg.ComboInputStart,
                seg.ComboInputEnd,
                EndThreshold
            );

            // 根据连段决策执行相应操作
            switch (decision)
            {
                case ComboDecision.Advance:
                    // 进入下一连段，并消耗缓冲输入
                    _comboIndex++;
                    _player.AttackBufferCounter = 0f; // 消耗输入：防同帧重复推进 + 防泄漏
                    // [新增] 必须在 StartSegment（内部会 SetAttack 覆盖 Attack）之前捕获"即将被替换的旧段"，
                    // 供过渡期的 HandleTrailWindow() 用旧段窗口给旧段拖尾收尾——这是这次要修的 bug 的关键一行。
                    _prevTrailDef = _warrior.MeleeHitDetector != null ? _warrior.MeleeHitDetector.Attack : null;
                    StartSegment(_comboIndex);
                    break;
                case ComboDecision.End:
                    // 结束连段，转为移动状态
                    TransitionToMovement();
                    break;
                // ComboDecision.Continue: 维持当前段，无操作
            }
        }

        #endregion


        #region 功能函数

        /// <summary>
        /// 切换到移动状态的私有方法
        /// 根据玩家是否在地面上来决定切换到地面状态还是空中状态
        /// </summary>
        private void TransitionToMovement()
        {
            // 检查玩家是否接触地面
            if (_player.GroundChecker.IsGrounded)
                // 如果玩家在地面，则切换到地面状态
                _player.StateMachine.ChangeState(_player.GroundedState);
            else
                // 如果玩家不在地面，则切换到空中状态
                _player.StateMachine.ChangeState(_player.AirborneState);
        }

        #endregion
    }
}
