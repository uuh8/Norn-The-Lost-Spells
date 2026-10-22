using System;
using System.Collections.Generic;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class LiquidWorldRuntime
    {
        private FixedSolidWorldRuntime _materialSolidSource;
        private Dictionary<Vector3Int,uint> _residueOccupancy;
        private Vector3Int[] _residueKeys;
        private bool[] _residueKeyValid;
        private int _residueCursor;
        internal void SetMaterialSolidOccupancy(FixedSolidWorldRuntime source)
        {
            _materialSolidSource=source;
            int capacity=Math.Min(16384,_profile.MaximumActiveCells);
            _residueOccupancy=new Dictionary<Vector3Int,uint>(capacity); _residueKeys=new Vector3Int[capacity];
            _residueKeyValid=new bool[capacity];
            _store.SetExternalOccupancy(ReadCombinedExternalOccupancy);
            _store.SetMaterialSolidOccupancy(ReadResidueOccupancy);
        }
        private uint ReadCombinedExternalOccupancy(Vector3Int cell)
            => (uint)Math.Min((ulong)_store.CapacityGmu,(ulong)_stable.OccupancyAt(cell)+ReadResidueOccupancy(cell));
        private uint ReadResidueOccupancy(Vector3Int cell)
        {
            // 物质量全局计数 O(1)，没有残留时沿用便宜的旧路径；有残留才查有界粗格缓存。
            if(_materialSolidSource==null || _materialSolidSource.MaterialQuantity(MaterialId.DriedSlime)==0) return 0;
            if(_residueOccupancy.TryGetValue(cell,out uint amount)) return amount;
            int scale=_volumeSettings.ResolutionScale;
            Vector3Int min=cell*scale; ulong total=0;
            for(int z=0;z<scale;z++) for(int y=0;y<scale;y++) for(int x=0;x<scale;x++)
                if(_materialSolidSource.TryGetQuantity(min+new Vector3Int(x,y,z),MaterialId.DriedSlime,out uint quantity)) total+=quantity;
            amount=(uint)Math.Min((ulong)_store.CapacityGmu,total);
            // 局部失效可能删除了 Ring 以外的键。每次覆盖槽位都淘汰其旧键，
            // 才能保证 Dictionary 永不超过预分配容量，避免相变多了以后悄悄扩容分配。
            if(_residueKeyValid[_residueCursor]) _residueOccupancy.Remove(_residueKeys[_residueCursor]);
            _residueKeyValid[_residueCursor]=true;
            _residueKeys[_residueCursor]=cell; _residueCursor=(_residueCursor+1)%_residueKeys.Length;
            _residueOccupancy[cell]=amount; return amount;
        }
        private float ResidueBaseHeight(Vector3Int cell) => ReadResidueOccupancy(cell)/(float)Math.Max(1,CellCapacityGmu)*_cellSize;
    }
}
