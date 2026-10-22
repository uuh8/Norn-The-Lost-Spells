using NUnit.Framework;

namespace Game.Combat.Tests
{
    /// <summary>
    /// 验证 Task 2 连续反应状态机的时间轴、优先级、归因和生命周期。
    /// 这些是 EditMode 纯 C# 测试，不依赖场景与 MonoBehaviour，因此能精确控制 deltaTime。
    /// </summary>
    public sealed class ElementReactionRuntimeTests
    {
        // 用不同 Id 明确区分“最后一次触发反应的来源”和“原有 Fire 的来源”，验证伤害归因规则。
        private static readonly StatusSource TriggerSource = new StatusSource(99, 2);
        private static readonly StatusSource FireSource = new StatusSource(10, 1);

        [TestCase(1f / 30f)]
        [TestCase(1f / 120f)]
        public void Extinguish_OneWaterRemovesTwoFireAcrossFrameRates(float step)
        {
            // 同一段 0.6 秒模拟分别用 30 FPS 与 120 FPS 步长执行，
            // 证明结果取决于 rate * totalTime，而不是 Tick 调用次数。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 30f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, step); // 启动帧只发 Started，下一帧才推进。
            float elapsed = 0f;
            while (elapsed < 0.6f - 1e-5f)
            {
                float delta = Min(step, 0.6f - elapsed);
                state = Step(runtime, state, delta);
                elapsed += delta;
            }

            Assert.AreEqual(20f, state.Fire, 1e-3f);
            Assert.AreEqual(0f, state.Water, 1e-3f);
        }

        [Test]
        public void Extinguish_FormalRateRemainsLatchedAfterWaterFallsBelowThreshold()
        {
            // Started 帧建立正式反应；0.4 秒后 Water 恰好从 30 降到阈值 10。
            // 后续 0.2 秒仍应使用 50/s 的 Formal Rate 将其清零，而不是退回 10/s 的 Low Rate。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 30f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, 0.01f);
            state = Step(runtime, state, 0.4f);
            Assert.AreEqual(40f, state.Fire, 1e-4f);
            Assert.AreEqual(10f, state.Water, 1e-4f);

            state = Step(runtime, state, 0.2f);
            Assert.AreEqual(20f, state.Fire, 1e-4f);
            Assert.AreEqual(0f, state.Water, 1e-4f);
        }

        [Test]
        public void Extinguish_BelowFormalThresholdUsesLowRate()
        {
            // 双方低于 FormalThreshold 时仍会中和，但不会走正式高速反应与 Started VFX 信号。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 5f, water: 5f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, 0.01f);
            state = Step(runtime, state, 0.5f);

            Assert.AreEqual(0f, state.Fire, 1e-4f);
            Assert.AreEqual(2.5f, state.Water, 1e-4f);
        }

        [Test]
        public void ToxicCombustion_ResolvesOnlyAfterWindUp()
        {
            // Arrange：Fire/Poison 均达到门槛；Act：分两个 0.1 秒步推进 0.2 秒 Wind-up；
            // Assert：中途无伤害，结束时才输出按实际 Poison 消耗计算的命令。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 30f, poison: 40f);
            runtime.MarkDirty(TriggerSource);

            ElementReactionFrame started = runtime.Tick(in state, 0.01f);
            state = Apply(state, started);
            Assert.AreEqual(ElementReactionPhase.Started, started.Signal0.Phase);
            Assert.IsFalse(started.HasAreaDamage);

            ElementReactionFrame half = runtime.Tick(in state, 0.1f);
            state = Apply(state, half);
            Assert.IsFalse(half.HasAreaDamage);
            Assert.AreEqual(20f, state.Poison, 1e-4f);

            ElementReactionFrame resolved = runtime.Tick(in state, 0.1f);
            state = Apply(state, resolved);
            Assert.IsTrue(resolved.HasAreaDamage);
            Assert.AreEqual(ElementReactionPhase.Resolved, resolved.Signal0.Phase);
            Assert.AreEqual(24f, resolved.AreaDamage.Amount, 1e-4f);
            Assert.AreEqual(2.5f, resolved.AreaDamage.Radius, 1e-4f);
            Assert.AreEqual(TriggerSource.Id, resolved.AreaDamage.Source.Id);
            Assert.AreEqual(0f, state.Poison, 1e-4f);
        }

        [Test]
        public void WetCleanse_ReducesToxicActualConsumptionAndDamage()
        {
            // 该测试锁定“Wet Cleanse 先于 Toxic 消耗”的顺序：Poison 被抢先清洗后，
            // Toxic 的 actualConsumed 下降，最终 Damage 也必须低于未清洗情况。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 30f, water: 100f, poison: 20f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, 0.01f);
            ElementReactionFrame resolved = runtime.Tick(in state, 0.2f);
            state = Apply(state, resolved);

            Assert.IsTrue(resolved.HasAreaDamage);
            Assert.Less(resolved.AreaDamage.Amount, 16f);
            Assert.GreaterOrEqual(state.Poison, 0f);
        }

        [Test]
        public void IgniteGoo_ConvertsFortyGooIntoFiftyFirePerSecond()
        {
            // 公式：40 Goo/s * 1.25 Fire/Goo * 1s = 50 Fire。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 20f, goo: 40f);
            runtime.MarkDirty(TriggerSource);

            state = Step(runtime, state, 0.01f);
            ElementReactionFrame frame = runtime.Tick(in state, 1f);
            state = Apply(state, frame);

            Assert.AreEqual(70f, state.Fire, 1e-4f);
            Assert.AreEqual(0f, state.Goo, 1e-4f);
            Assert.IsTrue((frame.SuppressNaturalDecay & StatusMask.Fire) != 0);
            Assert.IsTrue((frame.SuppressNaturalDecay & StatusMask.Goo) != 0);
        }

        [Test]
        public void IgniteGoo_PausesAtWetThresholdAndResumesBelowIt()
        {
            // 同时验证边界 water == threshold 会暂停，而 9.99 < threshold 会恢复。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 20f, goo: 40f);
            runtime.MarkDirty(TriggerSource);
            state = Step(runtime, state, 0.01f);

            state = Snapshot(state.Fire, water: 10f, poison: state.Poison, goo: state.Goo);
            ElementReactionFrame paused = runtime.Tick(in state, 0.1f);
            Assert.AreEqual(ElementReactionPhase.Paused, paused.Signal0.Phase);
            Assert.AreEqual(0f, paused.FireDelta, 1e-4f,
                "暂停后 Ignite 不能继续把 Goo 转成 Fire。");
            Assert.Less(paused.GooDelta, 0f,
                "Wet Cleanse 优先于 Ignite；暂停点燃时仍应继续清洗 Goo。");

            state = Snapshot(state.Fire, water: 9.99f, poison: state.Poison, goo: state.Goo);
            ElementReactionFrame resumed = runtime.Tick(in state, 0.1f);
            Assert.AreEqual(ElementReactionPhase.Resumed, resumed.Signal0.Phase);
            Assert.AreEqual(0f, resumed.FireDelta, 1e-4f,
                "Resumed 是离散阶段通知，下一帧才恢复转换。");
            Assert.Less(resumed.GooDelta, 0f,
                "恢复通知帧仍会执行优先级更高的 Wet Cleanse。");

            ElementReactionFrame progress = runtime.Tick(in state, 0.1f);
            Assert.Less(progress.GooDelta, 0f);
            Assert.Greater(progress.FireDelta, 0f);
        }

        [Test]
        public void MarkDirty_DoesNotDuplicateAnActiveReaction()
        {
            // dirty 只要求重新检查门槛；已经 Active 的 Process 不应再次发 Started。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 30f);
            runtime.MarkDirty(TriggerSource);
            ElementReactionFrame first = runtime.Tick(in state, 0.01f);

            runtime.MarkDirty(TriggerSource);
            ElementReactionFrame second = runtime.Tick(in state, 0.01f);

            Assert.AreEqual(1, first.SignalCount);
            Assert.AreEqual(ElementReactionPhase.Started, first.Signal0.Phase);
            Assert.AreEqual(0, second.SignalCount);
        }

        [Test]
        public void ThreeDifferentProcesses_CanBeActiveWithoutBreakingSignalBudget()
        {
            // 固定信号槽容量为 3；这里验证多 Process 并存时不会产生动态集合，
            // 同时正式信号仍按固定启动优先级写入。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 9f, poison: 40f, goo: 40f);
            runtime.MarkDirty(TriggerSource);

            ElementReactionFrame frame = runtime.Tick(in state, 0.01f);

            // Water=9 只启动无正式事件的低速中和；Toxic 与 Ignite 各发布 Started。
            Assert.IsTrue(runtime.IsExtinguishActive);
            Assert.IsTrue(runtime.IsToxicActive);
            Assert.IsTrue(runtime.IsIgniteActive);
            Assert.AreEqual(2, frame.SignalCount);
            Assert.AreEqual(ElementReactionId.ToxicCombustion, frame.Signal0.Reaction);
            Assert.AreEqual(ElementReactionId.IgniteGoo, frame.Signal1.Reaction);
        }

        [Test]
        public void CancelAll_PreventsPendingToxicDamage()
        {
            // 模拟对象死亡/Disable：取消 Wind-up 后，即使继续 Tick 也不能补发爆炸。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 30f, poison: 40f);
            runtime.MarkDirty(TriggerSource);
            state = Step(runtime, state, 0.01f);

            ElementReactionFrame cancelled = runtime.CancelAll();
            ElementReactionFrame later = runtime.Tick(in state, 1f);

            Assert.GreaterOrEqual(cancelled.SignalCount, 1);
            Assert.IsFalse(later.HasAreaDamage);
        }

        [Test]
        public void ZeroDeltaTime_DoesNotAdvanceProcesses()
        {
            // ApplyStatus 当帧可以 deltaTime=0 建立 Process，但不能偷偷消耗状态强度。
            ElementReactionRuntime runtime = CreateRuntime();
            ElementStateSnapshot state = Snapshot(fire: 80f, water: 30f);
            runtime.MarkDirty(TriggerSource);

            ElementReactionFrame frame = runtime.Tick(in state, 0f);

            Assert.AreEqual(0f, frame.FireDelta, 1e-4f);
            Assert.AreEqual(0f, frame.WaterDelta, 1e-4f);
        }

        private static ElementReactionRuntime CreateRuntime()
        {
            // 每个测试复用同一组明确参数；测试失败时可直接用公式手算预期值。
            return new ElementReactionRuntime(new ElementReactionTuningSnapshot(
                new ExtinguishTuning
                {
                    FormalThreshold = 10f,
                    LowRatePerSecond = 10f,
                    FormalRatePerSecond = 50f,
                    FireRemovedPerWater = 2f,
                },
                new WetCleanseTuning
                {
                    BudgetPerSecondAtFullWet = 30f,
                },
                new ToxicCombustionTuning
                {
                    FireThreshold = 25f,
                    PoisonThreshold = 20f,
                    WindUpSeconds = 0.2f,
                    MaxPoisonConsume = 40f,
                    MinEffectiveConsume = 5f,
                    BaseDamage = 8f,
                    DamagePerPoison = 0.4f,
                    Radius = 2.5f,
                    CooldownSeconds = 1f,
                },
                new IgniteGooTuning
                {
                    FireThreshold = 15f,
                    GooThreshold = 20f,
                    WetInhibitThreshold = 10f,
                    RequiredFireCapacity = 10f,
                    GooConsumePerSecond = 40f,
                    FirePerGoo = 1.25f,
                    CooldownSeconds = 0.5f,
                },
                poisonWetCleanseMultiplier: 0.75f,
                gooWetCleanseMultiplier: 1.25f));
        }

        private static ElementStateSnapshot Step(
            ElementReactionRuntime runtime,
            ElementStateSnapshot state,
            float deltaTime)
        {
            // 模拟真实 Adapter 的两阶段调用：先计算 Frame，再把 Delta 应用到状态快照。
            ElementReactionFrame frame = runtime.Tick(in state, deltaTime);
            return Apply(state, frame);
        }

        private static ElementStateSnapshot Apply(
            ElementStateSnapshot state,
            ElementReactionFrame frame)
        {
            // 测试侧只实现最小的 [0,100] Clamp，不复制 StatusController 的生命周期逻辑。
            return Snapshot(
                Clamp(state.Fire + frame.FireDelta),
                Clamp(state.Water + frame.WaterDelta),
                Clamp(state.Poison + frame.PoisonDelta),
                Clamp(state.Goo + frame.GooDelta));
        }

        private static ElementStateSnapshot Snapshot(
            float fire = 0f,
            float water = 0f,
            float poison = 0f,
            float goo = 0f)
        {
            return new ElementStateSnapshot(
                fire,
                water,
                poison,
                goo,
                FireSource,
                TriggerSource,
                TriggerSource,
                TriggerSource);
        }

        private static float Clamp(float value)
        {
            if (value < 0f)
                return 0f;
            return value > 100f ? 100f : value;
        }

        private static float Min(float a, float b)
        {
            return a < b ? a : b;
        }
    }
}
