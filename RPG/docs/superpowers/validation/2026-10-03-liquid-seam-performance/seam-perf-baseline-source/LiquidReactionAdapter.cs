using Game.Combat;
using Game.Materials;
using UnityEngine;

namespace Game.ElementField
{
    internal delegate bool GridApplyFireDelta(in ElementCellDeltaRequest request);
    internal delegate bool GridApplyFireAdd(in ElementCellAddRequest request);
    internal delegate void GridApplyReactionDamage(in WorldReactionDamageCommand request);

    /// <summary>
    /// Grid 与旧 PBF 共用 Material Reaction Catalog 和纯数值公式；Grid 直接按 GMU
    /// 提交材料消费。反应命令不会假扮成“每粒子一份”的旧 GPU 命令。
    /// </summary>
    internal sealed class LiquidReactionAdapter
    {
        private static readonly Vector3Int[] Contacts =
        {
            Vector3Int.zero, Vector3Int.left, Vector3Int.right,
            Vector3Int.down, Vector3Int.up, Vector3Int.forward, Vector3Int.back,
        };

        private readonly LiquidWorldRuntime _liquids;
        private readonly IMaterialAmountReadOnly _fire;
        private readonly MaterialReactionCatalogSnapshot _catalog;
        private readonly ElementReactionTuningSnapshot _tuning;
        private readonly GridApplyFireDelta _applyFireDelta;
        private readonly GridApplyFireAdd _applyFireAdd;
        private readonly GridApplyReactionDamage _applyDamage;
        private readonly LiquidMaterialCellSample[] _stickySamples;
        private readonly ToxicWorldReactionResolution[] _toxicResolutions;
        private readonly MaterialReactionProcessTable _processes;
        private readonly ToxicCombustionTuning _worldToxic;

        internal LiquidReactionAdapter(LiquidWorldRuntime liquids, IMaterialAmountReadOnly fire,
            MaterialReactionCatalogSnapshot catalog, in ElementReactionTuningSnapshot tuning,
            int capacity, GridApplyFireDelta applyFireDelta,
            GridApplyFireAdd applyFireAdd, GridApplyReactionDamage applyDamage)
        {
            _liquids = liquids;
            _fire = fire;
            _catalog = catalog;
            _tuning = tuning;
            _applyFireDelta = applyFireDelta;
            _applyFireAdd = applyFireAdd;
            _applyDamage = applyDamage;
            _stickySamples = new LiquidMaterialCellSample[capacity];
            _toxicResolutions = new ToxicWorldReactionResolution[capacity];
            // 角色的 20% Poison 阈值不能用来要求每格薄毒液至少 51 GMU。
            // 世界毒爆仍要求有效燃料，但按局部足量材料判断；范围内预热/冷却共用一次爆炸。
            _worldToxic = CreateWorldToxicTuning(in tuning.ToxicCombustion);
            _processes = new MaterialReactionProcessTable(capacity,
                Mathf.Max(1,Mathf.CeilToInt(_worldToxic.Radius/liquids.CellSize)),true);
        }

        internal static ToxicCombustionTuning CreateWorldToxicTuning(in ToxicCombustionTuning source)
        {
            ToxicCombustionTuning world=source;
            world.FireThreshold=100f/byte.MaxValue;
            world.PoisonThreshold=Mathf.Max(1f,world.MinEffectiveConsume)*(100f/byte.MaxValue);
            return world;
        }

        internal void Process(LiquidCellFireSample[] fireCells, int fireCount, float deltaTime)
        {
            // Catalog是初始化冻结快照，同一Tick无需对每份Fire/每个邻格重复解析相同规则。
            bool extinguish=_catalog.TryResolve(MaterialId.Fire,MaterialId.Water,out ElementReactionId waterRule) && waterRule==ElementReactionId.Extinguish;
            bool ignite=_catalog.TryResolve(MaterialId.Fire,MaterialId.Sticky,out ElementReactionId stickyRule) && stickyRule==ElementReactionId.IgniteGoo;
            bool toxic=_catalog.TryResolve(MaterialId.Fire,MaterialId.Poison,out ElementReactionId poisonRule) && poisonRule==ElementReactionId.ToxicCombustion;
            for (int i = 0; i < fireCount; i++)
            {
                Vector3Int fireCell = fireCells[i].GlobalCell;
                Vector3Int liquidSourceCell = _liquids.WorldCellToLiquidCell(fireCell);
                for (int neighbor = 0; neighbor < Contacts.Length; neighbor++)
                {
                    // Fire 属于世界细格，液体反应邻接属于粗格；两套编号必须显式映射。
                    Vector3Int liquidCell = liquidSourceCell + Contacts[neighbor];
                    if (extinguish)
                        Extinguish(fireCell, liquidCell, deltaTime);
                    if (ignite)
                        Ignite(fireCell, liquidCell, deltaTime);
                    if (toxic && _liquids.TryGetAmount(liquidCell, MaterialId.Poison,out _)
                        && !_processes.HasToxicContact(liquidSourceCell,liquidCell)
                        && _fire.TryGetAmount(fireCell, MaterialId.Fire, out byte fireAmount))
                        _processes.TryStartToxic(liquidSourceCell, liquidCell, fireAmount,
                            MaterialReactionProcessTable.ReadPoison(_liquids,liquidCell,true),
                            in _worldToxic);
                }
            }

            int resolvedCount = _processes.TickToxic(deltaTime, _liquids,
                in _worldToxic, _toxicResolutions);
            for (int i = 0; i < resolvedCount; i++)
            {
                ToxicWorldReactionResolution resolved = _toxicResolutions[i];
                int consumed = ConsumePoison(resolved.LiquidCell,resolved.ConsumedGmu);
                if (consumed < Mathf.Max(1f,_worldToxic.MinEffectiveConsume)) continue;
                Vector3 center = _liquids.Origin + ((Vector3)resolved.LiquidCell
                    + Vector3.one * .5f) * _liquids.CellSize;
                var damage = new WorldReactionDamageCommand(ElementReactionId.ToxicCombustion,
                    center, _worldToxic.BaseDamage+consumed*_worldToxic.DamagePerPoison, resolved.Radius);
                _applyDamage(in damage);
            }

            if (!_catalog.TryResolve(MaterialId.Water, MaterialId.Sticky,
                    out ElementReactionId absorbRule)
                || absorbRule != ElementReactionId.AbsorbWater) return;
            int stickyCount = _liquids.CopyOccupiedCells(MaterialId.Sticky, _stickySamples);
            for (int i = 0; i < stickyCount; i++)
            {
                LiquidMaterialCellSample sticky = _stickySamples[i];
                for (int neighbor = 0; neighbor < Contacts.Length; neighbor++)
                {
                    Vector3Int waterCell = sticky.GlobalCell + Contacts[neighbor];
                    if (!_liquids.TryGetAmount(waterCell, MaterialId.Water, out byte water)
                        || water == 0) continue;
                    var contact = new MaterialContactSnapshot(MaterialId.Water, water,
                        MaterialId.Sticky, sticky.Amount);
                    if (ElementReactionEvaluator.TryEvaluateMaterialContact(in contact,
                            deltaTime, ElementReactionId.AbsorbWater, in _tuning,
                            out MaterialReactionResult result)
                        && result.FirstConsumedGmu > 0
                        && result.SecondProducedGmu > 0)
                        _liquids.TryConvert(waterCell, MaterialId.Water,
                            MaterialId.Sticky, (uint)result.FirstConsumedGmu);
                    break;
                }
            }
        }

        private int ConsumePoison(Vector3Int center,int requested)
        {
            int consumed=0;
            // 同一 Tick 内只消费实际仍存在的燃料；不要求移动后的毒液重新回到原触发格。
            for(int z=-1;z<=1 && consumed<requested;z++)
            for(int x=-1;x<=1 && consumed<requested;x++)
            {
                Vector3Int cell=center+new Vector3Int(x,0,z);
                if(!_liquids.TryGetAmount(cell,MaterialId.Poison,out byte amount))continue;
                int take=Mathf.Min(amount,requested-consumed);
                if(take>0 && _liquids.TryConsume(cell,MaterialId.Poison,(uint)take))consumed+=take;
            }
            return consumed;
        }

        private void Extinguish(Vector3Int fireCell, Vector3Int waterCell, float deltaTime)
        {
            // 稀疏湖/法术大多不与远处Fire接触。先查便宜的局部数量，避免空接触反复
            // 执行World活动区域AABB查询；有接触时仍实时读取Fire，保留消费/回滚语义。
            if (!_liquids.TryGetAmount(waterCell, MaterialId.Water, out byte water)
                || !_fire.TryGetAmount(fireCell, MaterialId.Fire, out byte fire)) return;
            var contact = new MaterialContactSnapshot(MaterialId.Fire, fire,
                MaterialId.Water, water);
            if (!ElementReactionEvaluator.TryEvaluateMaterialContact(in contact, deltaTime,
                    ElementReactionId.Extinguish, in _tuning,
                    out MaterialReactionResult result)
                || result.FirstConsumedGmu <= 0 || result.SecondConsumedGmu <= 0) return;
            var delta = new ElementCellDeltaRequest(fireCell, MaterialId.Fire,
                (byte)result.FirstConsumedGmu);
            if (!_applyFireDelta(in delta)) return;
            if (_liquids.TryConsume(waterCell, MaterialId.Water,
                    (uint)result.SecondConsumedGmu)) return;
            // 同 Tick 回滚火比在满容量湖泊内重建一份水安全：火格刚释放了对应容量。
            var rollback = new ElementCellAddRequest(fireCell, MaterialId.Fire,
                (byte)result.FirstConsumedGmu);
            _applyFireAdd(in rollback);
        }

        private void Ignite(Vector3Int fireCell, Vector3Int stickyCell, float deltaTime)
        {
            if (!_liquids.TryGetAmount(stickyCell, MaterialId.Sticky, out byte sticky)
                || !_fire.TryGetAmount(fireCell, MaterialId.Fire, out byte fire)) return;
            Vector3Int targetFireCell = _liquids.LiquidCellToWorldCell(stickyCell);
            _fire.TryGetAmount(targetFireCell, MaterialId.Fire, out byte targetFire);
            _liquids.TryGetAmount(stickyCell, MaterialId.Water, out byte water);
            if (!TryPlanIgnition(fire, targetFire, sticky, water, deltaTime,
                    in _tuning.IgniteGoo, out int consumed, out int produced)) return;
            var add = new ElementCellAddRequest(targetFireCell, MaterialId.Fire,
                (byte)produced);
            if (!_applyFireAdd(in add)) return;
            if (!_liquids.TryConsume(stickyCell, MaterialId.Sticky,
                    (uint)consumed))
            {
                var rollback = new ElementCellDeltaRequest(targetFireCell, MaterialId.Fire,
                    (byte)produced);
                _applyFireDelta(in rollback);
            }
            else _liquids.RecordStickyIgnition((uint)consumed);
        }

        internal static bool TryPlanIgnition(byte sourceFire, byte targetFire, byte fuel,
            byte water, float deltaTime, in IgniteGooTuning tuning,
            out int consumed, out int produced)
        {
            consumed = produced = 0;
            // 世界里 Fire 是点火源，新增火占用燃料所在地的容量；不能拿源火的满格
            // 阻止在邻格点火。角色 Goo 的百分比阈值也不代表薄膜燃料是否存在。
            int capacity = byte.MaxValue - targetFire;
            if (deltaTime <= 0f || fuel == 0 || tuning.FirePerGoo <= 0f
                || sourceFire * (100f / byte.MaxValue) < tuning.FireThreshold
                || water * (100f / byte.MaxValue) >= tuning.WetInhibitThreshold
                || capacity < Mathf.Max(1, Mathf.CeilToInt(tuning.RequiredFireCapacity * 2.55f)))
                return false;
            int limit = Mathf.CeilToInt(Mathf.Max(0f, tuning.GooConsumePerSecond)
                * deltaTime * 2.55f);
            consumed = Mathf.Min(fuel, Mathf.Min(limit,
                Mathf.FloorToInt(capacity / tuning.FirePerGoo)));
            produced = Mathf.Min(capacity, Mathf.RoundToInt(consumed * tuning.FirePerGoo));
            if (consumed > 0 && produced > 0)
            {
                // 世界燃烧需要传递点火热量：少量燃料产生的 5~14 Fire 低于下一格的
                // 点火阈值，会让前沿立即熄灭。热量种子不等同于新增液体 GMU；下一 Tick
                // 新火再点邻格，保留 Wet/容量限制，也不一次遍历整滩粘液。
                int ignitionHeat=Mathf.Min(byte.MaxValue,
                    Mathf.CeilToInt(tuning.FireThreshold*2.55f)+26);
                produced=Mathf.Max(produced,Mathf.Min(capacity,Mathf.Max(0,ignitionHeat-targetFire)));
            }
            return consumed > 0 && produced > 0;
        }
    }
}
