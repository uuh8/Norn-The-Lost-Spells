using System.Reflection;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Game.Rendering.Tests
{
    /// <summary>检验实际Renderer如何消费同步窗口，避免只测readmodel却遗漏Capture与CPU缓存生命周期。</summary>
    public sealed class GridLiquidReplicaPlaybackTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _host;
        private LiquidWorldProfile _profile;
        private LiquidBehaviorProfile _water;
        private GridLiquidFieldRenderProfile _renderProfile;
        private WorldReplicaStore _store;
        private WorldReplicaLiquidReadModel _source;
        private GridLiquidFieldSurfaceRenderer _renderer;
        private static readonly Vector3Int Cell = new Vector3Int(1, 2, 1);
        private static readonly WorldChunkKey Key = new WorldChunkKey(WorldMaterialDomain.Liquid, 0, 0, 0);

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("Replica liquid playback regression"); _host.SetActive(false);
            _profile = ScriptableObject.CreateInstance<LiquidWorldProfile>();
            _water = ScriptableObject.CreateInstance<LiquidBehaviorProfile>();
            JsonUtility.FromJsonOverwrite("{\"_maximumChunks\":8,\"_maximumActiveCells\":256,\"_maximumStableRegions\":4,\"_maximumStableRegionCells\":64,\"_maximumStableSurfacePatches\":16,\"_resolutionScale\":2,\"_unitVolumeMultiplier\":4}", _profile);
            typeof(LiquidWorldProfile).GetField("_materials", Private).SetValue(_profile, new[] { _water });
            _renderProfile = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GridLiquidFieldRenderProfile>(
                "Assets/_Project/ScriptableObjects/ElementField/Liquids/GridLiquidFieldRenderProfile_Default.asset"));
            JsonUtility.FromJsonOverwrite("{\"_budgetMiB\":32,\"_inputChunks\":16,\"_views\":16,\"_fineViews\":16,\"_cullByCamera\":false}", _renderProfile);
            var geometry = new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, .5f, 4);
            _store = new WorldReplicaStore(7, 8, 256, 0, 256);
            Assert.That(_store.ConfigureGeometry(geometry), Is.True);
            Assert.That(_store.ConfigureMaterials(WorldMaterialDomain.Liquid, 510, new[] { (byte)MaterialId.Water }), Is.True);
            _source = new WorldReplicaLiquidReadModel(_store, geometry, _profile, .25f, null); _source.ResetConnection(3);
            Install(20, 1);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_host); Object.DestroyImmediate(_renderProfile);
            Object.DestroyImmediate(_profile); Object.DestroyImmediate(_water);
        }

        [Test]
        public void CapturedFrameAndCpuCopyKeepTheirIntervalAfterTheSourceAdvances()
        {
            var local = _host.AddComponent<LiquidWorldRuntime>(); typeof(LiquidWorldRuntime).GetField("_profile", Private).SetValue(local, _profile);
            Assert.That(local.TryInitialize(Vector3.zero, .25f, 4, _host.AddComponent<ElementFieldSolidBaker>()), Is.True);
            Assert.That(local.TryAddInitial(Cell, MaterialId.Water, 20, false), Is.True); local.Step(.1f);
            var frame = new GridLiquidFieldFrame(local, 16, 64); frame.Capture();
            var copied = new GridLiquidFieldFrame(local, 16, 64, snapshotOnly: true); copied.CopyFrom(frame);
            local.Step(.2f);
            Assert.That(local.TransportInterval, Is.EqualTo(.2f).Within(.0001f));
            Assert.That(frame.Interval, Is.EqualTo(.1f).Within(.0001f), "已冻结样本不能读取下一Tick的interval。");
            Assert.That(copied.Interval, Is.EqualTo(.1f).Within(.0001f), "远页CPU快照必须复制原窗口时长。");
        }

        [Test]
        public void RendererUsesSynchronizedAlphaAndRecaptureDoesNotRestartTheWindow()
        {
            RequireCompute(); PublishMoving(); StartRenderer(); Late();
            Assert.That(_renderer.InterpolationAlpha, Is.EqualTo(.25f).Within(.0001f));
            typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Capture", Private).Invoke(_renderer, null); Late();
            Assert.That(_renderer.InterpolationAlpha, Is.EqualTo(.25f).Within(.0001f), "重新打包GPU页不拥有播放时钟。");
            _source.AdvancePresentation(10.15, false); Late();
            Assert.That(_renderer.InterpolationAlpha, Is.EqualTo(.5f).Within(.0001f));
        }

        [Test]
        public void MotionOnlyPublicationRefreshesAnInitiallyStaticReplica()
        {
            RequireCompute(); StartRenderer(); Late();
            Assert.That(_renderer.Frame.Read(Cell, 0, false).OffsetCells, Is.EqualTo(Vector3.zero));
            Assert.That(_source.TryPublish(Frame(1, 10, 20, 1), 10), Is.True); Late();
            Assert.That(_renderer.Frame.Read(Cell, 0, false).OffsetCells.y, Is.EqualTo(-.2f).Within(.001f),
                "Quantity版本没变、ActiveCellCount原为0，也必须读取新motion姿态。");
        }

        [Test]
        public void LateMotionDoesNotRewindAnAlreadyPresentedQuantityEndpoint()
        {
            RequireCompute(); PublishMoving(); StartRenderer(); Late();
            Install(7, 2); _source.AdvancePresentation(10.2, false); Late();
            Assert.That(_renderer.Frame.Read(Cell, 0, false).Quantity, Is.EqualTo(7));
            Assert.That(_source.TryPublish(Frame(3, 10.2, 7, 2), 10.225), Is.True); Late();
            Assert.That(_renderer.InterpolationAlpha, Is.EqualTo(1), "数量端点已经可见，迟到sidecar不能倒放Previous。");
            Assert.That(_source.TryPublish(Frame(4, 10.3, 7, 2), 10.325), Is.True); Late();
            Assert.That(_renderer.InterpolationAlpha, Is.EqualTo(.25f).Within(.0001f), "保守snap只影响该窗口，后续连续窗口恢复正常。");
        }

        [Test]
        public void CollectionInvalidationInterruptsAnOldPendingCpuSurface()
        {
            PublishMoving(); JsonUtility.FromJsonOverwrite("{\"_fineViews\":0}", _renderProfile); StartRenderer(); Late();
            Assert.That(_renderer.CpuFallback, Is.True);
            Assert.That(_renderer.Frame.Read(Cell, 0, false).Quantity, Is.EqualTo(20));
            Install(7, 2); _source.AdvancePresentation(10.15, false); Late();
            Assert.That(_renderer.Frame.Read(Cell, 0, false).Quantity, Is.EqualTo(7),
                "旧CPU构建不能阻挡采集后新数量的Capture，更不能把旧壳继续当成有效世界。");
            var cpu = (GridLiquidFieldCpuSurface)typeof(GridLiquidFieldSurfaceRenderer).GetField("_cpu", Private).GetValue(_renderer);
            if (_renderer.IsPresentationPending)
                Assert.That(cpu.HasVisibleSurface, Is.False, "新的CPU构建尚未完成时，应明确没有旧数量可见壳。");
        }

        [Test]
        public void PartialCrossChunkQuantityInstallHoldsTheLastValidatedEndpointUntilMotionCanPublish()
        {
            RequireCompute(); PublishMoving(); StartRenderer(); Late();
            var target = new Vector3Int(4, 2, 1);
            var targetKey = new WorldChunkKey(WorldMaterialDomain.Liquid, 1, 0, 0);
            var next = CrossChunkCandidate();
            // 可选候选能力必须在数量凑齐前保持“旧已验证端点”，不能安装候选的未来GMU。
            var stage = typeof(WorldReplicaLiquidReadModel).GetMethod("StagePresentationCandidate");
            Assert.That(stage, Is.Not.Null, "完整sidecar需要一个有界的只读候选衔接入口。");
            stage.Invoke(_source, new object[] { next, 10.225 });
            var tombstone = new WorldChunkPayloadHeader(7, 2, Key, 1, 0, 2, 103, 0, 2166136261u, false);
            Assert.That(_store.TryInstall(tombstone, null, 0), Is.True);
            _source.AdvancePresentation(10.23, false); Late();
            Assert.That(_store.TotalQuantity(WorldMaterialDomain.Liquid), Is.Zero, "候选不得替数量Store补量。");
            Assert.That(_renderer.Frame.PlaybackWindowId, Is.EqualTo(2));
            Assert.That(_renderer.InterpolationAlpha, Is.EqualTo(1), "这里只保留已验证旧端点，不播放尚未安装的未来运动。");
            InstallAt(target, targetKey, 20, 1);
            Assert.That(_source.TryPublish(next, 10.25), Is.True); Late();
            Assert.That(_renderer.Frame.PlaybackWindowId, Is.EqualTo(3));
            Assert.That(_renderer.InterpolationAlpha, Is.EqualTo(.5f).Within(.0001f),
                "跨Chunk中间安装态不能触发raw→snap，让每个合法窗口都只剩10Hz终点。");
        }

        [TestCase("harvest")]
        [TestCase("timeout")]
        [TestCase("interest")]
        [TestCase("unrelated")]
        [TestCase("frozen")]
        [TestCase("connection")]
        public void PendingCandidateCannotHideLaterCollectionOrLifecycleInvalidation(string invalidation)
        {
            RequireCompute(); PublishMoving(); StartRenderer(); Late();
            _source.StagePresentationCandidate(CrossChunkCandidate(), 10.225);
            Assert.That(_store.TryInstall(new WorldChunkPayloadHeader(7, 2, Key, 1, 0, 2, 103, 0, 2166136261u, false), null, 0), Is.True);
            _source.AdvancePresentation(10.23, false); Late();
            Assert.That(_source.PresentationHolding, Is.True);
            var target = new Vector3Int(4, 2, 1); var key = new WorldChunkKey(WorldMaterialDomain.Liquid, 1, 0, 0);
            switch (invalidation)
            {
                case "harvest": InstallAt(target, key, 7, 2); break; // 高于候选引用Revision，哪怕仍有水也必须撤旧壳。
                case "timeout": _source.StagePresentationCandidate(CrossChunkCandidate(), 10.32); _source.AdvancePresentation(10.34, false); break;
                case "interest": InstallAt(target, key, 20, 1); Assert.That(_store.TryEvictInterest(key), Is.True); break;
                case "unrelated": InstallAt(new Vector3Int(8, 2, 1), new WorldChunkKey(WorldMaterialDomain.Liquid, 2, 0, 0), 9, 1); break;
                case "frozen": _source.AdvancePresentation(10.23, true); break;
                case "connection": _source.ResetConnection(4); break;
            }
            Late();
            if (invalidation == "unrelated")
            {
                Assert.That(_source.PresentationHolding, Is.True, "无关row变化不必打断这一组合法搬运。");
                Assert.That(_renderer.Frame.Read(new Vector3Int(8, 2, 1), 0, false).Quantity, Is.EqualTo(9),
                    "只保留候选覆盖的row，不能冻结整份Frame遮住其它物品采集/材料变化。");
                return;
            }
            Assert.That(_source.PresentationHolding, Is.False);
            Assert.That(_source.TryGetPresentationAlpha(2, out _), Is.False);
            Assert.That(_renderer.Frame.PlaybackWindowId, Is.Zero, "旧端点不得遮住已确认的数量变化或生命周期失效。");
            Assert.That(_renderer.Frame.Read(Cell, 0, false).Quantity, Is.Zero);
        }

        private static WorldLiquidMotionFrame CrossChunkCandidate()
        {
            var next = Frame(3, 10.2, 0, 2); next.AnchorCount = 2; next.RowCount = 2;
            next.Anchors[0] = new WorldLiquidMotionAnchor(Key, 1, 2, 1, false);
            next.Anchors[1] = new WorldLiquidMotionAnchor(new WorldChunkKey(WorldMaterialDomain.Liquid, 1, 0, 0), 1, 1, 1, true);
            next.Rows[0] = new WorldLiquidMotionSample(Cell, (byte)MaterialId.Water, 20, 0,
                new Vector3(0, -.2f, 0), Vector3.down, Vector3.zero, Vector3.zero, 0);
            next.Rows[1] = new WorldLiquidMotionSample(new Vector3Int(4, 2, 1), (byte)MaterialId.Water, 0, 20,
                Vector3.zero, Vector3.zero, new Vector3(0, -.3f, 0), Vector3.down, 1);
            return next;
        }

        private static void RequireCompute()
        { if (!SystemInfo.supportsComputeShaders) Assert.Ignore("此用例需要真实Compute Renderer；CPU路径由独立用例验证。"); }
        private void StartRenderer()
        {
            _renderer = _host.AddComponent<GridLiquidFieldSurfaceRenderer>(); _renderer.ConfigureForValidation(_source, _renderProfile);
            typeof(GridLiquidFieldSurfaceRenderer).GetMethod("Start", Private).Invoke(_renderer, null);
            Assert.That(_renderer.IsReady, Is.True);
        }
        private void Late() => typeof(GridLiquidFieldSurfaceRenderer).GetMethod("LateUpdate", Private).Invoke(_renderer, null);
        private void PublishMoving()
        { Assert.That(_source.TryPublish(Frame(1, 10, 20, 1), 10.025), Is.True); Assert.That(_source.TryPublish(Frame(2, 10.1, 20, 1), 10.125), Is.True); }
        private static WorldLiquidMotionFrame Frame(ulong sequence, double time, uint quantity, ulong revision)
        {
            var frame = new WorldLiquidMotionFrame { WorldEpoch = 7, ConnectionEpoch = 3, Sequence = sequence,
                MaterialTick = (uint)(100 + sequence), SampleTime = time, Interval = .1f, ChunkSize = 4, AnchorCount = 1, RowCount = 1 };
            frame.Anchors[0] = new WorldLiquidMotionAnchor(Key, 1, revision, 1, true);
            frame.Rows[0] = new WorldLiquidMotionSample(Cell, (byte)MaterialId.Water, quantity, quantity,
                new Vector3(0, -.1f, 0), Vector3.down, new Vector3(0, -.2f, 0), Vector3.down, 0);
            return frame;
        }
        private void Install(uint quantity, ulong revision)
            => InstallAt(Cell, Key, quantity, revision);
        private void InstallAt(Vector3Int cell, WorldChunkKey key, uint quantity, ulong revision)
        {
            var words = new uint[18]; words[0] = WorldChunkPayloadCodec.Magic; words[1] = 1; words[3] = 1;
            WorldChunkPayloadCodec.WriteGeometry(words, 4, new WorldGridGeometry(WorldMaterialDomain.Liquid, Vector3.zero, .5f, 4));
            WorldChunkPayloadCodec.WriteCell(words, 11, new WorldMaterialCellSample(WorldMaterialDomain.Liquid, WorldMaterialStorage.Resident,
                cell, (byte)MaterialId.Water, quantity));
            Assert.That(_store.TryInstall(new WorldChunkPayloadHeader(7, revision, key, 1, 0, revision, 100,
                words.Length, WorldChunkPayloadCodec.ComputeChecksum(words, words.Length), true), words, words.Length), Is.True);
        }
    }
}
