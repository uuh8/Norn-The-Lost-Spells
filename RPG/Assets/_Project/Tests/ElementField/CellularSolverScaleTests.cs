using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.ElementField.Tests
{
    // 直接调用正式 Solver 和冷页 Owner。竖井相互隔离，能对每一条井独立证明水、砂、气有进展，
    // 同时保留正式队列、事务、采样周期、扩散参数和冷页通知。观察代码与 JSON 均不计入 Solver 耗时。
    public sealed partial class CellularSolverScaleTests
    {
        private const string CellProfilePath = "Assets/_Project/ScriptableObjects/ElementField/CellularWorld.asset";
        private const string Profiles = "Assets/_Project/ScriptableObjects/World/Online/Materials/P7_OnlineCPU_";
        private const int FillerX = 2000000;
        private const int SaturationLimit = 32768;
        private static readonly MaterialId[] Materials = { MaterialId.Water, MaterialId.Sand, MaterialId.Steam };
        private static double Ms(long delta) => delta * 1000.0 / Stopwatch.Frequency;

        [Test]
        public void MeasureMotionBacklogAndResidentPressure()
        {
            var cfg = AssetDatabase.LoadAssetAtPath<ElementCellWorldProfile>(CellProfilePath);
            var geo = AssetDatabase.LoadAssetAtPath<ElementWorldProfile>(Profiles+"ElementWorldProfile.asset");
            var gas = AssetDatabase.LoadAssetAtPath<GasWorldProfile>(Profiles+"GasWorldProfile.asset");
            Assert.That(cfg && geo && gas, Is.True, "需要正式 Cellular、P7 世界与 P7 Gas 配置。");
            string outDir = Path.GetFullPath(Path.Combine(Application.dataPath,"../Temp/ElementWorldScale"));
            Directory.CreateDirectory(outDir);
            string path = Path.Combine(outDir,"solver-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".json");
            var report = new Report { utc=DateTime.UtcNow.ToString("O"), unity=Application.unityVersion,
                solverAssembly=typeof(ElementCellSimulation).Module.ModuleVersionId.ToString("D"),
                platform=SystemInfo.operatingSystem, processor=SystemInfo.processorType, cellProfile=CellProfilePath,
                workBudget=cfg.WorkBudget, coldBudget=cfg.RestingBudget, residentLimit=cfg.MaximumCells, saturationTestLimit=SaturationLimit,
                gasBudget=gas.MaximumCellsPerTick, capacity=cfg.Capacity, cellSize=geo.CellSize, tickHz=geo.TickRate, chunkEdge=geo.ChunkSize };
            try
            {
                var warm = new Rig(cfg,geo,gas,4,0,0); warm.Seed(3); warm.Measure("warmup",8,true);
                var small = new Rig(cfg,geo,gas,48,8,0);
                Add(report,small.Measure("idle-small",24,false),path);
                small.Seed(3); var smallMoving=small.Measure("active-same-small",64,true); Add(report,smallMoving,path);
                var huge = new Rig(cfg,geo,gas,48,128,0);
                Add(report,huge.Measure("idle-large",24,false),path);
                huge.Seed(3); var largeMoving=huge.Measure("active-same-large",64,true); Add(report,largeMoving,path);
                if (smallMoving.trajectoryHash!=largeMoving.trajectoryHash) report.errors.Add("相同活动下不同静水体积改变了运动轨迹。");
                var moderate = new Rig(cfg,geo,gas,256,0,0); moderate.Seed(3);
                Add(report,moderate.Measure("active-medium",80,true),path);
                var heavy = new Rig(cfg,geo,gas,1024,0,0); heavy.Seed(3);
                Add(report,heavy.Measure("active-large",128,true),path);
                var longRun = new Rig(cfg,geo,gas,192,0,0); longRun.Seed(3);
                Add(report,longRun.Measure("long-run",512,true),path);
                var pressure = new Rig(cfg,geo,gas,1,0,SaturationLimit-3,residentLimit:SaturationLimit);
                pressure.Seed(1);
                report.atCapacity = pressure.cells.OccupiedCellCount;
                report.extraDepositRefused = !pressure.cells.TryDeposit(new Vector3Int(FillerX+SaturationLimit,0,0),MaterialId.Rock,1);
                var pinned=pressure.Measure("resident-saturated",24,true,false); Add(report,pinned,path);
                if (report.atCapacity!=SaturationLimit || !report.extraDepositRefused || pinned.species[2].redistributed!=0 ||
                    pinned.species[0].redistributed==0 || pinned.species[1].redistributed==0) report.errors.Add("满槽时应允许整格交换、拒绝扩容气体并保留原量。");
                pressure.ReleaseSlots(8);
                Add(report,pressure.Measure("resident-released",32,true),path);
                var sediment=new Rig(cfg,geo,gas,256,0,0);
                sediment.SetBulkFloor(-8); sediment.Seed(3);
                var settled=sediment.Measure("settle-and-archive",160,true,true,false); Add(report,settled,path);
                if (settled.coldWaterAtEnd!=settled.species[0].endGmu || settled.coldSandAtEnd!=settled.species[1].endGmu)
                    report.errors.Add("稳定地板上的水和砂未全部交接给冷页。");
                sediment.SetBulkFloor(-100000);
                var reopened=sediment.Measure("reopen-sediment",96,true,true,true); Add(report,reopened,path);
                if (reopened.coldWaterAtEnd!=0 || reopened.coldSandAtEnd!=0)
                    report.errors.Add("开底后仍有水或砂留在不受支撑的冷页中。");
                // 全部驻留槽均为真实水、砂或气。768 组活动井自身就多于单次预算，其余槽是单独密闭的浓蒸汽。
                const int pressureLanes=768;
                var packed=new Rig(cfg,geo,gas,pressureLanes,0,SaturationLimit-3*pressureLanes,true,SaturationLimit);
                packed.Seed(1);
                var full=packed.Measure("resident-full-mixed-open",420,true,true,true,256); Add(report,full,path);
                if (full.peakResident!=SaturationLimit || packed.cells.FreeResidentSlots!=0 ||
                    full.species[2].minimumMovingTicksPerLane<2 || full.species[2].endMovingPositions!=pressureLanes)
                    report.errors.Add("满槽敞开气井应逐井至少搬运两次，且整份迁移不新增驻留地址。");
                report.saturatedStagedCollectionRoundTrip=packed.ProbeGasCollection();
                if (!report.saturatedStagedCollectionRoundTrip) report.errors.Add("满槽搬运后的可逆取料/回填未恢复原格和总量。");
                report.releasedBackgroundSteam=packed.OpenGasMouthsAndRelease(4*pressureLanes);
                var split=packed.Measure("resident-capacity-returned",32,true); Add(report,split,path);
                if (split.species[2].lanesWithSplitAtEnd!=pressureLanes || split.species[2].startGmu+report.releasedBackgroundSteam!=full.species[2].endGmu)
                    report.errors.Add("开侧口并回收槽后，普通分量扩散应恢复到每一条气井，收走的背景量可核对。");
                report.mixedOwnerStayedAndResumed=CheckMixedSourceAtLimit(cfg,geo,gas);
                if (!report.mixedOwnerStayedAndResumed) report.errors.Add("满槽时一个物种不能把同格另一种气体整体拖走。");

                // 与旧满载基准分开：这个世界真正提交 36864 个动态地址，在正式预算下持续落体/扩散。
                // 逐井记首动和最大断流，不能把远端材料仅仅成功存入表就算作参与了模拟。
                var grown=new Rig(cfg,geo,gas,6144,0,0);
                report.emptyWorldAllocatedSlots=grown.cells.AllocatedResidentSlots;
                grown.Seed(4);
                report.grownSeedResident=grown.cells.OccupiedCellCount;
                report.grownSeedAllocatedSlots=grown.cells.AllocatedResidentSlots;
                var growing=grown.Measure("demand-grown-live",256,true); Add(report,growing,path);
                report.grownSnapshotExact=grown.HasExactDynamicAddressSnapshot();
                if (report.emptyWorldAllocatedSlots!=0 || report.grownSeedResident!=36864 ||
                    report.grownSeedAllocatedSlots<report.grownSeedResident || report.grownSeedAllocatedSlots>=cfg.MaximumCells ||
                    growing.peakResident<=report.grownSeedResident || !report.grownSnapshotExact)
                    report.errors.Add("空世界、按需扩页、旧上限之外的真实输运或晚绑定完整枚举失配。");
                report.pageBoundaryRetryAndOwnership=CheckPageBoundaryAndRetry();
                if (!report.pageBoundaryRetryAndOwnership) report.errors.Add("非整页硬上限的物理拒绝、满槽拒绝、采集回填与通知未能分辨或回滚。");
            }
            catch (Exception e) { report.errors.Add(e.ToString()); }
            report.complete=true; Write(report,path);
            TestContext.WriteLine(path);
            Assert.That(report.errors, Is.Empty, string.Join("\n",report.errors));
        }

        private static void Add(Report report,Case row,string path)
        { report.cases.Add(row); foreach(string error in row.errors) report.errors.Add(row.name+" / "+error); Write(report,path); }
        private static void Write(Report report,string path) => File.WriteAllText(path,JsonUtility.ToJson(report,true));

        private static bool CheckMixedSourceAtLimit(ElementCellWorldProfile profile,ElementWorldProfile world,GasWorldProfile gas)
        {
            Vector3Int a=Vector3Int.zero,b=Vector3Int.down,filled=new Vector3Int(4,0,0);
            var cells=new ElementCellSimulation(profile.Capacity,2,p=>p!=a && p!=b && p!=filled);
            cells.ConfigureGasEnvironment(gas,Vector3.zero,world.CellSize,world.ChunkSize,null,null);
            if(!cells.TryDeposit(a,MaterialId.Steam,32) || !cells.TryDeposit(a,MaterialId.ToxicVapor,12) ||
                !cells.TryDeposit(filled,MaterialId.Rock,1)) return false;
            for(int i=0;i<160;i++)
            { cells.BeginGasEnvironmentTick(1f/world.TickRate,null,false,true); cells.Step(profile.WorkBudget); }
            if(cells.TryGetOccupiedCell(b,out _,out _) || !cells.TryGetQuantity(a,MaterialId.Steam,out uint steam) || steam!=32 ||
                !cells.TryGetQuantity(a,MaterialId.ToxicVapor,out uint vapor) || vapor!=12 || !cells.TryRemove(filled,MaterialId.Rock,1)) return false;
            for(int i=0;i<16;i++)
            { cells.BeginGasEnvironmentTick(1f/world.TickRate,null,false,true); cells.Step(profile.WorkBudget); }
            return cells.TryGetQuantity(b,MaterialId.Steam,out steam) && steam>0 && steam<32 &&
                cells.TryGetQuantity(b,MaterialId.ToxicVapor,out vapor) && vapor>0 && vapor<12 &&
                cells.GetTotalQuantity(MaterialId.Steam)==32 && cells.GetTotalQuantity(MaterialId.ToxicVapor)==12;
        }

        private static bool CheckPageBoundaryAndRetry()
        {
            // 513 = 一整页 + 最后一个合法地址。把同一个可写空格分别放在“已满”和“刚采空”时提交，
            // 并独立制造有空槽时的目标材质冲突：只有后一种可通过换用同材质在原目标写回。
            const int limit=513;
            var cells=new ElementCellSimulation(255,limit,null);
            var first=Vector3Int.zero;
            var extra=new Vector3Int(int.MinValue,0,int.MaxValue);
            if (cells.GetLocalFreeCapacity(first,MaterialId.Rock)!=255 || cells.AllocatedResidentSlots!=0 ||
                !cells.TryDeposit(first,MaterialId.Rock,1) || cells.AllocatedResidentSlots>=limit) return false;
            if (cells.FreeResidentSlots==0 || cells.GetLocalFreeCapacity(first,MaterialId.Sand)!=0 ||
                cells.TryDeposit(first,MaterialId.Sand,1) || cells.GetLocalFreeCapacity(first,MaterialId.Rock)!=254) return false;
            for (int i=1;i<limit;i++)
                if (!cells.TryDeposit(new Vector3Int(i,0,0),MaterialId.Rock,1)) return false;
            var born=new HashSet<Vector3Int>();
            while (cells.TryDequeueChanged(out var cell)) if (!born.Add(cell)) return false;
            if (born.Count!=limit || cells.AllocatedResidentSlots!=limit || cells.FreeResidentSlots!=0 ||
                cells.GetLocalFreeCapacity(extra,MaterialId.Sand)!=0 || cells.TryDeposit(extra,MaterialId.Sand,1) ||
                cells.ChangedCellCount!=0 || cells.GetTotalQuantity(MaterialId.Rock)!=limit) return false;
            if (!cells.TryRemove(first,MaterialId.Rock,1) || cells.GetLocalFreeCapacity(extra,MaterialId.Sand)!=255 ||
                !cells.TryDeposit(extra,MaterialId.Sand,1)) return false;
            var changed=new HashSet<Vector3Int>();
            while (cells.TryDequeueChanged(out var updated)) if (!changed.Add(updated)) return false;
            if (changed.Count!=2 || !changed.Contains(first) || !changed.Contains(extra) ||
                cells.TryGetOccupiedCell(first,out _,out _) || cells.GetTotalQuantity(MaterialId.Rock)!=limit-1 ||
                cells.GetTotalQuantity(MaterialId.Sand)!=1 || cells.AllocatedResidentSlots!=limit) return false;
            born.Remove(first); born.Add(extra);
            for (int i=0;i<cells.OccupiedCellCount;i++)
                if (!cells.TryGetOccupiedCoordinate(i,out var p) || !born.Remove(p)) return false;
            if (born.Count!=0 || cells.OccupiedCellCount!=limit || cells.TryGetOccupiedCoordinate(limit,out _)) return false;
            // 同一逻辑地址换成主相和两种气体。复制入口必须读出三笔实际 GMU，通知只发布该地址一次。
            return cells.TryRemove(extra,MaterialId.Sand,1) && cells.TryDeposit(extra,MaterialId.Water,3) &&
                cells.TryDeposit(extra,MaterialId.Steam,4) && cells.TryDeposit(extra,MaterialId.ToxicVapor,5) &&
                cells.OccupiedCellCount==limit && cells.ChangedCellCount==1 &&
                cells.TryReadContents(extra,out var primary,out uint amount,out uint steam,out uint toxic) &&
                primary==MaterialId.Water && amount==3 && steam==4 && toxic==5;
        }

        private sealed partial class Rig
        {
            private readonly ElementCellWorldProfile cfg;
            private readonly float dt;
            private readonly int lanes, coldEdge;
            private int bulkFloor=-100000;
            private readonly bool sealedGasFiller;
            private bool sideMouthsOpen;
            private readonly ulong authoredWater;
            private readonly int startupScans;
            private readonly double startupMs;
            internal readonly ElementCellSimulation cells;
            private readonly CellularRestingLiquidStore cold;
            private readonly Dictionary<Vector3Int,uint> quantities = new Dictionary<Vector3Int,uint>();
            private readonly List<Vector3Int> changed = new List<Vector3Int>(8192);

            internal Rig(ElementCellWorldProfile cfg,ElementWorldProfile world,GasWorldProfile gas,int lanes,int coldEdge,int filler,
                bool sealedGasFiller=false,int residentLimit=0)
            {
                this.cfg=cfg; this.lanes=lanes; this.coldEdge=coldEdge; this.sealedGasFiller=sealedGasFiller; dt=1f/world.TickRate;
                var regions=coldEdge==0 ? null : new[]{new CellularRestingLiquidRegion { Material=MaterialId.Water,
                    Cells=new BoundsInt(-1000000,0,0,coldEdge,coldEdge,coldEdge) }};
                authoredWater=(ulong)coldEdge*(uint)coldEdge*(uint)coldEdge*(uint)cfg.Capacity;
                cold=new CellularRestingLiquidStore(regions,(uint)cfg.Capacity,cfg.RestingQueueCapacity);
                cells=new ElementCellSimulation(cfg.Capacity,residentLimit>0?residentLimit:cfg.MaximumCells,Blocked,cfg.Transport);
                cells.BindRestingMaterials(cold);
                cells.ConfigureGasEnvironment(gas,Vector3.zero,world.CellSize,world.ChunkSize,null,null);
                long start=Stopwatch.GetTimestamp();
                int visited;
                do
                {
                    visited=cold.Step(cfg.RestingBudget,cells); startupScans+=visited;
                    if (startupScans>6L*coldEdge*coldEdge+cfg.RestingBudget+16) throw new InvalidOperationException("封闭湖的初始表面未能休眠。");
                } while (visited>0);
                startupMs=Ms(Stopwatch.GetTimestamp()-start);
                for (int i=0;i<filler;i++)
                {
                    var pos=new Vector3Int(FillerX+(sealedGasFiller?2*i:i),0,0);
                    if (!cells.TryDeposit(pos,sealedGasFiller?MaterialId.Steam:MaterialId.Rock,sealedGasFiller?32u:1u))
                        throw new InvalidOperationException("填充驻留槽失败。");
                }
                if (sealedGasFiller)
                {
                    int warmTicks=0;
                    do
                    {
                        Publish(); cells.BeginGasEnvironmentTick(dt,null,false,true); cells.Step(cfg.WorkBudget); cells.StepGasDilution();
                        if (++warmTicks>2+(filler+cfg.WorkBudget-1)/cfg.WorkBudget) throw new InvalidOperationException("密闭浓气背景没有休眠。");
                    } while(cells.ActiveCellCount!=0);
                }
            }

            private bool Blocked(Vector3Int p)
            {
                if (cold.BlocksSimulation(p)) return true;
                if (p.x>=FillerX) return p.y!=0 || p.z!=0 || sealedGasFiller && (p.x&1)!=0;
                return p.z!=0 && !(sideMouthsOpen && p.z==1 && p.x%6==4) || p.x<0 || p.x>=lanes*6 || (p.x&1)!=0 ||
                    p.y>0 || p.y < -100000 || p.y<bulkFloor && p.x%6!=4;
            }

            internal void SetBulkFloor(int y)
            {
                int old=bulkFloor; bulkFloor=y;
                if (y>=old) return;
                // 沿每条井实际打开旧地板，发出与地形开挖相同的动态和冷页邻格通知。
                for(int lane=0;lane<lanes;lane++) for(int s=0;s<2;s++)
                {
                    var opened=new Vector3Int(lane*6+s*2,old-1,0);
                    cells.WakeNeighbors(opened); cold.WakeNeighbors(opened);
                }
            }

            internal void Seed(int gasDepth)
            {
                for (int lane=0;lane<lanes;lane++)
                {
                    SeedAt(lane,0,0,(uint)cfg.Capacity);
                    SeedAt(lane,1,0,97);
                    for (int depth=0;depth<gasDepth;depth++) SeedAt(lane,2,-depth,32);
                }
                Publish();  // 技能产生的一次离散出生已完成；测量从下一个完整 Tick 开始。
                changed.Clear();
            }

            private void SeedAt(int lane,int species,int y,uint quantity)
            {
                var p=new Vector3Int(lane*6+species*2,y,0);
                if (!cells.TryDeposit(p,Materials[species],quantity)) throw new InvalidOperationException("初始分档投放失败。");
                quantities.Add(p,quantity);
            }

            internal void ReleaseSlots(int count)
            {
                for(int i=0;i<count;i++) if(!cells.TryRemove(new Vector3Int(FillerX+i,0,0),MaterialId.Rock,1))
                    throw new InvalidOperationException("未能归还驻留槽。");
                Publish(); changed.Clear();
            }

            internal bool ProbeGasCollection()
            {
                foreach(var entry in quantities)
                {
                    if (entry.Key.x%6!=4 || entry.Value<2) continue;
                    ulong before=MaterialTotal(2);
                    if (cells.TryRemove(entry.Key,MaterialId.Steam,entry.Value+1) || !cells.TryRemove(entry.Key,MaterialId.Steam,1) ||
                        cells.GetLocalFreeCapacity(entry.Key,MaterialId.Steam)<1 || !cells.TryDeposit(entry.Key,MaterialId.Steam,1)) return false;
                    return cells.TryGetQuantity(entry.Key,MaterialId.Steam,out uint after) && after==entry.Value && before==MaterialTotal(2);
                }
                return false;
            }

            internal bool HasExactDynamicAddressSnapshot()
            {
                var seen=new HashSet<Vector3Int>();
                if (quantities.Count!=cells.OccupiedCellCount) return false;
                for(int i=0;i<cells.OccupiedCellCount;i++)
                {
                    if (!cells.TryGetOccupiedCoordinate(i,out var cell) || !seen.Add(cell) ||
                        !quantities.TryGetValue(cell,out uint expected) ||
                        !cells.TryReadContents(cell,out var primary,out uint amount,out uint steam,out uint toxic)) return false;
                    int species=cell.x%6/2;
                    if (toxic!=0 || (species==2 ? primary!=MaterialId.Empty || amount!=0 || steam!=expected :
                        primary!=Materials[species] || amount!=expected || steam!=0)) return false;
                }
                return !cells.TryGetOccupiedCoordinate(cells.OccupiedCellCount,out _);
            }

            internal long OpenGasMouthsAndRelease(int count)
            {
                for(int i=0;i<count;i++) if(!cells.TryRemove(new Vector3Int(FillerX+2*i,0,0),MaterialId.Steam,32))
                    throw new InvalidOperationException("收走密闭背景蒸汽失败。");
                sideMouthsOpen=true;
                foreach(var entry in quantities) if (entry.Key.x%6==4) cells.WakeNeighbors(entry.Key+Vector3Int.forward);
                Publish(); changed.Clear(); return 32L*count;
            }

            private void Publish()
            {
                changed.Clear();
                while(cells.TryDequeueChanged(out var cell)) { cold.WakeNeighbors(cell); changed.Add(cell); }
                while(cold.TryDequeueChanged(out var restingCell)) { cells.WakeNeighbors(restingCell); changed.Add(restingCell); }
            }

            private ulong MaterialTotal(int species) => cells.GetTotalQuantity(Materials[species])+cold.GetTotalQuantity(Materials[species]);
        }
    }
}
