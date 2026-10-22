using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.EditorTools
{
    /// <summary>保存提示会阻塞TestRunner；此定向检查在自有临时Scene中调用同一个NUnit用例，
    /// 随后销毁临时Scene，完整保留用户未保存关卡。报告与正式TestRunner XML分开。</summary>
    [InitializeOnLoad]
    internal static class GridLiquidSlopeGate
    {
        static GridLiquidSlopeGate()=>EditorApplication.update+=Poll;
        private static void Poll()
        {
            const string request="Temp/ElementPerformance/liquid-slope-gate.request";
            if(EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(request))return;
            File.Delete(request);
            var original=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var roots=original.GetRootGameObjects();var active=new bool[roots.Length];
            // Physics.Raycast使用默认PhysicsScene；暂时禁用正式关卡Collider，避免其地板干扰坡面用例。
            for(int i=0;i<roots.Length;i++){active[i]=roots[i].activeSelf;roots[i].SetActive(false);}
            var temporary=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            UnityEngine.SceneManagement.SceneManager.SetActiveScene(temporary);
            var result=new Result{Utc=DateTime.UtcNow.ToString("O"),Cases=new Case[3]};
            try
            {
                var type=Type.GetType("Game.ElementField.Tests.LiquidVolumeMappingTests, Game.ElementField.Tests",true);
                var test=Activator.CreateInstance(type);var method=type.GetMethod("ActualRotatedColliderSuppliesPlaneAndDownhillVelocity");
                float[] angles={25,30,60};
                for(int i=0;i<angles.Length;i++)
                {
                    var item=new Case{Angle=angles[i]};result.Cases[i]=item;
                    try{method.Invoke(test,new object[]{angles[i]});item.Passed=true;}
                    catch(Exception e){item.Error=(e.InnerException??e).ToString();}
                }
                try
                {
                    var frameType=Type.GetType("Game.Rendering.Tests.GridLiquidFieldFrameTests, Game.Rendering.Tests",true);
                    frameType.GetMethod("NegativeBoundaryIncludesEmptyOutputOwnersAndWarmCaptureAllocatesNothing")
                        .Invoke(Activator.CreateInstance(frameType),null);result.FrameCapturePassed=true;
                }
                catch(Exception e){result.FrameCaptureError=(e.InnerException??e).ToString();}
            }
            finally
            {
                EditorSceneManager.CloseScene(temporary,true);UnityEngine.SceneManagement.SceneManager.SetActiveScene(original);
                for(int i=0;i<roots.Length;i++)roots[i].SetActive(active[i]);
                result.UserSceneDirty=original.isDirty;File.WriteAllText("Temp/ElementPerformance/liquid-slope-gate.json",JsonUtility.ToJson(result,true));
            }
        }
        [Serializable]private sealed class Result{public string Utc,FrameCaptureError;public bool UserSceneDirty,FrameCapturePassed;public Case[] Cases;}
        [Serializable]private sealed class Case{public float Angle;public bool Passed;public string Error;}
    }
}
