#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Character;
using Game.Combat;
using Game.Core;
using Game.ElementField;
using Game.Materials;
using Game.Skills;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>直接附着Scene，实际配置/库存/施法/Projectile接触与世界导入；故障只修改临时SO。</summary>
    public sealed class SpellAttachmentPlayModeTests
    {
        private const string ScenePath = "Assets/_Project/Scenes/Tests/AttachmentSandbox.unity";
        private const string Root = "Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/";
        private Scene _scene;
        private SinglePlayerInventorySession _session;
        private OwnedWandCastAdapter _adapter;
        private SpellCaster _caster;
        private WizardController _wizard;
        private MaterialCollectionController _collection;
        private ContainerUseController _use;
        private ElementWorldRuntime _world;
        private ContainerSandboxMaterialAuthoring _supply;
        private SinglePlayerInventoryController _ui;
        private SpellAttachmentView _view;
        private ItemCatalog _catalog;
        private string _directory;
        private ulong _sequence;
        private float _time;
        private CursorLockMode _lock;
        private bool _cursor;
        private EditorWindow _previous;
        private readonly List<Object> _temporary = new List<Object>();
        [UnitySetUp] public IEnumerator Setup()
        {
            _time = Time.timeScale; _lock = Cursor.lockState; _cursor = Cursor.visible; Time.timeScale = 1;
            _directory = Path.GetFullPath(Path.Combine("Temp/SinglePlayer/AttachmentTests", Guid.NewGuid().ToString("N")));
            _catalog = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerContainers/Catalog.asset")); _temporary.Add(_catalog);
            var originals = (ItemDefinition[])Field(typeof(ItemCatalog), "_definitions").GetValue(_catalog); var copies = new ItemDefinition[originals.Length];
            for (int i = 0; i < copies.Length; i++)
            {
                copies[i] = Object.Instantiate(originals[i]); _temporary.Add(copies[i]);
                if (copies[i].Kind == ItemKind.Container && copies[i].WorldModel == null)
                {
                    // 当前投掷链必须有可见WorldModel；旧测试目录缺这一字段。
                    // 只给本次克隆提供临时几何，不修改正式SO，也不绕过运行时的资源校验。
                    var model = GameObject.CreatePrimitive(PrimitiveType.Capsule); model.name = "Attachment test bottle model";
                    model.SetActive(false); _temporary.Add(model); Field(typeof(ItemDefinition), "_worldModel").SetValue(copies[i], model);
                }
                if (copies[i].SpellDefinition == null) continue;
                var spell = Object.Instantiate(copies[i].SpellDefinition); _temporary.Add(spell); spell.CastSfx = null;
                Field(typeof(ItemDefinition), "_spellDefinition").SetValue(copies[i], spell);
            }
            Field(typeof(ItemCatalog), "_definitions").SetValue(_catalog, copies);
            SceneManager.sceneLoaded += Loaded;
            _scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            yield return null; yield return null; yield return null; SceneManager.sceneLoaded -= Loaded;
            Assert.That(_session.ReadOnly, Is.Not.Null); Assert.That(_view, Is.Not.Null); Assert.That(_supply.Initialized, Is.True);
        }
        private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        private T Find<T>() where T : Component
        { foreach (var root in _scene.GetRootGameObjects()) { var component = root.GetComponentInChildren<T>(true); if (component != null) return component; } return null; }
        private void Loaded(Scene scene, LoadSceneMode _)
        {
            if (scene.path != ScenePath) return; _scene = scene;
            _session = Find<SinglePlayerInventorySession>(); _adapter = Find<OwnedWandCastAdapter>(); _caster = Find<SpellCaster>(); _wizard = Find<WizardController>();
            _collection = Find<MaterialCollectionController>(); _use = Find<ContainerUseController>(); _world = Find<ElementWorldRuntime>(); _supply = Find<ContainerSandboxMaterialAuthoring>();
            _ui = Find<SinglePlayerInventoryController>(); _view = Find<SpellAttachmentView>();
            Assert.That(_session.ConfigureAndInitialize(_catalog, AssetDatabase.LoadAssetAtPath<SinglePlayerStartingInventory>("Assets/_Project/Tests/Fixtures/Run/SinglePlayerAttachments/StartingInventory.asset"), _directory), Is.True);
        }
        private void Fill(ulong quantity = 3)
        {
            _collection.transform.position = _supply.MixedSamplePosition + new Vector3(0, .5f, -.5f);
            _collection.SubmitSample(_supply.MixedSamplePosition, false, quantity); Assert.That(_collection.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
        }
        private ulong Bottle(string id)
        { _session.TryCopyContainer(id, new MaterialAmount[8], out _, out ulong total, out _); return total; }
        private string Card(int wand = 0, int slot = 0)
        { _session.WandsReadOnly.TryGetSlot(wand, out var state); _session.WandsReadOnly.TryGetProgram(state.InstanceId, out var program); return program.Program.GetInstanceId(slot); }
        private void Set(string card, string source, ulong dose = 10, bool enabled = true) => _view.Submit(card, source, dose, enabled);
        private int Cast(int slot = 0, bool down = true)
        {
            _adapter.Select(slot); Assert.That(_adapter.TryCapture(out var program, out var context), Is.True);
            return _caster.CastOwnedProgram(program, context, new Vector3(1.25f, 2, .25f),
                down ? new Vector3(1.25f, -2, .25f) : new Vector3(1.25f, 2, 30), 1, 777, _wizard.CharacterController);
        }
        [UnityTest] public IEnumerator RealCollisionDepositsPartialMixedBudgetAndBalancesFamilies()
        {
            Fill(); string bottle = _collection.SelectedContainerId; Set(Card(), bottle);
            Assert.That(Cast(), Is.EqualTo(1)); Assert.That(Bottle(bottle), Is.Zero); Assert.That(_session.PendingMaterialOutputs, Is.EqualTo(1));
            Assert.That(_view.Feedback, Does.Contain("3/10")); float until = Time.time + 3;
            while (_session.PendingMaterialOutputs > 0 && Time.time < until) yield return null;
            Assert.That(_session.PendingMaterialOutputs, Is.Zero);
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(2)); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Poison), Is.EqualTo(1));
            _world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased, out var water); Assert.That(water.IsBalanced, Is.True);
            _world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.Toxic, out var poison); Assert.That(poison.IsBalanced, Is.True);
        }
        [UnityTest] public IEnumerator SameNameUnexecutedCardDoesNotConsumeAndSourceThrownKeepsBaseCast()
        {
            Fill(30); string bottle = _collection.SelectedContainerId; Set(Card(2, 1), bottle);
            Assert.That(Cast(), Is.EqualTo(1)); Assert.That(Bottle(bottle), Is.EqualTo(30)); Assert.That(_session.PendingMaterialOutputs, Is.Zero);
            Set(Card(), bottle); _use.SubmitUse(ContainerUseKind.Throw, 0, _collection.transform.position + Vector3.forward);
            Assert.That(_use.LastResult.Success, Is.True, _use.LastResult.Transfer.Failure.ToString()); yield return new WaitForSeconds(.9f);
            Assert.That(Cast(), Is.EqualTo(1)); Assert.That(_view.Feedback, Does.Contain("供料失效"));
        }
        [UnityTest] public IEnumerator EmptySourceKeepsOrdinaryOutputAndManaCharge()
        {
            string bottle = _collection.SelectedContainerId; Set(Card(), bottle);
            _session.WandsReadOnly.TryGetSlot(0, out var before); Assert.That(Cast(), Is.EqualTo(1)); _session.WandsReadOnly.TryGetSlot(0, out var after);
            Assert.That(after.CurrentMana, Is.LessThan(before.CurrentMana)); Assert.That(_session.PendingMaterialOutputs, Is.Zero); Assert.That(_view.Feedback, Does.Contain("来源为空")); yield return null;
        }
        [UnityTest] public IEnumerator RootInitFailureRollsBackMaterialsAndEveryCreatedProxy()
        {
            Fill(); string bottle = _collection.SelectedContainerId; Set(Card(), bottle);
            _adapter.TryCapture(out var program, out _);
            var failing = new GameObject("attachment-init-fault", typeof(Rigidbody), typeof(BoxCollider), typeof(ThrowingOwnedTestProjectile)); failing.SetActive(false); _temporary.Add(failing);
            program.Program[0].ProjectilePrefab = failing; _session.WandsReadOnly.TryGetSlot(0, out var before);
            Assert.That(Cast(), Is.Zero); Assert.That(Bottle(bottle), Is.EqualTo(3)); Assert.That(_session.PendingMaterialOutputs, Is.Zero);
            _session.WandsReadOnly.TryGetSlot(0, out var after); Assert.That(after.CurrentMana, Is.EqualTo(before.CurrentMana)); Assert.That(after.CooldownRemaining, Is.Zero);
            yield return null; Assert.That(Object.FindObjectsByType<MaterialPayloadRuntimeAdapter>(FindObjectsSortMode.None), Is.Empty); Assert.That(_session.CastResourcesReadOnly.ActiveReservations, Is.Zero);
        }
        [UnityTest] public IEnumerator TriggerDoesNotPreTakeFutureMaterialsAndChildRetainsOriginalWand()
        {
            Fill(); string bottle = _collection.SelectedContainerId; Set(Card(2, 1), bottle);
            Assert.That(Cast(2, false), Is.EqualTo(1)); Assert.That(Bottle(bottle), Is.EqualTo(3)); Assert.That(_session.PendingMaterialOutputs, Is.Zero);
            var parent = Object.FindFirstObjectByType<ProjectileBase>(); _adapter.Select(1);
            var impact = (Action<Vector3, Vector3>)Field(typeof(ProjectileBase), "Impacted").GetValue(parent);
            Assert.That(impact, Is.Not.Null); impact(new Vector3(1.25f, 1, .25f), Vector3.down);
            Assert.That(Bottle(bottle), Is.Zero); Assert.That(_session.PendingMaterialOutputs, Is.EqualTo(1));
            _session.WandsReadOnly.TryGetSlot(1, out var other); Assert.That(other.CurrentMana, Is.EqualTo(60)); yield return null;
        }
        [UnityTest] public IEnumerator WorldRejectKeepsVisibleOutputAndSaveBlockedThenRetryDepositsOnce()
        {
            Fill(); Set(Card(), _collection.SelectedContainerId); var deposit = Find<ContainerDepositAdapter>(); deposit.Configure(_world, "unavailable");
            Assert.That(Cast(), Is.EqualTo(1)); yield return new WaitForSeconds(.5f);
            var proxy = Object.FindFirstObjectByType<MaterialPayloadRuntimeAdapter>(); Assert.That(proxy, Is.Not.Null); Assert.That(proxy.IsWaiting, Is.True);
            Assert.That(proxy.GetComponent<Renderer>().enabled, Is.True); Assert.That(_session.PendingMaterialOutputs, Is.EqualTo(1)); Assert.That(_session.Save().Success, Is.False);
            string id = proxy.OutputId; deposit.Configure(_world, "container-world"); yield return new WaitForSeconds(.3f);
            Assert.That(_session.PendingMaterialOutputs, Is.Zero); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(2));
            EventBus<SpellMaterialOutputEvent>.Publish(new SpellMaterialOutputEvent { InventoryId = _session.ReadOnly.InventoryId, OutputId = id, Position = Vector3.one });
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(2));
        }
        [UnityTest] public IEnumerator UiButtonsConfigureExactCardChangeDoseAndCloseWithoutSpending()
        {
            Fill(); string card = Card(); string bottle = _collection.SelectedContainerId; _ui.Open(); _ui.SelectItem(card); yield return null;
            var worldHud = (GameObject)Field(typeof(SpellAttachmentView), "_worldHud").GetValue(_view);
            Assert.That(worldHud, Is.Not.Null); Assert.That(worldHud.activeSelf, Is.False, "整备控件不能被高排序材料Canvas遮挡。");
            var sources = (Dropdown)Field(typeof(SpellAttachmentView), "_sources").GetValue(_view);
            var ids = (List<string>)Field(typeof(SpellAttachmentView), "_sourceIds").GetValue(_view);
            sources.value = ids.IndexOf(bottle); ((InputField)Field(typeof(SpellAttachmentView), "_dose").GetValue(_view)).text = "25";
            ((Button)Field(typeof(SpellAttachmentView), "_apply").GetValue(_view)).onClick.Invoke();
            _session.ReadOnly.TryGetView(card, out var configured); Assert.That(configured.Attachment.Dose, Is.EqualTo(25)); Assert.That(configured.Attachment.ContainerId, Is.EqualTo(bottle)); Assert.That(Bottle(bottle), Is.EqualTo(3));
            ((Button)Field(typeof(SpellAttachmentView), "_clear").GetValue(_view)).onClick.Invoke(); _session.ReadOnly.TryGetView(card, out configured); Assert.That(configured.Attachment, Is.Null);
            Assert.That(_session.Save().Success, Is.True); _ui.Close(); yield return null;
            Assert.That(worldHud.activeSelf, Is.True);
        }
        [UnityTest] public IEnumerator DestroyingOutputEndsOwnershipWithoutRefillingSource()
        {
            Fill(); string bottle = _collection.SelectedContainerId; Set(Card(), bottle); Assert.That(Cast(0, false), Is.EqualTo(1));
            var proxy = Object.FindFirstObjectByType<MaterialPayloadRuntimeAdapter>(); Object.Destroy(proxy.gameObject); yield return null; yield return null;
            Assert.That(_session.PendingMaterialOutputs, Is.Zero); Assert.That(Bottle(bottle), Is.Zero); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.Zero);
            Assert.That(_view.Feedback, Does.Contain("归属已结束"));
        }
        [UnityTest] public IEnumerator DisablingCommittedProxyEndsOwnershipAndReleasesSaveGuard()
        {
            Fill(); string bottle = _collection.SelectedContainerId; Set(Card(), bottle); Assert.That(Cast(0, false), Is.EqualTo(1));
            var proxy = Object.FindFirstObjectByType<MaterialPayloadRuntimeAdapter>(); proxy.enabled = false; yield return null; yield return null;
            Assert.That(_session.PendingMaterialOutputs, Is.Zero); Assert.That(Bottle(bottle), Is.Zero);
            Assert.That(_session.Save().Success, Is.True); Assert.That(Object.FindObjectsByType<MaterialPayloadRuntimeAdapter>(FindObjectsSortMode.None), Is.Empty);
        }
        [UnityTest] public IEnumerator SkyfallDelayKeepsOneBudgetAndReleasesAtActualCollision()
        {
            Fill(); Set(Card(), _collection.SelectedContainerId); _adapter.TryCapture(out var program, out var context);
            var spell = program.Program[0]; spell.Kind = SpellKind.StaticProjectile; spell.SpawnMode = SpellSpawnMode.SkyfallAtPoint;
            spell.LandingSitePrefab = null; spell.LandingSiteDuration = .2f; spell.SkyfallHeight = 2; spell.SkyfallBackOffset = 0;
            Assert.That(_caster.CastOwnedProgram(program, context, new Vector3(1.25f, 2, .25f), new Vector3(1.25f, 0, .25f), 1, 777, _wizard.CharacterController), Is.EqualTo(1));
            Assert.That(_session.PendingMaterialOutputs, Is.EqualTo(1)); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.Zero);
            yield return new WaitForSeconds(.8f); Assert.That(_session.PendingMaterialOutputs, Is.Zero); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(2));
        }
        [UnityTest] public IEnumerator StaticShieldAlsoTransfersOnlyItsApprovedMaterials()
        {
            Fill(); Set(Card(), _collection.SelectedContainerId); _adapter.TryCapture(out var program, out _);
            var prefab = new GameObject("attachment-static-test", typeof(BoxCollider), typeof(ProjectileShield)); prefab.SetActive(false); _temporary.Add(prefab);
            var spell = program.Program[0]; spell.Kind = SpellKind.StaticProjectile; spell.SpawnMode = SpellSpawnMode.StaticAtPoint; spell.ProjectilePrefab = prefab; spell.PayloadTrigger = PayloadTriggerMode.None;
            Assert.That(Cast(), Is.EqualTo(1)); Assert.That(_session.PendingMaterialOutputs, Is.Zero); Assert.That(_world.ImportedMaterialQuantity(MaterialId.Water), Is.EqualTo(2)); yield return null;
        }
        [UnityTest] public IEnumerator GasAttachmentReturnsTrueSteamToWorld()
        {
            _collection.transform.position = _supply.GasSamplePosition + new Vector3(0, .5f, -.5f);
            _collection.SubmitSample(_supply.GasSamplePosition, false, 3); Assert.That(_collection.LastFailure, Is.EqualTo(MaterialTransferFailure.None));
            Set(Card(), _collection.SelectedContainerId); Assert.That(Cast(), Is.EqualTo(1)); yield return new WaitForSeconds(.5f);
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Steam), Is.EqualTo(3)); Assert.That(_session.PendingMaterialOutputs, Is.Zero);
            _world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased, out var water); Assert.That(water.IsBalanced, Is.True);
        }
        [UnityTest] public IEnumerator ActualGameViewShowsReleasedAttachedLiquidAndConfigurationPanel()
        {
            Fill(30); string card = Card(); Set(card, _collection.SelectedContainerId, 30); Assert.That(Cast(), Is.EqualTo(1)); yield return new WaitForSeconds(.5f);
            Assert.That(_world.ImportedMaterialQuantity(MaterialId.Poison), Is.EqualTo(10));
            var camera = Find<Camera>(); var brain = camera.GetComponent("CinemachineBrain") as Behaviour; if (brain != null) brain.enabled = false;
            _wizard.transform.position = new Vector3(0, .1f, -2.5f); camera.fieldOfView = 75;
            camera.transform.position = new Vector3(0, 6, -4); camera.transform.rotation = Quaternion.LookRotation(new Vector3(.25f, 0, .25f) - camera.transform.position);
            _previous = EditorWindow.focusedWindow; EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
            yield return new WaitForSecondsRealtime(.3f); const string output = "Temp/SinglePlayer/attachment-world.png";
            yield return CaptureRenderedGameView(output);
            var texture = new Texture2D(2, 2);
            try
            {
                ImageConversion.LoadImage(texture, File.ReadAllBytes(output)); var point = camera.WorldToViewportPoint(new Vector3(1.25f, .12f, .25f));
                int cx = Mathf.RoundToInt(point.x * texture.width), cy = Mathf.RoundToInt(point.y * texture.height); bool visible = false;
                for (int y = Mathf.Max(0, cy - 30); y <= Mathf.Min(texture.height - 1, cy + 30); y++)
                for (int x = Mathf.Max(0, cx - 30); x <= Mathf.Min(texture.width - 1, cx + 30); x++)
                { Color pixel = texture.GetPixel(x, y); if (pixel.g > .2f && pixel.g > pixel.r * 1.4f && pixel.g > pixel.b * 1.25f) visible = true; }
                Assert.That(visible, Is.True, "真实附着Poison材料像素未出现。");
            }
            finally { Object.Destroy(texture); }
            _ui.Open(); _ui.SelectItem(card); yield return null; yield return new WaitForSecondsRealtime(.2f);
            const string panel = "Temp/SinglePlayer/attachment-ui.png";
            yield return CaptureRenderedGameView(panel);
        }
        private static IEnumerator CaptureRenderedGameView(string output)
        {
            // GameView切换后ScreenCapture可能得到尚未渲染的全黑帧；只等待截图就绪，材料像素断言不变。
            // 有限重试明确区分“没有画面”与“画面里没有材料”，不使用Camera.Render替代真实GameView。
            for (int attempt = 0; attempt < 6; attempt++)
            {
                DateTime capture = DateTime.UtcNow;
                EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Repaint();
                ScreenCapture.CaptureScreenshot(output); yield return null; yield return new WaitForSecondsRealtime(.3f);
                if (!File.Exists(output) || File.GetLastWriteTimeUtc(output) < capture) continue;
                var texture = new Texture2D(2, 2); bool rendered = false;
                try
                {
                    Assert.That(ImageConversion.LoadImage(texture, File.ReadAllBytes(output)), Is.True);
                    for (int y = 0; y < texture.height && !rendered; y += 32)
                    for (int x = 0; x < texture.width && !rendered; x += 32)
                    { var color = texture.GetPixel(x, y); rendered = color.r > .05f || color.g > .05f || color.b > .05f; }
                }
                finally { Object.Destroy(texture); }
                if (rendered) yield break;
            }
            Assert.Fail("有限等待后GameView截图仍未就绪或全黑；无法进行材料视觉验证。");
        }
        [UnityTest] public IEnumerator SceneUnloadEndsOutputsAndCleansProjectileSceneOwnership()
        {
            Fill(); Set(Card(), _collection.SelectedContainerId); Assert.That(Cast(0, false), Is.EqualTo(1));
            Assert.That(Object.FindFirstObjectByType<ProjectileBase>().gameObject.scene, Is.EqualTo(_scene));
            int resolved = 0; Action<SpellMaterialOutputResultEvent> listener = e => { if (e.Resolved) resolved++; };
            EventBus<SpellMaterialOutputResultEvent>.Subscribe(listener);
            try { yield return SceneManager.UnloadSceneAsync(_scene); yield return null; Assert.That(resolved, Is.EqualTo(1)); Assert.That(Object.FindObjectsByType<MaterialPayloadRuntimeAdapter>(FindObjectsSortMode.None), Is.Empty); }
            finally { EventBus<SpellMaterialOutputResultEvent>.Unsubscribe(listener); }
        }
        [UnityTest] public IEnumerator ProfilerCapturesRealCapacityWaitingPathWithoutProjectMarkerAllocations()
        {
            Fill(30); Set(Card(), _collection.SelectedContainerId);
            var liquid = _world.GetComponent<LiquidWorldRuntime>(); var cell = Vector3Int.FloorToInt((new Vector3(1.25f, .12f, .25f) - liquid.Origin) / liquid.CellSize);
            Assert.That(liquid.TryAddInitial(cell, MaterialId.Water, (uint)liquid.CellCapacityGmu), Is.True);
            Assert.That(Cast(), Is.EqualTo(1)); yield return new WaitForSeconds(.5f);
            Assert.That(Object.FindFirstObjectByType<MaterialPayloadRuntimeAdapter>().IsWaiting, Is.True);
            bool recording = UnityEditorInternal.ProfilerDriver.enabled;
            bool cpu = UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            try
            {
                // 只开启CPU记录并保留原开关，不清用户已有缓存、不启用Deep Profile；读取测量后的原始帧。
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, true);
                UnityEditorInternal.ProfilerDriver.enabled = true;
                for (int i = 0; i < 5; i++) yield return null;
                int first = UnityEditorInternal.ProfilerDriver.lastFrameIndex + 1;
                for (int i = 0; i < 60; i++) yield return null;
                int last = UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                var report = CaptureProfile(first, last);
                File.WriteAllText("Temp/SinglePlayer/attachment-profiler.json", JsonUtility.ToJson(report, true));
                UnityEditorInternal.ProfilerDriver.SaveProfile("Temp/SinglePlayer/attachment-profiler.data");
                Assert.That(report.ValidFrames, Is.GreaterThanOrEqualTo(30));
                Assert.That(report.Markers[0].Samples, Is.GreaterThan(0)); Assert.That(report.Markers[1].Samples, Is.GreaterThan(0));
                for (int i = 0; i < report.Markers.Length; i++) Assert.That(report.Markers[i].GcBytes, Is.Zero, report.Markers[i].Name);
            }
            finally
            {
                UnityEditorInternal.ProfilerDriver.enabled = recording;
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU, cpu);
            }
        }
        [Serializable] private sealed class MarkerSample { public string Name; public int Samples; public double CpuMs, MaximumMs; public long GcBytes; }
        [Serializable] private sealed class ProfileReport
        {
            public string Utc, Scene, Scope;
            public int FirstFrame, LastFrame, ValidFrames;
            public double AverageMainThreadMs, MaximumMainThreadMs;
            public long MainThreadGcBytes;
            public MarkerSample[] Markers;
        }
        private static ProfileReport CaptureProfile(int first, int last)
        {
            var names = new[] { "Norn.SpellMaterials.Proxy", "Norn.SpellMaterials.Deposit", "Norn.MaterialTransfer.Deposit", "Norn.ContainerHud.Update", "Norn.WandResources.Tick", "Norn.WandHud.Update" };
            var report = new ProfileReport { Utc = DateTime.UtcNow.ToString("O"), Scene = ScenePath, Scope = "Editor/TestRunner, Solver paused, actual full-liquid-capacity waiting/retry; CPU scopes only, not a complete GPU/performance Gate",
                FirstFrame = first, LastFrame = last, Markers = new MarkerSample[names.Length] };
            for (int i = 0; i < names.Length; i++) report.Markers[i] = new MarkerSample { Name = names[i] };
            for (int frame = Math.Max(first, UnityEditorInternal.ProfilerDriver.firstFrameIndex); frame <= last; frame++)
            {
                using var data = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame, 0);
                if (!data.valid || data.sampleCount == 0) continue;
                report.ValidFrames++; double mainMs = data.GetSampleTimeMs(0); report.AverageMainThreadMs += mainMs; report.MaximumMainThreadMs = Math.Max(report.MaximumMainThreadMs, mainMs);
                for (int sample = 0; sample < data.sampleCount; sample++)
                {
                    string name = data.GetSampleName(sample);
                    if (name == "GC.Alloc" && data.GetSampleMetadataCount(sample) > 0) report.MainThreadGcBytes += data.GetSampleMetadataAsLong(sample, 0);
                    for (int m = 0; m < names.Length; m++) if (name == names[m])
                    {
                        var stat = report.Markers[m]; double start = data.GetSampleStartTimeMs(sample), duration = data.GetSampleTimeMs(sample);
                        stat.Samples++; stat.CpuMs += duration; stat.MaximumMs = Math.Max(stat.MaximumMs, duration);
                        // 原始Main Thread时间区间定位嵌套GC.Alloc；测量结束后的导出分配不进入该历史窗口。
                        for (int child = sample + 1; child < data.sampleCount; child++)
                        {
                            double time = data.GetSampleStartTimeMs(child); if (time > start + duration) break;
                            if (time >= start && data.GetSampleName(child) == "GC.Alloc" && data.GetSampleMetadataCount(child) > 0)
                                stat.GcBytes += data.GetSampleMetadataAsLong(child, 0);
                        }
                    }
                }
            }
            if (report.ValidFrames > 0) report.AverageMainThreadMs /= report.ValidFrames; return report;
        }
        [UnityTearDown] public IEnumerator Cleanup()
        {
            SceneManager.sceneLoaded -= Loaded; if (_ui != null) _ui.Close();
            if (_scene.IsValid() && _scene.isLoaded) yield return SceneManager.UnloadSceneAsync(_scene);
            foreach (var projectile in Object.FindObjectsByType<ProjectileBase>(FindObjectsSortMode.None)) Object.Destroy(projectile.gameObject);
            foreach (var proxy in Object.FindObjectsByType<MaterialPayloadRuntimeAdapter>(FindObjectsSortMode.None)) Object.Destroy(proxy.gameObject);
            for (int i = _temporary.Count - 1; i >= 0; i--) if (_temporary[i] != null) Object.Destroy(_temporary[i]); _temporary.Clear(); yield return null;
            Time.timeScale = _time; Cursor.lockState = _lock; Cursor.visible = _cursor; if (_previous != null) _previous.Focus();
            string root = Path.GetFullPath("Temp/SinglePlayer/AttachmentTests") + Path.DirectorySeparatorChar;
            if (_directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
#endif
