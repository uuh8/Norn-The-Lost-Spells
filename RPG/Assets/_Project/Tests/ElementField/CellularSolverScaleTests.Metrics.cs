using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField.Tests
{
    public sealed partial class CellularSolverScaleTests
    {
        [Serializable] private sealed class Report
        {
            public string utc,unity,platform,processor,cellProfile,solverAssembly;
            public string scope="EditMode CPU Solver+冷页+双向唤醒通知。计时排除建图、投放、逐格验证、序列化、Physics、网络、Renderer 和完整 PlayerLoop。";
            public string transportMetric="每 Tick 从真实脏坐标回读，按物质累加正净增 GMU。同 Tick 同格的等量进出会抵消，该量是搬运下界。气体消散单独对账。每条井分别记录首动与最长静止间隔。";
            public int workBudget,coldBudget,gasBudget,residentLimit,capacity,chunkEdge,atCapacity,saturationTestLimit;
            public int emptyWorldAllocatedSlots,grownSeedResident,grownSeedAllocatedSlots;
            public bool grownSnapshotExact,pageBoundaryRetryAndOwnership;
            public float cellSize,tickHz;
            public bool extraDepositRefused,complete;
            public bool saturatedStagedCollectionRoundTrip;
            public bool mixedOwnerStayedAndResumed;
            public long releasedBackgroundSteam;
            public List<Case> cases=new List<Case>();
            public List<string> errors=new List<string>();
        }

        [Serializable] private sealed class Case
        {
            public string name,trajectoryHash;
            public int lanes,coldEdge,startupSurfaceProbes,ticks,peakResident,peakQueue,peakQueueAfterStep,
                peakUnservedAtTickStart,ticksAtWorkLimit,ticksMovingAllThree,ticksOverRealTimeInterval;
            public int residentLimit,allocatedSlotsAtStart,allocatedSlotsAtEnd;
            public long authoredWaterGmu,inspectedCandidates,inspectedColdCandidates,changedNotices;
            public long coldWaterAtEnd,coldSandAtEnd;
            public double setupSurfaceMs,simulatedSeconds,meanColdMs,meanSimulationMs,meanPublishMs,
                meanTickMs,p50Ms,p95Ms,p99Ms,maxTickMs;
            public Species[] species;
            public Frame[] frames;
            public List<string> errors=new List<string>();
        }

        [Serializable] private sealed class Species
        {
            public string material;
            public long startGmu,endGmu,dissipated,redistributed,changedCellSamples;
            public int lanesEverMoved,worstFirstMotionTick,worstLaneGapTicks,worstGapAfterFirstTick,ticksWithMotion;
            public int minimumMovingTicksPerLane,endMovingPositions,lanesWithSplitAtEnd;
        }

        [Serializable] private struct Frame
        {
            public int tick,queueBefore,queueReady,processed,queueAfter,notices,resident,sleepingCandidates,coldProcessed;
            public long waterMoved,sandMoved,steamMoved;
            public double coldMs,simulationMs,publishMs;
            internal double Total => coldMs+simulationMs+publishMs;
        }

        private sealed partial class Rig
        {
            internal Case Measure(string name,int tickCount,bool moving,bool expectGas=true,bool sustainedFall=true,int maxGasGap=0)
            {
                var row=new Case { name=name,lanes=lanes,ticks=tickCount,coldEdge=coldEdge,authoredWaterGmu=(long)authoredWater,
                    residentLimit=cells.ResidentLimit,allocatedSlotsAtStart=cells.AllocatedResidentSlots,
                    startupSurfaceProbes=startupScans,setupSurfaceMs=startupMs,simulatedSeconds=(double)tickCount*dt,
                    species=new Species[3],frames=new Frame[tickCount] };
                var first=new int[3,lanes]; var last=new int[3,lanes]; var motionTicks=new int[3,lanes]; var elapsed=new double[tickCount];
                for (int s=0;s<3;s++) row.species[s]=new Species { material=Materials[s].ToString(),startGmu=(long)MaterialTotal(s) };
                ulong previousDissipated=cells.DissipatedGasQuantity(MaterialId.Steam);
                ulong rolling=14695981039346656037UL;
                for (int index=0;index<tickCount;index++)
                {
                    ref Frame f=ref row.frames[index]; f.tick=index+1; f.queueBefore=cells.ActiveCellCount;
                    long begin=Stopwatch.GetTimestamp();
                    f.coldProcessed=cold.Step(cfg.RestingBudget,cells);
                    long coldEnd=Stopwatch.GetTimestamp();
                    cells.BeginGasEnvironmentTick(dt,null,false,true);
                    f.queueReady=cells.ActiveCellCount;
                    f.processed=cells.Step(cfg.WorkBudget);
                    cells.StepGasDilution();
                    long simulationEnd=Stopwatch.GetTimestamp();
                    Publish();
                    long end=Stopwatch.GetTimestamp();
                    f.coldMs=Ms(coldEnd-begin); f.simulationMs=Ms(simulationEnd-coldEnd); f.publishMs=Ms(end-simulationEnd);
                    f.queueAfter=cells.ActiveCellCount; f.notices=changed.Count; f.resident=cells.OccupiedCellCount;
                    f.sleepingCandidates=cells.SleepingLiquidCandidateCount;
                    ulong changes=0;
                    foreach (Vector3Int cell in changed)
                    {
                        if (cell.x<0 || cell.x>=lanes*6) continue;
                        int lane=cell.x/6, s=cell.x%6/2;
                        quantities.TryGetValue(cell,out uint before);
                        cells.TryGetQuantity(cell,Materials[s],out uint after);
                        if (cold.TryGetQuantity(cell,Materials[s],out uint resting)) after+=resting;
                        if (before==after) continue;
                        if (after==0) quantities.Remove(cell); else quantities[cell]=after;
                        var species=row.species[s]; species.changedCellSamples++;
                        // 同一地址同时在两个 Owner 的变化表中只计一次，因第一次观察已经写回最终值。
                        if (after>before)
                        {
                            long gained=after-before; species.redistributed+=gained;
                            if (s==0) f.waterMoved+=gained; else if (s==1) f.sandMoved+=gained; else f.steamMoved+=gained;
                            if (first[s,lane]==0) first[s,lane]=f.tick;
                            if (last[s,lane]!=f.tick) motionTicks[s,lane]++;
                            species.worstLaneGapTicks=Math.Max(species.worstLaneGapTicks,f.tick-last[s,lane]-1);
                            if(last[s,lane]!=0) species.worstGapAfterFirstTick=Math.Max(species.worstGapAfterFirstTick,f.tick-last[s,lane]-1);
                            last[s,lane]=f.tick;
                        }
                        unchecked
                        {
                            ulong h=(uint)cell.x; h=(h^((ulong)(uint)cell.y<<1))*1099511628211UL;
                            h=(h^((ulong)(uint)cell.z<<1))*1099511628211UL;
                            changes^=((h^before)*1099511628211UL)^after;
                        }
                    }
                    unchecked { rolling=(rolling^changes)*1099511628211UL; }
                    if (f.waterMoved>0) row.species[0].ticksWithMotion++;
                    if (f.sandMoved>0) row.species[1].ticksWithMotion++;
                    if (f.steamMoved>0) row.species[2].ticksWithMotion++;
                    if (f.waterMoved>0 && f.sandMoved>0 && f.steamMoved>0) row.ticksMovingAllThree++;
                    for (int s=0;s<3;s++)
                    {
                        long dissipated=s==2?(long)(cells.DissipatedGasQuantity(MaterialId.Steam)-previousDissipated):0;
                        if ((long)MaterialTotal(s)+dissipated!=row.species[s].startGmu && row.errors.Count<8)
                            row.errors.Add("第 "+f.tick+" Tick 的 "+Materials[s]+" 总量不守恒。");
                    }
                    row.meanColdMs+=f.coldMs; row.meanSimulationMs+=f.simulationMs; row.meanPublishMs+=f.publishMs;
                    elapsed[index]=f.Total; row.meanTickMs+=f.Total;
                    row.inspectedCandidates+=f.processed; row.inspectedColdCandidates+=f.coldProcessed; row.changedNotices+=f.notices;
                    row.peakQueue=Math.Max(row.peakQueue,f.queueBefore); row.peakQueueAfterStep=Math.Max(row.peakQueueAfterStep,f.queueAfter);
                    row.peakResident=Math.Max(row.peakResident,f.resident);
                    row.peakUnservedAtTickStart=Math.Max(row.peakUnservedAtTickStart,f.queueReady-f.processed);
                    if (f.processed==cfg.WorkBudget) row.ticksAtWorkLimit++;
                    if (f.Total>1000d*dt) row.ticksOverRealTimeInterval++;
                    if ((f.processed>cfg.WorkBudget || f.coldProcessed>cfg.RestingBudget || f.resident>cells.ResidentLimit) && row.errors.Count<8)
                        row.errors.Add("第 "+f.tick+" Tick 越过配置容量。");
                }
                row.trajectoryHash=rolling.ToString("x16");
                row.allocatedSlotsAtEnd=cells.AllocatedResidentSlots;
                row.coldWaterAtEnd=(long)(cold.GetTotalQuantity(MaterialId.Water)-authoredWater);
                row.coldSandAtEnd=(long)cold.GetTotalQuantity(MaterialId.Sand);
                Array.Sort(elapsed); row.p50Ms=Percentile(elapsed,.5); row.p95Ms=Percentile(elapsed,.95);
                row.p99Ms=Percentile(elapsed,.99); row.maxTickMs=elapsed[elapsed.Length-1];
                row.meanColdMs/=tickCount; row.meanSimulationMs/=tickCount; row.meanPublishMs/=tickCount; row.meanTickMs/=tickCount;
                var finalPositions=new int[3,lanes];
                foreach(var pos in quantities.Keys) finalPositions[pos.x%6/2,pos.x/6]++;
                for(int s=0;s<3;s++)
                {
                    var species=row.species[s]; species.endGmu=(long)MaterialTotal(s);
                    if (s==2) species.dissipated=(long)(cells.DissipatedGasQuantity(MaterialId.Steam)-previousDissipated);
                    species.minimumMovingTicksPerLane=int.MaxValue;
                    for(int lane=0;lane<lanes;lane++)
                    {
                        species.minimumMovingTicksPerLane=Math.Min(species.minimumMovingTicksPerLane,motionTicks[s,lane]);
                        species.endMovingPositions+=finalPositions[s,lane];
                        if (finalPositions[s,lane]>1) species.lanesWithSplitAtEnd++;
                        if (first[s,lane]!=0) species.lanesEverMoved++;
                        species.worstFirstMotionTick=Math.Max(species.worstFirstMotionTick,first[s,lane]==0?tickCount+1:first[s,lane]);
                        species.worstLaneGapTicks=Math.Max(species.worstLaneGapTicks,tickCount-last[s,lane]);
                        if(last[s,lane]!=0) species.worstGapAfterFirstTick=Math.Max(species.worstGapAfterFirstTick,tickCount-last[s,lane]);
                    }
                    if (moving && (s<2 || expectGas) && (species.lanesEverMoved!=lanes || species.redistributed==0))
                        row.errors.Add(species.material+" 有井在整个窗口没有向新位置搬运 GMU。");
                    // 两路持续落体在整个窗口都没有终点。初次动过后仍须能按队列圈数重新获得进展。
                    int queueTurns=(row.peakQueue+cfg.WorkBudget-1)/cfg.WorkBudget;
                    int fallGap=name=="reopen-sediment"?species.worstGapAfterFirstTick:species.worstLaneGapTicks;
                    if (moving && sustainedFall && s<2 && fallGap>4*Math.Max(1,queueTurns)+4)
                        row.errors.Add(species.material+" 个别落体超出四个队列轮转窗口仍未前进。");
                    // 气井下方仍有低浓度空间。每条井都必须在更长的物理扩散窗口内持续搬运，
                    // 防止开头几份气体动过、其余测量时间整批丢失调度仍被视为通过。
                    if (moving && expectGas && s==2 && species.worstLaneGapTicks>(maxGasGap>0?maxGasGap:Math.Max(64,16*queueTurns)))
                        row.errors.Add("个别气井超过扩散滚动窗口未搬运。");
                }
                if (moving && expectGas && row.ticksMovingAllThree==0) row.errors.Add("没有一帧同时搬运三种材料。");
                if (!moving && (row.inspectedCandidates!=0 || row.inspectedColdCandidates!=0 || row.peakResident!=0 || row.changedNotices!=0))
                    row.errors.Add("已经稳定的闭湖仍产生元胞/冷页工作。");
                return row;
            }

            private static double Percentile(double[] sorted,double fraction) => sorted[(int)Math.Ceiling(sorted.Length*fraction)-1];
        }
    }
}
