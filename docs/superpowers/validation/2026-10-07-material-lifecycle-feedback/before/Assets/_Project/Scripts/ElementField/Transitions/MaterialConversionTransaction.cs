using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public readonly struct MaterialConversionAddress : IEquatable<MaterialConversionAddress>
    {
        // Liquid 地址必须是唯一粗格地址；Solid/Gas 使用基础 World Cell，严禁混用别名来重复消费。
        public readonly Vector3Int Cell;
        public readonly MaterialId Material;
        public MaterialConversionAddress(Vector3Int cell, MaterialId material) { Cell=cell; Material=material; }
        public bool Equals(MaterialConversionAddress other) => Cell==other.Cell && Material==other.Material;
        public override bool Equals(object obj) => obj is MaterialConversionAddress other && Equals(other);
        public override int GetHashCode() => unchecked(Cell.GetHashCode()*397^(byte)Material);
    }
    public readonly struct MaterialConversionPlacement
    {
        public readonly MaterialConversionAddress Address;
        public readonly uint Quantity;
        public MaterialConversionPlacement(Vector3Int cell, MaterialId material, uint quantity)
        { Address=new MaterialConversionAddress(cell,material); Quantity=quantity; }
    }
    public readonly struct MaterialConversionStamp
    {
        public readonly uint Liquid, Solid, Gas, SourceQuantity;
        public MaterialConversionStamp(uint liquid, uint solid, uint gas, uint sourceQuantity)
        { Liquid=liquid; Solid=solid; Gas=gas; SourceQuantity=sourceQuantity; }
    }
    public interface IMaterialConversionPort
    {
        bool TryPrepare(MaterialConversionAddress source, uint consume, MaterialConversionPlacement[] targets, int count, out MaterialConversionStamp stamp);
        bool TryCommit(MaterialConversionAddress source, uint consume, MaterialConversionPlacement[] targets, int count, in MaterialConversionStamp stamp);
        void Cancel();
    }
    public enum MaterialConversionTransactionState : byte { Idle, Prepared, Committed, Cancelled }
    public sealed class MaterialConversionTransaction
    {
        private readonly IMaterialConversionPort _port;
        private readonly MaterialWorldQuantityLedger _ledger;
        private readonly MaterialConversionPlacement[] _targets;
        private MaterialConversionAddress _source;
        private uint _consume;
        private int _count;
        private MaterialConversionStamp _stamp;
        public MaterialConversionTransactionState State { get; private set; }
        public MaterialConversionTransaction(IMaterialConversionPort port, MaterialWorldQuantityLedger ledger, int capacity=128)
        { _port=port ?? throw new ArgumentNullException(nameof(port)); _ledger=ledger ?? throw new ArgumentNullException(nameof(ledger)); _targets=new MaterialConversionPlacement[capacity]; }
        public bool TryPrepare(MaterialConversionAddress source, uint consume, MaterialConversionPlacement[] targets, int count)
        {
            if (State==MaterialConversionTransactionState.Prepared || targets==null || count<=0 || count>targets.Length
                || count>_targets.Length || !_ledger.Allows(source.Material,consume,targets,count)) return false;
            for(int i=0;i<count;i++) if (targets[i].Address.Material==source.Material) return false;
            Array.Copy(targets,_targets,count);
            if (!_port.TryPrepare(source,consume,_targets,count,out _stamp)) return false;
            _source=source; _consume=consume; _count=count; State=MaterialConversionTransactionState.Prepared; return true;
        }
        public bool Commit()
        {
            if (State!=MaterialConversionTransactionState.Prepared) return false;
            if (!_port.TryCommit(_source,_consume,_targets,_count,in _stamp)) { Cancel(); return false; }
            _ledger.Record(_source.Material,_consume,_targets,_count); State=MaterialConversionTransactionState.Committed; return true;
        }
        public void Cancel()
        {
            if (State!=MaterialConversionTransactionState.Prepared) return;
            _port.Cancel(); _ledger.CancelledTransactions++; State=MaterialConversionTransactionState.Cancelled;
        }
    }
}
