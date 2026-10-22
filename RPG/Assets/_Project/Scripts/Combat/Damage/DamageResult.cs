namespace Game.Combat
{
    /// <summary>DamagePipeline.Resolve 的输出。纯数据。</summary>
    public readonly struct DamageResult
    {
        public readonly float Final;
        public readonly DamageType Type;
        public readonly bool WasMitigated;  // 是否被减伤

        public DamageResult(float final, DamageType type, bool wasMitigated)
        {
            Final        = final;
            Type         = type;
            WasMitigated = wasMitigated;
        }
    }
}
