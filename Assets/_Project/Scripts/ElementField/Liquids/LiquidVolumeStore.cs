using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 固定容量的 Sparse Chunk：初始化时预留数组；新增液体页只取空闲槽。
    /// Amount 与 Material 分开存储，水／毒／粘液共用一格的容量并保留各自身份。
    /// </summary>
    public sealed class LiquidVolumeStore : ILiquidQuantityReadOnly
    {
        private readonly struct PlannedTerrainMove
        {
            public readonly Vector3Int Target;
            public readonly MaterialId Material;
            public readonly uint Quantity;

            public PlannedTerrainMove(Vector3Int target, MaterialId material, uint quantity)
            {
                Target = target;
                Material = material;
                Quantity = quantity;
            }
        }

        private sealed class Chunk
        {
            internal ElementChunkKey Key;
            internal bool IsResident;
            internal readonly ushort[] Amounts;
            internal readonly bool[] Solids;
            internal readonly uint[] MaterialTotals;
            internal readonly uint[] MaterialSurfaceVersions;
            internal readonly bool[] NeighborhoodMaterials;
            internal uint NeighborhoodCandidateVersion=uint.MaxValue;
            internal uint Version;

            internal Chunk(int cellCount, int materialCount)
            {
                Amounts = new ushort[checked(cellCount * materialCount)];
                Solids = new bool[cellCount];
                MaterialTotals = new uint[materialCount];
                MaterialSurfaceVersions = new uint[materialCount];
                NeighborhoodMaterials=new bool[materialCount];
            }
        }

        private readonly Dictionary<ElementChunkKey, int> _indices;
        private readonly Dictionary<Vector3Int, bool> _solidCache;
        private readonly Vector3Int[] _solidCacheKeys;
        private readonly Chunk[] _pool;
        private Chunk _locatedChunk;
        private readonly int[] _materialSlots = new int[byte.MaxValue + 1];
        private readonly MaterialId[] _materials;
        private readonly float[] _relativeDensities;
        private readonly uint[] _candidateVersions;
        private readonly uint[] _materialSurfaceVersions;
        private readonly int[] _reactionScanPositions;
        private readonly int _chunkSize, _cellCount, _capacity, _materialCount;
        private readonly Func<Vector3Int, bool> _solidSampler;
        private readonly int _solidCacheCapacity;
        private Func<Vector3Int, uint> _externalOccupancy;
        private Action<Vector3Int, bool> _dynamicOccupancyChanged;
        private Action<Vector3Int> _beforeCellChanged;
        public void SetBeforeCellChanged(Action<Vector3Int> callback) => _beforeCellChanged = callback;
        private int _used;
        private int _solidCacheNext;
        private uint _surfaceVersion;
        private uint _staticVersion;

        public LiquidVolumeStore(int chunkSize, int maximumChunks, int capacityGmu,
            MaterialId[] materials, Func<Vector3Int, bool> solidSampler, float[] relativeDensities = null)
        {
            if (chunkSize < 1 || chunkSize > ushort.MaxValue || maximumChunks < 1
                || capacityGmu < 1 || capacityGmu > ushort.MaxValue
                || materials == null || materials.Length == 0 || solidSampler == null
                || relativeDensities!=null && relativeDensities.Length!=materials.Length)
                throw new ArgumentOutOfRangeException(nameof(maximumChunks));
            _chunkSize = chunkSize;
            _cellCount = checked(chunkSize * chunkSize * chunkSize);
            _capacity = capacityGmu;
            _materialCount = materials.Length;
            _materials = (MaterialId[])materials.Clone();
            _relativeDensities = new float[materials.Length];
            for(int i=0;i<materials.Length;i++)
            {
                float density=relativeDensities != null ? relativeDensities[i] : 1f;
                if(!float.IsFinite(density) || density<=0f)
                    throw new ArgumentOutOfRangeException(nameof(relativeDensities));
                _relativeDensities[i]=density;
            }
            _candidateVersions = new uint[_materialCount];
            _materialSurfaceVersions = new uint[_materialCount];
            _solidSampler = solidSampler;
            Array.Fill(_materialSlots, -1);
            for (int i = 0; i < materials.Length; i++)
            {
                int material = (byte)materials[i];
                if (material == 0 || _materialSlots[material] != -1)
                    throw new ArgumentException("液体材料必须唯一且非空。", nameof(materials));
                _materialSlots[material] = i;
            }
            _indices = new Dictionary<ElementChunkKey, int>(maximumChunks);
            _solidCacheCapacity = Math.Min(checked(maximumChunks * _cellCount), 65536);
            _solidCache = new Dictionary<Vector3Int, bool>(_solidCacheCapacity);
            _solidCacheKeys = new Vector3Int[_solidCacheCapacity];
            _pool = new Chunk[maximumChunks];
            _reactionScanPositions = new int[materials.Length];
            // 热路径新增 Chunk 不能分配托管数组；代价是启动期保留明确的内存上限。
            for (int i = 0; i < maximumChunks; i++)
                _pool[i] = new Chunk(_cellCount, _materialCount);
        }

        public int ChunkSize => _chunkSize;
        public int CapacityGmu => _capacity;
        public int ResidentChunkCount => _used;
        public int ResidentCellCount => _used * _cellCount;
        internal int ConversionSlotCount => _used*_cellCount*_materialCount;
        internal bool TryGetConversionCell(int cursor,out Vector3Int cell,out MaterialId material)
        {
            cell=default; material=MaterialId.Empty;
            if((uint)cursor>=(uint)ConversionSlotCount) return false;
            int perChunk=_cellCount*_materialCount, slot=cursor/perChunk, local=cursor%perChunk/_materialCount, m=cursor%_materialCount;
            if(_pool[slot].Amounts[local*_materialCount+m]==0) return false;
            cell=ElementWorldCoordinates.ComposeGlobalCell(_pool[slot].Key,new Vector3Int(local%_chunkSize,local/_chunkSize%_chunkSize,local/(_chunkSize*_chunkSize)),_chunkSize);
            material=_materials[m]; return true;
        }
        public int MaximumChunks => _pool.Length;
        public int MaximumSurfaceCandidateKeys => _pool.Length;
        public uint SurfaceVersion => _surfaceVersion;
        public uint StaticVersion => _staticVersion;
        public ulong TotalQuantity { get; private set; }
        private Func<Vector3Int,uint> _materialSolidOccupancy;
        internal void SetMaterialSolidOccupancy(Func<Vector3Int,uint> reader) => _materialSolidOccupancy=reader;
        internal bool HasMaterialSolidOccupancy(Vector3Int cell) => (_materialSolidOccupancy?.Invoke(cell) ?? 0)>0;
        internal ulong MaterialQuantity(MaterialId material)
        { int slot=_materialSlots[(byte)material]; if(slot<0) return 0; ulong total=0; for(int i=0;i<_used;i++) total+=_pool[i].MaterialTotals[slot]; return total; }
        public ulong PendingQuantity => 0;

        public bool Supports(MaterialId material) => _materialSlots[(byte)material] >= 0;
        public bool HasResidentChunk(ElementChunkKey key) => _indices.ContainsKey(key);
        public void SetExternalOccupancy(Func<Vector3Int, uint> occupancy) =>
            _externalOccupancy = occupancy;
        public void SetDynamicOccupancyChangedCallback(Action<Vector3Int, bool> callback) =>
            _dynamicOccupancyChanged = callback;

        public bool HasAnyLiquid(Vector3Int cell)
        {
            if (!TryLocate(cell, out Chunk chunk, out int index)) return false;
            int offset = index * _materialCount;
            for (int i = 0; i < _materialCount; i++)
                if (chunk.Amounts[offset + i] != 0) return true;
            return false;
        }

        public bool TryRelocateCellForStaticSolid(Vector3Int source, int maxVisitedCells,
            List<Vector3Int> movedTargets)
        {
            if (maxVisitedCells < 1 || movedTargets == null || IsSolid(source)) return false;
            movedTargets.Clear();
            // 地形编辑是离散事件：先按材料规划整格数量，再沿可通行的相邻格
            // 寻找容量。满湖的一格可拆到多个自由面格，不能只尝试一个邻格。
            var remaining = new uint[_materialCount];
            uint left = 0;
            for (int i = 0; i < _materialCount; i++)
            {
                TryGetQuantity(source, _materials[i], out remaining[i]);
                left += remaining[i];
            }
            if (left == 0) return true;
            var plan = new List<PlannedTerrainMove>(_capacity);
            var visited = new HashSet<Vector3Int>(maxVisitedCells) { source };
            var frontier = new Queue<Vector3Int>(maxVisitedCells);
            frontier.Enqueue(source);
            while (frontier.Count > 0 && visited.Count <= maxVisitedCells && left > 0)
            {
                Vector3Int current = frontier.Dequeue();
                for (int side = 0; side < 6 && left > 0; side++)
                {
                    Vector3Int next = current + (side == 0 ? Vector3Int.up
                        : side == 1 ? Vector3Int.left
                        : side == 2 ? Vector3Int.right
                        : side == 3 ? Vector3Int.back
                        : side == 4 ? Vector3Int.forward : Vector3Int.down);
                    if (visited.Contains(next)) continue;
                    if (visited.Count == maxVisitedCells) break;
                    visited.Add(next);
                    if (IsSolid(next)) continue;
                    frontier.Enqueue(next);
                    int free = FreeCapacity(next);
                    if (free == 0) continue;
                    for (int i = 0; i < _materialCount && free > 0; i++)
                    {
                        uint amount = Math.Min(remaining[i], (uint)free);
                        if (amount == 0) continue;
                        plan.Add(new PlannedTerrainMove(next, _materials[i], amount));
                        remaining[i] -= amount;
                        free -= (int)amount;
                        left -= amount;
                    }
                }
            }
            if (left != 0) return false;
            for (int i = 0; i < plan.Count; i++)
            {
                PlannedTerrainMove move = plan[i];
                if (TryTransfer(source, move.Target, move.Material, move.Quantity))
                {
                    movedTargets.Add(move.Target);
                    continue;
                }
                // 预检后的意外失败必须反向搬回已提交的全部材料；地形仍未改变。
                for (int undo = i - 1; undo >= 0; undo--)
                {
                    PlannedTerrainMove previous = plan[undo];
                    if (!TryTransfer(previous.Target, source,
                        previous.Material, previous.Quantity))
                        throw new InvalidOperationException("静态格排水事务回滚失败。");
                }
                movedTargets.Clear();
                return false;
            }
            return true;
        }

        public bool HasOtherMaterial(Vector3Int cell, MaterialId material)
        {
            // 回压缩只可收走原材料；混合格里的 Poison/Sticky 仍由动态 Store 持有。
            int selected = _materialSlots[(byte)material];
            if (!TryLocate(cell, out Chunk chunk, out int index)) return false;
            int offset = index * _materialCount;
            for (int i = 0; i < _materialCount; i++)
                if (i != selected && chunk.Amounts[offset + i] != 0) return true;
            return false;
        }

        public Vector3Int GetResidentCellAt(int flatIndex)
        {
            int chunkIndex = flatIndex / _cellCount;
            int cellIndex = flatIndex % _cellCount;
            ElementChunkKey key = _pool[chunkIndex].Key;
            return new Vector3Int(key.X * _chunkSize + cellIndex % _chunkSize,
                key.Y * _chunkSize + cellIndex / _chunkSize % _chunkSize,
                key.Z * _chunkSize + cellIndex / (_chunkSize * _chunkSize));
        }

        public bool HasResidentLiquidAt(int flatIndex)
        {
            Chunk chunk = _pool[flatIndex / _cellCount];
            int offset = (flatIndex % _cellCount) * _materialCount;
            for (int material = 0; material < _materialCount; material++)
                if (chunk.Amounts[offset + material] > 0) return true;
            return false;
        }

        public bool TryGetQuantity(Vector3Int globalCell, MaterialId material, out uint quantity)
        {
            int slot = _materialSlots[(byte)material];
            quantity = 0;
            if (slot < 0 || !TryLocate(globalCell, out Chunk chunk, out int index)) return false;
            quantity = chunk.Amounts[index * _materialCount + slot];
            return quantity > 0;
        }

        public bool IsDominantMaterial(Vector3Int cell, MaterialId material)
        {
            int selected = _materialSlots[(byte)material];
            if (selected < 0 || !TryLocate(cell, out Chunk chunk, out int index)) return false;
            int offset = index * _materialCount;
            int amount = chunk.Amounts[offset + selected];
            if (amount == 0) return false;
            // 相等时按 Profile 顺序选一个 owner，避免三层透明面叠画闪烁。
            for (int i = 0; i < _materialCount; i++)
                if (chunk.Amounts[offset + i] > amount
                    || (i < selected && chunk.Amounts[offset + i] == amount)) return false;
            return true;
        }

        public bool TryGetAmount(Vector3Int globalCell, MaterialId material, out byte amount)
        {
            bool found = TryGetQuantity(globalCell, material, out uint quantity);
            amount = (byte)Math.Min(byte.MaxValue, quantity);
            return found;
        }

        public bool IsSolid(Vector3Int globalCell)
        {
            if (TryLocate(globalCell, out Chunk chunk, out int index)) return chunk.Solids[index];
            if (_solidCache.TryGetValue(globalCell, out bool solid)) return solid;
            solid = _solidSampler(globalCell);
            // 缓存达到上限后循环替换旧坐标；否则大场景运行一段时间后所有新位置
            // 都会反复执行 Physics.CheckBox，导致流体越扩散越慢。
            if (_solidCache.Count == _solidCacheCapacity)
                _solidCache.Remove(_solidCacheKeys[_solidCacheNext]);
            _solidCacheKeys[_solidCacheNext] = globalCell;
            _solidCacheNext = (_solidCacheNext + 1) % _solidCacheCapacity;
            _solidCache.Add(globalCell, solid);
            return solid;
        }

        public void InvalidateStaticCell(Vector3Int cell)
        {
            // 候选湖盆的墙体可位于未分配 Chunk；静态版本必须覆盖这种边界变化。
            _staticVersion++;
            bool solid = _solidSampler(cell);
            if (_solidCache.ContainsKey(cell)) _solidCache[cell] = solid;
            if (!TryLocate(cell, out Chunk chunk, out int index)) return;
            if (chunk.Solids[index] == solid) return;
            chunk.Solids[index] = solid;
            Touch(chunk);
        }

        internal void MarkMotionChanged(Vector3Int cell, MaterialId material)
        {
            int slot = _materialSlots[(byte)material];
            if (slot < 0 || !TryLocate(cell, out Chunk chunk, out _)) return;
            Touch(chunk);
            MarkMaterialSurfaceChanged(chunk, slot);
        }

        public int FreeCapacity(Vector3Int cell)
        {
            if (IsSolid(cell)) return 0;
            int external = (int)(_externalOccupancy?.Invoke(cell) ?? 0u);
            if (!TryLocate(cell, out Chunk chunk, out int index))
                return Math.Max(0, _capacity - external);
            int occupied = 0;
            int offset = index * _materialCount;
            for (int i = 0; i < _materialCount; i++) occupied += chunk.Amounts[offset + i];
            return Math.Max(0, _capacity - occupied - external);
        }

        public uint OccupancyAt(Vector3Int cell)
        {
            uint occupied=_externalOccupancy?.Invoke(cell) ?? 0u;
            if(TryLocate(cell,out Chunk chunk,out int index))
                for(int i=0;i<_materialCount;i++)occupied+=chunk.Amounts[index*_materialCount+i];
            return occupied;
        }

        public bool TryGetSurfaceFill(Vector3Int cell,MaterialId material,out float totalFill)
        {
            totalFill=0f;
            if(!TryLocate(cell,out Chunk chunk,out int index))return false;
            int selected=-1;
            for(int i=0;i<_materialCount;i++)
            {
                if(chunk.Amounts[index*_materialCount+i]==0)continue;
                if(selected<0 || _relativeDensities[i]<_relativeDensities[selected]
                    || _relativeDensities[i]==_relativeDensities[selected] && (byte)_materials[i]<(byte)_materials[selected])selected=i;
            }
            totalFill=Mathf.Clamp01(OccupancyAt(cell)/(float)_capacity);
            return selected>=0 && _materials[selected]==material;
        }

        public int CopyLiquidChunkKeys(ElementChunkKey[] destination)
        {
            // Resident页可以因Solid缓存继续存在；显示候选只枚举仍有液量的页。
            // 刚腾空的页由提交History保留一Tick，避免为整个下落轨迹反复打包空页。
            int count=0;
            for(int i=0;i<_used && count<destination.Length;i++)
            {bool wet=false;for(int m=0;m<_materialCount;m++)wet|=_pool[i].MaterialTotals[m]>0;if(wet)destination[count++]=_pool[i].Key;}
            return count;
        }

        public void CopyChunkQuantities(ElementChunkKey key,ushort[] destination)
        {
            if(_indices.TryGetValue(key,out int slot))Array.Copy(_pool[slot].Amounts,destination,_cellCount*_materialCount);
            else Array.Clear(destination,0,_cellCount*_materialCount);
        }

        public bool TryExchange(Vector3Int upper,Vector3Int lower,MaterialId sinking,
            MaterialId rising,uint quantity)
        {
            // 等体积交换不需要空容量：先验证两份数量，再同时改写四个槽位，不能先 Remove
            // 引发 Chunk 回收或暴露半个事务。每种材料的全场数量和每格总量都保持不变。
            int a=_materialSlots[(byte)sinking],b=_materialSlots[(byte)rising];
            if(a<0 || b<0 || a==b || upper==lower || quantity==0
                || !TryLocate(upper,out Chunk u,out int ui) || !TryLocate(lower,out Chunk l,out int li)
                || u.Amounts[ui*_materialCount+a]<quantity || l.Amounts[li*_materialCount+b]<quantity)return false;
            bool aNew=l.MaterialTotals[a]==0,bNew=u.MaterialTotals[b]==0;
            _beforeCellChanged?.Invoke(upper); _beforeCellChanged?.Invoke(lower);
            u.Amounts[ui*_materialCount+a]-=(ushort)quantity;
            u.Amounts[ui*_materialCount+b]+=(ushort)quantity;
            l.Amounts[li*_materialCount+b]-=(ushort)quantity;
            l.Amounts[li*_materialCount+a]+=(ushort)quantity;
            u.MaterialTotals[a]-=quantity;u.MaterialTotals[b]+=quantity;
            l.MaterialTotals[b]-=quantity;l.MaterialTotals[a]+=quantity;
            if(u.MaterialTotals[a]==0 || aNew)_candidateVersions[a]++;
            if(l.MaterialTotals[b]==0 || bNew)_candidateVersions[b]++;
            TouchAffected(u,a,-1,-1);TouchAffected(l,b,-1,-1);
            return true;
        }

        public bool TryAdd(Vector3Int cell, MaterialId material, uint quantity)
        {
            int slot = _materialSlots[(byte)material];
            if (slot < 0 || quantity == 0 || quantity > _capacity || FreeCapacity(cell) < quantity
                || !TryLocateOrCreate(cell, out Chunk chunk, out int index)) return false;
            int offset = index * _materialCount + slot;
            int previousDominant = DominantSlot(chunk, index);
            _beforeCellChanged?.Invoke(cell);
            bool wasEmpty = chunk.MaterialTotals[slot] == 0;
            chunk.Amounts[offset] = checked((ushort)(chunk.Amounts[offset] + quantity));
            chunk.MaterialTotals[slot] += quantity;
            // 候选只拥有实际含材料的页；页内扩散只使 Mesh 版本失效，
            // 不必重新筛选所有可见页。跨页采样由 Renderer 的 Halo 缓存完成。
            if (wasEmpty) _candidateVersions[slot]++;
            TotalQuantity += quantity;
            TouchAffected(chunk, slot, previousDominant, DominantSlot(chunk, index));
            if (previousDominant < 0) _dynamicOccupancyChanged?.Invoke(cell, true);
            return true;
        }

        public bool TryRemove(Vector3Int cell, MaterialId material, uint quantity)
        {
            int slot = _materialSlots[(byte)material];
            if (slot < 0 || quantity == 0 || !TryLocate(cell, out Chunk chunk, out int index)) return false;
            int offset = index * _materialCount + slot;
            if (chunk.Amounts[offset] < quantity) return false;
            _beforeCellChanged?.Invoke(cell);
            int previousDominant = DominantSlot(chunk, index);
            chunk.Amounts[offset] -= (ushort)quantity;
            chunk.MaterialTotals[slot] -= quantity;
            if (chunk.MaterialTotals[slot] == 0)
            {
                _candidateVersions[slot]++;
            }
            TotalQuantity -= quantity;
            int currentDominant = DominantSlot(chunk, index);
            TouchAffected(chunk, slot, previousDominant, currentDominant);
            if (currentDominant < 0) _dynamicOccupancyChanged?.Invoke(cell, false);
            ReleaseIfEmpty(chunk);
            return true;
        }

        public bool TryTransfer(Vector3Int source, Vector3Int target, MaterialId material, uint quantity)
        {
            if (source == target || quantity == 0 || FreeCapacity(target) < quantity
                || !TryGetQuantity(source, material, out uint present) || present < quantity
                || !TryLocate(source, out Chunk sourceChunk, out int sourceIndex)) return false;
            int slot = _materialSlots[(byte)material];
            if (!TryLocateOrCreate(target, out Chunk targetChunk, out int targetIndex))
            {
                // 池已满，但若源页的最后一份液体正好跨页移动，可原子地回收该槽位。
                // 这样单个水滴经过第 1025 个位置时不会因历史页数而突然停住。
                if (_used != _pool.Length || !ContainsOnly(sourceChunk, slot, quantity)
                    || !TryMoveWithRecycledSource(source, target, material, quantity)) return false;
                return true;
            }
            bool targetWasEmpty = targetChunk.MaterialTotals[slot] == 0;
            _beforeCellChanged?.Invoke(source); _beforeCellChanged?.Invoke(target);
            int sourceDominant = DominantSlot(sourceChunk, sourceIndex);
            int targetDominant = DominantSlot(targetChunk, targetIndex);
            sourceChunk.Amounts[sourceIndex * _materialCount + slot] -= (ushort)quantity;
            sourceChunk.MaterialTotals[slot] -= quantity;
            targetChunk.Amounts[targetIndex * _materialCount + slot] += (ushort)quantity;
            targetChunk.MaterialTotals[slot] += quantity;
            if (sourceChunk.MaterialTotals[slot] == 0)
            {
                _candidateVersions[slot]++;
            }
            if (targetWasEmpty) _candidateVersions[slot]++;
            int sourceAfter = DominantSlot(sourceChunk, sourceIndex);
            TouchAffected(sourceChunk, slot, sourceDominant, sourceAfter);
            TouchAffected(targetChunk, slot, targetDominant,
                DominantSlot(targetChunk, targetIndex));
            if (sourceAfter < 0) _dynamicOccupancyChanged?.Invoke(source, false);
            if (targetDominant < 0) _dynamicOccupancyChanged?.Invoke(target, true);
            if (!ReferenceEquals(sourceChunk, targetChunk)) ReleaseIfEmpty(sourceChunk);
            return true;
        }

        private bool TryMoveWithRecycledSource(Vector3Int source, Vector3Int target,
            MaterialId material, uint quantity)
        {
            if (!TryRemove(source, material, quantity)) return false;
            if (TryAdd(target, material, quantity)) return true;
            // Target 理论上已通过容量检查；即使地形回调改变结果，也把数量还给源。
            if (!TryAdd(source, material, quantity))
                throw new InvalidOperationException("Grid Liquid 跨页转移回滚失败。");
            return false;
        }

        private bool ContainsOnly(Chunk chunk, int slot, uint quantity)
        {
            if (chunk.MaterialTotals[slot] != quantity) return false;
            for (int i = 0; i < _materialCount; i++)
                if (i != slot && chunk.MaterialTotals[i] != 0) return false;
            return true;
        }

        public bool TryConvert(Vector3Int cell, MaterialId sourceMaterial,
            MaterialId targetMaterial, uint quantity)
        {
            int sourceSlot = _materialSlots[(byte)sourceMaterial];
            int targetSlot = _materialSlots[(byte)targetMaterial];
            if (quantity == 0 || sourceSlot < 0 || targetSlot < 0 || sourceSlot == targetSlot
                || !TryLocate(cell, out Chunk chunk, out int index)) return false;
            int sourceOffset = index * _materialCount + sourceSlot;
            int targetOffset = index * _materialCount + targetSlot;
            if (chunk.Amounts[sourceOffset] < quantity) return false;
            _beforeCellChanged?.Invoke(cell);
            int previousDominant = DominantSlot(chunk, index);
            chunk.Amounts[sourceOffset] -= (ushort)quantity;
            bool targetWasEmpty = chunk.MaterialTotals[targetSlot] == 0;
            chunk.Amounts[targetOffset] = checked((ushort)(chunk.Amounts[targetOffset] + quantity));
            chunk.MaterialTotals[sourceSlot] -= quantity;
            chunk.MaterialTotals[targetSlot] += quantity;
            if (chunk.MaterialTotals[sourceSlot] == 0)
            {
                _candidateVersions[sourceSlot]++;
            }
            if (targetWasEmpty) _candidateVersions[targetSlot]++;
            TouchAffected(chunk, sourceSlot, previousDominant, DominantSlot(chunk, index));
            MarkMaterialSurfaceChanged(chunk, targetSlot);
            return true;
        }

        public bool TryExtractFullBox(Vector3Int minimum, Vector3Int maximum,
            MaterialId material, out ulong extracted)
            => TryExtractFullFootprint(minimum, maximum, null, material, out extracted);

        public bool TryExtractFullFootprint(Vector3Int minimum, Vector3Int maximum,
            bool[] footprint, MaterialId material, out ulong extracted)
            => TryExtractShape(minimum, maximum, footprint, null, material,
                false, 0, out extracted);

        public bool TryExtractFullHeightfield(Vector3Int minimum, Vector3Int maximum,
            int[] floorByColumn, MaterialId material, out ulong extracted)
            => TryExtractShape(minimum, maximum, null, floorByColumn, material,
                false, 0, out extracted);

        public bool TryExtractLevelFootprint(Vector3Int minimum, Vector3Int maximum,
            bool[] footprint, MaterialId material, ulong expectedQuantity,
            out ulong extracted)
            => TryExtractShape(minimum, maximum, footprint, null, material,
                true, expectedQuantity, out extracted);

        public bool TryExtractLevelHeightfield(Vector3Int minimum, Vector3Int maximum,
            int[] floorByColumn, MaterialId material, ulong expectedQuantity,
            out ulong extracted)
            => TryExtractShape(minimum, maximum, null, floorByColumn, material,
                true, expectedQuantity, out extracted);

        private bool TryExtractShape(Vector3Int minimum, Vector3Int maximum,
            bool[] footprint, int[] floorByColumn, MaterialId material,
            bool allowPartialTop, ulong expectedQuantity, out ulong extracted)
        {
            extracted = 0;
            int slot = _materialSlots[(byte)material];
            if (slot < 0 || maximum.x <= minimum.x || maximum.y <= minimum.y
                || maximum.z <= minimum.z) return false;
            long width = (long)maximum.x - minimum.x;
            long depth = (long)maximum.z - minimum.z;
            if (width > int.MaxValue / depth
                || (footprint != null && footprint.Length < width * depth)
                || (floorByColumn != null && floorByColumn.Length < width * depth))
                return false;
            int included = 0;
            ulong measured = 0;
            // 掩码外可以是岸线 Solid 或另一片动态材料；只提取本区域明确拥有的列。
            // 先验证完整底层和唯一的可部分填充顶层，并核对预计总量；随后才
            // 改写 Chunk。这样安静期后的任何水量变化都不会造成半片湖被提取。
            for (int z = minimum.z; z < maximum.z; z++)
            for (int y = minimum.y; y < maximum.y; y++)
            for (int x = minimum.x; x < maximum.x; x++)
            {
                int column = (z - minimum.z) * (int)width + x - minimum.x;
                if (footprint != null && !footprint[column]) continue;
                if (floorByColumn != null)
                {
                    int floor = floorByColumn[column];
                    if (floor != int.MaxValue && (floor < minimum.y || floor >= maximum.y))
                        return false;
                    if (y < floor) continue;
                }
                if (!TryLocate(new Vector3Int(x, y, z), out Chunk chunk, out int index)) return false;
                int offset = index * _materialCount;
                uint amount = chunk.Amounts[offset + slot];
                if (amount == 0 || ((y != maximum.y - 1 || !allowPartialTop)
                    && amount != _capacity)) return false;
                for (int other = 0; other < _materialCount; other++)
                    if (other != slot && chunk.Amounts[offset + other] != 0) return false;
                measured += amount;
                included++;
            }
            if (included == 0 || (allowPartialTop && measured != expectedQuantity))
                return false;
            for (int z = minimum.z; z < maximum.z; z++)
            for (int y = minimum.y; y < maximum.y; y++)
            for (int x = minimum.x; x < maximum.x; x++)
            {
                int column = (z - minimum.z) * (int)width + x - minimum.x;
                if (footprint != null && !footprint[column]) continue;
                if (floorByColumn != null && y < floorByColumn[column]) continue;
                TryLocate(new Vector3Int(x, y, z), out Chunk chunk, out int index);
                _beforeCellChanged?.Invoke(new Vector3Int(x,y,z));
                uint amount = chunk.Amounts[index * _materialCount + slot];
                chunk.Amounts[index * _materialCount + slot] = 0;
                chunk.MaterialTotals[slot] -= amount;
                TotalQuantity -= amount;
                extracted += amount;
                if (chunk.MaterialTotals[slot] == 0)
                {
                    _candidateVersions[slot]++;
                }
                TouchAffected(chunk, slot, slot, -1);
                _dynamicOccupancyChanged?.Invoke(new Vector3Int(x, y, z), false);
                ReleaseIfEmpty(chunk);
            }
            return true;
        }

        public uint GetChunkVersion(ElementChunkKey key) =>
            _indices.TryGetValue(key, out int index) ? _pool[index].Version : 0;

        public uint GetMaterialSurfaceVersion(MaterialId material)
        {
            int slot = _materialSlots[(byte)material];
            return slot >= 0 ? unchecked(_materialSurfaceVersions[slot] + _staticVersion) : 0;
        }

        public uint GetMaterialChunkSurfaceVersion(ElementChunkKey key, MaterialId material)
        {
            int slot = _materialSlots[(byte)material];
            return slot >= 0 && _indices.TryGetValue(key, out int index)
                ? unchecked(_pool[index].MaterialSurfaceVersions[slot] + _staticVersion)
                : _staticVersion;
        }

        public uint GetCandidateVersion(MaterialId material)
        {
            int slot = _materialSlots[(byte)material];
            return slot >= 0 ? _candidateVersions[slot] : 0;
        }

        public bool HasMaterialInChunk(ElementChunkKey key, MaterialId material)
        {
            int slot = _materialSlots[(byte)material];
            return slot >= 0 && _indices.TryGetValue(key, out int index)
                && _pool[index].MaterialTotals[slot] > 0;
        }

        public int CopyChunkKeys(MaterialId material, ElementChunkKey[] destination)
        {
            int slot = _materialSlots[(byte)material];
            if (slot < 0 || destination == null) return 0;
            int count = 0;
            // Chunk Pool 的每个驻留页天然唯一，无需 HashSet 去重，也无需创建
            // 没有液体的 Halo owner。邻页只作为构建输入，不消耗 View 或候选容量。
            for (int i = 0; i < _used && count < destination.Length; i++)
            {
                Chunk chunk = _pool[i];
                if (chunk.MaterialTotals[slot] == 0) continue;
                destination[count++] = chunk.Key;
            }
            return count;
        }

        public int CopyOccupiedCells(MaterialId material, LiquidMaterialCellSample[] destination,
            int startIndex = 0)
        {
            int slot = _materialSlots[(byte)material];
            if (slot < 0 || destination == null || _used == 0
                || startIndex < 0 || startIndex >= destination.Length) return 0;
            int totalCells = _used * _cellCount;
            int cursor = _reactionScanPositions[slot] % totalCells;
            int inspected = 0;
            int maximumInspected = Math.Min(totalCells,
                checked((destination.Length - startIndex) * 16));
            int count = 0;
            while (inspected < maximumInspected && count < destination.Length - startIndex)
            {
                int chunkIndex = cursor / _cellCount;
                int cellIndex = cursor % _cellCount;
                Chunk chunk = _pool[chunkIndex];
                if (chunk.MaterialTotals[slot] == 0)
                {
                    int skip = _cellCount - cellIndex;
                    cursor = (cursor + skip) % totalCells;
                    inspected += skip;
                    continue;
                }
                ushort quantity = chunk.Amounts[cellIndex * _materialCount + slot];
                if (quantity > 0)
                {
                    int x = cellIndex % _chunkSize;
                    int y = cellIndex / _chunkSize % _chunkSize;
                    int z = cellIndex / (_chunkSize * _chunkSize);
                    var global = new Vector3Int(chunk.Key.X * _chunkSize + x,
                        chunk.Key.Y * _chunkSize + y, chunk.Key.Z * _chunkSize + z);
                    destination[startIndex + count++] = new LiquidMaterialCellSample(global,
                        (byte)Math.Min(byte.MaxValue, quantity));
                }
                cursor = (cursor + 1) % totalCells;
                inspected++;
            }
            _reactionScanPositions[slot] = cursor;
            return count;
        }

        public bool TryFindFullCell(MaterialId material, ref int cursor,
            int maximumInspections, out Vector3Int cell, out int inspected,
            out int traversed)
        {
            cell = default;
            inspected = 0;
            traversed = 0;
            int slot = _materialSlots[(byte)material];
            int residentCells = ResidentCellCount;
            if (slot < 0 || residentCells == 0 || maximumInspections <= 0) return false;
            if (cursor < 0 || cursor >= residentCells) cursor = 0;
            // 自动湖盆识别独享游标，不推进反应系统的抽样位置。整页没有该材料时
            // 一次跳过，预算按实际读到的页/格计，避免空 Chunk 拖慢候选发现。
            while (inspected < maximumInspections && traversed < residentCells)
            {
                int chunkIndex = cursor / _cellCount;
                int cellIndex = cursor % _cellCount;
                Chunk chunk = _pool[chunkIndex];
                if (chunk.MaterialTotals[slot] == 0)
                {
                    int skip = _cellCount - cellIndex;
                    cursor = (cursor + skip) % residentCells;
                    traversed += skip;
                    inspected++;
                    continue;
                }
                int quantity = chunk.Amounts[cellIndex * _materialCount + slot];
                cursor = (cursor + 1) % residentCells;
                traversed++;
                inspected++;
                if (quantity != _capacity) continue;
                cell = new Vector3Int(chunk.Key.X * _chunkSize + cellIndex % _chunkSize,
                    chunk.Key.Y * _chunkSize + cellIndex / _chunkSize % _chunkSize,
                    chunk.Key.Z * _chunkSize + cellIndex / (_chunkSize * _chunkSize));
                return true;
            }
            return false;
        }

        private bool TryLocate(Vector3Int cell, out Chunk chunk, out int index)
        {
            // 同一格的三种材料、容量与Solid查询会连续访问同一页。
            // 只缓存页对象的位置，不缓存可变数量；省去重复FloorDiv与Dictionary Hash。
            // 对象可能移槽/回收到池中，必须检查Resident，不能用default Key判断有效性。
            chunk = _locatedChunk;
            if (chunk != null && chunk.IsResident)
            {
                int x = cell.x - chunk.Key.X * _chunkSize;
                int y = cell.y - chunk.Key.Y * _chunkSize;
                int z = cell.z - chunk.Key.Z * _chunkSize;
                if ((uint)x < (uint)_chunkSize && (uint)y < (uint)_chunkSize
                    && (uint)z < (uint)_chunkSize)
                {
                    index = x + _chunkSize * (y + _chunkSize * z);
                    return true;
                }
            }
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _chunkSize, out var key, out var local);
            index = local.x + _chunkSize * (local.y + _chunkSize * local.z);
            if (_indices.TryGetValue(key, out int slot))
            {
                chunk = _locatedChunk = _pool[slot];
                return true;
            }
            chunk = null;
            return false;
        }

        private bool TryLocateOrCreate(Vector3Int cell, out Chunk chunk, out int index)
        {
            if (TryLocate(cell, out chunk, out index)) return true;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, _chunkSize, out var key, out var local);
            index = local.x + _chunkSize * (local.y + _chunkSize * local.z);
            if (_used == _pool.Length) { chunk = null; return false; }
            chunk = _pool[_used];
            chunk.Key = key;
            // 与地形 Bake 使用相同世界坐标；只在页首次创建时查询 Collider。
            for (int z = 0; z < _chunkSize; z++)
            for (int y = 0; y < _chunkSize; y++)
            for (int x = 0; x < _chunkSize; x++)
            {
                int cellIndex = x + _chunkSize * (y + _chunkSize * z);
                Vector3Int global = new Vector3Int(
                    key.X * _chunkSize + x, key.Y * _chunkSize + y, key.Z * _chunkSize + z);
                chunk.Solids[cellIndex] = _solidCache.TryGetValue(global, out bool cached)
                    ? cached : _solidSampler(global);
            }
            _indices.Add(key, _used++);
            chunk.IsResident = true;
            _locatedChunk = chunk;
            return true;
        }

        private void Touch(Chunk chunk)
        {
            // 槽位回收后同一坐标可能重新创建 Chunk；使用 Store 级单调版本，
            // 避免新页的局部版本从 1 重启并碰巧等于旧 Mesh 的版本摘要。
            chunk.Version = ++_surfaceVersion;
        }

        private void MarkMaterialSurfaceChanged(Chunk chunk, int slot)
        {
            // 每种材料独立失效；使用 Store 级单调版本防止 Chunk 槽位回收后误命中旧 Mesh。
            chunk.MaterialSurfaceVersions[slot] = ++_materialSurfaceVersions[slot];
        }

        private void TouchAffected(Chunk chunk, int changedSlot,
            int previousDominant, int currentDominant)
        {
            Touch(chunk); // 自动湖盆候选仍使用所有材料共享的事务版本。
            MarkMaterialSurfaceChanged(chunk, changedSlot);
            // 共享液面高度只使必要 Halo 内的材料失效；远处毒液不会因本地注水重建。
            // 邻域材料存在性只在候选页集合变化时重算，普通每格通量不用搜索27个 Chunk。
            uint neighborhood=2166136261u;
            for(int i=0;i<_materialCount;i++)unchecked{neighborhood=(neighborhood^_candidateVersions[i])*16777619u;}
            if(chunk.NeighborhoodCandidateVersion!=neighborhood && _materialCount>1)
            {
                Array.Clear(chunk.NeighborhoodMaterials,0,_materialCount);
                int radius=Mathf.CeilToInt(7f/_chunkSize);
                for(int z=-radius;z<=radius;z++)for(int y=-radius;y<=radius;y++)for(int x=-radius;x<=radius;x++)
                    if(_indices.TryGetValue(new ElementChunkKey(chunk.Key.X+x,chunk.Key.Y+y,chunk.Key.Z+z),out int index))
                        for(int slot=0;slot<_materialCount;slot++)
                            chunk.NeighborhoodMaterials[slot]|=_pool[index].MaterialTotals[slot]>0;
                chunk.NeighborhoodCandidateVersion=neighborhood;
            }
            for(int slot=0;slot<_materialCount;slot++)
                if(slot!=changedSlot && chunk.NeighborhoodMaterials[slot])MarkMaterialSurfaceChanged(chunk,slot);
        }

        private int DominantSlot(Chunk chunk, int cellIndex)
        {
            int offset = cellIndex * _materialCount;
            int selected = -1, maximum = 0;
            for (int slot = 0; slot < _materialCount; slot++)
            {
                int amount = chunk.Amounts[offset + slot];
                if (amount > maximum) { selected = slot; maximum = amount; }
            }
            return selected;
        }

        private void ReleaseIfEmpty(Chunk chunk)
        {
            for (int material = 0; material < _materialCount; material++)
                if (chunk.MaterialTotals[material] != 0) return;
            if (!_indices.TryGetValue(chunk.Key, out int slot)) return;
            int last = --_used;
            _indices.Remove(chunk.Key);
            chunk.IsResident = false;
            if (slot != last)
            {
                Chunk moved = _pool[last];
                _pool[slot] = moved;
                _indices[moved.Key] = slot;
                _pool[last] = chunk;
            }
            chunk.Key = default;
            chunk.Version = 0;
            chunk.NeighborhoodCandidateVersion=uint.MaxValue;
            Array.Clear(chunk.MaterialSurfaceVersions, 0, _materialCount);
        }

    }
}
