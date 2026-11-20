using System;
using System.Reflection;
using Game.Core;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    /// <summary>
    /// 通过真实 LiquidWorldRuntime / Store 驱动运动来源。缺失的 sidecar 用 Reflection 查找，
    /// 使第一轮 RED 能报告缺失行为，而非令整个测试程序集因新合同尚未加入而无法编译。
    /// </summary>
    public sealed class WorldLiquidMotionSourceTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Set(object owner, string field, object value) => owner.GetType().GetField(field, Hidden).SetValue(owner, value);
        private static object Read(object owner, string field) => owner.GetType().GetField(field).GetValue(owner);
        private static void Scope(LiquidTransportHistory history, bool enabled)
        {
            var method = typeof(LiquidTransportHistory).GetMethod("SetSolverCaptureScope");
            Assert.That(method, Is.Not.Null, "History必须显式区分Solver运动与F/反应/Stable写入。");
            method.Invoke(history, new object[] { enabled });
        }
        private static void Link(LiquidTransportHistory history, Vector3Int a, Vector3Int b)
        {
            var method = typeof(LiquidTransportHistory).GetMethod("RecordSolverTransfer");
            Assert.That(method, Is.Not.Null, "跨格迁移必须以完整组输出。");
            method.Invoke(history, new object[] { a, b });
        }

        [Test]
        public void OffsetOnlyTickExportsBothPhasesWithoutChangingAuthoritativeQuantity()
        {
            using (var f = new Fixture())
            {
                Assert.That(f.Liquid.TryAddInitial(new Vector3Int(0, 4, 0), MaterialId.Water, 80), Is.True);
                f.Liquid.Step(.1f, .1f);
                Assert.That(f.Capture(), Is.True);
                Assert.That(f.Count, Is.EqualTo(1));
                object row = f.Row(0);
                Assert.That(Read(row, "PreviousQuantity"), Is.EqualTo(80u));
                Assert.That(Read(row, "CurrentQuantity"), Is.EqualTo(80u));
                Assert.That(((Vector3)Read(row, "CurrentOffset")).y, Is.LessThan(0));
                Assert.That(f.Liquid.TotalQuantity, Is.EqualTo(80UL));
            }
        }

        [Test]
        public void CrossChunkFallExportsEmptySourceAndDestinationInOneGroup()
        {
            using (var f = new Fixture())
            {
                var source = new Vector3Int(0, 4, 0);
                Assert.That(f.Liquid.TryAddInitial(source, MaterialId.Water, 80), Is.True);
                f.Liquid.Step(.4f, .4f);
                Assert.That(f.Capture(), Is.True);
                Assert.That(f.Count, Is.EqualTo(2));
                Assert.That(f.Group(0), Is.EqualTo(f.Group(1)));
                bool emptySource = false, populatedTarget = false;
                for (int i = 0; i < f.Count; i++)
                {
                    object row = f.Row(i);
                    if ((Vector3Int)Read(row, "Cell") == source)
                        emptySource = (uint)Read(row, "PreviousQuantity") == 80 && (uint)Read(row, "CurrentQuantity") == 0;
                    else populatedTarget = (uint)Read(row, "PreviousQuantity") == 0 && (uint)Read(row, "CurrentQuantity") == 80;
                }
                Assert.That(emptySource && populatedTarget, Is.True);
                Assert.That(f.Liquid.TotalQuantity, Is.EqualTo(80UL));
            }
        }

        [Test]
        public void ContinuousPendingJetKeepsRealSolverBalanceAndPublishesOnlyTheCommittedVisualBirth()
        {
            using(var f=new Fixture())
            {
                var nozzle=new Vector3Int(0,4,0);
                for(int tick=1;tick<=6;tick++)
                {
                    Assert.That(f.Liquid.TryEnqueueDeposit(new ElementWriteRequest(
                        f.Liquid.Origin+((Vector3)nozzle+Vector3.one*.5f)*f.Liquid.CellSize,
                        MaterialId.Water,40,.01f,false,new Vector3(4,2,0))),Is.True);
                    f.Liquid.Step(.1f,tick*.1f);
                    Assert.That(f.Capture(),Is.True);Assert.That(f.Count,Is.GreaterThan(0),"持续喷口注入不能污染整条Solver运动链。");
                    ulong before=0,after=0;
                    for(int i=0;i<f.Count;i++)
                    {before+=(uint)Read(f.Row(i),"PreviousQuantity");after+=(uint)Read(f.Row(i),"CurrentQuantity");}
                    // Solver内部仍从加量后的真实baseline核验组守恒；表示Previous只扣掉这次
                    // 已知且已提交的40GMU出生，才能衔接上一窗口。不能把visual差误报为丢量。
                    Assert.That(after-before,Is.EqualTo(40UL));Assert.That(after,Is.GreaterThanOrEqualTo(40));
                    Assert.That(f.SolverBaselineTotal(),Is.EqualTo(f.CurrentHistoryTotal()));
                    Assert.That(f.Liquid.TotalQuantity+f.Liquid.PendingQuantity,Is.EqualTo((ulong)tick*40));
                }
            }
        }

        [Test]
        public void CommittedBirthHasZeroVisualPreviousWhileTheRealSolverBaselineContainsTheAddedQuantity()
        {
            using(var f=new Fixture())
            {
                var nozzle=new Vector3Int(0,4,0);
                Assert.That(f.Liquid.TryEnqueueDeposit(new ElementWriteRequest(
                    f.Liquid.Origin+((Vector3)nozzle+Vector3.one*.5f)*f.Liquid.CellSize,
                    MaterialId.Water,40,.01f,false,new Vector3(1,2,0))),Is.True);
                f.Liquid.Step(.1f,.1f);
                Assert.That(f.History.ReadPrevious(nozzle,MaterialId.Water).Quantity,Is.Zero,
                    "原单机History仍保存本Tick注入前的值；不能为网络表现悄悄改掉原插值语义。");
                Assert.That(f.Capture(),Is.True);Assert.That(f.Count,Is.GreaterThan(0));
                bool found=false;
                for(int i=0;i<f.Count;i++)if((Vector3Int)Read(f.Row(i),"Cell")==nozzle)
                {Assert.That(Read(f.Row(i),"PreviousQuantity"),Is.Zero);found=true;}
                Assert.That(found,Is.True,"出生前visual Previous必须为0，不能把新水带回旧窗口。");
                Assert.That(f.SolverBaselineTotal(),Is.EqualTo(40UL),"真实Solver守恒边界仍包含已提交的新量。");
            }
        }

        [Test]
        public void BaselineAloneDoesNotPublishUntouchedPendingRowsAsSolverMotion()
        {
            using(var f=new Fixture())
            {
                f.History.BeginTick();Assert.That(f.Store.TryAdd(new Vector3Int(0,4,0),MaterialId.Water,40),Is.True);
                var prepare=typeof(LiquidTransportHistory).GetMethod("PrepareSolverMotionBaseline",Hidden);
                Assert.That(prepare,Is.Not.Null);prepare.Invoke(f.History,null);
                Assert.That(f.Capture(),Is.True);Assert.That(f.Count,Is.Zero,
                    "基线只界定本窗口数量，不授予未被Solver处理的行运动资格。");
                Assert.That(f.Liquid.TotalQuantity,Is.EqualTo(40));
            }
        }
        [Test]
        public void CollectionAfterSolverBaselineStillDisqualifiesMotionAndCannotBeRebasedTwice()
        {
            using(var f=new Fixture())
            {
                var cell=new Vector3Int(0,4,0);
                Assert.That(f.Liquid.TryAddInitial(cell,MaterialId.Water,80),Is.True);f.Liquid.Step(.1f,.1f);
                Assert.That(f.Liquid.TryConsume(cell,MaterialId.Water,80),Is.True);
                var prepare=typeof(LiquidTransportHistory).GetMethod("PrepareSolverMotionBaseline",Hidden);
                Assert.That(prepare,Is.Not.Null);prepare.Invoke(f.History,null);
                Assert.That(f.Capture(),Is.True);Assert.That(f.Count,Is.Zero,
                    "Solver开始后的F收取必须整组失效，重复baseline不能重新授予资格并放回旧水。");
                Assert.That(f.Liquid.TotalQuantity,Is.Zero);
            }
        }

        [Test]
        public void CollectionAfterTransferDisqualifiesWholeGroupWithoutLeavingSourceTail()
        {
            using (var f = new Fixture())
            {
                Vector3Int a = Vector3Int.zero, b = Vector3Int.right;
                f.Transfer(a, b, 80);
                Assert.That(f.Liquid.TryConsume(b, MaterialId.Water, 80), Is.True);
                Assert.That(f.Capture(), Is.True);
                Assert.That(f.Count, Is.Zero, "不能把F收取消失解释成需要继续播放的尾水。");
            }
        }

        [Test]
        public void NonSolverMutationBeforeTransferStillDisqualifiesTheMergedGroup()
        {
            using (var f = new Fixture())
            {
                f.History.BeginTick();
                Assert.That(f.Store.TryAdd(Vector3Int.zero, MaterialId.Water, 80), Is.True);
                Scope(f.History, true);
                Assert.That(f.Store.TryTransfer(Vector3Int.zero, Vector3Int.right, MaterialId.Water, 80), Is.True);
                Link(f.History, Vector3Int.zero, Vector3Int.right);
                Scope(f.History, false);
                Assert.That(f.Capture(), Is.True); Assert.That(f.Count, Is.Zero);
            }
        }

        [Test]
        public void CanonicalStableMixMismatchRejectsWholeMotionGroup()
        {
            using (var f = new Fixture())
            {
                f.Transfer(Vector3Int.zero, Vector3Int.right, 80);
                var quantities = f.Quantities();
                quantities.Cells[1] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid,
                    WorldMaterialStorage.Stable, Vector3Int.right, (byte)MaterialId.Water, 1);
                quantities.Header = new WorldReplicaHeader(27, 2, 0, 1);
                Assert.That(f.Capture(quantities), Is.True); Assert.That(f.Count, Is.Zero);
            }
        }

        [Test]
        public void HistoryOverflowClearsWholeCandidateAndDoesNotFaultQuantityWorld()
        {
            using (var f = new Fixture(1))
            {
                f.Transfer(Vector3Int.zero, Vector3Int.right, 80);
                Assert.That(f.Capture(), Is.False); Assert.That(f.Count, Is.Zero);
                Assert.That(f.World.IsSimulationFaulted, Is.False);
                Assert.That(f.Liquid.TotalQuantity, Is.EqualTo(80UL));
            }
        }

        [Test]
        public void NextTickClearsPreviousGroupsAndSnapshotTickMismatchFailsClosed()
        {
            using (var f = new Fixture())
            {
                f.Transfer(Vector3Int.zero, Vector3Int.right, 80);
                Assert.That(f.Capture(), Is.True); Assert.That(f.Count, Is.EqualTo(2));
                f.History.BeginTick();
                Assert.That(f.Capture(), Is.True); Assert.That(f.Count, Is.Zero);
                var quantities = f.Quantities();
                quantities.Header = new WorldReplicaHeader(26, quantities.Header.CellCount, 0, 1);
                Assert.That(f.Capture(quantities), Is.False); Assert.That(f.Count, Is.Zero);
            }
        }

        [Test]
        public void CandidateCapacityNeverPublishesAPartialMigrationGroup()
        {
            using (var f = new Fixture(8193))
            {
                for (int i = 0; i < 8193; i++)
                    Assert.That(f.Store.TryAdd(new Vector3Int(i % 4, i / 4 % 4, i / 16), MaterialId.Water, 1), Is.True);
                f.History.BeginTick(); Scope(f.History, true);
                for (int i = 0; i < 8192; i++)
                {
                    var a = new Vector3Int(i % 4, i / 4 % 4, i / 16);
                    var b = new Vector3Int((i + 1) % 4, (i + 1) / 4 % 4, (i + 1) / 16);
                    Assert.That(f.Store.TryTransfer(a, b, MaterialId.Water, 1), Is.True); Link(f.History, a, b);
                }
                Scope(f.History, false);
                Assert.That(f.Capture(), Is.False); Assert.That(f.Count, Is.Zero);
                Assert.That(f.Liquid.TotalQuantity, Is.EqualTo(8193UL));
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly GameObject Host = new GameObject("Liquid motion source fixture");
            internal readonly ElementWorldRuntime World;
            internal readonly LiquidWorldRuntime Liquid;
            internal readonly LiquidVolumeStore Store;
            internal readonly LiquidTransportHistory History;
            private readonly LiquidWorldProfile _profile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            private readonly LiquidBehaviorProfile _water = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            private object _destination;
            internal int Count => (int)Read(_destination, "Count");
            internal object Row(int index) => ((Array)Read(_destination, "Rows")).GetValue(index);
            internal int Group(int index) => ((int[])Read(_destination, "GroupIds"))[index];

            internal Fixture(int capacity = 64)
            {
                Host.SetActive(false); World = Host.AddComponent<ElementWorldRuntime>(); Liquid = Host.AddComponent<LiquidWorldRuntime>();
                World.ConfigureExecutionMode(WorldExecutionMode.Authority);
                JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":256,\"_maximumActiveCells\":64,\"_maximumCellsPerTick\":64,\"_enableAutoStableDetection\":false}", _profile);
                Set(_profile, "_maximumActiveCells", capacity); Set(_profile, "_materials", new[] { _water }); Set(Liquid, "_profile", _profile);
                var baker = Host.GetComponent<ElementFieldSolidBaker>(); Set(baker, "_includeDefaultEnvironment", false);
                Assert.That(Liquid.TryInitialize(Vector3.zero, .5f, 4, baker), Is.True);
                Set(World, "_gridLiquidRuntime", Liquid); Set(World, "<IsInitialized>k__BackingField", true); Set(World, "_worldTick", 27L);
                Store = (LiquidVolumeStore)typeof(LiquidWorldRuntime).GetField("_store", Hidden).GetValue(Liquid);
                History = (LiquidTransportHistory)typeof(LiquidWorldRuntime).GetField("_history", Hidden).GetValue(Liquid);
                // 手工Store事务夹具不推进Solver数量；只提供既有合法材料Tick间隔给只读sidecar。
                Set(typeof(LiquidWorldRuntime).GetField("_solver", Hidden).GetValue(Liquid), "_deltaTime", .1f);
            }

            internal void Transfer(Vector3Int a, Vector3Int b, uint quantity)
            {
                Assert.That(Store.TryAdd(a, MaterialId.Water, quantity), Is.True);
                History.BeginTick(); Scope(History, true);
                Assert.That(Store.TryTransfer(a, b, MaterialId.Water, quantity), Is.True);
                Link(History, a, b); Scope(History, false);
            }

            internal WorldReplicaBuffer Quantities()
            {
                var buffer = new WorldReplicaBuffer(Math.Max(1024, Store.ResidentCellCount), 0, 100000); int count = 0;
                buffer.Geometries[0] = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, .5f, 4);
                for (int i = 0; i < Store.ResidentCellCount; i++)
                {
                    var cell = Store.GetResidentCellAt(i);
                    if (Store.TryGetQuantity(cell, MaterialId.Water, out uint quantity))
                        buffer.Cells[count++] = new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Dynamic, cell, (byte)MaterialId.Water, quantity);
                }
                buffer.Header = new WorldReplicaHeader(27, count, 0, 1); return buffer;
            }

            internal bool Capture(WorldReplicaBuffer quantities = null)
            {
                var sourceType = typeof(LiquidWorldRuntime).Assembly.GetType("Game.ElementField.WorldLiquidMotionSource");
                var bufferType = typeof(GameLog).Assembly.GetType("Game.Core.WorldLiquidMotionCaptureBuffer");
                Assert.That(sourceType, Is.Not.Null, "缺少只读motion source。");
                Assert.That(bufferType, Is.Not.Null, "缺少有界motion buffer。");
                if (_destination == null) _destination = Activator.CreateInstance(bufferType);
                var source = Activator.CreateInstance(sourceType, new object[] { World });
                return (bool)sourceType.GetMethod("TryCapture").Invoke(source, new[] { _destination, quantities ?? Quantities() });
            }
            internal ulong SolverBaselineTotal()
            {
                var rows=(LiquidCellPresentationSample[])typeof(LiquidTransportHistory).GetField("_motionPrevious",Hidden).GetValue(History);
                int count=(int)typeof(LiquidTransportHistory).GetField("_count",Hidden).GetValue(History);
                ulong sum=0;for(int i=0;i<count;i++)sum+=rows[i].Quantity;return sum;
            }
            internal ulong CurrentHistoryTotal()
            {
                // 此fixture只有Water一种材料；读取真实当前Store，不修改History以配合断言。
                var cells=(Vector3Int[])typeof(LiquidTransportHistory).GetField("_cells",Hidden).GetValue(History);
                int count=(int)typeof(LiquidTransportHistory).GetField("_count",Hidden).GetValue(History);
                ulong sum=0;for(int i=0;i<count;i++)if(Store.TryGetQuantity(cells[i],MaterialId.Water,out uint q))sum+=q;return sum;
            }

            public void Dispose() { Object.DestroyImmediate(Host); Object.DestroyImmediate(_profile); Object.DestroyImmediate(_water); }
        }
    }
}
