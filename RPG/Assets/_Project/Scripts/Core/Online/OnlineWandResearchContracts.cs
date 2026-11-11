using System;

namespace Game.Core
{
    /// <summary>
    /// DS离散导出的原研究台事实，也是同登录session和平往返的连续性正文。
    /// Core仅保存基础值，不引用Run、Skills、Shared或Unity对象；收到这些值本身不授予施法、扣费或保存权限。
    /// Wire身份认证与个人保存session scope由上层负责；Run仍须验证真实Catalog、固定roll和完整安装序列。
    /// </summary>
    [Serializable] public sealed class OnlineWandResearchStateSnapshot
    {
        public const uint CurrentSchemaVersion = 1;
        public const int MaximumUtf8Bytes = 24 * 1024 * 1024;
        public uint SchemaVersion;
        public string PlayerId = "", InventoryId = "", GameplayConfigHash = "", SelectedWandId = "";
        // 这是捕获时的领域水位。Client安装要求同当前Owner；新DS可信接续只比较真实实例闭包，不能回灌旧版本。
        public uint InventoryVersion, ResourceVersion, SpellUseVersion;
        public OnlineWandResearchEntrySnapshot[] Wands = Array.Empty<OnlineWandResearchEntrySnapshot>();
        public OnlineWandLibraryCardSnapshot[] Library = Array.Empty<OnlineWandLibraryCardSnapshot>();
        // 保留原Archive的全局oldest-first顺序，最多三条，不是每根杖各发三条。
        public OnlineWandExecutionReplaySnapshot[] Replays = Array.Empty<OnlineWandExecutionReplaySnapshot>();
        public OnlineWandResearchEntrySnapshot[] GroundWands = Array.Empty<OnlineWandResearchEntrySnapshot>();
        public OnlineInventorySpellUsePresentationSnapshot[] GroundSpellUses = Array.Empty<OnlineInventorySpellUsePresentationSnapshot>();
    }

    [Serializable] public sealed class OnlineWandResearchEntrySnapshot
    {
        public string WandId = "";
        public uint ProgramRevision;
        public OnlineOwnedWandStatsSnapshot Stats;
        // 保存时的真实ContentId/ProgramIndex映射，使售出或换装后的旧条目仍可独立严格验证，再按原交集语义接续。
        public OnlineWandRecordedProgramSnapshot Program;
        // 与真实程序容量等长；空字符串表示空槽，不能压缩后把SourceSlot当成ProgramIndex。
        public string[] InstalledIds = Array.Empty<string>();
        public float Mana, Ready, Cast, Reload;
        public OnlineWandDrawSnapshot Draw;
        public OnlineSpellExecutionInputsSnapshot Inputs;
    }

    [Serializable] public sealed class OnlineWandDrawSnapshot
    {
        public long ProgramRevision, ExecutionRevision;
        public int CycleIndex;
        public uint RandomState;
        public float CycleReloadDelta;
        public int[] Deck = Array.Empty<int>(), Discard = Array.Empty<int>();
    }

    [Serializable] public sealed class OnlineSpellExecutionInputsSnapshot
    {
        public float AvailableMana, MaxMana, HpRatio;
        public bool SelfWet, SelfBurning;
        public long InputVersion;
        public int[] RemainingUses = Array.Empty<int>();
        public bool[] Eligibility = Array.Empty<bool>();
    }

    [Serializable] public sealed class OnlineWandLibraryCardSnapshot
    { public string InstanceId = "", ContentId = ""; }

    /// <summary>
    /// 一次历史程序的真实编译输入；耗尽卡可能已经不在当前Store，所以历史必须自带受控ContentId映射。
    /// Cards数组下标就是原ProgramIndex，SourceSlot保留原物品格号；固定卡SourceSlot=-1、实例名为@always:i。
    /// </summary>
    [Serializable] public sealed class OnlineWandRecordedProgramSnapshot
    {
        public long ProgramRevision;
        public OnlineOwnedWandStatsSnapshot Stats;
        public OnlineWandRecordedCardSnapshot[] Cards = Array.Empty<OnlineWandRecordedCardSnapshot>();
    }
    [Serializable] public sealed class OnlineWandRecordedCardSnapshot
    { public string InstanceId = "", ContentId = ""; public int SourceSlot; }

    [Serializable] public sealed class OnlineWandExecutionReplaySnapshot
    {
        public string TicketId = "", SourceWandId = "", CallerId = "";
        public OnlineWandRecordedProgramSnapshot Program, AfterProgram;
        public OnlineWandDrawSnapshot Before, After;
        public OnlineSpellExecutionInputsSnapshot Inputs;
        public OnlineSpellBuildSummarySnapshot Summary;
        public uint InventoryVersionBefore, InventoryVersionAfter;
        public OnlineWandUseReceiptSnapshot[] UsesPaid = Array.Empty<OnlineWandUseReceiptSnapshot>();
        public byte WorldStatus;
        public byte[] LayerStatuses = Array.Empty<byte>();
        public bool[] ReleasedOutputs = Array.Empty<bool>();
        public OnlineRecordedSpellScopeSnapshot[] Scopes = Array.Empty<OnlineRecordedSpellScopeSnapshot>();
        public OnlineRecordedSpellOutputSnapshot[] Outputs = Array.Empty<OnlineRecordedSpellOutputSnapshot>();
        public OnlineRecordedSpellTraceStepSnapshot[] Trace = Array.Empty<OnlineRecordedSpellTraceStepSnapshot>();
        public int TraceTotalEvents;
        public bool TraceTruncated, TraceWasCaptured;
    }
    [Serializable] public sealed class OnlineWandUseReceiptSnapshot
    { public string InstanceId = ""; public int Count; }

    [Serializable] public sealed class OnlineSpellBuildSummarySnapshot
    {
        public byte Outcome, Error;
        public int DiagnosticSourceSlot, InstructionSteps;
        public string DiagnosticMessage = "";
        public float ManaAfter, CastInterval, ReloadInterval;
        public bool NeedsReload;
    }

    [Serializable] public sealed class OnlineRecordedSpellScopeSnapshot
    {
        public int Id, ParentScopeId, OwnerOutputId, ClosedAtStep;
        public OnlineSpellModifierSnapshot FinalModifiers;
        public float CastDelayDelta;
        public int[] OutputIds = Array.Empty<int>();
        // Contribution的操作与channel来自该ProgramIndex的可信定义，不接受Wire另造一份修正公式。
        public OnlineRecordedSpellContributionSnapshot[] Contributions = Array.Empty<OnlineRecordedSpellContributionSnapshot>();
    }
    [Serializable] public sealed class OnlineRecordedSpellContributionSnapshot
    { public int CardIndex, StepId; }

    [Serializable] public sealed class OnlineSpellModifierSnapshot
    {
        public float DamageAddFlat, DamageMul, SpeedMul, SpreadDegrees;
        public int BounceCount;
        public bool UseGravity;
        public float HomingRadius, HomingDuration, HomingTurnRateDegrees;
        public float OrbitRadius, OrbitAngularSpeedDegrees, OrbitPhaseOffsetDegrees, OrbitPlaneTiltDegrees;
        public byte MotionMode;
        public float LifetimeAddSeconds, LifetimeMul;
    }
    [Serializable] public sealed class OnlineRecordedSpellOutputSnapshot
    {
        public int Id, ScopeId, SourceCardIndex, PayloadScopeId, RegisteredAtStep;
        public string SourceInstanceId = "";
        public OnlineRecordedSpellCommandSnapshot Command;
    }

    /// <summary>
    /// 原EmitCommand的最终数值证据；Prefab/Audio引用从同Hash受控Catalog重取，不允许Wire指定资源路径。
    /// Skills纯冻结factory逐字段比对原Baker结果，然后构造只读Graph；它不运行Draw、支付或创建世界对象。
    /// </summary>
    [Serializable] public sealed class OnlineRecordedSpellCommandSnapshot
    {
        public float LifetimeSeconds, LifetimeAddSeconds, LifetimeMul;
        public byte SpawnMode;
        public float SkyfallHeight, SkyfallBackOffset, LandingSiteDuration;
        public int ShieldReflectCount;
        public float Damage, ExplosionDamage, FireFieldDamagePerTick, FireFieldTickInterval, FireFieldDuration, Speed;
        public int DamageType;
        public float SpreadDegrees;
        public int BounceCount;
        public bool UseGravity;
        public float HomingRadius, HomingDuration, HomingTurnRateDegrees;
        public float OrbitRadius, OrbitAngularSpeedDegrees, OrbitPhaseOffsetDegrees, OrbitPlaneTiltDegrees;
        public byte MotionMode, PayloadTrigger;
        public float PayloadDelaySeconds;
    }

    [Serializable] public sealed class OnlineRecordedSpellTraceStepSnapshot
    {
        public int StepId, ScopeId, CardIndex, RequestId, ParentRequestId, OutputId, CauseCardIndex, TargetPosition;
        public byte Kind, InvocationMode, From, To, Error;
        public float ManaBefore, ManaAfter;
        public int UsesBefore, UsesAfter;
    }
}
