using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementCellSimulation
    {
        private sealed partial class GasEnvironment
        {
            private sealed class GasChunk
            {
                internal Vector3Int Key;
                internal int Index, Dense;
                internal ulong Quantity, DomainEpoch, LastCheck;
                internal double EligibleSince = -1;
                internal GasChunk PreviousThin, NextThin;
                internal bool ThinCandidate;
                internal readonly List<Vector3Int> Cells = new List<Vector3Int>();
                // 仅为此页曾实际驻留的气格增长，每格两个物种各六个面加一个通风口。
                // 空页进池保留数组；纯沙水的世界不分配这些余额，也不随普通槽回收清数组。
                internal double[] Credits = Array.Empty<double>();
            }

            private readonly Dictionary<Vector3Int,GasChunk> _byChunk = new Dictionary<Vector3Int,GasChunk>();
            private readonly Dictionary<Vector3Int,int> _byCell = new Dictionary<Vector3Int,int>();
            private readonly List<GasChunk> _chunks = new List<GasChunk>();
            private readonly Stack<GasChunk> _free = new Stack<GasChunk>();
            // 一笔 CA 事务最多涉及中心和六个面。最后同审各 Chunk，避免同块平移在一帧内
            // 暂时“变空又新生”，或把两次写入之间的临时浓度误当成浓雾而重置计时。
            private readonly GasChunk[] _edited = new GasChunk[7];
            private int _editedCount, _wakePage, _wakeCell, _recover, _wakeBudget, _thinCount;
            private GasChunk _firstThin, _lastThin;
            private ulong _tick, _domainEpoch;
            private double _elapsed;
            private readonly Bounds[] _lastBounds = new Bounds[ElementWorldActivityRegions.MaximumGpuRegions];
            private int _lastCount = -1;
            private Bounds[] _recoveryAreas;
            private int _recoveryAreaCount;
            private bool _recoverAll;

            private Vector3Int ChunkOf(Vector3Int cell) =>
                new Vector3Int(Divide(cell.x,_edge),Divide(cell.y,_edge),Divide(cell.z,_edge));

            internal void Observe(Vector3Int cell,in Contents previous,in Contents next)
            {
                uint before=previous.Steam+previous.Vapor, after=next.Steam+next.Vapor;
                var key=ChunkOf(cell);
                if (!_byChunk.TryGetValue(key,out var page))
                {
                    page=_free.Count==0?new GasChunk():_free.Pop();
                    page.Key=key; page.Index=_chunks.Count; page.Dense=0; page.Quantity=0;
                    page.EligibleSince=-1; page.LastCheck=0;
                    _byChunk.Add(key,page); _chunks.Add(page);
                }
                if (before==0)
                {
                    int index=page.Cells.Count;
                    if (index>=page.Credits.Length/14) Array.Resize(ref page.Credits,checked(Math.Max(1,index*2)*14));
                    else Array.Clear(page.Credits,index*14,14);
                    _byCell.Add(cell,index); page.Cells.Add(cell);
                }
                else if (after==0)
                {
                    int index=_byCell[cell], last=page.Cells.Count-1;
                    // 恢复游标仍在轮转时，换尾删除可能把尚未访问的旧坐标换到已查区；补足一圈但保留游标进度。
                    if (_recover > 0 && (page.Index < _wakePage || page.Index == _wakePage && index < _wakeCell))
                        _recover = Math.Max(_recover, _byCell.Count);
                    var end=page.Cells[last]; page.Cells[index]=end; _byCell[end]=index;
                    if (index!=last) Array.Copy(page.Credits,last*14,page.Credits,index*14,14);
                    page.Cells.RemoveAt(last); _byCell.Remove(cell);
                }
                else
                {
                    int start=_byCell[cell]*14;
                    if (previous.Steam!=0 && next.Steam==0) Array.Clear(page.Credits,start,7);
                    if (previous.Vapor!=0 && next.Vapor==0) Array.Clear(page.Credits,start+7,7);
                }
                uint max=_profile.DiluteMaximumCellGmu;
                page.Dense+=(after>max?1:0)-(before>max?1:0);
                page.Quantity=page.Quantity-before+after;
                for (int i=0;i<_editedCount;i++) if (_edited[i]==page) return;
                _edited[_editedCount++]=page;
            }

            private static int Divide(int n,int d) => n%d<0?n/d-1:n/d;
            private double MaximumThinTotal => Math.Floor((double)_edge*_edge*_edge*
                _profile.CellCapacityGmu*_profile.DiluteMaximumChunkConcentration);

            internal void FinishEdits()
            {
                for (int i=0;i<_editedCount;i++)
                {
                    var page=_edited[i]; _edited[i]=null;
                    bool canDilute=_profile.DiluteMaximumCellGmu!=0 && page.Cells.Count!=0 &&
                        page.Dense==0 && page.Quantity<=MaximumThinTotal;
                    if (canDilute) AddThin(page);
                    else { RemoveThin(page); page.EligibleSince=-1; }
                    if (page.Cells.Count==0)
                    {
                        int last=_chunks.Count-1; var end=_chunks[last];
                        _chunks[page.Index]=end; end.Index=page.Index;
                        _chunks.RemoveAt(last); _byChunk.Remove(page.Key); _free.Push(page);
                    }
                }
                _editedCount=0;
            }

            // 质量/局部峰值只随提交改变，用独立侵入式队列维护资格。一个新薄雾页无需等待远处数千
            // 个静止浓气页轮流做阈值检查。页被压浓或回收时 O(1) 脱链，稳定浓气没有 Tick 工作。
            private void AddThin(GasChunk page)
            {
                if (page.ThinCandidate) return;
                page.ThinCandidate=true; page.PreviousThin=_lastThin; page.NextThin=null;
                if (_lastThin==null) _firstThin=page; else _lastThin.NextThin=page;
                _lastThin=page; _thinCount++;
            }

            private void RemoveThin(GasChunk page)
            {
                if (!page.ThinCandidate) return;
                if (page.PreviousThin==null) _firstThin=page.NextThin; else page.PreviousThin.NextThin=page.NextThin;
                if (page.NextThin==null) _lastThin=page.PreviousThin; else page.NextThin.PreviousThin=page.PreviousThin;
                page.PreviousThin=page.NextThin=null; page.ThinCandidate=false; _thinCount--;
            }

            internal void Begin(float dt,ElementWorldActivityRegions regions,bool hasVents,bool hasVentBounds)
            {
                if (!float.IsFinite(dt) || dt<=0) throw new ArgumentOutOfRangeException(nameof(dt));
                _dt=dt; _elapsed+=dt; _tick++; _domain=regions;
                _gasWork=_wakeBudget=_profile.MaximumCellsPerTick;
                // 没有几何回调的独立调用方在“首次出现通风口”时恢复一次。正式世界直接给出发生变化的入口 Bounds。
                if (hasVents && !_hasVents && !hasVentBounds) RequestGasRecovery();
                _hasVents=hasVents;
                int reserved = _recover > 0 ? Math.Max(1,_wakeBudget/2) : 0;
                _wakeBudget -= reserved;
                int count=regions?.Count??-1;
                bool changed=count!=_lastCount;
                for (int i=0;i<count;i++)
                {
                    Bounds current=regions.GetBounds(i);
                    if (i < _lastCount && _lastBounds[i].Equals(current)) continue;
                    changed=true;
                    // 从全域收小活动范围，或一个范围只向内收缩，都没有新进入的休眠气格。
                    if (_lastCount >= 0)
                    {
                        if (i >= _lastCount) WakeInBounds(current);
                        else WakeAddedVolume(_lastBounds[i], current);
                    }
                    _lastBounds[i]=current;
                }
                if (changed)
                {
                    if (count < 0) RequestGasRecovery();
                    _lastCount=count; _domainEpoch++;
                }
                RecordActivityCoverage(regions,changed,dt);
                _wakeBudget+=reserved;
                int work=Math.Min(_wakeBudget,_recover);
                for (int n=0;n<work && _chunks.Count!=0;n++)
                {
                    if (_wakePage>=_chunks.Count) { _wakePage=0; _wakeCell=0; }
                    var page=_chunks[_wakePage];
                    if (_wakeCell>=page.Cells.Count) { _wakeCell=0; _wakePage=(_wakePage+1)%_chunks.Count; page=_chunks[_wakePage]; }
                    var cell=page.Cells[_wakeCell++];
                    _recover--; _wakeBudget--;
                    if (Active(cell) && IsRecoveryTarget(cell)) _world.Wake(cell);
                }
                if (_chunks.Count==0) _recover=0;
                if (_recover==0) { _recoverAll=false; _recoveryAreaCount=0; }
            }

            private void RequestGasRecovery()
            { _recover=Math.Max(_recover,_byCell.Count); _recoverAll=true; _recoveryAreaCount=0; }

            private void RequestGasRecovery(Bounds area)
            {
                _recover=Math.Max(_recover,_byCell.Count);
                if (_recoverAll) return;
                if (_recoveryAreas==null) _recoveryAreas=new Bounds[ElementWorldActivityRegions.MaximumGpuRegions];
                for (int i=0;i<_recoveryAreaCount;i++)
                    if (_recoveryAreas[i].Contains(area.min) && _recoveryAreas[i].Contains(area.max)) return;
                if (_recoveryAreaCount<_recoveryAreas.Length) _recoveryAreas[_recoveryAreaCount++]=area;
                else _recoveryAreas[_recoveryAreaCount-1].Encapsulate(area);
            }

            private bool IsRecoveryTarget(Vector3Int cell)
            {
                if (_recoverAll) return true;
                var center=Center(cell);
                for (int i=0;i<_recoveryAreaCount;i++) if (_recoveryAreas[i].Contains(center)) return true;
                return false;
            }

            private void WakeAddedVolume(Bounds previous,Bounds current)
            {
                Vector3 low=current.min, high=current.max;
                Vector3 sharedLow=Vector3.Max(low,previous.min), sharedHigh=Vector3.Min(high,previous.max);
                if (sharedLow.x>=sharedHigh.x || sharedLow.y>=sharedHigh.y || sharedLow.z>=sharedHigh.z)
                { WakeInBounds(current); return; }
                // 活动范围随角色小幅平移时，只需唤醒新进入的一侧。交集依次沿三轴裁掉，所得薄片互不争工作。
                for (int axis=0;axis<3;axis++)
                {
                    if (low[axis]<sharedLow[axis])
                    {
                        Vector3 end=high; end[axis]=sharedLow[axis]; Bounds slab=default;
                        slab.SetMinMax(low,end); WakeInBounds(slab); low[axis]=sharedLow[axis];
                    }
                    if (high[axis]>sharedHigh[axis])
                    {
                        Vector3 start=low; start[axis]=sharedHigh[axis]; Bounds slab=default;
                        slab.SetMinMax(start,high); WakeInBounds(slab); high[axis]=sharedHigh[axis];
                    }
                }
            }

            internal void WakeInBounds(Bounds bounds)
            {
                if (_chunks.Count==0) return;
                if (!FluidSpawnRequestValidator.IsFinite(bounds.min) || !FluidSpawnRequestValidator.IsFinite(bounds.max)) return;
                Vector3Int low=WakeCellAt(bounds.min,true), high=WakeCellAt(bounds.max,false);
                if (low.x>high.x || low.y>high.y || low.z>high.z) return;
                low=ChunkOf(low); high=ChunkOf(high);
                double pageCount=((long)high.x-low.x+1d)*((long)high.y-low.y+1d)*((long)high.z-low.z+1d);
                // 小入口直接查询重叠的稀疏页。大范围、同帧大量入口移动或稠密整页用有界轮转继续，
                // 不遍历未分配空间。静止的通风口不调用此方法，稳定气体只会因真实环境变化重新唤醒。
                if (pageCount>_wakeBudget || pageCount>Math.Max(8,_chunks.Count)) { RequestGasRecovery(bounds); return; }
                for (long z=low.z;z<=high.z;z++) for (long y=low.y;y<=high.y;y++) for (long x=low.x;x<=high.x;x++)
                {
                    if (_wakeBudget <= 0) { RequestGasRecovery(bounds); return; }
                    _wakeBudget--;
                    if (!_byChunk.TryGetValue(new Vector3Int((int)x,(int)y,(int)z),out var page)) continue;
                    foreach (var cell in page.Cells)
                    {
                        if (_wakeBudget<=0) { RequestGasRecovery(bounds); return; }
                        _wakeBudget--;
                        if (bounds.Contains(Center(cell)) && Active(cell)) _world.Wake(cell);
                    }
                }
            }

            private Vector3Int WakeCellAt(Vector3 position,bool minimum) => new Vector3Int(WakeAxis(position.x,_origin.x,minimum),
                WakeAxis(position.y,_origin.y,minimum),WakeAxis(position.z,_origin.z,minimum));
            private int WakeAxis(float coordinate,float origin,bool minimum)
            {
                double coordinateInCells=((double)coordinate-origin)/_size-.5d;
                double cell=minimum?Math.Ceiling(coordinateInCells):Math.Floor(coordinateInCells);
                return (int)Math.Max(int.MinValue,Math.Min(int.MaxValue,cell));
            }

            private void ChunkCenters(Vector3Int key,out Vector3 minimum,out Vector3 maximum)
            {
                minimum=_origin+new Vector3((float)((double)key.x*_edge+.5),(float)((double)key.y*_edge+.5),
                    (float)((double)key.z*_edge+.5))*_size;
                maximum=minimum+Vector3.one*((_edge-1)*_size);
            }

            private static bool ContainsChunk(Bounds b,Vector3 minimum,Vector3 maximum)
            {
                var low=b.min; var high=b.max;
                return low.x<=minimum.x && low.y<=minimum.y && low.z<=minimum.z &&
                       maximum.x<high.x && maximum.y<high.y && maximum.z<high.z;
            }

            private bool WholeChunkActive(Vector3Int key)
            {
                if (_domain==null) return true;
                ChunkCenters(key,out var minimum,out var maximum);
                for (int i=0;i<_domain.Count;i++)
                {
                    if (ContainsChunk(_domain.GetBounds(i),minimum,maximum)) return true;
                }
                return false;
            }

            internal void ClearDilute()
            {
                if (_profile.DiluteMaximumCellGmu==0 || _dt<=0) return;
                int pages=Math.Min(_profile.DiluteChunksPerTick,_thinCount), cells=_profile.MaximumCellsPerTick;
                for (int i=0;i<pages && cells>0 && _firstThin!=null;i++)
                {
                    var page=_firstThin;
                    if (_thinCount>1) { RemoveThin(page); AddThin(page); }
                    bool continuous=page.DomainEpoch==_domainEpoch || page.LastCheck+1==_tick;
                    page.DomainEpoch=_domainEpoch; page.LastCheck=_tick;
                    if (page.Dense!=0 || page.Quantity>MaximumThinTotal || !WholeChunkActive(page.Key))
                    { page.EligibleSince=-1; continue; }
                    // 区域在两次检查之间曾改变且无法证明全程活动时，重新计时。Frozen 不调用 Begin，
                    // 不会在恢复那一帧补算离场或暂停的秒数。
                    if (page.EligibleSince<0 || !continuous && !ContinuouslyCoveredForDelay(page.Key))
                    { page.EligibleSince=_elapsed; continue; }
                    if (_elapsed-page.EligibleSince<_profile.DiluteDelaySeconds) continue;
                    while (cells>0 && page.Cells.Count!=0)
                    {
                        cells--; var cell=page.Cells[page.Cells.Count-1]; var value=_world.Read(cell);
                        if (!Dissipate(cell,MaterialId.Steam,value.Steam) || !Dissipate(cell,MaterialId.ToxicVapor,value.Vapor)) break;
                    }
                }
            }

            private bool Dissipate(Vector3Int cell,MaterialId material,uint quantity)
            {
                if (quantity==0) return true;
                if (!_world.TryRemove(cell,material,quantity)) return false;
                Dissipated[(byte)material]+=quantity; _consume?.Invoke(material,quantity); return true;
            }
        }
    }
}
