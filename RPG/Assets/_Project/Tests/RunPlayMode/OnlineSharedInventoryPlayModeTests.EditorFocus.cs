#if UNITY_EDITOR
using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif

namespace Game.Run.Tests
{
    public sealed partial class OnlineSharedInventoryPlayModeTests
    {
        /// <summary>
        /// 输入用例的环境前置只要求真实Application.isFocused及GameView内部焦点，
        /// 与产品原门保持一致；OS前景/EditorFocus只记录诊断，不额外阻止真实输入验证。
        /// InputTestFixture只隔离InputManager，不负责伪造Application焦点。
        /// 只请求本Editor的窗口，不设置Application.isFocused、不调用产品Focus回调，也不向OS发按键。
        /// Win32 attachment在每次同步请求的finally内解除，不跨yield借用另一线程的输入队列。
        /// </summary>
        private static IEnumerator RequireGroundPickupGameViewFocus(EditorWindow gameView)
        {
            Assert.That(gameView,Is.Not.Null);
            double deadline=EditorApplication.timeSinceStartup+4;
            var native=new GroundPickupOwnedEditorFocus();int frames=0;
            for(;frames<120 && EditorApplication.timeSinceStartup<deadline;frames++)
            {
                if(frames%8==0 || EditorWindow.focusedWindow!=gameView)
                {
                    native.Request();
                    // OS主窗激活不等于内部GameView获得键盘焦点。先恢复前者，再通过真实
                    // EditorWindow API选择标签/HostView，不反射修改Unity的焦点bool。
                    gameView.ShowTab();gameView.Focus();gameView.Repaint();
                    EditorApplication.QueuePlayerLoopUpdate();
                }
                // 必须让真实Window消息和后续PlayerLoop完成；不在发请求的同一调用栈宣布成功。
                yield return null;
                // Native前景请求可能被Windows拒绝，但这不等于GameView/产品Focus门为false。
                // 原F/drag行为仍会实际检验InputSystem是否收到输入，不能在发输入前另加更强门槛。
                if(Application.isFocused && EditorWindow.focusedWindow==gameView)
                {
                    TestContext.Progress.WriteLine("GroundPickup focus ready: frames="+(frames+1)+" "+native.State
                        +"; GameView="+EditorWindow.focusedWindow.GetType().FullName+"; AppFocus="+Application.isFocused+"; EditorFocus="+EditorApplication.isFocused);
                    yield break;
                }
            }
            string focused=EditorWindow.focusedWindow!=null?EditorWindow.focusedWindow.GetType().FullName:"none";
            Assert.Fail("真实输入前未获得GameView焦点；未发送输入、未绕过产品Focus门。frames="+frames
                +"; AppFocus="+Application.isFocused+"; EditorFocus="+EditorApplication.isFocused+"; FocusedEditorType="+focused
                +"; BatchMode="+Application.isBatchMode+"; "+native.State);
        }

        private sealed class GroundPickupOwnedEditorFocus
        {
#if UNITY_EDITOR_WIN
            private readonly IntPtr _window;
            private readonly uint _processId;
            private bool _lastForegroundRequest;
            internal GroundPickupOwnedEditorFocus()
            {
                // 不按窗口名/进程名搜索，也不枚举其它窗口标题；句柄和PID都来自当前Editor。
                using(var process=System.Diagnostics.Process.GetCurrentProcess())
                {process.Refresh();_processId=(uint)process.Id;_window=process.MainWindowHandle;}
                Assert.That(_window,Is.Not.EqualTo(IntPtr.Zero),"当前Editor没有可聚焦的主窗口。");
                VerifyOwnership();
            }
            private uint VerifyOwnership()
            {
                uint thread=GetWindowThreadProcessId(_window,out uint owner);
                Assert.That(thread,Is.Not.Zero);Assert.That(owner,Is.EqualTo(_processId),"焦点请求必须严格指向当前Editor主窗口。");
                return thread;
            }
            internal void Request()
            {
                uint targetThread=VerifyOwnership(),callerThread=GetCurrentThreadId();bool attached=false;
                try
                {
                    // 一般两者同在Editor UI主线程，无需Attach。仅当本进程主窗属于另一UI线程
                    // 时短暂连接；不连接/激活其它进程窗口，不修改全局foreground lock timeout。
                    if(targetThread!=callerThread)attached=AttachThreadInput(callerThread,targetThread,true);
                    if(IsIconic(_window))ShowWindow(_window,9); // SW_RESTORE只用于已最小化的自有窗。
                    _lastForegroundRequest=SetForegroundWindow(_window);
                    SetActiveWindow(_window);SetFocus(_window);
                }
                finally{if(attached)AttachThreadInput(callerThread,targetThread,false);}
            }
            internal bool IsOwnedForeground
            {
                get{GetWindowThreadProcessId(GetForegroundWindow(),out uint owner);return owner==_processId;}
            }
            internal string State
            {
                get
                {
                    GetWindowThreadProcessId(GetForegroundWindow(),out uint foregroundOwner);
                    return "OwnedPID="+_processId+"; ForegroundPID="+foregroundOwner+"; SetForeground="+_lastForegroundRequest;
                }
            }
            [DllImport("user32.dll",SetLastError=true)]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
            [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
            [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
            [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AttachThreadInput(uint from,uint to,[MarshalAs(UnmanagedType.Bool)]bool attach);
            [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetForegroundWindow(IntPtr window);
            [DllImport("user32.dll")]private static extern IntPtr SetActiveWindow(IntPtr window);
            [DllImport("user32.dll")]private static extern IntPtr SetFocus(IntPtr window);
            [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsIconic(IntPtr window);
            [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ShowWindow(IntPtr window,int command);
#else
            // 非Windows只保留原EditorWindow.Focus流程，不载入user32，也不伪造焦点事件。
            internal void Request(){}
            internal bool IsOwnedForeground=>true;
            internal string State=>"NativeFocus=EditorWindow.Focus only (non-Windows)";
#endif
        }
    }
}
#endif
