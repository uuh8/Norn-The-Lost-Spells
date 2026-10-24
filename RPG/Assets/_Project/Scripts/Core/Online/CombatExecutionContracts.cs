namespace Game.Core
{
    /// <summary>战斗权限不随移动 Prediction 转移；客户端预测只能播放动作，不能修改 Gameplay 资源。</summary>
    public enum CombatExecutionMode : byte
    {
        SinglePlayer,
        Authority,
        Replica
    }

    /// <summary>服务器已裁决的生命读模型；Replica 安装时不重新运行 DamagePipeline。</summary>
    public struct HealthReplicaSnapshot
    {
        public float CurrentHp;
        public float MaxHp;
        public bool IsInvulnerable;
        public byte TeamId;
    }

    public struct ManaReplicaSnapshot
    {
        public float CurrentMana;
        public float MaxMana;
    }

    /// <summary>四种状态的强度和最终移动倍率均来自 Authority；Replica 不推演 Reaction 或 NaturalDecay。</summary>
    public struct StatusReplicaSnapshot
    {
        public float Burning;
        public float Wet;
        public float Poisoned;
        public float Sticky;
        public float MoveSpeedMultiplier;
    }
}
