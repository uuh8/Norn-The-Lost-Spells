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
            private int _editedCount, _scan, _wakePage, _wakeCell, _recover;
            private ulong _tick, _domainEpoch;
            private double _elapsed;
            private readonly Bounds[] _lastBounds = new Bounds[ElementWorldActivityRegions.MaximumGpuRegions];
            private int _lastCount = -2;

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
                    if (page.Cells.Count==0)
                    {
                        int last=_chunks.Count-1; var end=_chunks[last];
                        _chunks[page.Index]=end; end.Index=page.Index;
                        _chunks.RemoveAt(last); _byChunk.Remove(page.Key); _free.Push(page);
                    }
                    else if (page.Dense!=0 || page.Quantity>MaximumThinTotal) page.EligibleSince=-1;
                }
                _editedCount=0;
            }

            internal void Begin(float dt,ElementWorldActivityRegions regions,bool hasVents)
            {
                if (!float.IsFinite(dt) || dt<=0) throw new ArgumentOutOfRangeException(nameof(dt));
                _dt=dt; _elapsed+=dt; _tick++; _domain=regions; _hasVents=hasVents;
                _gasWork=_profile.MaximumCellsPerTick;
                int count=regions?.Count??-1;
                bool changed=count!=_lastCount;
                for (int i=0;!changed && i<count;i++) changed=!_lastBounds[i].Equals(regions.GetBounds(i));
                if (changed)
                {
                    for (int i=0;i<count;i++) _lastBounds[i]=regions.GetBounds(i);
                    _lastCount=count;_domainEpoch++;_recover=_byCell.Count;
                }
                // 圈域变化或可移动的真实通风口只检查已有气体，固定预算轮换，不遍历空气或世界总体积。
                int work=Math.Min(_profile.MaximumCellsPerTick,hasVents?_byCell.Count:_recover);
                for (int n=0;n<work && _chunks.Count!=0;n++)
                {
                    if (_wakePage>=_chunks.Count) { _wakePage=0; _wakeCell=0; }
                    var page=_chunks[_wakePage];
                    if (_wakeCell>=page.Cells.Count) { _wakeCell=0; _wakePage=(_wakePage+1)%_chunks.Count; page=_chunks[_wakePage]; }
                    var cell=page.Cells[_wakeCell++];
                    bool recover=_recover>0; if (recover) _recover--;
                    if (Active(cell) && (recover || _vent!=null && (_vent(Center(cell),MaterialId.Steam)>0 ||
                        _vent(Center(cell),MaterialId.ToxicVapor)>0))) _world.Wake(cell);
                }
            }

            private bool WholeChunkActive(Vector3Int key)
            {
                if (_domain==null) return true;
                Vector3 minimum=_origin+new Vector3((float)((double)key.x*_edge+.5),(float)((double)key.y*_edge+.5),
                    (float)((double)key.z*_edge+.5))*_size;
                Vector3 maximum=minimum+Vector3.one*((_edge-1)*_size);
                for (int i=0;i<_domain.Count;i++)
                {
                    var b=_domain.GetBounds(i); if (b.Contains(minimum) && b.Contains(maximum)) return true;
                }
                return false;
            }

            internal void ClearDilute()
            {
                if (_profile.DiluteMaximumCellGmu==0 || _dt<=0) return;
                int pages=Math.Min(_profile.DiluteChunksPerTick,_chunks.Count), cells=_profile.MaximumCellsPerTick;
                for (int i=0;i<pages && cells>0 && _chunks.Count!=0;i++)
                {
                    if (_scan>=_chunks.Count) _scan=0;
                    var page=_chunks[_scan++];
                    bool continuous=page.DomainEpoch==_domainEpoch || page.LastCheck+1==_tick;
                    page.DomainEpoch=_domainEpoch; page.LastCheck=_tick;
                    if (page.Dense!=0 || page.Quantity>MaximumThinTotal || !WholeChunkActive(page.Key))
                    { page.EligibleSince=-1; continue; }
                    // 区域在两次检查之间曾改变且无法证明全程活动时，重新计时。Frozen 不调用 Begin，
                    // 不会在恢复那一帧补算离场或暂停的秒数。
                    if (page.EligibleSince<0 || !continuous) { page.EligibleSince=_elapsed; continue; }
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
