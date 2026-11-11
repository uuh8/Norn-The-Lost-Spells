namespace Game.NetworkProtocol
{
    /// <summary>Owner冷路径也保留完整复用边界：先清保留的子对象，再断开父引用与unknown fields。</summary>
    public sealed partial class InventorySnapshotMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = SchemaVersion = ConnectionEpoch = ResourceVersion = SelectionVersion = SpellUseVersion = ServerTick = 0;
            SnapshotSequence = 0; SelectedToolSlotPlusOne = 0;
            ResearchOpen = false; ResearchSessionVersion = 0; ResearchJson = Google.Protobuf.ByteString.Empty;
            if (Assets != null) Assets.ResetForReuse(); Assets = null;
            if (Ground != null) Ground.ResetForReuse(); Ground = null;
            foreach (var item in Wands) item.ResetForReuse(); Wands.Clear();
            foreach (var item in SpellUses) item.ResetForReuse(); SpellUses.Clear();
            _unknownFields = null;
        }
    }
    /// <summary>共享地面先清保留的子项，再断开父引用；复用不能残留上一批数量、姿态或unknown fields。</summary>
    public sealed partial class InventoryGroundSnapshotMessage
    {
        public void ResetForReuse()
        {
            Version = 0;
            foreach (var item in Items) item.ResetForReuse(); Items.Clear();
            foreach (var coin in Coins) coin.ResetForReuse(); Coins.Clear(); _unknownFields = null;
        }
    }
    /// <summary>地面消息复用连保留的Coin子引用也清零；旧局金额/位置及unknown fields不能进入新快照。</summary>
    public sealed partial class InventoryGroundCoinMessage
    {
        public void ResetForReuse()
        {
            CoinId = 0; Amount = 0; PositionX = PositionY = PositionZ = 0;
            _unknownFields = null;
        }
    }
    public sealed partial class InventoryGroundItemMessage
    {
        public void ResetForReuse()
        {
            InstanceId = ContentId = WorldOwnerId = string.Empty; Revision = 0;
            PositionX = PositionY = PositionZ = RotationX = RotationY = RotationZ = RotationW = 0;
            if (ContainerState != null) ContainerState.ResetForReuse(); ContainerState = null;
            if (MaterialState != null) MaterialState.ResetForReuse(); MaterialState = null;
            if (Item != null) Item.ResetForReuse(); Item = null;
            foreach (var child in Children) child.ResetForReuse(); Children.Clear();
            _unknownFields = null;
        }
    }
    public sealed partial class InventoryOwnedAssetMessage
    {
        public void ResetForReuse()
        {
            SchemaVersion = InventoryVersion = CurrencyVersion = 0; ActorId = 0; Gold = 0;
            RunId = PlayerId = InventoryId = GameplayConfigHash = SelectedWandInstanceId = string.Empty;
            foreach (var item in Items) item.ResetForReuse(); Items.Clear(); _unknownFields = null;
        }
    }
    public sealed partial class InventoryOwnedItemMessage
    {
        public void ResetForReuse()
        {
            InstanceId = ContentId = ParentInstanceId = string.Empty; Revision = 0;
            LocationKind = Slot = X = Y = QuarterTurns = RemainingCharges = 0;
            HasUnlimitedCharges = UseTemplateWandStats = EntryBoundBackpack = false;
            if (WandStats != null) WandStats.ResetForReuse(); WandStats = null;
            if (ContainerState != null) ContainerState.ResetForReuse(); ContainerState = null;
            if (MaterialState != null) MaterialState.ResetForReuse(); MaterialState = null;
            if (AttachmentState != null) AttachmentState.ResetForReuse(); AttachmentState = null;
            _unknownFields = null;
        }
    }
    public sealed partial class InventoryOwnedWandStatsMessage
    {
        public void ResetForReuse()
        {
            MaxMana = ManaRegenPerSecond = BaseCastDelay = BaseReloadTime = BaseSpreadDegrees = 0;
            ProgramCapacity = BaseDraws = 0; Shuffle = false; AlwaysCastContentIds.Clear(); _unknownFields = null;
        }
    }
    public sealed partial class InventoryOwnedContainerStateMessage
    {
        public void ResetForReuse()
        {
            SchemaVersion = 0;
            foreach (var part in Components) part.ResetForReuse(); Components.Clear(); _unknownFields = null;
        }
    }
    public sealed partial class InventoryOwnedMaterialAmountMessage
    {
        public void ResetForReuse() { MaterialKey = 0; Quantity = 0; _unknownFields = null; }
    }
    /// <summary>先清保留的固体状态对象及unknown fields，父对象随后断开引用；复用不能携带上个Owner的真实量。</summary>
    public sealed partial class InventoryOwnedMaterialStateMessage
    {
        public void ResetForReuse() { SchemaVersion = MaterialKey = 0; Quantity = 0; _unknownFields = null; }
    }
    /// <summary>附件是卡的设置而非库存数量；保留的子对象仍须先清来源/剂量/启用位及unknown fields。</summary>
    public sealed partial class InventorySpellAttachmentStateMessage
    {
        public void ResetForReuse() { SchemaVersion = 0; ContainerId = string.Empty; Dose = 0; Enabled = false; _unknownFields = null; }
    }
    public sealed partial class InventoryWandPresentationMessage
    {
        public void ResetForReuse()
        { InstanceId = string.Empty; ProgramRevision = 0; CurrentMana = CooldownRemaining = 0; _unknownFields = null; }
    }
    public sealed partial class InventorySpellUsePresentationMessage
    {
        public void ResetForReuse() { InstanceId = string.Empty; Eligible = false; _unknownFields = null; }
    }
    /// <summary>命令当前按离散请求创建，仍提供完整归还边界；以后复用时不能泄漏旧F/H射线或GMU。</summary>
    public sealed partial class InventoryCommandMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = ConnectionEpoch = ExpectedInventoryVersion = Kind = SourceProgramRevision = TargetProgramRevision = 0;
            GameplayHash = CommandId = TransferBudget = 0;
            ItemId = SourceWandId = TargetWandId = WorldOwnerId = TargetBackpackId = string.Empty;
            TargetSlot = GridX = GridY = QuarterTurns = TargetLocationKind = 0; ToSpellStorage = AutoPlace = WholeStack = false;
            AimOriginX = AimOriginY = AimOriginZ = AimDirectionX = AimDirectionY = AimDirectionZ = 0;
            AttachmentContainerId = string.Empty; AttachmentDose = 0; AttachmentEnabled = false;
            _unknownFields = null;
        }
    }
    public sealed partial class InventoryCommandReceiptMessage
    {
        public void ResetForReuse()
        {
            ProtocolVersion = ConnectionEpoch = InventoryVersion = Failure = TransferFailure = 0;
            GameplayHash = CommandId = Transferred = 0;
            _unknownFields = null;
        }
    }

}
