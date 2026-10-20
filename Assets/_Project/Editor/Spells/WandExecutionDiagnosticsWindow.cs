using System;
using System.Collections.Generic;
using Game.Core;
using Game.Run;
using Game.Skills;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>临时SO和真实归属表组成的隔离服务入口；不打开Scene、不写存档、不Instantiate世界弹体。</summary>
    public sealed class WandExecutionDiagnosticsWindow : EditorWindow
    {
        private const string WandId = "sandbox.wand";
        private SpellProgramSandboxDefinition _sample;
        private readonly List<UnityEngine.Object> _ownedAssets = new List<UnityEngine.Object>();
        private ItemOwnershipStore _store;
        private SinglePlayerProfile _profile;
        private WandLoadoutService _wands;
        private WandExecutionTransactionService _service;
        private ShotExecutionGraph _graph;
        private string _ticket, _message;
        private ulong _sequence;
        private int _layer = 1;
        private Vector2 _scroll;
        public uint InventoryVersion => _store?.Version ?? 0;

        [MenuItem("Norn/法术/打开资源事务 Sandbox")]
        public static void Open() => GetWindow<WandExecutionDiagnosticsWindow>("法杖资源事务诊断");
        private void OnEnable()
        { _sample = AssetDatabase.LoadAssetAtPath<SpellProgramSandboxDefinition>("Assets/_Project/Tests/Fixtures/Spells/Workbench/SevenCard.asset"); }
        private void OnDisable() => Release();
        private void OnGUI()
        {
            EditorGUILayout.HelpBox("仅临时内存库存：准备→提交/取消。不会扣真实玩家资源，也不代表世界已发射。Cast/Reload/Shuffle使用样例的独立固定属性。", MessageType.Info);
            _sample = (SpellProgramSandboxDefinition)EditorGUILayout.ObjectField("规则样例", _sample, typeof(SpellProgramSandboxDefinition), false);
            using (new EditorGUI.DisabledScope(_sample == null || EditorApplication.isPlaying))
                if (GUILayout.Button("创建 / 重置隔离会话")) Build();
            if (!string.IsNullOrEmpty(_message)) EditorGUILayout.HelpBox(_message, MessageType.None);
            if (_service == null) return;
            using (new EditorGUI.DisabledScope(_service.IsPending))
            {
                if (GUILayout.Button("准备下一次根施法")) Send(CastResourceOperation.PrepareRoot, 0);
                if (GUILayout.Button("推进1秒（恢复与等待）")) _wands.Tick(1);
            }
            _layer = EditorGUILayout.IntField("操作层（根为0）", _layer);
            using (new EditorGUI.DisabledScope(_ticket == null))
            {
                if (GUILayout.Button("准备选中子层")) Send(CastResourceOperation.PrepareLayer, _layer);
                if (GUILayout.Button("提交选中层")) Send(CastResourceOperation.Complete, _layer);
                if (GUILayout.Button("取消选中层")) Send(CastResourceOperation.Cancel, _layer);
                if (GUILayout.Button("放弃未触发子层")) Send(CastResourceOperation.Abandon, _layer);
            }
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (_service.TryGetState(WandId, out var state))
            {
                EditorGUILayout.LabelField($"Mana {state.Mana:0.##} · Cast {state.CastRemaining:0.##}s · Reload {state.ReloadRemaining:0.##}s");
                EditorGUILayout.LabelField($"Program {state.Program.Revision} · Execution {state.Draw.ExecutionRevision} · Inventory {InventoryVersion}");
                EditorGUILayout.LabelField("deck", Pile(state.Program, state.Draw.Deck));
                EditorGUILayout.LabelField("discard", Pile(state.Program, state.Draw.Discard));
            }
            if (_graph != null)
                foreach (var scope in _graph.Scopes)
                {
                    EditorGUILayout.LabelField($"层{scope.Id} · 父层{scope.ParentScopeId} · 输出{scope.OutputIds.Count}", EditorStyles.boldLabel);
                    foreach (int id in scope.OutputIds)
                    { var output = _graph.Outputs[id]; EditorGUILayout.LabelField($"  {output.SourceInstanceId} · Damage {output.Command.Damage} · 子层 {output.PayloadScopeId}"); }
                }
            EditorGUILayout.LabelField($"保留记录 {_service.Archive.Count}/3 · 有效票据 {_service.ActiveReservations}");
            if (_service.Archive.TryGetLatest(WandId, out var replay))
            {
                EditorGUILayout.LabelField($"最近根：资源已提交，世界 {replay.WorldStatus}");
                for (int i = 0; i < replay.Graph.Scopes.Count; i++) EditorGUILayout.LabelField($"  层{i}资源：{replay.GetLayerStatus(i)}");
            }
            EditorGUILayout.EndScrollView();
        }
        private static string Pile(SpellProgram program, IReadOnlyList<int> pile)
        {
            var names = new string[pile.Count];
            for (int i = 0; i < pile.Count; i++) names[i] = program.Instructions[pile[i]].InstanceId;
            return names.Length == 0 ? "空" : string.Join(" → ", names);
        }
        private void Send(CastResourceOperation operation, int layer)
        {
            var result = _service.Execute(Request(operation, layer));
            if (operation == CastResourceOperation.PrepareRoot && result.Success)
            { _ticket = result.TicketId; _service.TryGetGraph(_ticket, out _graph); _layer = 0; }
            _message = $"{operation} · {(result.Success ? "成功" : result.Reason.ToString())}";
        }
        private CastResourceRequestedEvent Request(CastResourceOperation operation, int layer)
        {
            _service.TryGetState(WandId, out var state); long revision = state.Program.Revision;
            if (operation != CastResourceOperation.PrepareRoot && _ticket != null && _graph != null) revision = _graph.Program.Revision;
            return new CastResourceRequestedEvent { InventoryId = "sandbox", SourceWandId = WandId, CallerId = "window",
                RequestId = ++_sequence, TicketId = _ticket, Operation = operation, LayerIndex = layer, ProgramRevision = (uint)revision,
                ExecutionRevision = state.Draw.ExecutionRevision, ResourceVersion = state.ResourceVersion, InventoryVersion = _store.Version };
        }
        private void Build()
        {
            Release(); _sequence = 0; _ticket = null; _graph = null; _message = null;
            if (_sample == null || _sample.Spells == null || _sample.Spells.Length > 16 || _sample.Spells.Length == 0)
            { _message = "请选择1—16槽的独立样例。"; return; }
            try
            {
                var wand = Own(ScriptableObject.CreateInstance<WandDefinition>());
                var parameters = new SerializedObject(wand);
                parameters.FindProperty("_programCapacity").intValue = Math.Max(1, _sample.Spells.Length);
                parameters.FindProperty("_baseDraws").intValue = _sample.BaseDraws;
                parameters.FindProperty("_maxMana").floatValue = _sample.MaxMana;
                parameters.FindProperty("_manaRegenPerSecond").floatValue = _sample.ManaRegenPerSecond;
                parameters.FindProperty("_castCooldown").floatValue = _sample.CastDelay;
                parameters.FindProperty("_baseReloadTime").floatValue = _sample.ReloadTime;
                parameters.FindProperty("_shuffle").boolValue = _sample.Shuffle;
                parameters.ApplyModifiedPropertiesWithoutUndo();
                var definitions = new List<ItemDefinition> { Definition("sandbox.wand", ItemKind.Wand, wand, null) };
                var fallback = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Arrow.prefab");
                for (int i = 0; i < _sample.Spells.Length; i++)
                {
                    if (_sample.Spells[i] == null) continue;
                    // 规则样例可无Prefab；真实资源preflight使用专用克隆补引用，不写原SO。
                    var spell = Own(Instantiate(_sample.Spells[i]));
                    if (spell.ProjectilePrefab == null && spell.Kind == SpellKind.Emit) spell.ProjectilePrefab = fallback;
                    definitions.Add(Definition("sandbox.card." + i, ItemKind.Spell, null, spell));
                }
                var catalog = Own(ScriptableObject.CreateInstance<ItemCatalog>()); var data = new SerializedObject(catalog);
                var entries = data.FindProperty("_definitions"); entries.arraySize = definitions.Count;
                for (int i = 0; i < definitions.Count; i++) entries.GetArrayElementAtIndex(i).objectReferenceValue = definitions[i];
                data.ApplyModifiedPropertiesWithoutUndo();
                if (!catalog.TryCreateIndex(out var index, out string error)) throw new InvalidOperationException(error);
                _store = new ItemOwnershipStore(index);
                if (!_store.TryAdd(new RuntimeItemState(WandId, "sandbox.wand", 0),
                    new ItemLocation(ItemLocationKind.EquippedWand, "sandbox.actor", 0, 0, 0, 0))) throw new InvalidOperationException("测试法杖安装失败。");
                for (int i = 0; i < _sample.Spells.Length; i++) if (_sample.Spells[i] != null)
                {
                    var kind = _sample.Spells[i].Kind; bool qualification = kind == SpellKind.Modify || kind == SpellKind.Multicast;
                    if (!_store.TryAdd(new RuntimeItemState("card." + i, "sandbox.card." + i, 3, qualification),
                        new ItemLocation(ItemLocationKind.WandSlot, WandId, 0, 0, 0, i))) throw new InvalidOperationException("测试卡安装失败。");
                }
                var profile = new SinglePlayerProfile("sandbox.actor", _store); _wands = new WandLoadoutService(profile, "sandbox");
                _profile = profile;
                var eligibility = new RaidSpellEligibility(profile);
                _service = new WandExecutionTransactionService(profile, _wands, eligibility, "sandbox", new WandExecutionReplayArchive());
                if (_sample.InitialMana < 0 || _sample.InitialMana > _sample.MaxMana) throw new InvalidOperationException("起始Mana超出范围。");
                _wands.Execute(new WandResourceRequestedEvent { InventoryId = "sandbox", SourceWandId = WandId, CallerId = "initial",
                    RequestId = 1, ManaCost = _sample.MaxMana - _sample.InitialMana });
                for (int i = 0; i < _sample.StartAfterRoots; i++)
                {
                    var root = _service.Execute(Request(CastResourceOperation.PrepareRoot, 0));
                    if (!root.Success) throw new InvalidOperationException(root.Reason.ToString());
                    _ticket = root.TicketId; _service.TryGetGraph(_ticket, out _graph);
                    var complete = _service.Execute(Request(CastResourceOperation.Complete, 0));
                    if (!complete.Success) throw new InvalidOperationException(complete.Reason.ToString());
                    _wands.Tick(Math.Max(_sample.CastDelay, .05f));
                }
                _message = "隔离内存库存就绪；普通卡每实例3次，修正/多重为无限资格卡。";
            }
            catch (Exception exception) { _message = exception.Message; Release(); }
        }
        private ItemDefinition Definition(string content, ItemKind kind, WandDefinition wand, SpellDefinition spell)
        {
            var definition = Own(ScriptableObject.CreateInstance<ItemDefinition>()); var data = new SerializedObject(definition);
            data.FindProperty("_contentId").stringValue = content; data.FindProperty("_kind").intValue = (int)kind;
            data.FindProperty("_wandDefinition").objectReferenceValue = wand; data.FindProperty("_spellDefinition").objectReferenceValue = spell;
            data.ApplyModifiedPropertiesWithoutUndo(); return definition;
        }
        private T Own<T>(T asset) where T : UnityEngine.Object
        { asset.hideFlags = HideFlags.HideAndDontSave; _ownedAssets.Add(asset); return asset; }
        public static string ExportMigrationEvidence()
        {
            var window=CreateInstance<WandExecutionDiagnosticsWindow>();
            try
            {
                window.Build();if(window._profile==null)throw new InvalidOperationException(window._message);
                var source=ProfileSaveMapper.ToDto(window._profile,0);source.SchemaVersion=13;
                foreach(var item in source.Items)if(item.InstanceId==WandId)
                {
                    window._store.Catalog.TryGetWandStats("sandbox.wand",out var stats);item.HasRolledWandStats=true;
                    item.WandMaxMana=stats.MaxMana;item.WandManaRegen=stats.ManaRegenPerSecond;item.WandCooldown=stats.CastCooldown;
                    item.WandCapacity=stats.ProgramCapacity;item.WandBaseDraws=stats.BaseDraws;
                    item.WandReloadTime=item.WandSpreadDegrees=0;item.WandShuffle=false;
                }
                string original=JsonUtility.ToJson(source,true);
                var result=ProfileSaveMapper.TryRestore(source,window._store.Catalog,out var migrated);
                if(!result.Success)throw new InvalidOperationException(result.Message);
                string target=JsonUtility.ToJson(ProfileSaveMapper.ToDto(migrated,0),true);
                string directory=System.IO.Path.GetFullPath("Temp/Spells/MigrationSandbox");System.IO.Directory.CreateDirectory(directory);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"source.schema13.json"),original);
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"candidate.schema14.json"),target);
                var report=new System.Text.StringBuilder("# 法杖V2只读迁移报告\n\n来源为隔离样例，不读取或改写玩家正式存档。Schema13 → Schema14；源实例ID、Mana上限、Regen、Cooldown、容量和BaseDraws保持，Reload默认取旧Cooldown，Spread=0，Shuffle=false。\n\n");
                foreach(var item in migrated.Items)if(item.Item.RolledWandStats.HasValue)
                {var s=item.Item.RolledWandStats.Value;report.AppendLine($"- {item.Item.InstanceId}: Mana={s.MaxMana}, Regen={s.ManaRegenPerSecond}, Cast={s.BaseCastDelay}, Reload={s.BaseReloadTime}, Slots={s.ProgramCapacity}, Draws={s.BaseDraws}, Spread={s.BaseSpreadDegrees}, Shuffle={s.Shuffle}");}
                System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"migration-report.md"),report.ToString());return directory;
            }
            finally{DestroyImmediate(window);}
        }
        private void Release()
        {
            _service?.CancelAll(); _service?.MaterialOutputs.EndAll(); _service = null; _wands = null; _store = null; _profile=null;
            for (int i = _ownedAssets.Count - 1; i >= 0; i--) if (_ownedAssets[i] != null) DestroyImmediate(_ownedAssets[i]);
            _ownedAssets.Clear();
        }
    }
}
