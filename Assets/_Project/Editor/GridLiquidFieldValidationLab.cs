using System;
using System.IO;
using System.Reflection;
using Game.ElementField;
using Game.Materials;
using Game.Rendering;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>
    /// 显式Play验证室：使用真实Runtime/Collider/GPU，在临时GameObject上验证，
    /// 不改用户Profile、相机或关卡资产。结束Play自然撤销，截图与指标仅写Temp。
    /// </summary>
    [InitializeOnLoad]
    internal static class GridLiquidFieldValidationLab
    {
        private const string Request="Temp/ElementPerformance/continuous-lab.request";
        private static double _next;
        private static GridLiquidFieldLabDriver _driver;
        private static readonly int[] CaptureTicks={5,15,30};
        private static int _nextCapture;
        static GridLiquidFieldValidationLab()=>EditorApplication.update+=Poll;
        private static void Poll()
        {
            if(EditorApplication.timeSinceStartup<_next)return;_next=EditorApplication.timeSinceStartup+.3;
            if(EditorApplication.isCompiling || !EditorApplication.isPlaying)return;
            // 仅显式启动的验证室拍摄三个关键时刻；截图分配属于Editor诊断，Profiler采样排除这些帧。
            if(_driver!=null && _driver.CanCapture && _nextCapture<CaptureTicks.Length && _driver.Tick>=CaptureTicks[_nextCapture])
            {_driver.Capture("-tick"+CaptureTicks[_nextCapture]);_nextCapture++;}
            if(!File.Exists(Request))return;
            string command=File.ReadAllText(Request).Trim();File.Delete(Request);
            if(command=="close"){if(_driver!=null)UnityEngine.Object.DestroyImmediate(_driver.gameObject);_driver=null;return;}
            if(command=="capture"){if(_driver!=null)_driver.Capture();return;}
            if(command=="opaque" || command=="shaded"){if(_driver!=null)_driver.SetOpaque(command=="opaque");return;}
            if(command=="wave-off" || command=="swell-off" || command=="shadow-off" || command=="solid-alpha" || command=="normals" || command=="raw-normals"){if(_driver!=null)_driver.Diagnostic(command);return;}
            if(command!="wide" && command!="wide-cpu" && command!="air-wide" && command!="platform" && command!="same" && command!="contact" && command!="layered" && command!="slope30" && command!="slope60" && command!="toe30" && command!="toe60" && command!="join30" && command!="join60" && command!="lake" && command!="lake-contact" && command!="water" && command!="poison" && command!="sticky" && command!="thin" && command!="thin-cpu" && command!="bulk" && command!="bulk-calibrated" && command!="blob")return;
            if(_driver!=null)UnityEngine.Object.DestroyImmediate(_driver.gameObject);
            var host=new GameObject("Temporary Continuous Liquid Lab");host.SetActive(false);
            _driver=host.AddComponent<GridLiquidFieldLabDriver>();_driver.Initialize(command);_nextCapture=0;host.SetActive(true);Application.runInBackground=true;
        }
    }
}
