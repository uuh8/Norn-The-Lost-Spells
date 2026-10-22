#ifndef GAME_ELEMENT_FIELD_FLUID_GPU_COMMON_INCLUDED
#define GAME_ELEMENT_FIELD_FLUID_GPU_COMMON_INCLUDED

// 与 IFluidGpuSource.cs 中的 FluidGpuSpawnRequest 保持 64-byte 顺序一致。
// StructuredBuffer 的 element stride 是 CPU/GPU Contract，字段重排会直接读错 VRAM。
struct FluidGpuSpawnRequest
{
    float4 positionRadius;
    float4 velocity;
    uint4 materialSeedFlagsParticleCount;
    float4 packingParameters;
};

struct FluidGpuLiquidMaterialParameters
{
    float4 massDensityViscosityPressure;
    float4 cohesionAndSleep;
};

static const uint FLUID_ALIVE_FLAG = 1u << 0;
static const uint FLUID_INTEREST_ACTIVE_FLAG = 1u << 1;
static const uint FLUID_REQUIRES_SIMULATION_FLAG = 1u << 2;
static const uint FLUID_SLEEPING_FLAG = 1u << 3;
static const uint FLUID_REMOTE_SPAWN_ACTIVE_FLAG = 1u << 4;
static const uint FLUID_ARCHIVE_LOCKED_FLAG = 1u << 5;
static const uint FLUID_RESTORE_LOADING_FLAG = 1u << 6;
// 旧名字仅供旧 fixture 构造默认 awake 状态；新 Kernel 必须选择精确 predicate。
static const uint FLUID_ACTIVE_FLAG = FLUID_ALIVE_FLAG
    | FLUID_INTEREST_ACTIVE_FLAG
    | FLUID_REQUIRES_SIMULATION_FLAG;
static const uint FLUID_FREE_COUNT_COUNTER = 0u;
static const uint FLUID_ACTIVE_COUNT_COUNTER = 1u;
static const uint FLUID_DROPPED_PARTICLE_COUNTER = 2u;
static const uint FLUID_NUMERICAL_ERROR_COUNTER = 3u;
static const uint FLUID_AWAKE_ACTIVITY_COUNTER = 0u;
static const uint FLUID_SLEEPING_ACTIVITY_COUNTER = 1u;
static const uint FLUID_INTEREST_ACTIVITY_COUNTER = 2u;

bool FluidIsAlive(uint flags)
{
    return (flags & FLUID_ALIVE_FLAG) != 0u;
}

bool FluidIsInInterest(uint flags)
{
    return (flags & (FLUID_ALIVE_FLAG | FLUID_INTEREST_ACTIVE_FLAG))
        == (FLUID_ALIVE_FLAG | FLUID_INTEREST_ACTIVE_FLAG);
}

bool FluidRequiresSimulation(uint flags)
{
    // Solver Eligibility 有两个来源：玩家附近 Interest，或远处新生粒子尚未落稳的临时 Lease。
    // RequiresSimulation 不能单独成立，否则历史 Resident 会被无意全部唤醒。
    return (flags & (FLUID_ALIVE_FLAG | FLUID_REQUIRES_SIMULATION_FLAG))
        == (FLUID_ALIVE_FLAG | FLUID_REQUIRES_SIMULATION_FLAG)
        && (flags & (FLUID_INTEREST_ACTIVE_FLAG | FLUID_REMOTE_SPAWN_ACTIVE_FLAG)) != 0u
        && (flags & (FLUID_SLEEPING_FLAG | FLUID_ARCHIVE_LOCKED_FLAG | FLUID_RESTORE_LOADING_FLAG)) == 0u;
}

uint FluidHash(uint value)
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}

float FluidRandom01(uint value)
{
    return (float)FluidHash(value) * (1.0f / 4294967295.0f);
}

#endif
