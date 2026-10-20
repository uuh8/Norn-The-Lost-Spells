using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Game.Character;
using Game.Run;
using Game.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Game.EditorTools
{
    /// <summary>按离散请求检查真实输入链；只有明确恢复命令才清理已记录的测试设备，不修改玩家资产。</summary>
    [InitializeOnLoad]
    internal static class HubInputDiagnostics
    {
        private const string Request="Temp/SinglePlayer/hub-input-diagnostics.request";
        private const string Output="TestResults/SinglePlayer/M6-T1-Input";
        static HubInputDiagnostics(){EditorApplication.update+=Poll;}
        private static void Poll()
        {
            if(!File.Exists(Request) || EditorApplication.isCompiling || EditorApplication.isUpdating)return;
            var name=File.ReadAllText(Request).Trim();File.Delete(Request);
            Directory.CreateDirectory(Output);
            if(name=="recover-test-devices")
            {
                // 仅清理本次诊断已记录的10个泄漏键盘，保留之后添加的设备和其他设备类型。
                var before=JsonUtility.FromJson<Report>(File.ReadAllText(Output+"/before-edit.json"));
                foreach(var entry in before.Devices)
                {
                    var device=InputSystem.GetDeviceById(entry.Id);
                    if(device!=null && !entry.Native && device is Keyboard && !device.native)InputSystem.RemoveDevice(device);
                }
                foreach(var device in InputSystem.devices)
                    if(device.native && (device is Keyboard || device is Mouse))InputSystem.EnableDevice(device);
            }
            var report=new Report{Utc=DateTime.UtcNow.ToString("O"),Scene=SceneManager.GetActiveScene().path,
                Playing=EditorApplication.isPlaying,Paused=EditorApplication.isPaused,TimeScale=Time.timeScale,
                FocusedWindow=EditorWindow.focusedWindow==null?"":EditorWindow.focusedWindow.GetType().Name,
                ApplicationFocused=Application.isFocused,CursorLocked=Cursor.lockState.ToString()};
            foreach(var device in InputSystem.devices)report.Devices.Add(new Device{Id=device.deviceId,Name=device.name,Native=device.native,Enabled=device.enabled,
                Flags=typeof(InputDevice).GetField("m_DeviceFlags",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(device).ToString()});
            foreach(var actor in UnityEngine.Object.FindObjectsByType<WizardController>(FindObjectsSortMode.None))
            {
                report.PlayerEnabled=actor.enabled;report.PlayerActive=actor.gameObject.activeInHierarchy;report.PlayerPosition=actor.transform.position;
                // 生成的输入包装器属于另一个Assembly；诊断反射只读asset，不为临时工具新增模块依赖。
                var wrapper=typeof(PlayerControllerBase).GetField("_inputActions",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(actor);
                var asset=wrapper==null?null:wrapper.GetType().GetProperty("asset").GetValue(wrapper) as InputActionAsset;
                if(asset!=null){var move=asset.FindAction("Player/Move");report.MoveEnabled=move.enabled;report.MoveValue=move.ReadValue<Vector2>();report.LookEnabled=asset.FindAction("Player/Look").enabled;}
            }
            foreach(var pause in UnityEngine.Object.FindObjectsByType<RunPauseCoordinator>(FindObjectsSortMode.None))report.PauseReasons=pause.ActiveReasons.ToString();
            foreach(var inventory in UnityEngine.Object.FindObjectsByType<SinglePlayerInventoryController>(FindObjectsSortMode.None))
            {report.InventoryOpen=inventory.IsOpen;report.Initialization=inventory.Session.InitializationMessage;report.InventoryReady=inventory.Session.ReadOnly!=null;}
            File.WriteAllText(Output+"/"+name+".json",JsonUtility.ToJson(report,true));
        }
        [Serializable]private sealed class Device{public int Id;public string Name,Flags;public bool Native,Enabled;}
        [Serializable]private sealed class Report
        {
            public string Utc,Scene,FocusedWindow,CursorLocked,PauseReasons,Initialization;
            public bool Playing,Paused,ApplicationFocused,PlayerEnabled,PlayerActive,MoveEnabled,LookEnabled,InventoryOpen,InventoryReady;
            public float TimeScale;public Vector3 PlayerPosition;public Vector2 MoveValue;public List<Device> Devices=new List<Device>();
        }
    }
}
