using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Game.Character;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Multiplayer;
using Game.Run;
using UnityEngine;

namespace Game.Diagnostics
{
    public sealed partial class CellularNativeFrameBenchmark
    {
        private const string BottleId = "item.container.container_01";
        private static readonly MaterialId[] BottleSpecies = { MaterialId.Water, MaterialId.Sand, MaterialId.Oil, MaterialId.Steam };
        private enum BottleStep { Before, Water, WaterRest, OpenPit, PitRest, Mixed, MixedRest }
        private bool _bottleView, _mixCaptured;
        private readonly BottleReport _bottles = new BottleReport();
        private BottleStep _bottleStep;
        private long _lastBottleTick = -1, _lastPourTick, _lastShapeTick, _restStartedTick;
        private int _unchangedTicks;
        private string _previousShape;
        private Vector3 _outlet;
        private static readonly Vector3 PourAim = new Vector3(.94f, -.34f, 0f).normalized;
        private ContainerDepositAdapter _depositAdapter;
        private ContainerUseDefinition _use;
        private readonly BottleSource[] _sources = new BottleSource[4];

        [Serializable] private sealed class BottleReport
        {
            public string Status, Error, Map, SourceHash, GraphicsAPI, BottleItem = BottleId, AvatarPrefab, FinishUtc;
            public string Method = "Formal Home clone, fixed Wizard visual and side view with Home orbit lens. Source doses use the normal world-side container deposit transaction. After the isolated 1032 GMU checkpoint water dose settles, six raycast excavation commands open a 3 x 2 x 1 pit at its wet edge. The open pit is photographed before the next material tick. No water is added afterward.";
            public string Thickness = "Each XZ column sums active and compressed GMU vertically, scaled by cell size / capacity. This is an equivalent filled depth; airborne gas and grains may occupy a taller span. All occupied columns within 5 horizontal meters of the nozzle are counted. Global GMU includes existing Home contents.";
            public Vector3 GroundSize, OriginalGroundCenter, CameraPosition, CameraForward, ActorFeet, Outlet, Aim, InitialVelocity;
            public PitReport Excavation = new PitReport();
            public string LastPhase;
            public long LastObservedTick;
            public float Fov, CellMeters, TickSeconds, PourRate, GasRate, LiquidRadius, GasRadius;
            public int CapacityPerCell, CapacityPerBottle, Width, Height;
            public string TerrainAtSurface, TerrainUnderSurface, TerrainAboveSurface;
            public BottleFrame LastWorldState;
            public List<BottleFrame> Frames = new List<BottleFrame>();
        }

        [Serializable] private sealed class BottleFrame
        {
            public string Stage, Screenshot;
            public long WorldTick;
            public int TicksSinceLastPour, ObservedUnchangedLiquidAndSandTicks, DirtyVisiblePages;
            public BottleDistribution[] Materials = new BottleDistribution[4];
        }

        [Serializable] private sealed class BottleDistribution
        {
            public string Material, LastRejectedNozzleState;
            public long InitialDoseGmu, SubmittedGmu, GlobalGmu, LocalGmu, LocalRestingGmu;
            public int OccupiedCells, FootprintColumns, StackedColumns, MaxOccupiedCellsPerColumn, CapacityRejections;
            public long RemainingInBottleGmu;
            public Vector3 LocalQuantityCentroid;
            public float CentroidHorizontalDistanceFromActor;
            public int MinOccupiedY, MaxOccupiedY;
            public float MedianEquivalentDepthMm, MeanEquivalentDepthMm, MaxEquivalentDepthMm, FootprintM2;
            [NonSerialized] public ulong Shape;
            [NonSerialized] public readonly Dictionary<Vector2Int, Column> Columns = new Dictionary<Vector2Int, Column>();
        }
        private struct Column { public uint Occupied; public long Gmu; }

        // 诊断仅替代玩家在长按 H 期间提供的库存来源；预约与扣量依然由正式世界端的事务决定。
        // 某个喷口被沙堵住时保留其余量，下一 Tick 再试，禁止为截图静默换个空位置。
        private sealed class BottleSource : IMaterialTransferSource
        {
            internal readonly MaterialId Material;
            internal readonly ulong Initial;
            internal ulong Remaining;
            internal int CapacityRejections, ConsecutiveRejections;
            internal string LastRejectedNozzleState;
            private ulong _reserved;
            internal BottleSource(MaterialId material, ulong amount) { Material = material; Initial = Remaining = amount; }
            public bool TryReserve(MaterialAmount[] parts, int count, out MaterialTransferFailure failure)
            {
                bool valid = count == 1 && _reserved == 0 && parts[0].MaterialKey == (byte)Material &&
                             parts[0].Quantity > 0 && parts[0].Quantity <= Remaining;
                failure = valid ? MaterialTransferFailure.None : MaterialTransferFailure.InvalidRequest;
                if (valid) _reserved = parts[0].Quantity;
                return valid;
            }
            public void Commit() { Remaining -= _reserved; _reserved = 0; }
            public void Cancel() { _reserved = 0; }
        }

        private void ConfigureBottleView(Vector3 groundCenter)
        {
            var combat = Resources.Load<OnlineCombatDefinition>("Online/CombatDefinition");
            _use = combat != null ? combat.ContainerUse : null;
            if (_use == null || combat.Catalog == null || !combat.Catalog.TryCreateIndex(out var index, out _) ||
                !index.TryGetRules(BottleId, out var rules) || rules.ContainerCapacity == 0)
                throw new InvalidOperationException("缺少正式瓶子容量或倾倒配置。");
            _depositAdapter = _world.GetComponent<ContainerDepositAdapter>();
            if (_depositAdapter == null) throw new InvalidOperationException("正式 Home 缺少容器入场事务。");
            _depositAdapter.enabled = true;
            ulong[] dose = { 1032, rules.ContainerCapacity, 305, Math.Min(rules.ContainerCapacity, (ulong)(_use.PourRate * _use.GasPourMultiplier)) };
            for (int i = 0; i < dose.Length; i++)
            {
                if (dose[i] > rules.ContainerCapacity || (rules.ContainerMaterials & (1 << (byte)BottleSpecies[i])) == 0)
                    throw new InvalidOperationException("正式瓶子的容量或材料掩码无法接受本次用量。");
                _sources[i] = new BottleSource(BottleSpecies[i], dose[i]);
            }
            // 身前 0.8 m、手高 1.2 m 与在线和单机共用的 PrepareEmission 几何一致。
            // 保留 WizardPlayer 可见模型、Idle 姿势和 Prefab 比例；不实例化玩家命令与网络控制器。
            var movement = Resources.Load<OnlineMovementProfile>("Online/MovementProfile");
            var original = movement != null && movement.PlayerPrefab != null ? movement.PlayerPrefab.GetComponentInChildren<Animator>(true) : null;
            if (original == null || original.transform.parent != movement.PlayerPrefab.transform)
                throw new InvalidOperationException("不能从正式法师提取原尺寸模型。");
            // Home 房间 checkpoint 14 的 1032 GMU 水迹中心。只重放投放，不读取或改写玩家存档。
            Vector3 landing = groundCenter + new Vector3(-3.25f, 0, 8.625f);
            Vector3 feet = landing - Vector3.right * 1.8f;
            var figure = new GameObject("Home Wizard visual scale reference").transform;
            figure.position = feet; figure.rotation = Quaternion.LookRotation(Vector3.right); figure.gameObject.SetActive(false);
            var animator = Instantiate(original, figure, false);
            foreach (var script in animator.GetComponentsInChildren<MonoBehaviour>(true)) { script.enabled = false; Destroy(script); }
            foreach (var collider in animator.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            animator.applyRootMotion = false;
            figure.gameObject.SetActive(true);
            animator.Update(0); animator.speed = 0;
            _outlet = feet + figure.forward * .8f + Vector3.up * 1.2f;
            // 相机在倾倒方向的侧方，脚到目标的间距会投影到屏幕横向。相机朝向和真正的喷射方向独立。
            Vector3 focus = Vector3.Lerp(feet, landing, .6f) + Vector3.up * .38f;
            _camera.fieldOfView = 70f;
            _camera.transform.position = focus + new Vector3(.06f, .70f, -.71f).normalized * 5.23f;
            _camera.transform.LookAt(focus);
            _bottles.Map = _report.MapSource; _bottles.SourceHash = _report.MapSourceHash;
            _bottles.GraphicsAPI = _report.GraphicsAPI; _bottles.AvatarPrefab = movement.PlayerPrefab.name;
            _bottles.CameraPosition = _camera.transform.position; _bottles.CameraForward = _camera.transform.forward;
            _bottles.ActorFeet = feet; _bottles.Outlet = _outlet; _bottles.GroundSize = _ground.size;
            _bottles.Aim = PourAim; _bottles.InitialVelocity = PourAim * _use.PourSpeed + Vector3.up * _use.PourLift;
            _bottles.OriginalGroundCenter = _ground.center; _bottles.Fov = _camera.fieldOfView;
            _bottles.CapacityPerCell = checked((int)_world.CellularCellCapacity);
            _bottles.CapacityPerBottle = checked((int)rules.ContainerCapacity);
            _bottles.CellMeters = _world.CellSize; _bottles.TickSeconds = _world.CellularTickDuration;
            _bottles.PourRate = _use.PourRate; _bottles.GasRate = _use.PourRate * _use.GasPourMultiplier;
            _bottles.LiquidRadius = _use.DepositRadius; _bottles.GasRadius = _use.GasDepositRadius;
            var below = Vector3Int.FloorToInt((feet - Vector3.up * .001f - _world.Origin) / _world.CellSize);
            _bottles.TerrainAtSurface = _world.GetTerrainMaterial(below).ToString();
            _bottles.TerrainUnderSurface = _world.GetTerrainMaterial(below + Vector3Int.down).ToString();
            _bottles.TerrainAboveSurface = _world.GetTerrainMaterial(below + Vector3Int.up).ToString();
        }

        private void UpdateBottleView()
        {
            if (_capturingImage) return;
            long tick = _world.WorldTick;
            if (_bottleStep == BottleStep.Before) { QueueBottleFrame("before", BottleStep.Water); return; }
            if (tick == _lastBottleTick) return;
            _lastBottleTick = tick;
            if (_bottleStep == BottleStep.OpenPit)
            {
                OpenBottlePit();
                BeginBottleRest(BottleStep.PitRest);
                QueueBottleFrame("water-pit-open", BottleStep.PitRest);
                return;
            }
            if (_bottleStep == BottleStep.Water)
            {
                Pour(0, 0);
                if (_sources[0].Remaining == 0) BeginBottleRest(BottleStep.WaterRest);
                return;
            }
            if (_bottleStep == BottleStep.Mixed)
            {
                Pour(1, 0); Pour(2, -.45f); Pour(3, .75f);
                if (!_mixCaptured && (_sources[1].Initial > _sources[1].Remaining || _sources[1].ConsecutiveRejections >= 5))
                { _mixCaptured = true; QueueBottleFrame("pouring", BottleStep.Mixed); return; }
                if (PourFinished(_sources[1]) && PourFinished(_sources[2]) && PourFinished(_sources[3]))
                    BeginBottleRest(BottleStep.MixedRest);
                return;
            }
            if (tick - _lastShapeTick < 5) return;
            var sample = MeasureBottleFrame();
            string shape = sample.Materials[0].Shape + "/" + sample.Materials[1].Shape + "/" + sample.Materials[2].Shape;
            _unchangedTicks = shape == _previousShape ? _unchangedTicks + checked((int)(tick - _lastShapeTick)) : 0;
            _previousShape = shape; _lastShapeTick = tick;
            if (tick - _restStartedTick < 30 || _unchangedTicks < 20 && tick - _restStartedTick < 180) return;
            if (_bottleStep == BottleStep.WaterRest)
            {
                LocateBottlePit(sample.Materials[0]);
                QueueBottleFrame("water-1032", BottleStep.OpenPit);
            }
            else if (_bottleStep == BottleStep.PitRest) QueueBottleFrame("water-after-dig", BottleStep.Mixed);
            else QueueBottleFrame("settled", BottleStep.MixedRest, true);
        }

        private void BeginBottleRest(BottleStep step)
        {
            _bottleStep = step; _restStartedTick = _lastShapeTick = _world.WorldTick; _unchangedTicks = 0; _previousShape = null;
        }

        private static bool PourFinished(BottleSource source) => source.Remaining == 0 || source.ConsecutiveRejections >= 40;

        private void Pour(int slot, float lateralMeters)
        {
            var source = _sources[slot];
            if (PourFinished(source)) return;
            bool gas = source.Material == MaterialId.Steam;
            float rate = _use.PourRate * (gas ? _use.GasPourMultiplier : 1);
            ulong budget = Math.Min(source.Remaining, Math.Min(_use.MaximumPourBatchGmu,
                (ulong)Math.Max(1, Mathf.RoundToInt(rate * _world.CellularTickDuration))));
            var exchange = new MaterialDepositExchange
            {
                WorldId = _depositAdapter.WorldId, Position = _outlet + Vector3.forward * lateralMeters,
                Radius = gas ? _use.GasDepositRadius : _use.DepositRadius, Source = source,
                HasInitialMotion = true, InitialVelocity = _bottles.InitialVelocity,
                Amounts = new[] { new MaterialAmount((byte)source.Material, budget) }, Count = 1
            };
            EventBus<MaterialWorldDepositRequestedEvent>.Publish(new MaterialWorldDepositRequestedEvent { Exchange = exchange });
            if (exchange.Handled && exchange.Failure == MaterialTransferFailure.Capacity)
            {
                source.CapacityRejections++; source.ConsecutiveRejections++;
                if (source.ConsecutiveRejections == 1 || source.ConsecutiveRejections == 40)
                {
                    var cell = Vector3Int.FloorToInt((exchange.Position - _world.Origin) / _world.CellSize);
                    _world.TryGetCellularContents(cell, out var material, out var amount, out var steam, out var vapor);
                    _world.CellularRestingLiquids.TryGetCell(cell, out var cold, out var coldAmount);
                    source.LastRejectedNozzleState = $"Tick {_world.WorldTick}, {cell}, failure Capacity, dynamic {material}/{amount}, gas {steam}/{vapor}, resting {cold}/{coldAmount}, terrain {_world.GetTerrainMaterial(cell)}";
                    if (source.ConsecutiveRejections == 40)
                    {
                        GameLog.Warn($"{source.Material} 喷口连续被拒 40 Tick，剩余 {source.Remaining} GMU；{source.LastRejectedNozzleState}。","CellularBenchmark");
                        if (slot == 0) throw new InvalidOperationException("1032 GMU 水喷口持续被拒。");
                    }
                }
                return;
            }
            if (!exchange.Handled || exchange.Failure != MaterialTransferFailure.None || exchange.Transferred != budget)
                throw new InvalidOperationException($"正式容器世界端未完整提交 {source.Material} {budget} GMU ({exchange.Handled}, {exchange.Failure})。");
            _lastPourTick = _world.WorldTick; source.ConsecutiveRejections = 0;
        }

        private void QueueBottleFrame(string label, BottleStep next, bool final = false)
        {
            _capturingImage = true;
            _world.SetSimulationSuspended(true);
            StartCoroutine(CaptureBottleFrame(label, next, final));
        }

        private IEnumerator CaptureBottleFrame(string label, BottleStep next, bool final)
        {
            yield return null; yield return null;
            double deadline = Time.realtimeSinceStartupAsDouble + 8;
            while (_renderer.NativeFrame.DirtyVisiblePages > 0 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
            yield return new WaitForEndOfFrame();
            if (_phase != Phase.BottleView) yield break;
            try
            {
                if (_renderer.NativeFrame.CameraId != _camera.GetInstanceID() || _renderer.NativeFrame.Frame != Time.frameCount)
                    throw new InvalidOperationException("截图帧没有经过正式元胞 Renderer 和指定 Camera。");
                var frame = MeasureBottleFrame();
                frame.Stage = label;
                frame.DirtyVisiblePages = _renderer.NativeFrame.DirtyVisiblePages;
                frame.Screenshot = Path.Combine(Path.GetDirectoryName(_path), Path.GetFileNameWithoutExtension(_path) + "-" + label + ".png");
                var image = ScreenCapture.CaptureScreenshotAsTexture();
                if (image == null) throw new InvalidOperationException("玩家视角截图读回失败。");
                try
                {
                    _bottles.Width = image.width; _bottles.Height = image.height;
                    File.WriteAllBytes(frame.Screenshot, image.EncodeToPNG());
                }
                finally { Destroy(image); }
                _bottles.Frames.Add(frame);
                if (label == "water-1032" || label == "water-pit-open" || label == "water-after-dig") SaveBottleSection(label, frame);
                if (final) { Finish(null); }
                else { _capturingImage = false; _bottleStep = next; _world.SetSimulationSuspended(false); }
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        private BottleFrame MeasureBottleFrame()
        {
            var frame = new BottleFrame
            {
                WorldTick = _world.WorldTick, TicksSinceLastPour = checked((int)(_world.WorldTick - _lastPourTick)),
                ObservedUnchangedLiquidAndSandTicks = _unchangedTicks
            };
            for (int i = 0; i < BottleSpecies.Length; i++)
                frame.Materials[i] = new BottleDistribution { Material = BottleSpecies[i].ToString(),
                    InitialDoseGmu = (long)_sources[i].Initial, SubmittedGmu = (long)(_sources[i].Initial - _sources[i].Remaining),
                    CapacityRejections = _sources[i].CapacityRejections, RemainingInBottleGmu = (long)_sources[i].Remaining,
                    LastRejectedNozzleState = _sources[i].LastRejectedNozzleState };
            _world.VisitCellularOccupiedCells(cell =>
            {
                if (!_world.TryGetCellularContents(cell, out var primary, out uint amount, out uint steam, out _)) return;
                if (amount != 0) RecordBottleCell(frame, cell, primary, amount, false);
                if (steam != 0) RecordBottleCell(frame, cell, MaterialId.Steam, steam, false);
            });
            var resting = _world.CellularRestingLiquids;
            for (int i = 0; i < resting.RegionCount; i++)
            {
                var region = resting.GetRegion(i);
                if (Array.IndexOf(BottleSpecies, region.Material) < 0) continue;
                resting.VisitOccupiedBoxes(i, (box, amount) =>
                {
                    foreach (var cell in box.allPositionsWithin) RecordBottleCell(frame, cell, region.Material, amount, true);
                });
            }
            foreach (var row in frame.Materials)
            {
                row.FootprintColumns = row.Columns.Count;
                if (row.FootprintColumns == 0) continue;
                float mmPerGmu = 1000 * _world.CellSize / _world.CellularCellCapacity;
                var depths = new List<float>(row.FootprintColumns);
                foreach (var column in row.Columns.Values)
                {
                    depths.Add(column.Gmu * mmPerGmu);
                    if (column.Occupied > 1) row.StackedColumns++;
                    row.MaxOccupiedCellsPerColumn = Math.Max(row.MaxOccupiedCellsPerColumn, (int)column.Occupied);
                }
                depths.Sort();
                row.MedianEquivalentDepthMm = (depths[(depths.Count - 1) / 2] + depths[depths.Count / 2]) / 2;
                row.MeanEquivalentDepthMm = row.LocalGmu * mmPerGmu / row.FootprintColumns;
                row.MaxEquivalentDepthMm = depths[depths.Count - 1];
                row.FootprintM2 = row.FootprintColumns * _world.CellSize * _world.CellSize;
                row.LocalQuantityCentroid /= row.LocalGmu;
                Vector3 offset = row.LocalQuantityCentroid - _bottles.ActorFeet;
                row.CentroidHorizontalDistanceFromActor = new Vector2(offset.x, offset.z).magnitude;
            }
            return frame;
        }

        private void RecordBottleCell(BottleFrame frame, Vector3Int cell, MaterialId material, uint amount, bool resting)
        {
            int i = Array.IndexOf(BottleSpecies, material);
            if (i < 0) return;
            var row = frame.Materials[i]; row.GlobalGmu += amount;
            Vector3 p = _world.Origin + ((Vector3)cell + Vector3.one * .5f) * _world.CellSize - _outlet;
            if (p.x * p.x + p.z * p.z > 25) return;
            row.MinOccupiedY = row.OccupiedCells == 0 ? cell.y : Math.Min(row.MinOccupiedY, cell.y);
            row.MaxOccupiedY = row.OccupiedCells == 0 ? cell.y : Math.Max(row.MaxOccupiedY, cell.y);
            row.OccupiedCells++; row.LocalGmu += amount;
            row.LocalQuantityCentroid += (p + _outlet) * amount;
            if (resting) row.LocalRestingGmu += amount;
            var key = new Vector2Int(cell.x, cell.z);
            row.Columns.TryGetValue(key, out var column);
            column.Occupied++; column.Gmu += amount; row.Columns[key] = column;
            // 活动槽的遍历次序不稳定。按坐标和真实 GMU 混合后做可交换累加，冷/热表示交接不改变形状。
            unchecked
            {
                ulong h = (uint)cell.x; h = h * 1099511628211UL ^ (uint)cell.y;
                h = h * 1099511628211UL ^ (uint)cell.z; h = h * 1099511628211UL ^ amount;
                h = (h ^ (h >> 30)) * 0xbf58476d1ce4e5b9UL;
                row.Shape += h ^ (h >> 27);
            }
        }

        private void FinishBottleView(string error)
        {
            _bottles.LastPhase = _bottleStep.ToString(); _bottles.LastObservedTick = _world == null ? 0 : _world.WorldTick;
            if (_sources[3] != null && _world != null && _world.IsInitialized && !_world.IsSimulationFaulted)
            {
                try { _bottles.LastWorldState = MeasureBottleFrame(); }
                catch (Exception ex) { error = (error ?? string.Empty) + "\nFinal measurement: " + ex.Message; }
            }
            if (error == null && (_bottles.Frames.Count != 6 || !_bottles.Excavation.Conserved ||
                _bottles.Excavation.FilledWaterGmu <= 0 || _bottles.Excavation.OpenedCells != PitCellCount))
                error = "未取得完整的守恒入坑和混合投放证据，已保留实际读数。";
            if (error == null && (_sources[1].Remaining != 0 || _sources[2].Remaining != 0 || _sources[3].Remaining != 0))
                error = "喷口拒绝了部分投放，已保留剩余量与各阶段图片。";
            _bottles.Status = error == null ? "Captured" : "Incomplete"; _bottles.Error = error ?? string.Empty;
            _bottles.FinishUtc = DateTime.UtcNow.ToString("O");
            try { File.WriteAllText(_path, JsonUtility.ToJson(_bottles, true)); }
            catch (Exception ex) { error = ex.ToString(); }
            if (error != null) GameLog.Error(error, "CellularBenchmark");
            Application.Quit(error == null ? 0 : 2);
        }
    }
}
