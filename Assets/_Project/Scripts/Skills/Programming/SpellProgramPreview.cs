using System;
using System.Collections.Generic;

namespace Game.Skills
{
    public readonly struct SpellPreviewOptions
    {
        public readonly int MaxRoots;
        public readonly bool FromBeginning, StopAfterReload;
        public readonly uint ResetSeed;
        public readonly float InitialWaitSeconds;
        public SpellPreviewOptions(int maxRoots, bool fromBeginning = false, bool stopAfterReload = true,
            uint resetSeed = 1, float initialWaitSeconds = 0)
        { MaxRoots = maxRoots; FromBeginning = fromBeginning; StopAfterReload = stopAfterReload;
            ResetSeed = resetSeed; InitialWaitSeconds = initialWaitSeconds; }
    }

    public sealed class SpellPreviewRoot
    {
        public WandDrawSnapshot Before { get; }
        public WandDrawSnapshot After { get; }
        public ShotExecutionGraph Graph { get; }
        public SpellExecutionTrace Trace { get; }
        public SpellBuildSummary Summary { get; }
        public double SimulatedStartSeconds { get; }
        public float ManaBefore { get; }
        internal SpellPreviewRoot(WandDrawSnapshot before, SpellBuildBuffer build,
            SpellBuildSummary summary, double start, float mana)
        { Before = before; After = build.NextState; Graph = build.Graph; Trace = build.Trace;
            Summary = summary; SimulatedStartSeconds = start; ManaBefore = mana; }
        public static SpellPreviewRoot FromRecorded(WandDrawSnapshot before, WandDrawSnapshot after, ShotExecutionGraph graph,
            SpellExecutionTrace trace, in SpellBuildSummary summary, float manaBefore)
        {
            var buffer = new SpellBuildBuffer { NextState = after, Graph = graph, Trace = trace };
            return new SpellPreviewRoot(before, buffer, summary, 0, manaBefore);
        }
    }

    /// <summary>离散查询的缓存；只持有冻结结果，绝不把模拟游标交回权威法杖。</summary>
    public sealed class SpellPreviewBuffer
    {
        internal readonly List<SpellPreviewRoot> MutableRoots = new List<SpellPreviewRoot>();
        internal readonly SpellBuildBuffer Build = new SpellBuildBuffer();
        internal readonly SpellProgramMachine Machine = new SpellProgramMachine();
        public IReadOnlyList<SpellPreviewRoot> Roots { get; }
        public bool Truncated { get; internal set; }
        public SpellProgramDiagnostic Diagnostic { get; internal set; }
        public SpellPreviewBuffer() { Roots = MutableRoots.AsReadOnly(); }
        public void Clear()
        { MutableRoots.Clear(); Truncated=false; Diagnostic=default; }
    }

    public static class SpellProgramPreview
    {
        public static bool TryPreview(SpellProgram program, WandDrawSnapshot before,
            in SpellExecutionInputs inputs, in SpellPreviewOptions options, SpellPreviewBuffer output)
        {
            if (output == null) return false;
            output.Clear();
            if (program == null || before == null || options.MaxRoots < 1 ||
                options.MaxRoots > SpellProgramLimits.MaxPreviewRoots ||
                float.IsNaN(options.InitialWaitSeconds) || float.IsInfinity(options.InitialWaitSeconds) ||
                options.InitialWaitSeconds < 0 || float.IsNaN(inputs.AvailableMana) ||
                float.IsInfinity(inputs.AvailableMana) || inputs.AvailableMana < 0 ||
                float.IsNaN(inputs.MaxMana) || float.IsInfinity(inputs.MaxMana) ||
                inputs.MaxMana < inputs.AvailableMana)
            {
                output.Diagnostic = new SpellProgramDiagnostic(SpellProgramError.InvalidInput, -1, "预览输入或预算非法。");
                return false;
            }
            var state = options.FromBeginning ? WandDrawSnapshot.CreateInitial(program, options.ResetSeed) : before;
            double clock = options.InitialWaitSeconds;
            float mana = Recover(inputs.AvailableMana, program.ManaRegenPerSecond, clock, inputs.MaxMana);
            var uses = inputs.RemainingUses;
            int outputs = 0;
            for (int i = 0; i < options.MaxRoots; i++)
            {
                // 仅虚拟时钟推进。没有调用Run的Commit，也没有改写传入Snapshot或随机数状态。
                var virtualInputs = new SpellExecutionInputs(mana, inputs.MaxMana, uses, inputs.Eligibility,
                    inputs.HpRatio, inputs.SelfWet, inputs.SelfBurning, inputs.InputVersion);
                if (!output.Machine.TryBuild(program, state, virtualInputs, output.Build, out var summary))
                { output.Diagnostic = summary.Diagnostic; return false; }
                if (outputs + output.Build.Graph.Outputs.Count > SpellProgramLimits.MaxPreviewOutputs)
                { output.Truncated = true; return true; }
                output.MutableRoots.Add(new SpellPreviewRoot(state, output.Build, summary, clock, mana));
                outputs += output.Build.Graph.Outputs.Count;
                state = output.Build.NextState; uses = output.Build.RemainingUsesAfter;
                if ((options.StopAfterReload && summary.NeedsReload) || state.Deck.Count + state.Discard.Count == 0 && program.IntrinsicCount==0)
                    return true;
                double wait = summary.NeedsReload ? Math.Max(summary.CastInterval, summary.ReloadInterval) : summary.CastInterval;
                clock += wait;
                mana = Recover(summary.ManaAfter, program.ManaRegenPerSecond, wait, inputs.MaxMana);
            }
            output.Truncated = true;
            return true;
        }

        private static float Recover(float mana, float rate, double seconds, float maximum)
        { return (float)Math.Min(maximum, (double)mana + rate * seconds); }
    }
}
