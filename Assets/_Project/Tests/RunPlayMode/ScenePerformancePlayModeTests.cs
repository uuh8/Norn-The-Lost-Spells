#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Game.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object=UnityEngine.Object;

namespace Game.Run.Tests
{
    /// <summary>真实地图暖态CPU采样；仅诊断，不保存Scene、不发放物品、不关闭玩法来制造性能结果。</summary>
    public sealed class ScenePerformancePlayModeTests
    {
        private bool _profiler,_cpu,_editor,_logs,_background;
        private int _errors;
        private string _firstError;
        [SetUp] public void Setup()
        {
            _profiler=ProfilerDriver.enabled;_cpu=ProfilerDriver.IsAreaEnabled(ProfilerArea.CPU);_editor=ProfilerDriver.profileEditor;
            _logs=LogAssert.ignoreFailingMessages;_background=Application.runInBackground;
            LogAssert.ignoreFailingMessages=true;Application.runInBackground=true;
            Application.logMessageReceived+=OnLog;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor")).Focus();
        }
        private void OnLog(string text,string stack,LogType type)
        {if(type==LogType.Error||type==LogType.Exception||type==LogType.Assert){_errors++;if(_firstError==null)_firstError=text;}}
        [UnityTearDown] public IEnumerator Cleanup()
        {
            Application.logMessageReceived-=OnLog;ProfilerDriver.enabled=_profiler;ProfilerDriver.profileEditor=_editor;
            ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU,_cpu);LogAssert.ignoreFailingMessages=_logs;Application.runInBackground=_background;
            if(HubPracticeSession.Current!=null)Object.Destroy(HubPracticeSession.Current.gameObject);
            if(HomeSceneTransfer.Current!=null)Object.Destroy(HomeSceneTransfer.Current.gameObject);
            Time.timeScale=1;yield return null;
        }
        [UnityTest] public IEnumerator HubIdle(){return Capture("Assets/_Project/Scenes/SinglePlayer/SinglePlayer_Hub.unity");}
        [UnityTest] public IEnumerator P7Idle(){return Capture("Assets/_Project/Scenes/P7_DemoRun.unity");}
        [UnityTest] public IEnumerator P8Idle(){return Capture("Assets/_Project/Scenes/P8_BossField.unity");}
        private IEnumerator Capture(string path)
        {
            EditorSceneManager.LoadSceneInPlayMode(path,new LoadSceneParameters(LoadSceneMode.Single));
            for(int i=0;i<60;i++)yield return null;
            foreach(var guide in Object.FindObjectsByType<GameplayGuideController>(FindObjectsSortMode.None))guide.Close();
            Assert.That(Time.timeScale,Is.EqualTo(1),"性能基线必须是运行中的游戏。");
            ProfilerDriver.profileEditor=true;ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU,true);ProfilerDriver.enabled=true;
            for(int i=0;i<20;i++)yield return null;
            int first=ProfilerDriver.lastFrameIndex+1;
            var times=new List<float>(90);
            for(int i=0;i<90;i++){yield return null;times.Add(Time.unscaledDeltaTime*1000);}
            int last=ProfilerDriver.lastFrameIndex;ProfilerDriver.enabled=false;
            string label=File.Exists("Temp/Spells/performance-label.txt")?File.ReadAllText("Temp/Spells/performance-label.txt").Trim():"manual";
            string folder="TestResults/SpellWorldPerformance/"+label;Directory.CreateDirectory(folder);
            string name=Path.GetFileNameWithoutExtension(path);
            // 离线导出不纳入采样窗口，包含工作线程以免遗漏Shader/Render成本。
            using(var writer=new StreamWriter(folder+"/"+name+".tsv"))
            {
                writer.WriteLine("frame\tthread\tmarker\tms\tchildren\tgcBytes");
                for(int frame=Math.Max(first,ProfilerDriver.firstFrameIndex);frame<=last;frame++)for(int thread=0;thread<64;thread++)
                {
                    using var data=ProfilerDriver.GetRawFrameDataView(frame,thread);if(!data.valid)break;
                    for(int s=0;s<data.sampleCount;s++)
                    {
                        string marker=data.GetSampleName(s);float ms=data.GetSampleTimeMs(s);bool gc=marker=="GC.Alloc";
                        if(ms<.03f&&!gc&&!marker.StartsWith("Norn.",StringComparison.Ordinal)&&!marker.StartsWith("ElementWorld.",StringComparison.Ordinal))continue;
                        long bytes=gc&&data.GetSampleMetadataCount(s)>0?data.GetSampleMetadataAsLong(s,0):0;
                        writer.WriteLine(frame+"\t"+data.threadName+"\t"+marker+"\t"+ms.ToString(CultureInfo.InvariantCulture)+"\t"+data.GetSampleChildrenCountRecursive(s)+"\t"+bytes);
                    }
                }
            }
            ProfilerDriver.SaveProfile(folder+"/"+name+".data");
            times.Sort();float total=0;foreach(float t in times)total+=t;
            File.WriteAllText(folder+"/"+name+".json",JsonUtility.ToJson(new Report{Scene=path,Frames=times.Count,AverageMs=total/times.Count,
                MedianMs=times[times.Count/2],P95Ms=times[(int)(times.Count*.95f)],Errors=_errors,FirstError=_firstError},true));
            Assert.That(last,Is.GreaterThanOrEqualTo(first));
        }
        [Serializable] private sealed class Report{public string Scene,FirstError;public int Frames,Errors;public float AverageMs,MedianMs,P95Ms;}
    }
}
#endif
