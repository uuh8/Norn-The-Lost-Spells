#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Game.Character;
using Game.Combat;
using Game.Skills;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Game.Core;
using Game.UI;
using Unity.Profiling;
using Object=UnityEngine.Object;
namespace Game.Run.Tests
{
    /// <summary>分开测纯Build、一次性Spawn、稳定Physics；不把局部零分配说成整帧零分配。</summary>
    public sealed class SpellProgrammingV2PerformanceTests
    {
        private object _positiveAllocation;
        private static readonly ProfilerMarker IdleMarker=new ProfilerMarker("Norn.V2.Verify.Idle");
        private static readonly ProfilerMarker ControlMarker=new ProfilerMarker("Norn.V2.Verify.PositiveAllocation");
        [UnityTest]public IEnumerator Frozen128OutputsAnd512LiveArrowsHaveMeasuredCosts()
        {
            var previous=SceneManager.GetActiveScene();var scene=SceneManager.CreateScene("V2PerformanceIsolated");SceneManager.SetActiveScene(scene);
            var created=new List<GameObject>(512);var spell=ScriptableObject.CreateInstance<SpellDefinition>();
            try
            {
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/Art/Prefabs/Arrow.prefab");Assert.That(prefab,Is.Not.Null);
                spell.ProjectilePrefab=prefab;spell.LifetimeSeconds=20;spell.BaseSpeed=1;spell.CastSfx=null;
                var definitions=new SpellDefinition[128];for(int i=0;i<definitions.Length;i++)definitions[i]=spell;
                Assert.That(SpellProgramCompiler.TryCompile(definitions,new SpellProgramCompileInputs(128),out var program,out _),Is.True);
                var initial=WandDrawSnapshot.CreateInitial(program);var inputs=new SpellExecutionInputs(100);var buffer=new SpellBuildBuffer();var machine=new SpellProgramMachine();
                machine.TryBuild(program,initial,inputs,buffer,out _);var watch=new Stopwatch();long memory=GC.GetAllocatedBytesForCurrentThread();watch.Start();
                Assert.That(machine.TryBuild(program,initial,inputs,buffer,out _),Is.True);watch.Stop();long buildBytes=GC.GetAllocatedBytesForCurrentThread()-memory;double buildMs=watch.Elapsed.TotalMilliseconds;
                Assert.That(buffer.Graph.Outputs.Count,Is.EqualTo(128));int baseline=ProjectileBase.ActiveCount;watch.Restart();memory=GC.GetAllocatedBytesForCurrentThread();
                for(int batch=0;batch<4;batch++)foreach(var output in buffer.Graph.Outputs)
                {
                    var go=Object.Instantiate(output.Command.ProjectilePrefab,new Vector3(batch*3,30+output.Id*.05f,0),Quaternion.identity);created.Add(go);
                    var projectile=go.GetComponent<ProjectileBase>();projectile.ConfigureLifetime(20);projectile.Init(1,98765,1,DamageType.Magical,Vector3.forward,null,false);
                }
                watch.Stop();long spawnBytes=GC.GetAllocatedBytesForCurrentThread()-memory;double spawnMs=watch.Elapsed.TotalMilliseconds;
                Assert.That(ProjectileBase.ActiveCount-baseline,Is.EqualTo(512));var frames=new List<float>(180);
                for(int i=0;i<90;i++){yield return null;frames.Add(Time.unscaledDeltaTime*1000);}
                frames.Sort();Directory.CreateDirectory("Temp/Spells/Performance");
                long positiveStart=GC.GetAllocatedBytesForCurrentThread();_positiveAllocation=new object[1024];bool byteApiSupported=GC.GetAllocatedBytesForCurrentThread()>positiveStart;
                File.WriteAllText("Temp/Spells/Performance/128-512.txt","Scope=Editor synthetic 128-instruction technical graph (not a 128-slot owned wand), four direct frozen-output Arrow batches; 512 same-team Rigidbody/CCD, no material/VFX budget claim\nBuildMs="+buildMs+"\nManagedByteApiSupported="+byteApiSupported+"\nBuildManagedBytes="+(byteApiSupported?buildBytes.ToString():"unavailable")+"\nSpawn512Ms="+spawnMs+"\nSpawnManagedBytes="+(byteApiSupported?spawnBytes.ToString():"unavailable")+"\nP95FrameMs="+frames[84]+"\nP99FrameMs="+frames[88]);
            }
            finally{foreach(var go in created)if(go!=null)Object.Destroy(go);Object.Destroy(spell);if(previous.IsValid()&&previous.isLoaded)SceneManager.SetActiveScene(previous);}
            yield return SceneManager.UnloadSceneAsync(scene);
        }
        [UnityTest]public IEnumerator NativeProfilerChecksIdleResourceTickAndGridWithPositiveControl()
        {
            bool recording=UnityEditorInternal.ProfilerDriver.enabled,cpu=UnityEditorInternal.ProfilerDriver.IsAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU);
            var objects=new List<Object>();var host=new GameObject("V2NativeAllocationScope");objects.Add(host);
            try
            {
                var parameters=ScriptableObject.CreateInstance<WandDefinition>();objects.Add(parameters);var item=ScriptableObject.CreateInstance<ItemDefinition>();objects.Add(item);
                var data=new SerializedObject(item);data.FindProperty("_contentId").stringValue="perf.wand";data.FindProperty("_kind").intValue=(int)ItemKind.Wand;data.FindProperty("_wandDefinition").objectReferenceValue=parameters;data.ApplyModifiedPropertiesWithoutUndo();
                var catalog=ScriptableObject.CreateInstance<ItemCatalog>();objects.Add(catalog);data=new SerializedObject(catalog);var definitions=data.FindProperty("_definitions");definitions.arraySize=1;definitions.GetArrayElementAtIndex(0).objectReferenceValue=item;data.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(catalog.TryCreateIndex(out var index,out _),Is.True);var store=new ItemOwnershipStore(index);store.TryAdd(new RuntimeItemState("wand","perf.wand",0),new ItemLocation(ItemLocationKind.EquippedWand,"player",0,0,0,0));
                var wands=new WandLoadoutService(new SinglePlayerProfile("player",store),"perf");wands.TryGetExecution("wand",out _);
                var emit=ScriptableObject.CreateInstance<SpellDefinition>();objects.Add(emit);var modifier=ScriptableObject.CreateInstance<SpellDefinition>();objects.Add(modifier);modifier.Kind=SpellKind.Modify;modifier.ModDamageAddFlat=5;
                SpellProgramCompiler.TryCompile(new[]{modifier,emit},new SpellProgramCompileInputs(1),out var program,out _);
                var presenter=host.AddComponent<WandProgramWorkbenchPresenter>();presenter.InitializeOwned();presenter.BindOwned(program,WandDrawSnapshot.CreateInitial(program),new SpellExecutionInputs(100),program,0);
                var viewport=host.GetComponentInChildren<WandProgramCanvasViewport>();var scheduler=new OwnedWandFireScheduler();int steps=presenter.CurrentRoot.Trace.Steps.Count;
                UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,true);UnityEditorInternal.ProfilerDriver.enabled=true;
                for(int i=0;i<16;i++){presenter.Seek(i%2==0?0:steps);viewport.SetZoom(i%2==0?.8f:1);wands.Tick(.01f);yield return null;}
                int first=UnityEditorInternal.ProfilerDriver.lastFrameIndex+1;
                for(int i=0;i<40;i++)
                {
                    using(ControlMarker.Auto())_positiveAllocation=new object[128];
                    using(IdleMarker.Auto())for(int j=0;j<100;j++){scheduler.CanAttempt(j,j,true,true,1,true);scheduler.CanAttempt(j,j,false,true,0,true);}
                    wands.Tick(.01f);presenter.Seek(i%2==0?0:steps);viewport.SetZoom(i%2==0?.8f:1);yield return null;
                }
                int last=UnityEditorInternal.ProfilerDriver.lastFrameIndex;
                string[] names={"Norn.V2.Verify.PositiveAllocation","Norn.V2.Verify.Idle","Norn.WandResources.Tick","Norn.WandPreview.Refresh","Norn.WandPreview.Zoom"};
                var bytes=new long[names.Length];var samples=new int[names.Length];
                for(int f=Math.Max(first,UnityEditorInternal.ProfilerDriver.firstFrameIndex);f<=last;f++)
                {
                    using var frame=UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(f,0);if(!frame.valid)continue;
                    for(int s=0;s<frame.sampleCount;s++)for(int m=0;m<names.Length;m++)if(frame.GetSampleName(s)==names[m])
                    {
                        samples[m]++;int end=s+frame.GetSampleChildrenCountRecursive(s);
                        for(int c=s+1;c<=end;c++)if(frame.GetSampleName(c)=="GC.Alloc" && frame.GetSampleMetadataCount(c)>0)bytes[m]+=frame.GetSampleMetadataAsLong(c,0);
                    }
                }
                Directory.CreateDirectory("Temp/Spells/Performance");var lines=new List<string>{"Scope=40 warm Editor frames, actual native Profiler GC.Alloc subtrees, positive allocation control; excludes discrete Build/Spawn and whole-frame allocations"};
                for(int i=0;i<names.Length;i++)lines.Add(names[i]+" samples="+samples[i]+" GCBytes="+bytes[i]);File.WriteAllLines("Temp/Spells/Performance/native-hot-scopes.txt",lines);
                UnityEditorInternal.ProfilerDriver.SaveProfile("Temp/Spells/Performance/native-hot-scopes.data");
                Assert.That(bytes[0],Is.GreaterThan(0),"阳性对照必须检出分配，否则零值没有证明力。");
                for(int i=1;i<names.Length;i++){Assert.That(samples[i],Is.GreaterThan(0),names[i]);Assert.That(bytes[i],Is.Zero,names[i]);}
            }
            finally{UnityEditorInternal.ProfilerDriver.enabled=recording;UnityEditorInternal.ProfilerDriver.SetAreaEnabled(UnityEngine.Profiling.ProfilerArea.CPU,cpu);foreach(var value in objects)if(value!=null)Object.Destroy(value);}
            yield return null;
        }
    }
}
#endif
