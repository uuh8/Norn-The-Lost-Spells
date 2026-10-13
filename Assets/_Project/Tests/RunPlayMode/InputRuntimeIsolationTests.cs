#if UNITY_EDITOR
using System;
using NUnit.Framework;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Game.Run.Tests
{
    /// <summary>检查测试边界真的恢复原设备，而非仅证明测试里的虚拟键盘能工作。</summary>
    public sealed class InputRuntimeIsolationTests
    {
        [Test] public void IsolatedRuntimeRestoresOriginalDevicesAndKeepsPriorDisabledState()
        {
            var owned=InputSystem.AddDevice<Keyboard>();
            // 故意保留一个原先已禁用的非真实设备，避免修复变成“无条件启用所有设备”。
            InputSystem.DisableDevice(owned,true);
            var baseline=Capture();var fixture=new InputTestFixture();bool active=false;
            try
            {
                fixture.Setup();active=true;Assert.That(InputSystem.devices.Count,Is.Zero);
                var keyboard=InputSystem.AddDevice<Keyboard>();InputSystem.AddDevice<Mouse>();
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(Key.W));InputSystem.Update();
                Assert.That(keyboard.wKey.isPressed,Is.True);
                // 故意不Remove这两个临时设备：隔离Runtime整体释放仍须还原原设备集合。
                fixture.TearDown();active=false;AssertRestored(baseline);
                Assert.That(InputSystem.GetDeviceById(owned.deviceId).enabled,Is.False);
            }
            finally{if(active)fixture.TearDown();if(owned.added)InputSystem.RemoveDevice(owned);}
        }
        [Test] public void ExceptionCleanupRestoresOriginalRuntimeAndDiscardsSyntheticDevice()
        {
            var baseline=Capture();var fixture=new InputTestFixture();
            Assert.Throws<InvalidOperationException>(()=>
            {
                fixture.Setup();
                try{InputSystem.AddDevice<Keyboard>();throw new InvalidOperationException("Injected input test failure");}
                finally{fixture.TearDown();}
            });
            AssertRestored(baseline);
        }
        private static Baseline[] Capture()
        {
            var result=new Baseline[InputSystem.devices.Count];
            for(int i=0;i<result.Length;i++){var device=InputSystem.devices[i];result[i]=new Baseline{Device=device,Enabled=device.enabled};}
            return result;
        }
        private static void AssertRestored(Baseline[] baseline)
        {
            Assert.That(InputSystem.devices.Count,Is.EqualTo(baseline.Length));
            foreach(var entry in baseline)
            {
                var device=InputSystem.GetDeviceById(entry.Device.deviceId);
                Assert.That(device,Is.SameAs(entry.Device));Assert.That(device.enabled,Is.EqualTo(entry.Enabled),device.name);
            }
        }
        private struct Baseline{public InputDevice Device;public bool Enabled;}
    }
}
#endif
