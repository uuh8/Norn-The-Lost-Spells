using System.Collections.Generic;

namespace Game.Skills
{
    /// <summary>已烘焙的一层攻击与原程序槽位；私有数组不外泄，未来触发不再解释可变 SO。</summary>
    public sealed class CastExecutionLayer
    {
        private readonly EmitCommand[] _commands;
        private readonly int[] _children, _slots, _outputSlots;
        private readonly SpellKind[] _kinds;
        private readonly int[] _outputIds;
        public int Index { get; }
        public int Parent { get; }
        public float ManaCost { get; }
        public int CommandCount => _commands.Length;
        public int UseCount => _slots.Length;
        internal CastExecutionLayer(int index, int parent, float mana, EmitCommand[] commands, int[] children, int[] slots, SpellKind[] kinds, int[] outputSlots, int[] outputIds = null)
        { Index = index; Parent = parent; ManaCost = mana; _commands = commands; _children = children; _slots = slots; _kinds = kinds; _outputSlots = outputSlots; _outputIds=outputIds; }
        public int GetOutputId(int index) => _outputIds == null ? index : _outputIds[index];
        public EmitCommand GetCommand(int index) => _commands[index];
        public int GetChild(int index) => _children[index];
        public int GetSlot(int index) => _slots[index];
        public SpellKind GetKind(int index) => _kinds[index];
        public int GetOutputSlot(int index) => _outputSlots[index];
    }

    public sealed class CastExecutionPlan
    {
        private readonly CastExecutionLayer[] _layers;
        public int LayerCount => _layers.Length;
        public CastExecutionLayer GetLayer(int index) => _layers[index];
        private CastExecutionPlan(CastExecutionLayer[] layers) { _layers = layers; }
        /// <summary>运行适配器只复制冻结结果，不调用Legacy解释器；Mana已由根事务统一支付。</summary>
        public static CastExecutionPlan FromGraph(ShotExecutionGraph graph)
        {
            var layers = new CastExecutionLayer[graph.Scopes.Count];
            for (int l = 0; l < layers.Length; l++)
            {
                var scope = graph.Scopes[l]; int count = scope.OutputIds.Count;
                var commands = new EmitCommand[count]; var children = new int[count]; var slots = new int[count];
                for (int i = 0; i < count; i++)
                { var output = graph.Outputs[scope.OutputIds[i]]; commands[i] = output.Command; children[i] = output.PayloadScopeId;
                    slots[i] = graph.Program.Instructions[output.SourceCardIndex].SourceSlot; }
                var outputIds=new int[count];for(int i=0;i<count;i++)outputIds[i]=scope.OutputIds[i];
                layers[l] = new CastExecutionLayer(l, scope.ParentScopeId, 0, commands, children,
                    System.Array.Empty<int>(), System.Array.Empty<SpellKind>(), slots,outputIds);
            }
            return new CastExecutionPlan(layers);
        }
        public static bool TryBuild(IReadOnlyList<SpellDefinition> program, int draws, out CastExecutionPlan plan)
        {
            plan = null;
            if (program == null || draws <= 0 || draws > CastEvaluator.MaxEmitCommands) return false;
            // 未知 Kind 会让旧 switch 无法推进索引，因此新模式必须在进入解释器前拒绝。
            for (int i = 0; i < program.Count; i++)
            {
                SpellDefinition s = program[i]; if (s == null) continue;
                if (s.Kind > SpellKind.StaticProjectile || float.IsNaN(s.ManaCost) || float.IsInfinity(s.ManaCost)
                    || s.ManaCost < 0 || s.ExtraDraws < 0 || s.ExtraDraws > CastEvaluator.MaxEmitCommands) return false;
            }
            var layers = new List<CastExecutionLayer>(); var mapping = new int[program.Count];
            for (int i = 0; i < mapping.Length; i++) mapping[i] = i;
            int total = 0;
            if (!Build(program, mapping, draws, CastModifierState.Default, -1, 0, layers, ref total)) return false;
            plan = new CastExecutionPlan(layers.ToArray()); return true;
        }
        private static bool Build(IReadOnlyList<SpellDefinition> spells, int[] mapping, int draws, CastModifierState mods,
            int parent, int depth, List<CastExecutionLayer> layers, ref int total)
        {
            if (depth >= CastEvaluator.MaxActionDepth) return false;
            var recorder = new CastExecutionRecorder(); var commands = new List<EmitCommand>();
            CastSummary summary = CastEvaluator.EvaluateRecorded(spells, draws, float.PositiveInfinity, mods, commands, recorder);
            if (summary.Fizzled || commands.Count == 0 || float.IsNaN(summary.ManaSpent) || float.IsInfinity(summary.ManaSpent)) return false;
            total += commands.Count; if (total > CastEvaluator.MaxEmitCommands) return false;
            int index = layers.Count; layers.Add(null);
            var slots = new int[recorder.Count]; var kinds = new SpellKind[recorder.Count]; var children = new int[commands.Count];
            var outputSlots = new int[commands.Count];
            for (int i = 0; i < children.Length; i++) children[i] = -1;
            for (int i = 0; i < recorder.Count; i++)
            {
                CastExecutionUse use = recorder[i]; slots[i] = mapping[use.SlotIndex]; kinds[i] = use.Kind;
                // 产出序号来自执行记录，不能把多重施法/空槽后的command索引当卡片槽位。
                if (use.EmitIndex >= 0) outputSlots[use.EmitIndex] = mapping[use.SlotIndex];
                if (use.EmitIndex < 0 || !commands[use.EmitIndex].HasPayload) continue;
                EmitCommand cmd = commands[use.EmitIndex]; var childMap = new List<int>();
                // CaptureRange 省略 null，此处同步过滤；局部序号不等于原实例的槽位。
                for (int j = use.PayloadStart; j < use.PayloadEnd; j++) if (spells[j] != null) childMap.Add(mapping[j]);
                children[use.EmitIndex] = layers.Count;
                if (!Build(cmd.Payload, childMap.ToArray(), 1, cmd.PayloadMods, index, depth + 1, layers, ref total)) return false;
            }
            layers[index] = new CastExecutionLayer(index, parent, summary.ManaSpent, commands.ToArray(), children, slots, kinds, outputSlots);
            return true;
        }
    }
}
