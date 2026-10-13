using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Game.Core;
using Game.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Game.EditorTools
{
    /// <summary>显式请求的据点诊断；采样和离线导出分开，绝不保存 Scene 或改库存。</summary>
    [InitializeOnLoad]
    internal static class SinglePlayerHubPerformanceCapture
    {
        private const string Request="Temp/SinglePlayer/hub-performance.request";
        private const string Folder="TestResults/SinglePlayer/M6-T16-LayoutPerformance";
        private static double _nextPoll, _startAt, _finishAt, _startAfter;
        private static bool _capturing, _enabled, _editor, _gpu, _cpu, _background;
        private static string _name;
        private static Keyboard _motionKeyboard;
        private static Mouse _motionMouse;
        private static KeyboardState _forward, _backward;
        private static MouseState _look;
        private static EditorWindow _gameView;
        private static PropertyInfo _toolbarProperty;
        private static bool _toolbar, _hideToolbar;
        static SinglePlayerHubPerformanceCapture()
        {
            // Enter Play 的 Domain Reload 会清空 static 字段；离散请求通过 SessionState 衔接。
            if(SessionState.GetBool("Norn.HubCapture.Pending",false))_startAfter=EditorApplication.timeSinceStartup+4;
            EditorApplication.update+=Poll;
            EditorApplication.playModeStateChanged+=OnPlayModeChanged;
        }
        [MenuItem("Tools/SinglePlayer/Diagnostics/Capture Hub Native Input (20s)")]
        private static void CaptureNativeInput()
        {
            // 原生输入窗口不创建模拟设备；用户可按自己触发卡顿的路线行走和转镜头。
            if(_capturing)throw new InvalidOperationException("已有采样正在进行，请等待 Finished。");
            Start("hub-native-input-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        }
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            // 提前停止 Play 也必须撤回诊断设备和 Profiler 开关，避免泄漏到下一轮真实输入。
            if(state==PlayModeStateChange.ExitingPlayMode && _capturing)Finish();
        }
        private static void Poll()
        {
            if(EditorApplication.timeSinceStartup<_nextPoll)return;
            _nextPoll=EditorApplication.timeSinceStartup+.25;
            if(_capturing && EditorApplication.timeSinceStartup>=_finishAt){Finish();return;}
            if(_startAfter>0 && EditorApplication.isPlaying && EditorApplication.timeSinceStartup>=_startAfter)
            {_startAfter=0;SessionState.SetBool("Norn.HubCapture.Pending",false);Start(SessionState.GetString("Norn.HubCapture.Name","hub-normal-before"));return;}
            if(!File.Exists(Request)||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            try
            {
                if(command=="play" || command=="play-motion" || command=="play-motion-no-toolbar")
                {
                    if(EditorApplication.isPlayingOrWillChangePlaymode)throw new InvalidOperationException("采样 Play 已经在运行。");
                    if(SceneManager.sceneCount!=1 || SceneManager.GetActiveScene().name!="SinglePlayer_Hub" || SceneManager.GetActiveScene().isDirty)
                        throw new InvalidOperationException("只允许单独的已保存据点；不代保存或丢弃。");
                    SessionState.SetString("Norn.HubCapture.Name",command=="play"?"hub-normal-before":command=="play-motion"?"hub-motion-before":"hub-motion-no-toolbar");
                    SessionState.SetBool("Norn.HubCapture.Pending",true);_startAfter=EditorApplication.timeSinceStartup+4;EditorApplication.EnterPlaymode();
                }
                else if(command.StartsWith("sample-",StringComparison.Ordinal))Start(command.Substring(7));
            }
            catch(Exception e){Directory.CreateDirectory(Folder);File.WriteAllText(Folder+"/capture-error.txt",e.ToString());GameLog.Error(e.ToString(),"HubCapture");}
        }
        private static void Start(string name)
        {
            if(!EditorApplication.isPlaying || SceneManager.sceneCount!=1 || SceneManager.GetActiveScene().name!="SinglePlayer_Hub")
                throw new InvalidOperationException("采样必须来自正在运行的单独正式据点。");
            // 用公开 Close 释放各自暂停原因，不能强写 timeScale 冒充正常 gameplay。
            foreach(var ui in UnityEngine.Object.FindObjectsByType<SinglePlayerInventoryController>(FindObjectsInactive.Include,FindObjectsSortMode.None))ui.Close();
            foreach(var shop in UnityEngine.Object.FindObjectsByType<ShopController>(FindObjectsInactive.Include,FindObjectsSortMode.None))shop.Close();
            foreach(var guide in UnityEngine.Object.FindObjectsByType<GameplayGuideController>(FindObjectsInactive.Include,FindObjectsSortMode.None))guide.Close();
            _enabled=ProfilerDriver.enabled;_editor=ProfilerDriver.profileEditor;_gpu=ProfilerDriver.profileGPU;
            _cpu=ProfilerDriver.IsAreaEnabled(ProfilerArea.CPU);_background=Application.runInBackground;
            Application.runInBackground=true;
            ProfilerDriver.enabled=true;ProfilerDriver.profileEditor=true;ProfilerDriver.profileGPU=false;ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU,true);
            // 记录 EditorLoop 子树可区分 Editor 后台工作与 PlayerLoop；不开 Deep Profile。
            _name=name;_startAt=EditorApplication.timeSinceStartup;_finishAt=_startAt+20;_capturing=true;
            _gameView=EditorWindow.GetWindow(Type.GetType("UnityEditor.GameView,UnityEditor"));
            _toolbarProperty=_gameView.GetType().GetProperty("showToolbar",BindingFlags.Instance|BindingFlags.NonPublic);
            _toolbar=_toolbarProperty!=null && (bool)_toolbarProperty.GetValue(_gameView);_hideToolbar=name.EndsWith("no-toolbar",StringComparison.Ordinal);
            // 只做 Editor 工具条 A/B；保留原值并恢复，不改用户 Scene 或 gameplay HUD。
            if(_hideToolbar && _toolbarProperty!=null){_toolbarProperty.SetValue(_gameView,false);_gameView.Repaint();}
            if(name.Contains("motion"))
            {
                // 只创建本次诊断设备，沿实际 InputAction→角色/CameraRoot 链移动；结束只移除自己创建的设备。
                _motionKeyboard=InputSystem.AddDevice<Keyboard>();_motionMouse=InputSystem.AddDevice<Mouse>();
                _forward=new KeyboardState(Key.W);_backward=new KeyboardState(Key.S);_look=new MouseState{delta=new Vector2(2,0)};
                InputSystem.onBeforeUpdate+=TickMotion;
            }
            Directory.CreateDirectory(Folder);
            var viewData=new SerializedObject(_gameView);
            File.WriteAllText(Folder+"/capture-state.txt",DateTime.UtcNow.ToString("O")+"\nRecording="+name+"\nTimeScale="+Time.timeScale);
            File.WriteAllText(Folder+"/"+name+"-gameview.txt","ToolbarBefore="+_toolbar+"\nToolbarHidden="+_hideToolbar
                +"\nVSync="+viewData.FindProperty("m_VSyncEnabled")?.boolValue+"\nGizmos="+viewData.FindProperty("m_Gizmos")?.boolValue+"\nStats="+viewData.FindProperty("m_Stats")?.boolValue);
        }
        private static void Finish()
        {
            _capturing=false;
            double endAt=EditorApplication.timeSinceStartup;
            InputSystem.onBeforeUpdate-=TickMotion;
            if(_hideToolbar && _gameView!=null && _toolbarProperty!=null){_toolbarProperty.SetValue(_gameView,_toolbar);_gameView.Repaint();}
            if(_motionKeyboard!=null){InputSystem.RemoveDevice(_motionKeyboard);_motionKeyboard=null;}
            if(_motionMouse!=null){InputSystem.RemoveDevice(_motionMouse);_motionMouse=null;}
            try
            {
                // 先停录再做有分配的离线导出；导出自身不能算进卡顿帧。
                ProfilerDriver.enabled=false;
                int first=ProfilerDriver.firstFrameIndex,last=ProfilerDriver.lastFrameIndex;
                ProfilerDriver.SaveProfile(Folder+"/"+_name+".data");
                using(var writer=new StreamWriter(Folder+"/"+_name+".tsv"))
                {
                    writer.WriteLine("frame\tthread\tsample\tms\tgcBytes\tstartMs\tchildren");
                    for(int frame=first;frame<=last;frame++)for(int thread=0;thread<64;thread++)
                    {
                        using var data=ProfilerDriver.GetRawFrameDataView(frame,thread);if(!data.valid)break;
                        for(int sample=0;sample<data.sampleCount;sample++)
                        {
                            string marker=data.GetSampleName(sample);float ms=data.GetSampleTimeMs(sample);bool allocation=marker=="GC.Alloc";
                            if(ms<.05f && !allocation && !marker.StartsWith("Norn.",StringComparison.Ordinal) && !marker.StartsWith("GridLiquid.",StringComparison.Ordinal))continue;
                            long bytes=allocation && data.GetSampleMetadataCount(sample)>0?data.GetSampleMetadataAsLong(sample,0):0;
                            writer.WriteLine(frame+"\t"+data.threadName+"\t"+marker+"\t"+ms.ToString(CultureInfo.InvariantCulture)+"\t"+bytes+"\t"+data.GetSampleStartTimeMs(sample).ToString(CultureInfo.InvariantCulture)+"\t"+data.GetSampleChildrenCountRecursive(sample));
                        }
                    }
                }
                File.WriteAllText(Folder+"/"+_name+"-state.txt",DateTime.UtcNow.ToString("O")+"\nScene="+SceneManager.GetActiveScene().path
                    +"\nPlaying="+EditorApplication.isPlaying+"\nTimeScale="+Time.timeScale+"\nDurationSeconds="+(endAt-_startAt)+"\nExportSeconds="+(EditorApplication.timeSinceStartup-endAt)
                    +"\nFirst="+first+"\nLast="+last+"\nDeepProfile="+ProfilerDriver.deepProfiling);
                File.WriteAllText(Folder+"/capture-state.txt","Finished="+_name);
            }
            catch(Exception e){File.WriteAllText(Folder+"/capture-error.txt",e.ToString());GameLog.Error(e.ToString(),"HubCapture");}
            finally
            {ProfilerDriver.enabled=_enabled;ProfilerDriver.profileEditor=_editor;ProfilerDriver.profileGPU=_gpu;ProfilerDriver.SetAreaEnabled(ProfilerArea.CPU,_cpu);Application.runInBackground=_background;}
        }
        private static void TickMotion()
        {
            if(!_capturing || InputState.currentUpdateType!=InputUpdateType.Dynamic)return;
            // KeyboardState/MouseState 均为 struct，预先构造并按真实 Dynamic 输入帧排队；不在热路径 new 数组。
            bool forward=(EditorApplication.timeSinceStartup-_startAt)%4<2;
            InputSystem.QueueStateEvent(_motionKeyboard,forward?_forward:_backward);
            InputSystem.QueueStateEvent(_motionMouse,_look);
        }
    }
}
