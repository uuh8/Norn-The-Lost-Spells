using System;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    public sealed partial class ElementWorldRuntime
    {
        [Header("Material Conversion (Optional)")]
        [SerializeField] private MaterialConversionProfile _conversionProfile;
        private MaterialConversionScheduler _conversionScheduler;
        public MaterialWorldQuantityLedger ConversionLedger => _conversionScheduler?.Ledger;
        public int ConversionPendingCandidates => _conversionScheduler?.PendingCandidates ?? 0;
        public int ConversionProcessedLastTick => _conversionScheduler?.ProcessedLastTick ?? 0;
        public uint ConversionDeferredTransactions => _conversionScheduler?.DeferredTransactions ?? 0;
        public uint ConversionCandidateOverflows => _conversionScheduler?.CandidateOverflows ?? 0;
        internal Vector3 ConversionOrigin => _origin;
        internal float ConversionCellSize => _profile.CellSize;
        internal MaterialSimulationBackendKind ResolveConversionBackend(MaterialId material)
            => _materialRoutes!=null && _materialRoutes.TryResolve(material,out var backend)?backend:MaterialSimulationBackendKind.Unsupported;
        internal bool IsConversionAddressActive(MaterialConversionAddress address)
        {
            var domain=LiquidActivityDomain(); if(domain==null) return true;
            Vector3 position=ResolveConversionBackend(address.Material)==MaterialSimulationBackendKind.GridLiquid
                ?_gridLiquidRuntime.Origin+((Vector3)address.Cell+Vector3.one*.5f)*_gridLiquidRuntime.CellSize
                :_origin+((Vector3)address.Cell+Vector3.one*.5f)*_profile.CellSize;
            return domain.Contains(position);
        }
        private void InitializeMaterialConversions()
        {
            if(_conversionProfile==null) return;
            if(!_conversionProfile.IsValid || !_hasGridLiquidRoute || _fixedSolidRuntime==null || _gasRuntime==null
                || !_fixedSolidRuntime.IsMaterialInitialized || !_gasRuntime.IsMaterialInitialized)
                throw new InvalidOperationException("Material Conversion 需要有效 Profile 及三个真实数量 Owner。");
            _conversionScheduler=new MaterialConversionScheduler(this,_gridLiquidRuntime,_fixedSolidRuntime,_gasRuntime,_conversionProfile);
            if(_gridLiquidReactions!=null) _gridLiquidReactions.EvaporateWater=_conversionScheduler.EvaporateReactionWater;
        }
        public void RegisterMaterialEnvironment(MaterialEnvironmentAuthoring environment) => _conversionScheduler?.Register(environment);
        public void UnregisterMaterialEnvironment(MaterialEnvironmentAuthoring environment) => _conversionScheduler?.Unregister(environment);
        internal void NotifyExternalMaterialInjection(MaterialId material,ulong quantity) => _conversionScheduler?.Ledger.RecordExternal(material,quantity);
        public bool TryCaptureMaterialQuantityFamily(MaterialQuantityFamily family,out MaterialQuantityFamilySnapshot snapshot)
        {
            snapshot=default; if(_conversionScheduler==null || family==MaterialQuantityFamily.None) return false;
            // 显式诊断调用才做库存求和；Sparse Gas 的逐槽合并不在每帧或每 Tick 执行。
            ulong injected=0,resident=0,pending=0,vented=0,consumed=0,exported=0,imported=0;
            for(int i=1;i<=8;i++)
            {
                var material=(MaterialId)i; if(MaterialWorldQuantityLedger.Family(material)!=family) continue;
                injected+=ConversionLedger.ExternallyInjected(material); consumed+=ConversionLedger.ExplicitlyConsumed(material);
                resident+=_gridLiquidRuntime.MaterialQuantity(material)+_fixedSolidRuntime.MaterialQuantity(material)+_gasRuntime.MaterialQuantity(material);
                pending+=_gridLiquidRuntime.PendingMaterialQuantity(material)+_fixedSolidRuntime.PendingMaterialQuantity(material)+_gasRuntime.PendingMaterialQuantity(material);
                vented+=_gasRuntime.VentedMaterialQuantity(material);
                exported+=ExportedMaterialQuantity(material);
                imported+=ImportedMaterialQuantity(material);
            }
            snapshot=new MaterialQuantityFamilySnapshot(injected,resident,pending,vented,consumed,exported,imported); return true;
        }
    }
}
