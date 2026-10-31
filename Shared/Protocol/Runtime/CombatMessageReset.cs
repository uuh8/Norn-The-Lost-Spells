namespace Game.NetworkProtocol
{
    /// <summary>Schema 扩展时同步清理全部字段；生成文件保持 protoc 原样，避免省略字段继承上一包。</summary>
    public sealed partial class CombatCastIntentMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = ConnectionEpoch = ProgramRevision = 0;
            CommandId = GameplayHash = 0; WandInstanceId = string.Empty;
            OriginX = OriginY = OriginZ = AimX = AimY = AimZ = 0;
            _unknownFields = null;
        }
    }
    public sealed partial class CombatFactMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = Sequence = ServerTick = Kind = ConnectionEpoch = PrefabKey = OutputOrdinal = 0;
            Result = Phase = Team = ProgramRevision = RemainingCharges = Flags = DamageType = AnimationHash = ActionKind = SoundKey = 0;
            // ActorState按稳定装备槽定位资源；若省略清理，下一条无Owned来源的事实会借用旧槽并污染另一根杖。
            WandSlotPlusOne = 0;
            GameplayHash = ActorId = CommandId = OutputId = TargetActorId = AttackerActorId = 0;
            PositionX = PositionY = PositionZ = RotationX = RotationY = RotationZ = RotationW = 0;
            VelocityX = VelocityY = VelocityZ = CurrentHp = MaxHp = CurrentMana = MaxMana = Cooldown = 0;
            Burning = Wet = Poisoned = Sticky = MoveMultiplier = Amount = 0;
            WandInstanceId = string.Empty;
            _unknownFields = null;
        }
    }
}
