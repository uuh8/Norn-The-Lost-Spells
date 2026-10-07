using System;
using System.IO;
using Game.Core;
using Game.ElementField;
using Game.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>
    /// 显式只读审计。Edit Mode 检查 P7/P8 接线，Play Mode 只取已加载世界的 O(1) 计数；
    /// 不枚举材料格、不启动 Solver、不安装资产，也不保存或重载用户 Dirty Scene。
    /// </summary>
    [InitializeOnLoad]
    public static class MaterialWorldPhaseAudit
    {
        private const string Request="Temp/ElementPerformance/material-world-audit.request";
        private const string Result="Temp/ElementPerformance/material-world-audit.json";
        private static double _nextPoll;
        [Serializable] private sealed class Report
        {
            public string TimestampUtc, UnityVersion, Evidence;
            public bool Playing, ScriptCompilationFailed;
            public SceneRow[] Scenes;
        }
        [Serializable] private sealed class SceneRow
        {
            public string Scene, State;
            public bool WasLoaded, DirtyBefore, DirtyAfter;
            public WorldRow[] Worlds;
        }
        [Serializable] private sealed class WorldRow
        {
            public string Name, WorldProfile, SolidProfile, GasProfile, ConversionProfile, SolidMaterial, GasMaterial;
            public bool Initialized, SolidOwner, GasOwner, SolidRenderer, GasRenderer, Exposure;
            public bool PresentationReferencesMatch, MaterialBindingsValid;
            public int WorldChunkSize, SolidMaximumChunks, GasMaximumChunks, GasMaximumActiveCells,
                SolidDepositGrainCells, SolidRuntimeMaximumChunks,
                GasMaximumCellsPerTick, GasMaximumTerrainSamplesPerTick, SolidPendingDeposits,
                SolidResidentChunks, GasPendingDeposits, GasResidentChunks, GasActiveCells,
                GasProcessedLastTick, GasTerrainSamplesLastTick, ConversionPending, ConversionProcessedLastTick,
                GasUploadedLastFrame, GasDrawnLastFrame, GasUnrepresentedLastFrame, GasCopiedChunksLastFrame;
            public bool GasRecoveryPending;
            public uint SolidRebuildCount, ConversionDeferred, ConversionOverflows;
            public uint SolidChunkCapacityRejections, SolidGeometryCapacityRejections;
            public ulong SolidQuantity, SolidPendingQuantity, GasQuantity, GasPendingQuantity, GasVentedQuantity;
            public bool WaterFamilyAvailable, WaterFamilyBalanced;
            public ulong WaterFamilyInjected, WaterFamilyResident, WaterFamilyPending, WaterFamilyVented, WaterFamilyConsumed;
            public long GasUploadedBytesLastFrame, ConfiguredGasTexelBytes;
            public float GasFadeStart, GasFadeEnd;
            public float GasDensityBlendSeconds, GasConfiguredDensityBlendSeconds;
            public Vector3 GasWindVelocity;
        }
        static MaterialWorldPhaseAudit()=>EditorApplication.update+=Poll;
        private static void Poll()
        {
            if(EditorApplication.timeSinceStartup<_nextPoll) return;
            _nextPoll=EditorApplication.timeSinceStartup+1;
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating
                || SessionState.GetBool("ElementPerformance.GateRunning",false)) return;
            File.Delete(Request);
            try { Capture(); }
            catch(Exception error)
            { File.WriteAllText(Result,"FAILED\n"+error); GameLog.Error(error.ToString(),"MaterialAudit"); }
        }
        [MenuItem("Tools/Element World/Audit Solid Gas Budgets and Presentation")]
        public static void Capture()
        {
            if(EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("当前编译失败，不能用旧 Assembly 作为接线证据。");
            var report=new Report
            {
                TimestampUtc=DateTime.UtcNow.ToString("O"),UnityVersion=Application.unityVersion,
                Playing=Application.isPlaying,ScriptCompilationFailed=EditorUtility.scriptCompilationFailed,
                Evidence=Application.isPlaying?"One explicit runtime counter snapshot; no timing/FPS claim"
                    :"Read-only Editor scene wiring/configuration audit; runtime counters are not Play evidence",
                Scenes=new SceneRow[2]
            };
            Scene active=SceneManager.GetActiveScene();
            try
            {
                report.Scenes[0]=ReadScene("Assets/_Project/Scenes/P7_DemoRun.unity");
                report.Scenes[1]=ReadScene("Assets/_Project/Scenes/P8_BossField.unity");
            }
            finally { if(active.isLoaded) SceneManager.SetActiveScene(active); }
            Directory.CreateDirectory(Path.GetDirectoryName(Result));
            File.WriteAllText(Result,JsonUtility.ToJson(report,true));
            GameLog.Info("固体/气体只读审计已保存："+Result,"MaterialAudit");
        }
        private static SceneRow ReadScene(string path)
        {
            Scene scene=SceneManager.GetSceneByPath(path); bool opened=!scene.isLoaded;
            var row=new SceneRow { Scene=path,WasLoaded=!opened,DirtyBefore=scene.isLoaded && scene.isDirty };
            if(opened && Application.isPlaying)
            { row.State="Not loaded; Play audit never opens another scene";row.Worlds=Array.Empty<WorldRow>();return row; }
            if(opened) scene=EditorSceneManager.OpenScene(path,OpenSceneMode.Additive);
            try
            {
                var worlds=new System.Collections.Generic.List<WorldRow>();
                foreach(var root in scene.GetRootGameObjects())
                    foreach(var world in root.GetComponentsInChildren<ElementWorldRuntime>(true)) worlds.Add(ReadWorld(world));
                row.Worlds=worlds.ToArray(); row.State=worlds.Count==1?"One independent world":"Unexpected world count";
                row.DirtyAfter=scene.isDirty;
            }
            finally { if(opened) EditorSceneManager.CloseScene(scene,true); }
            return row;
        }
        private static WorldRow ReadWorld(ElementWorldRuntime world)
        {
            var serialized=new SerializedObject(world);
            var solids=Reference<FixedSolidWorldRuntime>(serialized,"_fixedSolidRuntime");
            var gases=Reference<GasWorldRuntime>(serialized,"_gasRuntime");
            var worldProfile=Reference<ElementWorldProfile>(serialized,"_profile");
            var solidProfile=solids!=null?Reference<FixedSolidWorldProfile>(new SerializedObject(solids),"_profile"):null;
            var gasProfile=gases!=null?Reference<GasWorldProfile>(new SerializedObject(gases),"_profile"):null;
            var conversion=serialized.FindProperty("_conversionProfile").objectReferenceValue;
            var gasRenderer=world.GetComponent<GasFieldVolumeRenderer>();
            var solidRenderer=world.GetComponent<FixedSolidSurfaceRenderer>();
            var row=new WorldRow
            {
                Name=world.name,Initialized=world.IsInitialized,SolidOwner=solids!=null,GasOwner=gases!=null,
                SolidRenderer=solidRenderer!=null,GasRenderer=gasRenderer!=null,
                Exposure=world.GetComponent<ElementWorldExposureSystem>()!=null,
                WorldProfile=AssetDatabase.GetAssetPath(worldProfile),SolidProfile=AssetDatabase.GetAssetPath(solidProfile),
                GasProfile=AssetDatabase.GetAssetPath(gasProfile),ConversionProfile=AssetDatabase.GetAssetPath(conversion),
                WorldChunkSize=worldProfile!=null?worldProfile.ChunkSize:0,
                SolidMaximumChunks=solidProfile!=null?solidProfile.MaximumChunks:0,
                SolidDepositGrainCells=solidProfile!=null?solidProfile.DepositGrainCells:0,
                GasWindVelocity=gasProfile!=null?gasProfile.WindVelocity:Vector3.zero,
                GasMaximumChunks=gasProfile!=null?gasProfile.MaximumChunks:0,
                GasMaximumActiveCells=gasProfile!=null?gasProfile.MaximumActiveCells:0,
                GasMaximumCellsPerTick=gasProfile!=null?gasProfile.MaximumCellsPerTick:0,
                GasMaximumTerrainSamplesPerTick=gasProfile!=null?gasProfile.MaximumTerrainSamplesPerTick:0,
                ConversionPending=world.ConversionPendingCandidates,ConversionProcessedLastTick=world.ConversionProcessedLastTick,
                ConversionDeferred=world.ConversionDeferredTransactions,ConversionOverflows=world.ConversionCandidateOverflows
            };
            if(solidRenderer!=null && gasRenderer!=null)
            {
                var solidPresentation=new SerializedObject(solidRenderer);
                var gasPresentation=new SerializedObject(gasRenderer);
                row.GasConfiguredDensityBlendSeconds=gasPresentation.FindProperty("_densityBlendSeconds").floatValue;
                row.GasDensityBlendSeconds=gasRenderer.EffectiveDensityBlendSeconds;
                var solidMaterial=Reference<Material>(solidPresentation,"_material");
                var gasMaterial=Reference<Material>(gasPresentation,"_material");
                row.SolidMaterial=AssetDatabase.GetAssetPath(solidMaterial);row.GasMaterial=AssetDatabase.GetAssetPath(gasMaterial);
                row.PresentationReferencesMatch=Reference<ElementWorldRuntime>(solidPresentation,"_world")==world
                    && Reference<ElementWorldRuntime>(gasPresentation,"_world")==world;
                row.MaterialBindingsValid=solidMaterial!=null && gasMaterial!=null && solidMaterial.shader!=null && gasMaterial.shader!=null
                    && solidMaterial.shader.name=="Game/Materials/FixedSolid" && gasMaterial.shader.name=="Game/Materials/RealGasVolume";
            }
            if(solids!=null)
            {
                row.SolidPendingDeposits=solids.PendingDepositCount;row.SolidResidentChunks=solids.ResidentChunkCount;
                row.SolidQuantity=solids.TotalQuantity;row.SolidPendingQuantity=solids.PendingQuantity;
                row.SolidRebuildCount=solids.SurfaceRebuildCount;
                // 配置容量与实际已初始化池分开记录；O(1) 原因计数用于区分存储满和几何拒绝，
                // 不为诊断每帧扫描材料，也不把一次快照当成性能计时。
                row.SolidRuntimeMaximumChunks=solids.MaximumChunks;
                row.SolidChunkCapacityRejections=solids.ChunkCapacityRejections;
                row.SolidGeometryCapacityRejections=solids.GeometryCapacityRejections;
            }
            if(gases!=null)
            {
                row.GasPendingDeposits=gases.PendingDepositCount;row.GasResidentChunks=gases.ResidentChunkCount;
                row.GasActiveCells=gases.ActiveCellCount;row.GasProcessedLastTick=gases.ProcessedCellsLastTick;
                row.GasTerrainSamplesLastTick=gases.TerrainSamplesLastTick;row.GasRecoveryPending=gases.HasPendingRecovery;
                row.GasQuantity=gases.TotalQuantity;row.GasPendingQuantity=gases.PendingQuantity;row.GasVentedQuantity=gases.VentedQuantity;
            }
            if(gasRenderer!=null)
            {
                row.GasUploadedLastFrame=gasRenderer.UploadedChunksLastFrame;row.GasDrawnLastFrame=gasRenderer.DrawnChunksLastFrame;
                row.GasUnrepresentedLastFrame=gasRenderer.UnrepresentedChunksLastFrame;
                row.GasCopiedChunksLastFrame=gasRenderer.CopiedDensityChunksLastFrame;
                row.GasUploadedBytesLastFrame=gasRenderer.UploadedDensityBytesLastFrame;
                var rendering=new SerializedObject(gasRenderer);
                row.GasFadeStart=rendering.FindProperty("_fadeStart").floatValue;row.GasFadeEnd=rendering.FindProperty("_fadeEnd").floatValue;
                int dimension=row.WorldChunkSize+2;
                row.ConfiguredGasTexelBytes=(long)row.GasMaximumChunks*dimension*dimension*dimension*4;
            }
            // 显式请求才合并各形态 O(1) 库存和有界 Pending；把冷凝后的水一起计入，
            // 才能区别“有限库存缓慢转移”与真正重复创建，不能只看 Gas 的合计或网格面积。
            row.WaterFamilyAvailable=world.TryCaptureMaterialQuantityFamily(MaterialQuantityFamily.WaterBased,out var water);
            if(row.WaterFamilyAvailable)
            {
                row.WaterFamilyBalanced=water.IsBalanced; row.WaterFamilyInjected=water.Injected;
                row.WaterFamilyResident=water.Resident; row.WaterFamilyPending=water.Pending;
                row.WaterFamilyVented=water.Vented; row.WaterFamilyConsumed=water.RuleConsumed;
            }
            return row;
        }
        private static T Reference<T>(SerializedObject target,string name) where T:UnityEngine.Object
            => target.FindProperty(name).objectReferenceValue as T;
    }
}
