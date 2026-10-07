using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;
using Unity.Profiling;

namespace Game.ElementField
{
    /// <summary>数量变化通知与显式环境区提供候选；每 Tick 只处理有界队列，不遍历全部常驻材料。</summary>
    internal sealed class MaterialConversionScheduler
    {
        private static readonly ProfilerMarker Marker=new ProfilerMarker("MaterialWorld.Convert");
        private struct Candidate
        { internal MaterialConversionAddress Address; internal double Credit, LastTime, Cooldown; internal int Rule, Epoch; }
        private readonly ElementWorldRuntime _world;
        private readonly LiquidWorldRuntime _liquid;
        private readonly FixedSolidWorldRuntime _solid;
        private readonly GasWorldRuntime _gas;
        private readonly MaterialConversionProfile _profile;
        private readonly MaterialConversionWorldPort _port;
        private readonly MaterialConversionTransaction _transaction;
        private readonly Candidate[] _candidates;
        private readonly int[] _queue, _free;
        private readonly Dictionary<MaterialConversionAddress,int> _indices;
        private readonly MaterialConversionPlacement[] _placements=new MaterialConversionPlacement[128];
        private readonly ElementWorldActivityRegions _previousDomain=new ElementWorldActivityRegions();
        private readonly MaterialEnvironmentAuthoring[] _environments=new MaterialEnvironmentAuthoring[64];
        private readonly bool[] _ambientLiquidCandidates=new bool[256];
        private int _ambientLiquidCount;
        private int _head,_tail,_count,_freeCount,_environmentCount,_zoneCursor,_zoneCell,_epoch,_commits,_solidCommits;
        private int _idleSearchRemaining=64;
        private double _clock;
        private bool _domainWasNull=true;
        private bool _recover, _recoverAgain;
        private int _recoveryBackend, _recoveryCursor;
        internal MaterialWorldQuantityLedger Ledger { get; }=new MaterialWorldQuantityLedger();
        internal uint CandidateOverflows { get; private set; }
        internal uint DeferredTransactions { get; private set; }
        internal int ProcessedLastTick { get; private set; }
        internal int PendingCandidates => _count;
        internal MaterialConversionScheduler(ElementWorldRuntime world, LiquidWorldRuntime liquid, FixedSolidWorldRuntime solid, GasWorldRuntime gas, MaterialConversionProfile profile)
        {
            _world=world; _liquid=liquid; _solid=solid; _gas=gas; _profile=profile;
            var ambient=new MaterialEnvironmentSample(profile.AmbientTemperature,profile.AmbientDryness);
            for(int i=0;i<profile.RuleCount;i++)
            {
                var rule=profile.GetRule(i);
                if(liquid.Supports(rule.Source) && MaterialConversionEvaluator.Matches(in rule,in ambient) && !_ambientLiquidCandidates[(byte)rule.Source])
                { _ambientLiquidCandidates[(byte)rule.Source]=true; _ambientLiquidCount++; }
            }
            _port=new MaterialConversionWorldPort(world,liquid,solid,gas);
            _transaction=new MaterialConversionTransaction(_port,Ledger);
            int capacity=profile.MaximumCandidates;
            _candidates=new Candidate[capacity]; _queue=new int[capacity]; _free=new int[capacity];
            _indices=new Dictionary<MaterialConversionAddress,int>(capacity);
            for(int i=0;i<capacity;i++) _free[_freeCount++]=capacity-1-i;
            liquid.SetConversionObserver(NotifyLiquid); solid.SetConversionObserver(NotifySolid); gas.SetConversionObserver(NotifyGas);
            liquid.SetMaterialSolidOccupancy(solid);
            liquid.SetConversionAccounting(Ledger.RecordExternal,Ledger.RecordExplicitConsumption);
            // 初始库存只在初始化边界合并一次；全量审计不放进每 Tick 的热路径。
            for(int i=1;i<=8;i++)
            {
                var material=(MaterialId)i;
                if(MaterialWorldQuantityLedger.Family(material)==MaterialQuantityFamily.None) continue;
                Ledger.RecordExternal(material,liquid.MaterialQuantity(material)+liquid.PendingMaterialQuantity(material)
                    +solid.MaterialQuantity(material)+solid.PendingMaterialQuantity(material)+gas.MaterialQuantity(material)+gas.PendingMaterialQuantity(material));
            }
        }
        internal void NotifyLiquid(Vector3Int cell)
        {
            // 常温水在默认环境下不会转换；别把每一次普通液体流动都加进相变工作队列。
            // 热接触和显式环境扫描另行提供候选，仍能唤醒已经稳定/休眠的水。
            if(_ambientLiquidCandidates[(byte)MaterialId.Water]) Enqueue(new MaterialConversionAddress(cell,MaterialId.Water));
            if(_ambientLiquidCandidates[(byte)MaterialId.Sticky]) Enqueue(new MaterialConversionAddress(cell,MaterialId.Sticky));
            if(_ambientLiquidCandidates[(byte)MaterialId.Poison]) Enqueue(new MaterialConversionAddress(cell,MaterialId.Poison));
        }
        private void NotifySolid(Vector3Int cell) => Enqueue(new MaterialConversionAddress(cell,MaterialId.Ice));
        private void NotifyGas(Vector3Int cell) => Enqueue(new MaterialConversionAddress(cell,MaterialId.Steam));
        private void Enqueue(MaterialConversionAddress address)
        {
            if(_indices.ContainsKey(address)) return;
            if(_freeCount==0)
            {
                CandidateOverflows++;
                if(_recover) _recoverAgain=true; else { _recover=true; _recoveryCursor=0; _recoveryBackend=0; }
                // 只替换没有配方/进度/冷却的条件等待项，数量仍属于 Store，恢复游标会重访。
                // 不淘汰正在冻结或有冷却的事务进度；所有通知合计至多 64 次检查/Tick，
                // 避免数千 Gas 通量通知各自遍历 64 项，把 O(1) 通知变成新的 CPU 峰值。
                if(!ReleaseIdleCandidate()) return;
            }
            int index=_free[--_freeCount];
            _candidates[index]=new Candidate {Address=address,LastTime=_clock,Rule=-1,Epoch=_epoch};
            _indices.Add(address,index); Push(index);
        }
        private void Push(int index) { _queue[_tail]=index; _tail=(_tail+1)%_queue.Length; _count++; }
        private void Release(int index) { _indices.Remove(_candidates[index].Address); _free[_freeCount++]=index; }
        private bool ReleaseIdleCandidate()
        {
            int visits=Math.Min(_count,_idleSearchRemaining);
            for(int i=0;i<visits;i++)
            {
                _idleSearchRemaining--;
                int index=_queue[_head]; _head=(_head+1)%_queue.Length; _count--;
                var candidate=_candidates[index];
                if(candidate.Rule<0 && candidate.Credit==0 && candidate.Cooldown<=0) { Release(index); return true; }
                Push(index);
            }
            return false;
        }
        internal void Register(MaterialEnvironmentAuthoring environment)
        {
            for(int i=0;i<_environmentCount;i++) if(_environments[i]==environment) return;
            if(_environmentCount<_environments.Length) _environments[_environmentCount++]=environment;
        }
        internal void Unregister(MaterialEnvironmentAuthoring environment)
        {
            for(int i=0;i<_environmentCount;i++) if(_environments[i]==environment)
            { _environments[i]=_environments[--_environmentCount]; _environments[_environmentCount]=null; _zoneCell=0; return; }
        }
        internal void BeginTick(float deltaTime,ElementWorldActivityRegions domain)
        {
            _clock+=deltaTime; _commits=0; _solidCommits=0; _idleSearchRemaining=64;
            bool changed=(domain==null)!=_domainWasNull;
            if(domain!=null)
            {
                changed|=domain.Count!=_previousDomain.Count;
                for(int i=0;i<domain.Count && !changed;i++) changed=domain.GetBounds(i)!=_previousDomain.GetBounds(i);
                if(changed) _previousDomain.CopyFrom(domain);
            }
            if(changed) _epoch++;
            _domainWasNull=domain==null;
        }
        internal void ObserveFire(LiquidCellFireSample[] samples,int count)
        {
            for(int i=0;i<count;i++)
            {
                var cell=_liquid.WorldCellToLiquidCell(samples[i].GlobalCell);
                Enqueue(new MaterialConversionAddress(cell,MaterialId.Water));
                Enqueue(new MaterialConversionAddress(cell,MaterialId.Sticky));
            }
        }
        internal void Step(float deltaTime)
        {
            using var scope=Marker.Auto();
            ScanEnvironmentCandidates(); ProcessedLastTick=0;
            int work=Math.Min(_count,_profile.CandidatesPerTick);
            for(int i=0;i<work;i++)
            {
                int index=_queue[_head]; _head=(_head+1)%_queue.Length; _count--;
                Candidate candidate=_candidates[index]; ProcessedLastTick++;
                if(!_port.Read(candidate.Address,out uint quantity)) { Release(index); continue; }
                bool active=_world.IsConversionAddressActive(candidate.Address);
                // 域改变或冻结时丢弃墙钟差值，保留已累计的 Credit/Cooldown；回来不补算离开的时间。
                double elapsed=active && candidate.Epoch==_epoch?Math.Min(.5,Math.Max(0,_clock-candidate.LastTime)):active?deltaTime:0;
                candidate.LastTime=_clock; candidate.Epoch=_epoch;
                if(!active) { _candidates[index]=candidate; Push(index); continue; }
                candidate.Cooldown=Math.Max(0,candidate.Cooldown-elapsed);
                var environment=EnvironmentAt(Position(candidate.Address));
                int ruleIndex=-1;
                for(int r=0;r<_profile.RuleCount;r++)
                {
                    var rule=_profile.GetRule(r);
                    if(rule.Source!=candidate.Address.Material || rule.Condition==MaterialConversionCondition.Volatile && !_liquid.IsVolatile(rule.Source)) continue;
                    if(MaterialConversionEvaluator.Matches(in rule,in environment)) { ruleIndex=r; break; }
                }
                if(candidate.Rule!=ruleIndex) { candidate.Credit=0; candidate.Rule=ruleIndex; }
                // 普通常温液体可以等 Fire/环境扫描重新唤醒；静止冰和稳定热蒸汽没有移动通知，
                // 必须留下有界条件检查，否则冷区升温后冰永远不融化。
                bool persistentCondition=candidate.Address.Material==MaterialId.Ice || candidate.Address.Material==MaterialId.Steam;
                if(ruleIndex<0 && candidate.Cooldown<=0 && !persistentCondition) { Release(index); continue; }
                if(ruleIndex>=0 && candidate.Cooldown<=0)
                {
                    var rule=_profile.GetRule(ruleIndex);
                    candidate.Credit=Math.Min(_liquid.CellCapacityGmu*2d,candidate.Credit+elapsed*rule.InputGmuPerSecond);
                    uint batches=MaterialConversionEvaluator.Batches(in rule,quantity,candidate.Credit);
                    bool freeze=rule.FirstProduct==MaterialId.Ice;
                    // 粗液体格采用保守阻挡；积足冻结量后提交整个纯水格，不能在剩余液体内插入冰墙。
                    if(freeze && ((ulong)batches*rule.InputUnits<quantity || quantity%rule.InputUnits!=0
                        || _liquid.ConversionOccupancy(candidate.Address.Cell)!=quantity)) batches=0;
                    if(freeze && batches>0) batches=quantity/rule.InputUnits;
                    if(_port.Backend(rule.FirstProduct)==MaterialSimulationBackendKind.GridLiquid)
                        batches=Math.Min(batches,(uint)_liquid.CellCapacityGmu/rule.InputUnits);
                    uint consume=batches*rule.InputUnits;
                    if(consume>0 && TryConvert(candidate.Address,consume,in rule,out int targetCount))
                    {
                        candidate.Credit-=consume;
                        for(int n=0;n<targetCount;n++) SetCooldown(_placements[n].Address,rule.CooldownSeconds);
                    }
                }
                if(_port.Read(candidate.Address,out _)) { _candidates[index]=candidate; Push(index); } else Release(index);
            }
            RecoverOverflow();
        }
        private void RecoverOverflow()
        {
            // 只有通知溢出才启用恢复游标；固定最多 128 槽/Tick，不反复重置而饿死后半段材料。
            for(int scan=0;scan<128 && _recover && _freeCount>0;scan++)
            {
                int limit=_recoveryBackend==0?_gas.ResidentCellCount:_recoveryBackend==1?(_ambientLiquidCount>0?_liquid.ConversionSlotCount:0):_solid.ResidentCellCount;
                if(_recoveryCursor>=limit)
                {
                    _recoveryCursor=0; _recoveryBackend++;
                    if(_recoveryBackend==3) { _recover=_recoverAgain; _recoverAgain=false; _recoveryBackend=0; }
                    continue;
                }
                int cursor=_recoveryCursor++;
                if(_recoveryBackend==1)
                { if(_liquid.TryGetConversionCell(cursor,out Vector3Int cell,out MaterialId material)) Enqueue(new MaterialConversionAddress(cell,material)); }
                else
                {
                    MaterialVolumeCell cell;
                    bool found=_recoveryBackend==0?_gas.TryGetCell(cursor,out cell):_solid.TryGetCell(cursor,out cell);
                    if(found && cell.Material!=MaterialId.ToxicVapor && cell.Material!=MaterialId.DriedSlime)
                        Enqueue(new MaterialConversionAddress(cell.Cell,cell.Material));
                }
            }
        }
        private void SetCooldown(MaterialConversionAddress address,float duration)
        {
            Enqueue(address);
            if(_indices.TryGetValue(address,out int index))
            { var candidate=_candidates[index]; candidate.Cooldown=Math.Max(candidate.Cooldown,duration); candidate.LastTime=_clock; candidate.Epoch=_epoch; _candidates[index]=candidate; }
        }
        private bool TryConvert(MaterialConversionAddress source,uint consume,in MaterialConversionRule rule,out int count,bool reaction=false)
        {
            count=0; if(!reaction && _commits>=_profile.CommitsPerTick) return false;
            bool solid=rule.FirstProduct==MaterialId.Ice || rule.FirstProduct==MaterialId.DriedSlime || source.Material==MaterialId.Ice;
            if(solid && _solidCommits>=1) return false;
            uint batches=consume/rule.InputUnits;
            if(!BuildProducts(source,rule.FirstProduct,batches*rule.FirstUnits,ref count)) return false;
            if(rule.SecondUnits>0 && !BuildProducts(source,rule.SecondProduct,batches*rule.SecondUnits,ref count)) return false;
            if(!_transaction.TryPrepare(source,consume,_placements,count) || !_transaction.Commit()) { DeferredTransactions++; return false; }
            if(!reaction) _commits++; if(solid) _solidCommits++; return true;
        }
        private bool BuildProducts(MaterialConversionAddress source,MaterialId target,uint amount,ref int count)
        {
            Vector3 position=Position(source);
            if(_port.Backend(target)==MaterialSimulationBackendKind.FixedSolid)
            {
                int scale=Mathf.RoundToInt(_liquid.CellSize/_world.ConversionCellSize);
                Vector3Int baseCell=_liquid.WorldCellToLiquidCell(ElementWorldCoordinates.WorldToGlobalCell(position,_world.ConversionOrigin,_world.ConversionCellSize))*scale;
                baseCell.y=Mathf.FloorToInt((_liquid.GetGameplayBaseHeight(source.Cell)-_world.ConversionOrigin.y)/_world.ConversionCellSize+.001f);
                int plane=scale*scale;
                for(int y=0;y<scale && amount>0;y++)
                {
                    uint layer=Math.Min(amount,_solid.CellCapacity*(uint)plane), quotient=layer/(uint)plane, remainder=layer%(uint)plane;
                    for(int n=0;n<plane;n++)
                    {
                        uint part=quotient+(n<remainder?1u:0u); if(part==0) continue;
                        if(count==_placements.Length) return false;
                        _placements[count++]=new MaterialConversionPlacement(baseCell+new Vector3Int(n%scale,y,n/scale),target,part);
                    }
                    amount-=layer;
                }
                return amount==0;
            }
            Vector3Int cell;
            if(_port.Backend(target)==MaterialSimulationBackendKind.GridGas)
            {
                // 蒸发/干燥从真实液体自由面上方出生，避免在水体底部生成气体或穿到远处。
                if(_port.Backend(source.Material)==MaterialSimulationBackendKind.GridLiquid)
                    position.y=_liquid.GetGameplayBaseHeight(source.Cell)+_liquid.GetTotalFill(source.Cell)*_liquid.CellSize+_world.ConversionCellSize*.25f;
                cell=ElementWorldCoordinates.WorldToGlobalCell(position,_world.ConversionOrigin,_world.ConversionCellSize);
                if(amount>_gas.CellCapacity) return false;
            }
            else
            {
                cell=_liquid.WorldCellToLiquidCell(ElementWorldCoordinates.WorldToGlobalCell(position,_world.ConversionOrigin,_world.ConversionCellSize));
                if(source.Material==MaterialId.Ice) cell+=Vector3Int.up; // 生成在原冰体上方，随后由既有重力/支撑自然落下。
                else if(_liquid.ConversionFreeCapacity(cell)<amount) cell+=Vector3Int.up;
            }
            if(count==_placements.Length) return false;
            _placements[count++]=new MaterialConversionPlacement(cell,target,amount); return true;
        }
        internal bool EvaporateReactionWater(Vector3Int sourceCell,uint amount)
        {
            // Extinguish 消耗的水也必须生成真实 Steam，沿用同一事务；失败交给旧反应回滚 Fire。
            var rule=new MaterialConversionRule {Source=MaterialId.Water,FirstProduct=MaterialId.Steam,InputUnits=1,FirstUnits=1,
                Condition=MaterialConversionCondition.Heated,InputGmuPerSecond=1,Threshold=70,CooldownSeconds=3};
            // 已有灭火接触沿用原来的 Fire Sample 预算，不因普通相变的 8 次额度而延迟灭火。
            if(!TryConvert(new MaterialConversionAddress(sourceCell,MaterialId.Water),amount,in rule,out int count,true)) return false;
            for(int i=0;i<count;i++) SetCooldown(_placements[i].Address,3); return true;
        }
        private Vector3 Position(MaterialConversionAddress address)
        {
            if(_port.Backend(address.Material)==MaterialSimulationBackendKind.GridLiquid)
                return new Vector3(_liquid.Origin.x+(address.Cell.x+.5f)*_liquid.CellSize,
                    _liquid.GetGameplayBaseHeight(address.Cell)+_world.ConversionCellSize*.25f,
                    _liquid.Origin.z+(address.Cell.z+.5f)*_liquid.CellSize);
            return _world.ConversionOrigin+((Vector3)address.Cell+Vector3.one*.5f)*_world.ConversionCellSize;
        }
        private MaterialEnvironmentSample EnvironmentAt(Vector3 position)
        {
            var sample=new MaterialEnvironmentSample(_profile.AmbientTemperature,_profile.AmbientDryness);
            int priority=int.MinValue;
            for(int i=0;i<_environmentCount;i++)
            {
                var zone=_environments[i]; if(zone==null || !zone.isActiveAndEnabled || !zone.Bounds.Contains(position)) continue;
                if(zone.Priority>=priority) { sample=zone.Sample; priority=zone.Priority; }
            }
            Vector3Int cell=ElementWorldCoordinates.WorldToGlobalCell(position,_world.ConversionOrigin,_world.ConversionCellSize);
            bool heated=false;
            for(int n=0;n<7 && !heated;n++)
            {
                Vector3Int offset=n==0?Vector3Int.zero:n==1?Vector3Int.up:n==2?Vector3Int.down:n==3?Vector3Int.left:n==4?Vector3Int.right:n==5?Vector3Int.forward:Vector3Int.back;
                heated=_world.TryGetAmount(cell+offset,MaterialId.Fire,out byte fire) && fire>0;
            }
            return heated?new MaterialEnvironmentSample(Math.Max(100,sample.Temperature),Math.Max(.8f,sample.Dryness),sample.VolatileEnabled):sample;
        }
        private void ScanEnvironmentCandidates()
        {
            // 新放冷/干燥区要能找到已经 Solver-Sleeping 的水。只轮转 Authoring 覆盖的粗格，最多 32 格/Tick。
            for(int scan=0;scan<32 && _environmentCount>0;scan++)
            {
                _zoneCursor%=_environmentCount;
                var zone=_environments[_zoneCursor];
                if(zone==null || !zone.isActiveAndEnabled) { _zoneCursor++; _zoneCell=0; continue; }
                Vector3Int min=Vector3Int.FloorToInt((zone.Bounds.min-_liquid.Origin)/_liquid.CellSize);
                Vector3Int max=Vector3Int.CeilToInt((zone.Bounds.max-_liquid.Origin)/_liquid.CellSize);
                Vector3Int size=max-min; long cells=(long)size.x*size.y*size.z;
                if(cells<=0 || _zoneCell>=cells) { _zoneCell=0; _zoneCursor++; continue; }
                int offset=_zoneCell++;
                Vector3Int cell=min+new Vector3Int(offset%size.x,offset/size.x%size.y,offset/(size.x*size.y));
                var water=new MaterialConversionAddress(cell,MaterialId.Water);
                var sticky=new MaterialConversionAddress(cell,MaterialId.Sticky);
                var poison=new MaterialConversionAddress(cell,MaterialId.Poison);
                if(_port.Read(water,out _)) Enqueue(water);
                if(_port.Read(sticky,out _)) Enqueue(sticky);
                if(_liquid.IsVolatile(MaterialId.Poison) && _port.Read(poison,out _)) Enqueue(poison);
            }
        }
    }
}
