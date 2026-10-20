namespace Game.Skills
{
    /// <summary>技术预算与物品容量分开：库存的16/32槽限制仍由Run校验。</summary>
    public static class SpellProgramLimits
    {
        public const int MaxInstructions = 1024;
        public const int MaxSteps = 1024;
        public const int MaxOutputs = 128;
        public const int MaxScopeDepth = 16;
        public const int MaxTraceEvents = 16384;
        public const int MaxPreviewRoots = 32;
        public const int MaxPreviewOutputs = 1024;
        public const float MinimumInterval = .05f;
    }
}
