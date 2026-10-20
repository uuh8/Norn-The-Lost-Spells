using System;
using System.Collections.Generic;

namespace Game.Skills
{
    public readonly struct ShotOutput
    {
        public readonly int Id, ScopeId, SourceCardIndex, PayloadScopeId, RegisteredAtStep;
        public readonly string SourceInstanceId;
        public readonly EmitCommand Command;
        public readonly SpellParameterProvenance Parameters;
        internal ShotOutput(int id, int scope, int source, string instanceId, int child, int step,
            in EmitCommand command, SpellParameterProvenance parameters)
        {
            Id = id; ScopeId = scope; SourceCardIndex = source; SourceInstanceId = instanceId;
            PayloadScopeId = child; RegisteredAtStep = step; Command = command; Parameters = parameters;
        }
    }

    public sealed class ShotScope
    {
        public int Id { get; }
        public int ParentScopeId { get; }
        public int OwnerOutputId { get; }
        public int ClosedAtStep { get; }
        public CastModifierState FinalModifiers { get; }
        public float CastDelayDelta { get; }
        public IReadOnlyList<int> OutputIds { get; }
        internal ShotScope(int id, int parent, int owner, int closed, CastModifierState modifiers,
            float delay, int[] outputs)
        {
            Id = id; ParentScopeId = parent; OwnerOutputId = owner; ClosedAtStep = closed;
            FinalModifiers = modifiers; CastDelayDelta = delay; OutputIds = Array.AsReadOnly(outputs);
        }
    }

    /// <summary>V2触发使用PayloadScopeId，不把可变SpellDefinition后缀交给未来对象重解释。</summary>
    public sealed class ShotExecutionGraph
    {
        public SpellProgram Program { get; }
        public IReadOnlyList<ShotScope> Scopes { get; }
        public IReadOnlyList<ShotOutput> Outputs { get; }
        public int RootOutputCount => Scopes.Count == 0 ? 0 : Scopes[0].OutputIds.Count;
        internal ShotExecutionGraph(SpellProgram program, ShotScope[] scopes, ShotOutput[] outputs)
        { Program = program; Scopes = Array.AsReadOnly(scopes); Outputs = Array.AsReadOnly(outputs); }
    }
}
