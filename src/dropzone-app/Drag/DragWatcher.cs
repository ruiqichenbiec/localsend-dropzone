using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Dropzone.App.SystemIntegration;
using Dropzone.Core.Settings;
using Dropzone.Core.Ui;
using Microsoft.Win32;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Dropzone.App.Drag;

/// <summary>
/// Watches the mouse system-wide (<c>WH_MOUSE_LL</c>) for a left-button drag that reaches the configured corner of
/// the monitor the cursor is on. The hook callback only records the event and posts the real work to the UI thread.
/// Create, use and dispose on the UI thread (the hook runs on the thread that installed it). The hook is renewed after
/// an unlock or resume, every 10 minutes, and when a drag goes on without the hook seeing it, because Windows can drop
/// it without telling.
/// </summary>
public sealed class DragWatcher : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int SmSwapButton = 23;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;

    /// <summary>How far (in pixels, each axis) the cursor must move after the press before it counts as a drag.</summary>
    private const int DragThreshold = 10;

    /// <summary>
    /// Windows silently drops a low-level hook whose callback once took too long (for example while the PC was busy
    /// or asleep); there is no notification, so the hook is also renewed on this interval.
    /// </summary>
    private static readonly TimeSpan ReinstallInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the button and cursor are sampled to spot a dropped hook (<see cref="HookStallDetector"/>). Two cheap
    /// calls per second, so a drag that the hook misses renews it within a second or two.
    /// </summary>
    private static readonly TimeSpan StallCheckInterval = TimeSpan.FromSeconds(1);

    private readonly Dispatcher _dispatcher;
    private readonly Func<Corner> _corner;

    // Kept in a field so the delegate the hook calls is never collected.
    private readonly LowLevelMouseProc _proc;
    private readonly DispatcherTimer _reinstallTimer;
    private readonly DispatcherTimer _stallTimer;
    private readonly HookStallDetector _stallDetector = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IntPtr _hook;
    private long _hookMessages;
    private bool _disposed;

    private bool _down;
    private Point _downAt;
    private Point _latest;
    private bool _fired;
    private bool _checkPending;

    /// <param name="corner">The corner in force, read on every check so a settings change applies at once.</param>
    public DragWatcher(Dispatcher dispatcher, Func<Corner> corner)
    {
        _dispatcher = dispatcher;
        _corner = corner;
        _proc = HookProc;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Win32Exception error = new(Marshal.GetLastWin32Error(), "Installing the mouse hook failed.");
            AppLog.Error("Installing the mouse hook failed.", error);
            throw error;
        }

        AppLog.Info("Mouse hook installed.");

        // Unlock and resume are when Windows is most likely to have dropped the hook.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _reinstallTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = ReinstallInterval };
        _reinstallTimer.Tick += (_, _) => Reinstall("periodic");
        _reinstallTimer.Start();
        _stallTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = StallCheckInterval };
        _stallTimer.Tick += (_, _) => CheckForStall();
        _stallTimer.Start();
    }

    /// <summary>True when the primary button (the right one when the buttons are swapped) is physically down.</summary>
    private static bool PrimaryButtonHeld()
    {
        int key = GetSystemMetrics(SmSwapButton) != 0 ? VkRButton : VkLButton;
        return GetAsyncKeyState(key) < 0;
    }

    /// <summary>A drag reached the corner's trigger region; carries that monitor's work area. Once per press.</summary>
    public event Action<Rectangle>? NearCorner;

    /// <summary>The left button was released (or turned out not to be held any more); carries the cursor position.</summary>
    public event Action<Point>? Released;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _reinstallTimer.Stop();
        _stallTimer.Stop();
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    /// <summary>SystemEvents fire on their own thread; the hook belongs to the UI thread.</summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon
            or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            _dispatcher.BeginInvoke(() => Reinstall($"session {e.Reason}"));
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _dispatcher.BeginInvoke(() => Reinstall("resume"));
        }
    }

    /// <summary>
    /// Installs a fresh hook, then removes the old one (UI thread: no hook call can run in between). If installing
    /// fails, the old hook is kept.
    /// </summary>
    private void Reinstall(string reason)
    {
        if (_disposed)
        {
            return;
        }

        IntPtr fresh = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        if (fresh == IntPtr.Zero)
        {
            AppLog.Warn($"Reinstalling the mouse hook ({reason}) failed.", new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }

        IntPtr old = _hook;
        _hook = fresh;
        if (old != IntPtr.Zero)
        {
            UnhookWindowsHookEx(old);
        }

        AppLog.Info($"Mouse hook reinstalled ({reason}).");
    }

    /// <summary>
    /// Renews the hook when the primary button is held and the cursor moved since the last tick but the hook saw no
    /// message. The button-down was missed as well, so the drag under way is taken as starting here and can still
    /// reach the corner.
    /// </summary>
    private void CheckForStall()
    {
        Point? cursor = GetCursorPos(out NativePoint at) ? new Point(at.X, at.Y) : null;
        bool held = PrimaryButtonHeld();
        if (_disposed || !_stallDetector.Check(_clock.Elapsed, held, cursor, _hookMessages))
        {
            return;
        }

        Reinstall("a drag went unseen");
        if (!_down && held && cursor is { } position)
        {
            _down = true;
            _downAt = position;
            _latest = position;
            _fired = false;
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        _hookMessages++;
        if (code >= 0)
        {
            int message = (int)wParam;
            if (message is WmLButtonDown or WmMouseMove or WmLButtonUp)
            {
                MsLlHookStruct data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                Record(message, new Point(data.X, data.Y));
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>Bookkeeping only; anything slower is posted to the dispatcher.</summary>
    private void Record(int message, Point point)
    {
        switch (message)
        {
            case WmLButtonDown:
                _down = true;
                _downAt = point;
                _fired = false;
                break;
            case WmMouseMove when _down && !_fired:
                _latest = point;
                if (!_checkPending
                    && (Math.Abs(point.X - _downAt.X) > DragThreshold || Math.Abs(point.Y - _downAt.Y) > DragThreshold))
                {
                    _checkPending = true;
                    _dispatcher.BeginInvoke(CheckCorner);
                }

                break;
            case WmLButtonUp:
                _down = false;
                _dispatcher.BeginInvoke(() => Released?.Invoke(point));
                break;
        }
    }

    private void CheckCorner()
    {
        _checkPending = false;
        if (!_down || _fired || _hook == IntPtr.Zero)
        {
            return;
        }

        if (!PrimaryButtonHeld())
        {
            // The button-up never reached the hook (secure desktop, a higher-integrity window, a skipped hook call):
            // treat it as released now, so a plain hover cannot bring up the zone.
            _down = false;
            Released?.Invoke(_latest);
            return;
        }

        try
        {
            Rectangle workArea = WinFormsScreen.FromPoint(_latest).WorkingArea;
            if (CornerGeometry.TriggerRegion(workArea, _corner()).Contains(_latest))
            {
                _fired = true;
                NearCorner?.Invoke(workArea);
            }
        }
        catch (Exception error)
        {
            AppLog.Warn("Drag corner check failed.", error);
        }
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
