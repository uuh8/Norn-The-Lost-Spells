using System;
using System.Reflection;
using Game.Combat;
using Game.Materials;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.ElementField.Tests
{
    public sealed class ElementWorldFluidRoutingTests
    {
        [Test]
        public void SparseFireCollectionPreservesSampleSequenceAcrossChunkAndCursorWrap()
        {
            const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
            var host=new GameObject("Sparse fire collector order");host.SetActive(false);
            var profile=ScriptableObject.CreateInstance<ElementWorldProfile>();
            try
            {
                JsonUtility.FromJsonOverwrite("{\"_chunkSize\":4}",profile);
                var runtime=host.AddComponent<ElementWorldRuntime>();
                var chunks=new ElementWorldChunk[3];
                for(int i=0;i<3;i++)chunks[i]=new ElementWorldChunk(new ElementChunkKey(i-1,0,0),4,64);
                int[][] fire={new[]{0,12,63},new[]{0,31},new[]{0,63}};
                for(int p=0;p<3;p++)for(int n=0;n<fire[p].Length;n++)
                {int i=fire[p][n];chunks[p].SetCell(new Vector3Int(i%4,i/4%4,i/16),new ElementCell(MaterialId.Fire,(byte)(p*10+n+1)));}
                var samples=new LiquidCellFireSample[4];
                typeof(ElementWorldRuntime).GetField("_profile",fields).SetValue(runtime,profile);
                typeof(ElementWorldRuntime).GetField("_activeChunks",fields).SetValue(runtime,new[]{chunks[2],chunks[0],chunks[1]});
                typeof(ElementWorldRuntime).GetField("_activeChunkCount",fields).SetValue(runtime,3);
                typeof(ElementWorldRuntime).GetField("_gridFireChunks",fields).SetValue(runtime,new ElementWorldChunk[3]);
                typeof(ElementWorldRuntime).GetField("_fireReactionSamples",fields).SetValue(runtime,samples);
                var sort=(Action)Delegate.CreateDelegate(typeof(Action),runtime,typeof(ElementWorldRuntime).GetMethod("CollectAndSortGridFireChunks",fields));
                var collect=(Func<int>)Delegate.CreateDelegate(typeof(Func<int>),runtime,typeof(ElementWorldRuntime).GetMethod("CollectGridFireReactionSamples",fields));
                sort();uint hash=2166136261u;int total=0;long gc=GC.GetAllocatedBytesForCurrentThread();
                for(int tick=0;tick<12;tick++)
                {
                    int count=collect();total+=count;
                    unchecked{hash=(hash^(uint)count)*16777619u;for(int i=0;i<count;i++)
                    {var s=samples[i];hash=(hash^(uint)s.GlobalCell.x)*16777619u;hash=(hash^(uint)s.GlobalCell.y)*16777619u;
                     hash=(hash^(uint)s.GlobalCell.z)*16777619u;hash=(hash^s.Amount)*16777619u;}}
                }
                long allocated=GC.GetAllocatedBytesForCurrentThread()-gc;
                System.IO.File.WriteAllText("Temp/ElementPerformance/sparse-fire-sample-sequence.json",JsonUtility.ToJson(
                    new FireSequenceReport{Utc=DateTime.UtcNow.ToString("O"),Signature=hash,Samples=total,GcBytes=allocated},true));
                Assert.That(hash,Is.EqualTo(1675499582u),"负坐标、跨页和游标绕回后的采样数量/顺序必须与原版逐位一致。");
                Assert.That(total,Is.EqualTo(48));Assert.That(allocated,Is.Zero);
            }
            finally{Object.DestroyImmediate(host);Object.DestroyImmediate(profile);}
        }
        [Serializable]private sealed class FireSequenceReport{public string Utc;public uint Signature;public int Samples;public long GcBytes;}

        [TestCase(MaterialSimulationBackendKind.ElementCell, 1, 0)]
        [TestCase(MaterialSimulationBackendKind.GpuPbfLiquid, 0, 1)]
        public void RequestReachesExactlyOneExpectedSink(
            MaterialSimulationBackendKind backend,
            int expectedCellWrites,
            int expectedFluidWrites)
        {
            var routes = new FixedRoute(MaterialId.Water, backend);
            var cell = new RecordingCellSink(true);
            var fluid = new RecordingFluidSink(true);
            ElementWriteRequest request = Request(MaterialId.Water);

            bool accepted = ElementWorldWriteRouter.TryRoute(routes, in request, cell, fluid);

            Assert.That(accepted, Is.True);
            Assert.That(cell.WriteCount, Is.EqualTo(expectedCellWrites));
            Assert.That(fluid.DepositCount, Is.EqualTo(expectedFluidWrites));
        }

        [Test]
        public void RouterHotPathAcceptsRouteContractInsteadOfWaterMode()
        {
            int routeOverloads = 0;
            foreach (MethodInfo method in typeof(ElementWorldWriteRouter).GetMethods(
                BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != nameof(ElementWorldWriteRouter.TryRoute)) continue;
                routeOverloads++;
                ParameterInfo[] parameters = method.GetParameters();
                Assert.That(parameters[0].ParameterType,
                    Is.EqualTo(typeof(IMaterialSimulationRouteReadOnly)));
                Assert.That(Array.Exists(parameters,
                    parameter => parameter.ParameterType == typeof(WaterSimulationMode)), Is.False);
            }
            Assert.That(routeOverloads, Is.GreaterThan(0));
        }

        [TestCase(MaterialSimulationBackendKind.Unsupported)]
        [TestCase((MaterialSimulationBackendKind)255)]
        public void UnsupportedOrInvalidBackendTouchesNoSink(MaterialSimulationBackendKind backend)
        {
            var routes = new FixedRoute(MaterialId.Poison, backend);
            var cell = new RecordingCellSink(true);
            var fluid = new RecordingFluidSink(true);
            ElementWriteRequest request = Request(MaterialId.Poison);

            Assert.That(ElementWorldWriteRouter.TryRoute(routes, in request, cell, fluid), Is.False);
            Assert.That(cell.WriteCount, Is.Zero);
            Assert.That(fluid.DepositCount, Is.Zero);
        }

        [Test]
        public void MissingOrNullRouteTouchesNoSink()
        {
            var routes = new FixedRoute(MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            var cell = new RecordingCellSink(true);
            var fluid = new RecordingFluidSink(true);
            ElementWriteRequest request = Request(MaterialId.Water);

            Assert.That(ElementWorldWriteRouter.TryRoute(routes, in request, cell, fluid), Is.False);
            Assert.That(ElementWorldWriteRouter.TryRoute(null, in request, cell, fluid), Is.False);
            Assert.That(cell.WriteCount, Is.Zero);
            Assert.That(fluid.DepositCount, Is.Zero);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PbfSinkFailureNeverFallsBackToCell(bool initialized)
        {
            var routes = new FixedRoute(MaterialId.Water, MaterialSimulationBackendKind.GpuPbfLiquid);
            var cell = new RecordingCellSink(true);
            var fluid = new RecordingFluidSink(false, initialized);
            ElementWriteRequest request = Request(MaterialId.Water);

            Assert.That(ElementWorldWriteRouter.TryRoute(routes, in request, cell, fluid), Is.False);
            Assert.That(cell.WriteCount, Is.Zero);
            Assert.That(fluid.DepositCount, Is.EqualTo(initialized ? 1 : 0));
        }

        [Test]
        public void NullSinkIsRejectedBySelectedBackendOnly()
        {
            ElementWriteRequest water = Request(MaterialId.Water);
            var pbf = new FixedRoute(MaterialId.Water, MaterialSimulationBackendKind.GpuPbfLiquid);
            Assert.That(ElementWorldWriteRouter.TryRoute(pbf, in water, new RecordingCellSink(true), null), Is.False);

            ElementWriteRequest fire = Request(MaterialId.Fire);
            var cell = new FixedRoute(MaterialId.Fire, MaterialSimulationBackendKind.ElementCell);
            Assert.That(ElementWorldWriteRouter.TryRoute(cell, in fire, null, new RecordingFluidSink(true)), Is.False);
        }

        [Test]
        public void ProfileDefaultsToLegacyAndSnapshotsCellWaterToggle()
        {
            ElementWorldProfile profile = ScriptableObject.CreateInstance<ElementWorldProfile>();
            ElementReactionProfile reaction = ScriptableObject.CreateInstance<ElementReactionProfile>();
            try
            {
                SetPrivateField(profile, "_reactionProfile", reaction);
                Assert.That((int)WaterSimulationMode.LegacyCell, Is.Zero);
                Assert.That(profile.WaterSimulationMode, Is.EqualTo(WaterSimulationMode.LegacyCell));
                Assert.That(profile.CreateSimulationSettings().SimulateCellWater, Is.True);
                SetPrivateField(profile, "_waterSimulationMode", WaterSimulationMode.GpuPbf);
                Assert.That(profile.CreateSimulationSettings().SimulateCellWater, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(reaction);
            }
        }

        [Test]
        public void DepositConversionPreservesCanonicalLiquidId()
        {
            var queue = new FluidSpawnQueue(2);
            var adapter = new FluidDepositQueueAdapter(queue, LiquidTable(), 64, 0.05f);
            ElementWriteRequest water = Request(MaterialId.Water, 17);
            ElementWriteRequest poison = Request(MaterialId.Poison, 16);

            Assert.That(adapter.TryEnqueueDeposit(in water), Is.True);
            Assert.That(adapter.TryEnqueueDeposit(in poison), Is.True);
            var copied = new FluidSpawnRequest[2];
            Assert.That(queue.CopyAndClear(copied), Is.EqualTo(2));
            Assert.That(copied[0].MaterialId, Is.EqualTo((uint)MaterialId.Water));
            Assert.That(copied[1].MaterialId, Is.EqualTo((uint)MaterialId.Poison));
            Assert.That(copied[1].Seed, Is.EqualTo(copied[0].Seed + 1u));
        }

        [TestCase(MaterialId.Fire)]
        [TestCase(MaterialId.Empty)]
        [TestCase((MaterialId)255)]
        public void DepositAdapterRejectsNonLiquidOrUnknownMaterial(MaterialId material)
        {
            var queue = new FluidSpawnQueue(1);
            var adapter = new FluidDepositQueueAdapter(queue, LiquidTable(), 64, 0.05f);
            ElementWriteRequest request = Request(material);

            Assert.That(adapter.TryEnqueueDeposit(in request), Is.False);
            Assert.That(queue.Count, Is.Zero);
        }

        [Test]
        public void DepositConversionUsesCeilPackingAndFalloffFlag()
        {
            var queue = new FluidSpawnQueue(1);
            var adapter = new FluidDepositQueueAdapter(queue, LiquidTable(), 64, 0.05f);
            var request = new ElementWriteRequest(
                Vector3.one, MaterialId.Water, 17, 0.75f, true,
                new Vector3(2f, 3f, 4f), Vector3.up);

            Assert.That(adapter.TryEnqueueDeposit(in request), Is.True);
            var copied = new FluidSpawnRequest[1];
            queue.CopyAndClear(copied);
            Assert.That(copied[0].ParticleCount, Is.EqualTo(3u));
            Assert.That(copied[0].InitialVelocity, Is.EqualTo(new Vector3(2f, 3f, 4f)));
            Assert.That(copied[0].Radius, Is.EqualTo(0.08660254f).Within(1e-6f));
            Assert.That(copied[0].WorldPosition.y, Is.EqualTo(1.1376026f).Within(1e-6f));
            Assert.That(copied[0].Flags, Is.EqualTo(FluidSpawnFlags.UseLinearFalloff | FluidSpawnFlags.DensityPacked));
        }

        [Test]
        public void CapacityOrQueueRejectionDoesNotClampOrAdvanceSeed()
        {
            var queue = new FluidSpawnQueue(1);
            var limited = new FluidDepositQueueAdapter(queue, LiquidTable(), 2, 0.05f);
            ElementWriteRequest tooLarge = Request(MaterialId.Water, 17);
            Assert.That(limited.TryEnqueueDeposit(in tooLarge), Is.False);

            var adapter = new FluidDepositQueueAdapter(queue, LiquidTable(), 64, 0.05f);
            ElementWriteRequest request = Request(MaterialId.Water);
            Assert.That(adapter.TryEnqueueDeposit(in request), Is.True);
            Assert.That(adapter.TryEnqueueDeposit(in request), Is.False);
            var copied = new FluidSpawnRequest[1];
            queue.CopyAndClear(copied);
            Assert.That(adapter.TryEnqueueDeposit(in request), Is.True);
            queue.CopyAndClear(copied);
            Assert.That(copied[0].Seed, Is.EqualTo(1u));
        }

        private static ElementWriteRequest Request(MaterialId material, ushort totalAmount = 16)
        {
            return new ElementWriteRequest(Vector3.zero, material, totalAmount, 0.5f, false, Vector3.zero, Vector3.up);
        }

        private static LiquidMaterialSettingsTable LiquidTable()
        {
            return new LiquidMaterialSettingsTable(new[]
            {
                new LiquidMaterialSettings(MaterialId.Water, 8u, 1f, 1000f, 0.08f, 0.001f, 12f, 1f, 0.2f, 0.02f),
                new LiquidMaterialSettings(MaterialId.Poison, 8u, 1f, 1000f, 0.08f, 0.001f, 12f, 1f, 0.2f, 0.02f),
            });
        }

        private static void SetPrivateField<T>(object target, string fieldName, T value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"缺少序列化字段 {fieldName}。");
            field.SetValue(target, value);
        }

        private sealed class FixedRoute : IMaterialSimulationRouteReadOnly
        {
            private readonly MaterialId _material;
            private readonly MaterialSimulationBackendKind _backend;
            public FixedRoute(MaterialId material, MaterialSimulationBackendKind backend) { _material = material; _backend = backend; }
            public bool TryResolve(MaterialId material, out MaterialSimulationBackendKind backend) { backend = _backend; return material == _material; }
        }

        private sealed class RecordingCellSink : IElementWriteSink
        {
            private readonly bool _result;
            public RecordingCellSink(bool result) => _result = result;
            public int WriteCount { get; private set; }
            public bool TryEnqueueWrite(in ElementWriteRequest request) { WriteCount++; return _result; }
        }

        private sealed class RecordingFluidSink : IFluidDepositSink
        {
            private readonly bool _result;
            private readonly bool _initialized;
            public RecordingFluidSink(bool result, bool initialized = true) { _result = result; _initialized = initialized; }
            public int DepositCount { get; private set; }
            public bool IsFluidInitialized => _initialized;
            public bool TryEnqueueDeposit(in ElementWriteRequest request) { DepositCount++; return _result; }
        }
    }
}
