using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    /// <summary>
    /// 固体／气体复用的有界稀疏数量存储。稀疏的是 Chunk 索引，Chunk 内是连续数组；
    /// 初始化预分配全部槽位，写入和只读查询均不会扩容。它不拥有 Physics 或显示。
    /// </summary>
    public class SparseMaterialVolumeStore : IMaterialVolumeReadOnly
    {
        private readonly Dictionary<ElementChunkKey, int> _indices;
        private readonly ElementChunkKey[] _keys;
        private readonly bool[] _allocated;
        private readonly int[] _resident, _free;
        private readonly int[] _materialSlots = new int[256];
        private readonly MaterialId[] _materials;
        private readonly uint[] _amounts, _next, _nextStamps, _chunkVersions;
        private readonly ulong[] _chunkTotals;
        private readonly ulong[] _materialTotals=new ulong[256];
        private readonly int[] _touched;
        private readonly bool _singleMaterial;
        private int _freeCount, _residentCount, _touchedCount;
        private uint _epoch;
        private bool _inTick;
        private Action<Vector3Int> _changeObserver;
        internal void SetChangeObserver(Action<Vector3Int> observer) => _changeObserver=observer;
        public int ChunkSize { get; }
        public int CellsPerChunk { get; }
        public int MaximumChunks => _keys.Length;
        public int MaterialCount => _materials.Length;
        public int ResidentChunkCount => _residentCount;
        public int ResidentCellCount => _residentCount * CellsPerChunk * MaterialCount;
        public uint CellCapacity { get; }
        public uint Version { get; private set; }
        public ulong TotalQuantity { get; private set; }

        public SparseMaterialVolumeStore(int chunkSize, int maximumChunks, uint capacity,
            MaterialId[] materials, bool singleMaterial = false)
        {
            if (chunkSize < 1 || chunkSize > 16 || maximumChunks < 1 || capacity == 0
                || materials == null || materials.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(maximumChunks));
            ChunkSize = chunkSize; CellsPerChunk = checked(chunkSize * chunkSize * chunkSize);
            CellCapacity = capacity; _singleMaterial = singleMaterial;
            _materials = (MaterialId[])materials.Clone(); Array.Fill(_materialSlots, -1);
            for (int i = 0; i < materials.Length; i++)
            {
                int id = (byte)materials[i];
                if (id == 0 || _materialSlots[id] >= 0) throw new ArgumentException("材料必须唯一且非空。");
                _materialSlots[id] = i;
            }
            int length = checked(maximumChunks * CellsPerChunk * materials.Length);
            _amounts = new uint[length]; _next = new uint[length]; _nextStamps = new uint[length];
            _touched = new int[length]; _keys = new ElementChunkKey[maximumChunks];
            _allocated = new bool[maximumChunks]; _resident = new int[maximumChunks];
            _free = new int[maximumChunks]; _chunkVersions = new uint[maximumChunks];
            _chunkTotals = new ulong[maximumChunks];
            _indices = new Dictionary<ElementChunkKey, int>(maximumChunks);
            for (int i = 0; i < maximumChunks; i++) _free[_freeCount++] = maximumChunks - 1 - i;
        }

        public MaterialId GetMaterial(int index) => _materials[index];
        public bool Supports(MaterialId material) => _materialSlots[(byte)material] >= 0;
        internal ulong MaterialQuantity(MaterialId material)
        {
            return _materialTotals[(byte)material];
        }
        public ElementChunkKey GetChunkKey(int residentIndex) => _keys[_resident[residentIndex]];
        public uint GetChunkVersion(ElementChunkKey key) => _indices.TryGetValue(key, out int slot) ? _chunkVersions[slot] : 0;
        public void CopyMaterialBox(ElementChunkKey key,Vector3Int localMinimum,Vector3Int size,MaterialId material,
            uint[] destination,int destinationOffset,int rowStride,int sliceStride)
        {
            // 一次 Chunk 定位后顺序读 Current；Next 正在计算也不能成为显示/Exposure 的真值。
            // 缺失 Chunk/材料明确写 0，复用缓冲不会残留上一页密度。参数只由受控只读消费者构造。
            if(destination==null || size.x<=0 || size.y<=0 || size.z<=0 || localMinimum.x<0 || localMinimum.y<0 || localMinimum.z<0
                || localMinimum.x+size.x>ChunkSize || localMinimum.y+size.y>ChunkSize || localMinimum.z+size.z>ChunkSize
                || destinationOffset<0 || rowStride<size.x || sliceStride<(long)rowStride*size.y
                || destinationOffset+(long)(size.z-1)*sliceStride+(long)(size.y-1)*rowStride+size.x>destination.Length)
                throw new ArgumentOutOfRangeException(nameof(size));
            int materialSlot=_materialSlots[(byte)material];
            bool present=_indices.TryGetValue(key,out int slot) && materialSlot>=0;
            int baseOffset=slot*CellsPerChunk*MaterialCount;
            for(int z=0;z<size.z;z++) for(int y=0;y<size.y;y++)
            {
                int source=baseOffset+(localMinimum.x+ChunkSize*(localMinimum.y+y+ChunkSize*(localMinimum.z+z)))*MaterialCount+materialSlot;
                int target=destinationOffset+z*sliceStride+y*rowStride;
                for(int x=0;x<size.x;x++) destination[target+x]=present?_amounts[source+x*MaterialCount]:0;
            }
        }
        public ulong GetChunkQuantity(int residentIndex) => _chunkTotals[_resident[residentIndex]];
        public bool TryGetQuantity(Vector3Int cell, MaterialId material, out uint quantity)
        {
            quantity = 0;
            if (!Locate(cell, material, false, out int offset)) return false;
            quantity = _amounts[offset]; return quantity > 0;
        }
        public bool TryGetAmount(Vector3Int cell, MaterialId material, out byte amount)
        {
            bool found = TryGetQuantity(cell, material, out uint quantity);
            amount = (byte)Math.Min(255UL, (ulong)quantity * 255UL / CellCapacity);
            return found;
        }
        public uint OccupancyAt(Vector3Int cell)
        {
            if (!Locate(cell, _materials[0], false, out int offset)) return 0;
            uint sum = 0; for (int i = 0; i < MaterialCount; i++) sum += _amounts[offset + i];
            return sum;
        }
        public bool TryAdd(Vector3Int cell, MaterialId material, uint quantity)
        {
            if (_inTick || quantity == 0 || !Supports(material)) return false;
            uint occupied = OccupancyAt(cell);
            if (quantity > CellCapacity - occupied) return false;
            TryGetQuantity(cell, material, out uint own);
            if (_singleMaterial && occupied != own) return false;
            if (!Locate(cell, material, true, out int offset)) return false;
            SetCurrent(offset, _amounts[offset] + quantity); return true;
        }
        public bool TryRemove(Vector3Int cell, MaterialId material, uint quantity)
        {
            if (_inTick || quantity == 0 || !Locate(cell, material, false, out int offset)
                || _amounts[offset] < quantity) return false;
            SetCurrent(offset, _amounts[offset] - quantity); return true;
        }
        public bool TryGetCell(int index, out MaterialVolumeCell cell)
        {
            cell = default;
            if ((uint)index >= (uint)ResidentCellCount) return false;
            int perChunk = CellsPerChunk * MaterialCount;
            int slot = _resident[index / perChunk], local = index % perChunk / MaterialCount;
            int material = index % MaterialCount;
            uint quantity = _amounts[slot * perChunk + local * MaterialCount + material];
            if (quantity == 0) return false;
            var coordinate = new Vector3Int(local % ChunkSize, local / ChunkSize % ChunkSize,
                local / (ChunkSize * ChunkSize));
            cell = new MaterialVolumeCell(ElementWorldCoordinates.ComposeGlobalCell(_keys[slot],
                coordinate, ChunkSize), _materials[material], quantity);
            return true;
        }

        public void BeginFluxTick()
        {
            if (_inTick) throw new InvalidOperationException("上一个数量事务尚未提交。");
            _inTick = true; _touchedCount = 0;
            if (++_epoch == 0) { Array.Clear(_nextStamps, 0, _nextStamps.Length); _epoch = 1; }
        }
        public uint Transfer(Vector3Int source, Vector3Int target, MaterialId material, uint requested)
        {
            if (!_inTick || requested == 0 || source == target
                || !Locate(source, material, false, out int from)) return 0;
            uint available = Next(from);
            if (available == 0) return 0;
            if (!Locate(target, material, true, out int to)) return 0;
            int first = to - _materialSlots[(byte)material];
            uint occupied = 0;
            for (int i = 0; i < MaterialCount; i++) occupied += Next(first + i);
            uint transfer = Math.Min(requested, Math.Min(available, CellCapacity - occupied));
            if (transfer == 0) return 0;
            SetNext(from, available - transfer); SetNext(to, Next(to) + transfer);
            return transfer;
        }
        public uint DrainNext(Vector3Int cell, MaterialId material, uint requested)
        {
            if (!_inTick || !Locate(cell, material, false, out int offset)) return 0;
            uint removed = Math.Min(requested, Next(offset));
            if (removed > 0) SetNext(offset, Next(offset) - removed);
            return removed;
        }
        public void CommitFluxTick()
        {
            if (!_inTick) throw new InvalidOperationException("没有待提交的数量事务。");
            // 仅提交真正改变的槽；不用每 Tick Copy 整个世界。Current 在计算期间保持只读。
            for (int i = 0; i < _touchedCount; i++) SetCurrent(_touched[i], _next[_touched[i]]);
            _inTick = false;
        }
        public void ReclaimEmptyChunks()
        {
            if (_inTick) return;
            for (int i = _residentCount - 1; i >= 0; i--)
            {
                int slot = _resident[i]; if (_chunkTotals[slot] != 0) continue;
                _indices.Remove(_keys[slot]); _allocated[slot] = false;
                _resident[i] = _resident[--_residentCount]; _free[_freeCount++] = slot;
            }
        }
        private uint Next(int offset) => _nextStamps[offset] == _epoch ? _next[offset] : _amounts[offset];
        private void SetNext(int offset, uint amount)
        {
            if (_nextStamps[offset] != _epoch) { _nextStamps[offset] = _epoch; _touched[_touchedCount++] = offset; }
            _next[offset] = amount;
        }
        private void SetCurrent(int offset, uint amount)
        {
            uint old = _amounts[offset]; if (old == amount) return;
            _amounts[offset] = amount;
            int chunk = offset / (CellsPerChunk * MaterialCount);
            int material=(byte)_materials[offset%MaterialCount];
            if (amount >= old) { TotalQuantity += amount - old; _chunkTotals[chunk] += amount - old; _materialTotals[material]+=amount-old; }
            else { TotalQuantity -= old - amount; _chunkTotals[chunk] -= old - amount; _materialTotals[material]-=old-amount; }
            _chunkVersions[chunk] = BumpVersion();
            if (_changeObserver!=null)
            {
                int local=offset / MaterialCount % CellsPerChunk;
                _changeObserver(ElementWorldCoordinates.ComposeGlobalCell(_keys[chunk],
                    new Vector3Int(local%ChunkSize,local/ChunkSize%ChunkSize,local/(ChunkSize*ChunkSize)),ChunkSize));
            }
        }
        private bool Locate(Vector3Int cell, MaterialId material, bool create, out int offset)
        {
            offset = 0; int materialSlot = _materialSlots[(byte)material]; if (materialSlot < 0) return false;
            ElementWorldCoordinates.GlobalCellToChunkAndLocal(cell, ChunkSize, out ElementChunkKey key, out Vector3Int local);
            if (!_indices.TryGetValue(key, out int slot))
            {
                if (!create || _freeCount == 0) return false;
                slot = _free[--_freeCount]; _keys[slot] = key; _allocated[slot] = true;
                _indices.Add(key, slot); _resident[_residentCount++] = slot;
                _chunkVersions[slot] = BumpVersion();
            }
            int index = local.x + ChunkSize * (local.y + ChunkSize * local.z);
            offset = (slot * CellsPerChunk + index) * MaterialCount + materialSlot;
            return true;
        }
        private uint BumpVersion()
        {
            // 0 保留给“Chunk 不存在”；长期运行版本绕回时不能把真实固体错判为空并禁用 Collider。
            unchecked { Version++; if (Version == 0) Version++; }
            return Version;
        }
    }
}
