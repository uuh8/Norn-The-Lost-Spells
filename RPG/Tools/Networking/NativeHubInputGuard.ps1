#requires -Version 7.2
# 只定义函数，dot-source不会发送输入。仅供主Agent对本轮亲自启动的Native Client使用。
# 不抢焦点、不启停进程、不调用Unity回调；身份/焦点失效后当前Guard不可恢复使用。
Set-StrictMode -Version Latest

function Initialize-HubInputNativeType {
    if ('Norn.NativeHubInput.Session' -as [type]) { return }
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Norn.NativeHubInput
{
    public struct ClientBox { public int Width, Height, ScreenX, ScreenY; }
    public struct ClientPoint { public double X, Y; }

    // 只有这个实例能释放它发出的down。Timer覆盖分开的drag函数之间的按住窗口。
    public sealed class Session : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT
        { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT
        { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION
        { [FieldOffset(0)] public MOUSEINPUT mouse; [FieldOffset(0)] public KEYBDINPUT keyboard; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION data; }

        [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError=true)] static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll", SetLastError=true)] static extern bool ClientToScreen(IntPtr window, ref POINT point);
        [DllImport("user32.dll", SetLastError=true)] static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        readonly object gate = new object();
        readonly int processId;
        readonly long startTicks;
        readonly string executable;
        readonly IntPtr window;
        readonly HashSet<ushort> heldKeys = new HashSet<ushort>();
        readonly HashSet<ushort> heldUnicode = new HashSet<ushort>();
        readonly Timer watchdog;
        bool leftHeld, disposed;
        string failure;

        public int ProcessId { get { return processId; } }
        public long StartTicks { get { return startTicks; } }
        public string Executable { get { return executable; } }
        public IntPtr WindowHandle { get { return window; } }
        public bool LeftHeld { get { lock (gate) { return leftHeld; } } }

        public Session(Process ownedProcess, long expectedStartTicks, string expectedExecutable, IntPtr ownedWindow)
        {
            if (ownedProcess == null || ownedWindow == IntPtr.Zero) throw new InvalidOperationException("HubOwnedProcessOrWindowMissing");
            processId = ownedProcess.Id;
            startTicks = expectedStartTicks;
            executable = Path.GetFullPath(expectedExecutable);
            window = ownedWindow;
            // 构造也要求前台是这个精确HWND。焦点由外层主Agent给自有窗口，helper不改变焦点。
            ValidateCore();
            watchdog = new Timer(MonitorHeldInput, null, Timeout.Infinite, Timeout.Infinite);
        }

        void ValidateCore()
        {
            using (Process current = Process.GetProcessById(processId))
            {
                if (current.HasExited || current.StartTime.ToUniversalTime().Ticks != startTicks ||
                    !String.Equals(Path.GetFullPath(current.MainModule.FileName), executable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("HubOwnedProcessIdentityLost");
            }
            uint owner;
            if (!IsWindow(window) || GetWindowThreadProcessId(window, out owner) == 0 || owner != (uint)processId)
                throw new InvalidOperationException("HubOwnedWindowIdentityLost");
            if (GetForegroundWindow() != window) throw new InvalidOperationException("HubOwnedWindowLostForeground");
        }
        void RequireValid()
        {
            if (disposed) throw new InvalidOperationException("HubInputGuardDisposed");
            if (failure != null) throw new InvalidOperationException(failure);
            try { ValidateCore(); }
            catch (Exception) { FailAndRelease("HubInputGuardIdentityOrFocusLost"); throw new InvalidOperationException(failure); }
        }
        public void AssertValid() { lock (gate) { RequireValid(); } }
        void MonitorHeldInput(object ignored)
        {
            lock (gate)
            {
                if (disposed || !HasHeldInput()) return;
                try { ValidateCore(); }
                catch (Exception) { FailAndRelease("HubInputGuardIdentityOrFocusLostWhileHeld"); }
            }
        }
        bool HasHeldInput() { return leftHeld || heldKeys.Count != 0 || heldUnicode.Count != 0; }
        void UpdateWatchdog() { watchdog.Change(HasHeldInput() ? 20 : Timeout.Infinite, HasHeldInput() ? 20 : Timeout.Infinite); }
        static void Send(INPUT input)
        {
            if (SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT))) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HubNativeSendInputFailed");
        }
        static void SendKey(ushort key, bool up)
        { Send(new INPUT { type=1, data=new INPUTUNION { keyboard=new KEYBDINPUT { wVk=key, dwFlags=up ? 2u : 0u } } }); }
        static void SendUnicode(ushort unit, bool up)
        { Send(new INPUT { type=1, data=new INPUTUNION { keyboard=new KEYBDINPUT { wScan=unit, dwFlags=4u | (up ? 2u : 0u) } } }); }
        static void SendLeft(bool up)
        { Send(new INPUT { type=0, data=new INPUTUNION { mouse=new MOUSEINPUT { dwFlags=up ? 4u : 2u } } }); }
        void FailAndRelease(string code)
        {
            if (failure == null) failure = code;
            ReleaseCore();
        }
        bool ReleaseCore()
        {
            // 故障清理故意不要求前台仍有效：只补本helper发过的up，避免W/Ctrl/Left粘住。
            // 不触及未由本实例持有的键。发送失败则保留失败状态，不能把清理失败报告为通过。
            bool ok = true;
            foreach (ushort unit in heldUnicode) { try { SendUnicode(unit, true); } catch { ok = false; } }
            foreach (ushort key in heldKeys) { try { SendKey(key, true); } catch { ok = false; } }
            if (leftHeld) { try { SendLeft(true); } catch { ok = false; } }
            heldUnicode.Clear(); heldKeys.Clear(); leftHeld = false;
            if (watchdog != null) watchdog.Change(Timeout.Infinite, Timeout.Infinite);
            if (!ok && failure == null) failure = "HubOwnedInputReleaseFailed";
            return ok;
        }
        public bool ReleaseOwnedInput() { lock (gate) { return ReleaseCore(); } }

        static ushort ResolveKey(string name)
        {
            switch (name.ToUpperInvariant())
            {
                case "W": return 0x57; case "A": return 0x41; case "S": return 0x53; case "D": return 0x44;
                case "F": return 0x46; case "TAB": return 0x09; case "ESC": case "ESCAPE": return 0x1B;
                case "ENTER": return 0x0D; case "CTRL": case "CONTROL": return 0x11;
                default: throw new InvalidOperationException("HubKeyOutsideAllowedSet");
            }
        }
        public void KeyDown(string name)
        {
            lock (gate)
            {
                RequireValid(); ushort key = ResolveKey(name);
                if (!heldKeys.Add(key)) throw new InvalidOperationException("HubKeyAlreadyHeldByHelper");
                UpdateWatchdog();
                try { SendKey(key, false); RequireValid(); }
                catch { FailAndRelease("HubKeyDownFailed"); throw; }
            }
        }
        public void UnicodeUnit(ushort unit)
        {
            lock (gate)
            {
                RequireValid(); heldUnicode.Add(unit); UpdateWatchdog();
                try { SendUnicode(unit, false); RequireValid(); SendUnicode(unit, true); heldUnicode.Remove(unit); UpdateWatchdog(); }
                catch { FailAndRelease("HubUnicodeInputFailed"); throw; }
            }
        }
        public void LeftDown()
        {
            lock (gate)
            {
                RequireValid();
                if (leftHeld) throw new InvalidOperationException("HubLeftAlreadyHeldByHelper");
                leftHeld = true; UpdateWatchdog();
                try { SendLeft(false); RequireValid(); }
                catch { FailAndRelease("HubLeftDownFailed"); throw; }
            }
        }
        public void LeftUp()
        {
            lock (gate)
            {
                RequireValid();
                if (!leftHeld) throw new InvalidOperationException("HubLeftWasNotHeldByHelper");
                try { SendLeft(true); leftHeld = false; UpdateWatchdog(); RequireValid(); }
                catch { FailAndRelease("HubLeftUpFailed"); throw; }
            }
        }
        ClientBox ReadClientCore()
        {
            RECT rect; POINT origin = new POINT();
            if (!GetClientRect(window, out rect) || !ClientToScreen(window, ref origin) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                throw new InvalidOperationException("HubClientRectangleUnavailable");
            return new ClientBox { Width=rect.Right-rect.Left, Height=rect.Bottom-rect.Top, ScreenX=origin.X, ScreenY=origin.Y };
        }
        public ClientBox ReadClient() { lock (gate) { RequireValid(); return ReadClientCore(); } }
        public ClientPoint ReadPointer()
        {
            lock (gate)
            {
                RequireValid(); ClientBox client = ReadClientCore(); POINT point;
                if (!GetCursorPos(out point)) throw new InvalidOperationException("HubCursorUnavailable");
                return new ClientPoint { X=point.X-client.ScreenX, Y=client.Height-1-(point.Y-client.ScreenY) };
            }
        }
        public void MoveUnity(double x, double y, int unityWidth, int unityHeight)
        {
            lock (gate)
            {
                RequireValid(); ClientBox client = ReadClientCore();
                if (client.Width != unityWidth || client.Height != unityHeight)
                    throw new InvalidOperationException("HubUnityViewportAndClientSizeMismatch");
                if (Double.IsNaN(x) || Double.IsInfinity(x) || Double.IsNaN(y) || Double.IsInfinity(y) ||
                    x < 0 || y < 0 || x >= unityWidth || y >= unityHeight)
                    throw new InvalidOperationException("HubPointerOutsideOwnedClient");
                int px = client.ScreenX+(int)Math.Floor(x), py = client.ScreenY+client.Height-1-(int)Math.Floor(y);
                int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
                if (width < 2 || height < 2 || px < left || py < top || px >= left+width || py >= top+height)
                    throw new InvalidOperationException("HubPointerOutsideVirtualDesktop");
                try {
                    Send(new INPUT { type=0, data=new INPUTUNION { mouse=new MOUSEINPUT {
                        dx=(int)Math.Round((px-left)*65535.0/(width-1)), dy=(int)Math.Round((py-top)*65535.0/(height-1)),
                        dwFlags=0x0001u | 0x8000u | 0x4000u } } });
                    RequireValid();
                } catch { FailAndRelease("HubPointerMoveFailed"); throw; }
            }
        }
        public void WaitGuarded(int milliseconds)
        {
            if (milliseconds < 0 || milliseconds > 5000) throw new InvalidOperationException("HubWaitOutsideBoundedWindow");
            Stopwatch clock = Stopwatch.StartNew();
            do {
                AssertValid();
                int remaining = milliseconds-(int)clock.ElapsedMilliseconds;
                if (remaining <= 0) break;
                Thread.Sleep(Math.Min(15, remaining));
            } while (true);
            AssertValid();
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                ReleaseCore(); disposed = true; watchdog.Dispose();
            }
        }
    }
}
'@ | Out-Null
}

function New-HubInputGuard {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory,Position=0)][Alias('OwnedProcess')][Diagnostics.Process]$Process,
        [Parameter(Mandatory,Position=1)][Alias('ExpectedExecutable')][string]$ExpectedExe,
        [long]$ExpectedStartTicks = 0,
        [IntPtr]$WindowHandle = [IntPtr]::Zero
    )
    Initialize-HubInputNativeType
    $Process.Refresh()
    if ($ExpectedStartTicks -eq 0) { $ExpectedStartTicks = $Process.StartTime.ToUniversalTime().Ticks }
    if ($WindowHandle -eq [IntPtr]::Zero) { $WindowHandle = $Process.MainWindowHandle }
    return [Norn.NativeHubInput.Session]::new($Process, $ExpectedStartTicks, $ExpectedExe, $WindowHandle)
}

function Get-HubClientSize {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard)
    $completed = $false
    try { $result = $Guard.ReadClient(); $completed = $true; return $result }
    finally { if (!$completed) { [void]$Guard.ReleaseOwnedInput() } }
}

function Move-HubPointer {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard,[Parameter(Mandatory,Position=1)][double]$X,[Parameter(Mandatory,Position=2)][double]$Y,
        [Parameter(Mandatory,Position=3)][int]$Width,[Parameter(Mandatory,Position=4)][int]$Height)
    $completed = $false
    try { $Guard.MoveUnity($X,$Y,$Width,$Height); $completed = $true }
    finally { if (!$completed) { [void]$Guard.ReleaseOwnedInput() } }
}

function Press-HubKey {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard,
        [Parameter(Mandatory,Position=1)][Alias('Key')][ValidateSet('W','A','S','D','F','Tab','Esc','Escape','Enter','Ctrl','Control')][string[]]$Keys,
        [Parameter(Position=2)][ValidateRange(1,2000)][int]$HoldMilliseconds = 80)
    try {
        $Guard.AssertValid()
        if ($Guard.LeftHeld) { throw 'HubKeyChordDuringDragIsNotSupported' }
        foreach ($keyName in $Keys) { $Guard.KeyDown($keyName) }
        $Guard.WaitGuarded($HoldMilliseconds)
    }
    finally { if (!$Guard.ReleaseOwnedInput()) { throw 'HubOwnedInputReleaseFailed' } }
}

function Click-HubPointer {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard)
    try { $Guard.AssertValid(); $Guard.LeftDown(); $Guard.WaitGuarded(70); $Guard.LeftUp(); $Guard.WaitGuarded(60) }
    finally { if (!$Guard.ReleaseOwnedInput()) { throw 'HubOwnedInputReleaseFailed' } }
}

function Type-HubText {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard,[Parameter(Mandatory,Position=1)][AllowEmptyString()][string]$Text,
        [ValidateRange(0,50)][int]$CodeUnitDelayMilliseconds = 6)
    try {
        $Guard.AssertValid()
        if ($Guard.LeftHeld) { throw 'HubTextDuringDragIsNotSupported' }
        # 使用Unicode SendInput code unit，不接触Clipboard；绝不输出Text（可能是临时账号密码）。
        foreach ($unit in $Text.ToCharArray()) {
            if ([int]$unit -eq 0) { throw 'HubTextContainsNul' }
            $Guard.UnicodeUnit([ushort]$unit)
            $Guard.WaitGuarded($CodeUnitDelayMilliseconds)
        }
    }
    finally { if (!$Guard.ReleaseOwnedInput()) { throw 'HubOwnedInputReleaseFailed' } }
}

function Start-HubDrag {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard)
    $completed = $false
    try {
        $Guard.AssertValid()
        $Guard.LeftDown(); $Guard.WaitGuarded(40); $completed = $true
    }
    finally { if (!$completed) { [void]$Guard.ReleaseOwnedInput() } }
}

function Move-HubDrag {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard,[Parameter(Mandatory,Position=1)][double]$X,[Parameter(Mandatory,Position=2)][double]$Y,
        [Parameter(Mandatory,Position=3)][int]$Width,[Parameter(Mandatory,Position=4)][int]$Height,
        [ValidateRange(2,60)][int]$Steps = 12,[ValidateRange(60,2000)][int]$DurationMilliseconds = 360)
    $completed = $false
    try {
        $Guard.AssertValid()
        if (!$Guard.LeftHeld) { throw 'HubDragMoveWithoutOwnedLeftDown' }
        $from = $Guard.ReadPointer()
        for ($step = 1; $step -le $Steps; $step++) {
            $ratio = [double]$step/$Steps
            $Guard.MoveUnity($from.X+($X-$from.X)*$ratio,$from.Y+($Y-$from.Y)*$ratio,$Width,$Height)
            $Guard.WaitGuarded([Math]::Max(1,[int]($DurationMilliseconds/$Steps)))
        }
        $completed = $true
    }
    finally { if (!$completed) { [void]$Guard.ReleaseOwnedInput() } }
}

function End-HubDrag {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard)
    try { $Guard.WaitGuarded(60); $Guard.LeftUp(); $Guard.WaitGuarded(80) }
    finally { if (!$Guard.ReleaseOwnedInput()) { throw 'HubOwnedInputReleaseFailed' } }
}

function Save-HubScreenshot {
    [CmdletBinding()]
    param([Parameter(Mandatory,Position=0)]$Guard,[Parameter(Mandatory,Position=1)][string]$Path)
    $bitmap = $null; $graphics = $null; $created = $false; $completed = $false
    $target = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $target) { throw 'HubScreenshotMustUseANewFile' }
    try {
        Add-Type -AssemblyName System.Drawing
        $client = $Guard.ReadClient()
        $bitmap = [Drawing.Bitmap]::new($client.Width,$client.Height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        # 只截精确自有客户区；前后均核验身份/前台。不会截桌面或用户其它窗口的完整区域。
        $graphics.CopyFromScreen($client.ScreenX,$client.ScreenY,0,0,$bitmap.Size,[Drawing.CopyPixelOperation]::SourceCopy)
        $Guard.AssertValid()
        $after = $Guard.ReadClient()
        if ($after.Width -ne $client.Width -or $after.Height -ne $client.Height -or
            $after.ScreenX -ne $client.ScreenX -or $after.ScreenY -ne $client.ScreenY) { throw 'HubClientMovedDuringScreenshot' }
        $parentDirectory = Split-Path -Parent $target
        if (!(Test-Path -LiteralPath $parentDirectory -PathType Container)) { throw 'HubScreenshotDirectoryMissing' }
        $created = $true; $bitmap.Save($target,[Drawing.Imaging.ImageFormat]::Png)
        $Guard.AssertValid(); $completed = $true
    }
    finally {
        if ($null -ne $graphics) { $graphics.Dispose() }
        if ($null -ne $bitmap) { $bitmap.Dispose() }
        if (!$completed) {
            [void]$Guard.ReleaseOwnedInput()
            if ($created -and (Test-Path -LiteralPath $target -PathType Leaf)) { Remove-Item -LiteralPath $target -Force }
        }
    }
}

function Close-HubInputGuard {
    [CmdletBinding()]
    param([Parameter(Position=0)][AllowNull()]$Guard)
    if ($null -eq $Guard) { return }
    $released = $false
    try { $released = $Guard.ReleaseOwnedInput() }
    finally { $Guard.Dispose() }
    if (!$released) { throw 'HubOwnedInputReleaseFailed' }
}
