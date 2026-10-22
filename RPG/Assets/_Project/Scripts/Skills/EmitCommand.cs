using System.Collections.Generic;
using UnityEngine;
using Game.Combat;

namespace Game.Skills
{
    /// <summary>
    /// CastEvaluator 输出的一条只读攻击命令，是 Game.Skills 与 Unity Runtime 之间的数据边界。
    /// 它不含执行方法：SpellCaster 只消费这些已经烘焙好的最终值，不再重读或修改原始法术资产。
    /// readonly struct 保证字段在构造后不可改；其中 Prefab、AudioClip、Payload 仍是只读引用，不代表深拷贝资源。
    /// </summary>
    public readonly struct EmitCommand
    {
        // EmitCommand 具体是怎样设计的
        // ── 第一组：应该生成什么数据：Runtime 应创建哪个 Prefab、采用哪种空间生成策略 ──
        public readonly GameObject ProjectilePrefab;        // 投射物 Prefab 引用
        public readonly float LifetimeSeconds, LifetimeAddSeconds, LifetimeMul;
        public readonly SpellSpawnMode SpawnMode;           // 普通向前发射、定点生成还是天空坠落
        public readonly GameObject LandingSitePrefab;       // 落地点提示特效
        public readonly float SkyfallHeight;                // 天空生成高度
        public readonly float SkyfallBackOffset;            // 生成点后退距离
        public readonly float LandingSiteDuration;          // 落地点提示特效持续时间
        public readonly int ShieldReflectCount;             // 护盾反射次数

        // ── 第二组：这次攻击的最终战斗结果快照数据：由基础数据和 CastModifierState 在 BakeEmit 时合并 ──
        public readonly float Damage;                       // 最终直接伤害
        public readonly float ExplosionDamage;              // 最终爆炸伤害
        public readonly float FireFieldDamagePerTick;       // 最终持续区域伤害
        public readonly float FireFieldTickInterval;        // 持续区域伤害的间隔
        public readonly float FireFieldDuration;            // 持续区域伤害的持续时间
        public readonly float Speed;                        // 最终速度
        public readonly DamageType DamageType;              // 伤害类型

        // ── 第三组：这次投射物的运动快照：SpellCaster 注入 ProjectileBase/Rigidbody ──
        public readonly float SpreadDegrees;                // 散射角度
        public readonly int BounceCount;                    // 剩余弹射次数
        public readonly bool UseGravity;                    // 是否启用重力
        public readonly float HomingRadius;                 // 追踪半径
        public readonly float HomingDuration;               // 追踪持续时间
        public readonly float HomingTurnRateDegrees;        // 追踪转向角速度
        public readonly float OrbitRadius;                  // 轨道半径
        public readonly float OrbitAngularSpeedDegrees;     // 轨道角速度
        public readonly float OrbitPhaseOffsetDegrees;      // 轨道相位偏移
        public readonly float OrbitPlaneTiltDegrees;        // 轨道平面倾斜角度
        public readonly ProjectileMotionMode MotionMode;    // 当前最终采用哪一种主运动模式

        // ── 第四组：表现与后续触发上下文：Payload 是触发时才重新求值的有序子序列 ──
        public readonly AudioClip CastSfx;                          // 施法音效引用
        public readonly IReadOnlyList<SpellDefinition> Payload;     // 触发后要继续解释的法术子序列
        public readonly CastModifierState PayloadMods;              // 子序列需要继承的修正状态快照
        public readonly PayloadTriggerMode PayloadTrigger;          // 命中触发还是定时触发
        public readonly float PayloadDelaySeconds;                  // 定时触发的延迟时间

        /// <summary>同时检查引用与元素数量，空 Payload 会退化为普通 Emit，不建立无意义的事件订阅。</summary>
        public bool HasPayload => Payload != null && Payload.Count > 0;

        /// <summary>由 CastEvaluator.BakeEmit 唯一构造；把一次求值结果完整封装后交给 Runtime。</summary>
        public EmitCommand(GameObject projectilePrefab, SpellSpawnMode spawnMode, GameObject landingSitePrefab,
                           float skyfallHeight, float skyfallBackOffset, float landingSiteDuration,
                           int shieldReflectCount,
                           float damage, float explosionDamage,
                           float fireFieldDamagePerTick, float fireFieldTickInterval, float fireFieldDuration,
                           float speed, DamageType damageType,
                           float spreadDegrees, int bounceCount, bool useGravity,
                           float homingRadius, float homingDuration, float homingTurnRateDegrees,
                           float orbitRadius, float orbitAngularSpeedDegrees, float orbitPhaseOffsetDegrees,
                           float orbitPlaneTiltDegrees,
                           ProjectileMotionMode motionMode,
                           AudioClip castSfx,
                           IReadOnlyList<SpellDefinition> payload, CastModifierState payloadMods,
                           PayloadTriggerMode payloadTrigger, float payloadDelaySeconds, float lifetimeSeconds=-1, float lifetimeAddSeconds=0, float lifetimeMul=1)
        {
            ProjectilePrefab = projectilePrefab;
            LifetimeSeconds=lifetimeSeconds;LifetimeAddSeconds=lifetimeAddSeconds;
            LifetimeMul=lifetimeMul;
            SpawnMode = spawnMode;
            LandingSitePrefab = landingSitePrefab;
            SkyfallHeight = skyfallHeight;
            SkyfallBackOffset = skyfallBackOffset;
            LandingSiteDuration = landingSiteDuration;
            ShieldReflectCount = shieldReflectCount;
            Damage = damage;
            ExplosionDamage = explosionDamage;
            FireFieldDamagePerTick = fireFieldDamagePerTick;
            FireFieldTickInterval = fireFieldTickInterval;
            FireFieldDuration = fireFieldDuration;
            Speed = speed;
            DamageType = damageType;
            SpreadDegrees = spreadDegrees;
            BounceCount = bounceCount;
            UseGravity = useGravity;
            HomingRadius = homingRadius;
            HomingDuration = homingDuration;
            HomingTurnRateDegrees = homingTurnRateDegrees;
            OrbitRadius = orbitRadius;
            OrbitAngularSpeedDegrees = orbitAngularSpeedDegrees;
            OrbitPhaseOffsetDegrees = orbitPhaseOffsetDegrees;
            OrbitPlaneTiltDegrees = orbitPlaneTiltDegrees;
            MotionMode = motionMode;
            CastSfx = castSfx;
            Payload = payload;
            PayloadMods = payloadMods;
            PayloadTrigger = payloadTrigger;
            PayloadDelaySeconds = payloadDelaySeconds;
        }
    }
}
