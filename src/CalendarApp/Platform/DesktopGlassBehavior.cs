using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CalendarApp.Platform;

/// <summary>
/// Faithful port of Rainmeter's three-window z-order system (WPF edition).
///
/// Architecture (from Rainmeter/Library/System.cpp + Skin.cpp):
/// 1. System window: hidden, HWND_BOTTOM, blocks z-order changes via
///    SWP_NOZORDER in WM_WINDOWPOSCHANGING. Timer detects Show Desktop.
/// 2. Helper window: hidden, HWND_BOTTOM (or HWND_TOPMOST during Show
///    Desktop, positioned behind the backmost TOPMOST window).
/// 3. Main window: positioned via SetWindowPos(hwnd, helperHwnd).
///    Hooked via HwndSource.AddHook to block z-order changes and
///    minimize/close.
///
/// WPF adaptations vs the Avalonia original:
/// - SetWindowSubclass (comctl32) replaced with HwndSource.AddHook; the
///   hook delegate is kept alive by the HwndSource (no GC FailFast risk).
/// - Edge resize is NOT handled here: the panel is a layered window
///   (AllowsTransparency=true + WS_EX_NOACTIVATE), so neither WindowChrome
///   nor a WM_NCHITTEST border hit-test can start the native sizing loop.
///   It is implemented as a window-level, position-based resize in
///   Platform/EdgeResizer.cs (see also the rim overlay in MainWindow.xaml).
/// </summary>
public static class DesktopGlassBehavior
{
    // ── Win32 constants ──────────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_CLOSE = 0xF060;

    // SWP_NOZORDER is 0x0004. (The Avalonia original ORs 0x0200 which is
    // actually SWP_NOOWNERZORDER while its comment claims SWP_NOZORDER;
    // Rainmeter and this file's documented intention both require
    // SWP_NOZORDER, so the correct value is used here.)
    private const uint SWP_NOZORDER = 0x0004;

    // Exact copy of Rainmeter's ZPOS_FLAGS
    private const uint ZPOS_FLAGS =
        0x0002 /*SWP_NOMOVE*/ | 0x0001 /*SWP_NOSIZE*/ |
        0x0200 /*SWP_NOOWNERZORDER*/ | 0x0010 /*SWP_NOACTIVATE*/ |
        0x0400 /*SWP_NOSENDCHANGING*/;

    private static readonly IntPtr HWND_BOTTOM = new(1);
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private static readonly ConditionalWeakTable<Window, WindowState> States = new();

    // ── Public API ───────────────────────────────────────────────────

    public static void Attach(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        window.SourceInitialized += OnSourceInitialized;
    }

    public static void NotifyAppHiding(Window window, bool hiding)
    {
        if (States.TryGetValue(window, out var state))
            state.IsAppHiding = hiding;
    }

    /// <summary>
    /// Keeps the current Show Desktop z-order while an app-owned transient
    /// window (such as the date dialog) is active. The transient window
    /// changes the foreground HWND, which must not be interpreted as the
    /// user ending Show Desktop.
    /// </summary>
    public static void NotifyTransientWindow(Window mainWindow, bool visible)
    {
        if (States.TryGetValue(mainWindow, out var state))
            state.IsTransientWindowVisible = visible;
    }

    /// <summary>
    /// Reasserts the desktop-layer placement after an interaction inside
    /// the non-activating main panel. Important after a transient dialog
    /// closes: clicking a panel button must not end Show Desktop implicitly.
    /// </summary>
    public static void NotifyMainWindowInteraction(Window mainWindow)
    {
        if (States.TryGetValue(mainWindow, out var state))
            state.NotifyMainWindowInteraction();
    }

    // ── Setup ────────────────────────────────────────────────────────

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var state = States.GetValue(window, static _ => new WindowState());

        // Extended styles
        var exStyle = (long)GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        // Create system + helper windows (must be on UI thread for message loop)
        state.CreateSystemWindow();
        state.CreateHelperWindow();

        // Position main window relative to helper
        SetWindowPos(hwnd, state.HelperHwnd, 0, 0, 0, 0, ZPOS_FLAGS);

        // Hook main window to block z-order changes and minimize/close
        state.HookMainWindow(hwnd);

        // Start Show Desktop detection timer (background thread)
        state.StartDetectionTimer();
    }


    // ── Show Desktop detection ───────────────────────────────────────

    private static IntPtr GetDesktopIconsHostWindow()
    {
        // Windows 11 24H2+: use shell window
        var shellW = GetShellWindow();
        if (shellW != IntPtr.Zero)
        {
            if (FindWindowEx(shellW, IntPtr.Zero, "SHELLDLL_DefView", "") != IntPtr.Zero)
                return shellW;
        }
        // Fallback
        var defView = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return defView;
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero) return progman;
        return IntPtr.Zero;
    }

    private static bool IsShowDesktopActive()
    {
        var desktopHost = GetDesktopIconsHostWindow();
        if (desktopHost == IntPtr.Zero) return false;
        if (!IsWindowVisible(desktopHost)) return false;

        var fgWnd = GetForegroundWindow();
        if (fgWnd == IntPtr.Zero) return false;
        if (fgWnd == desktopHost) return true;

        var defaultShell = GetShellWindow();
        if (fgWnd == defaultShell) return true;

        // Desktop host at bottom = Show Desktop rearranged
        if (GetWindow(desktopHost, 3) == IntPtr.Zero) return true;

        return false;
    }

    // ── Window state ─────────────────────────────────────────────────

    private sealed class WindowState
    {
        private IntPtr _hwnd;
        private IntPtr _systemHwnd;
        private IntPtr _helperHwnd;
        private volatile bool _isAppHiding;
        private volatile bool _isTransientWindowVisible;
        private int _transientWindowGraceTicks;
        private bool _showDesktop;
        private System.Threading.Timer? _detectionTimer;
        private readonly string _systemClassName = "CalendarAppSystem";
        private readonly string _helperClassName = "CalendarAppHelper";

        public IntPtr HelperHwnd => _helperHwnd;
        public bool IsAppHiding { get => _isAppHiding; set => _isAppHiding = value; }
        public bool IsTransientWindowVisible
        {
            get => _isTransientWindowVisible;
            set
            {
                _isTransientWindowVisible = value;
                if (!value && _showDesktop)
                {
                    // Allow the foreground window to settle after the dialog
                    // closes; one false foreground sample must not hide the
                    // panel.
                    _transientWindowGraceTicks = 4; // 1 second at 250 ms
                }
            }
        }

        public void NotifyMainWindowInteraction()
        {
            if (!_showDesktop)
            {
                return;
            }

            // Keep the desktop-layer placement alive while the user
            // interacts with the non-activating panel, and immediately
            // repair any z-order change that happened during the
            // preceding transient-window close.
            _transientWindowGraceTicks = 4;
            ApplyZOrder();
        }

        public WindowState()
        {
        }


        // ── System window (Rainmeter's c_Window) ─────────────────────

        public void CreateSystemWindow()
        {
            var hInstance = GetModuleHandle(null);

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SimpleSystemWndProc),
                hInstance = hInstance,
                lpszClassName = _systemClassName,
            };
            RegisterClassEx(ref wc);

            _systemHwnd = CreateWindowEx(
                0, _systemClassName, "System",
                0, 0, 0, 1, 1,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            // System window always at HWND_BOTTOM
            SetWindowPos(_systemHwnd, HWND_BOTTOM, 0, 0, 0, 0, ZPOS_FLAGS);
        }

        // ── Helper window (Rainmeter's c_HelperWindow) ───────────────

        public void CreateHelperWindow()
        {
            var hInstance = GetModuleHandle(null);

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SimpleHelperWndProc),
                hInstance = hInstance,
                lpszClassName = _helperClassName,
            };
            RegisterClassEx(ref wc);

            _helperHwnd = CreateWindowEx(
                0x80 /*WS_EX_TOOLWINDOW*/, _helperClassName, "Helper",
                unchecked((int)0x80000000) /*WS_POPUP*/,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            // Helper always at HWND_BOTTOM
            SetWindowPos(_helperHwnd, HWND_BOTTOM, 0, 0, 0, 0, ZPOS_FLAGS);
        }

        // ── Main window hook (WPF: HwndSource.AddHook) ───────────────
        // Rainmeter skins: also block z-order + block minimize/close

        private IntPtr HookMainWindowWndProc(IntPtr hWnd, int uMsg,
            IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (uMsg == WM_WINDOWPOSCHANGING)
            {
                // Block external z-order changes
                var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                pos.flags |= SWP_NOZORDER;
                Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
                handled = true;
                return IntPtr.Zero;
            }

            if (uMsg == WM_SYSCOMMAND)
            {
                int cmd = (int)(wParam.ToInt64() & 0xFFF0);
                if (cmd is SC_MINIMIZE or SC_CLOSE && !_isAppHiding)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }

            handled = false;
            return IntPtr.Zero;
        }

        public void HookMainWindow(IntPtr hwnd)
        {
            _hwnd = hwnd;
            var source = HwndSource.FromHwnd(hwnd);
            if (source is null)
            {
                return;
            }

            // The hook delegate is referenced by the HwndSource for its
            // lifetime — no external keep-alive needed, unlike the WndProc
            // delegates below for RegisterClassEx.
            source.AddHook(HookMainWindowWndProc);
        }


        // ── Detection timer ──────────────────────────────────────────
        // Rainmeter: TIMER_SHOWDESKTOP every 250ms

        public void StartDetectionTimer()
        {
            _detectionTimer = new System.Threading.Timer(_ =>
            {
                if (_isAppHiding) return;

                bool showDesktop = IsShowDesktopActive();

                // A date/settings dialog belongs to the app but becomes the
                // foreground window. Keep the existing Show Desktop state
                // while it is visible; only a real shell transition may
                // clear it.
                if (_showDesktop && (_isTransientWindowVisible || _transientWindowGraceTicks > 0))
                {
                    showDesktop = true;
                    if (!_isTransientWindowVisible)
                    {
                        _transientWindowGraceTicks--;
                    }
                }

                if (showDesktop != _showDesktop)
                {
                    _showDesktop = showDesktop;
                    ApplyZOrder();
                }
                else if (_showDesktop)
                {
                    // Re-assert during Show Desktop
                    ApplyZOrder();
                }
            }, null, 0, 250);
        }

        // ── PrepareHelperWindow (Rainmeter pattern) ──────────────────

        private void ApplyZOrder()
        {
            if (_showDesktop)
            {
                // 1. Helper → HWND_TOPMOST
                SetWindowPos(_helperHwnd, HWND_TOPMOST, 0, 0, 0, 0, ZPOS_FLAGS);

                // 2. Find backmost TOPMOST window and insert helper behind it
                var desktopHost = GetDesktopIconsHostWindow();
                if (desktopHost != IntPtr.Zero)
                {
                    var current = desktopHost;
                    while (true)
                    {
                        var next = GetWindow(current, 3 /* GW_HWNDPREV */);
                        if (next == IntPtr.Zero) break;
                        long ex = GetWindowLong(next, GWL_EXSTYLE);
                        if ((ex & 0x8 /*WS_EX_TOPMOST*/) != 0)
                        {
                            SetWindowPos(_helperHwnd, next, 0, 0, 0, 0, ZPOS_FLAGS);
                            break;
                        }
                        current = next;
                    }
                }

                // 3. Main window follows helper
                if (_hwnd != IntPtr.Zero)
                    SetWindowPos(_hwnd, _helperHwnd, 0, 0, 0, 0, ZPOS_FLAGS);
            }
            else
            {
                // Helper → HWND_BOTTOM
                SetWindowPos(_helperHwnd, HWND_BOTTOM, 0, 0, 0, 0, ZPOS_FLAGS);

                // Remove TOPMOST from helper
                long helperEx = GetWindowLong(_helperHwnd, GWL_EXSTYLE);
                if ((helperEx & 0x8) != 0)
                    SetWindowLong(_helperHwnd, GWL_EXSTYLE, helperEx & ~0x8);

                // Main window follows helper
                if (_hwnd != IntPtr.Zero)
                    SetWindowPos(_hwnd, _helperHwnd, 0, 0, 0, 0, ZPOS_FLAGS);
            }
        }
    }


    // ── P/Invoke ─────────────────────────────────────────────────────

    private delegate IntPtr SimpleWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // IMPORTANT: these delegates must be kept alive for the process
    // lifetime. They are handed to RegisterClassEx as window procedures;
    // if the GC collects them, the next window message calls into a freed
    // delegate and the runtime calls Environment.FailFast (crash + exit).
    private static readonly SimpleWndProc SimpleSystemWndProc = SystemWndProc;
    private static readonly SimpleWndProc SimpleHelperWndProc = HelperWndProc;

    private static IntPtr SystemWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.flags |= SWP_NOZORDER;
            Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, uMsg, wParam, lParam);
    }

    private static IntPtr HelperWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            pos.flags |= SWP_NOZORDER;
            Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, uMsg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr FindWindow(string? cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? name);

    private static IntPtr GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLong(IntPtr hWnd, int nIndex, long value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value)) : new IntPtr(SetWindowLong32(hWnd, nIndex, (int)value));
}
