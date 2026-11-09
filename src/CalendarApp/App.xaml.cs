using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using CalendarApp.Models;
using CalendarApp.Platform;
using CalendarApp.ViewModels;
using CalendarApp.Views;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace CalendarApp;

/// <summary>
/// Application composition root. Owns the single-instance guard, the tray icon,
/// first-run bootstrap, window lifecycle and window-geometry persistence.
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private AppServices? _services;

    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private TaskbarIcon? _tray;

    private DispatcherTimer? _geometrySaveTimer;
    private Window? _geometryWindow;

    private DispatcherTimer? _displayChangedTimer;
    private DispatcherTimer? _displayChangedFollowUpTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard: a second instance silently exits before any
        // window is shown. The mutex must stay a field, not a `using` local —
        // it is released only in finally/exit, so the second instance detects us.
        _mutex = new Mutex(true, @"Global\CalendarApp_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _services = AppServices.Create();
        Theme.ApplyAccent(_services.Settings.AccentColor);
        Theme.RegisterResources();

        // RDP 连接/断开、显示器热插拔、分辨率/DPI 变化都不触发窗口的
        // LocationChanged/SizeChanged，但都会改变当前显示器的几何，所以必须
        // 监听系统事件并在变化后重新对齐窗口位置与大小。
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.TimeChanged += OnSystemTimeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Self-heal the Run key if the exe path changed since last launch.
        if (_services.Settings.LaunchAtStartup)
        {
            try { AutoStartService.SetEnabled(true); }
            catch { /* best-effort */ }
        }

        try { CreateTrayIcon(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"托盘图标创建失败: {ex.Message}"); }

        // First-run bootstrap: main panel only when a storage folder is open.
        if (_services.Items.IsOpen)
        {
            CreateAndShowMainWindow();
        }
        else
        {
            ShowSettingsWindow();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { _tray?.Dispose(); }
        catch { /* best-effort */ }
        try { _services?.Items.Dispose(); }
        catch { /* best-effort */ }
        try { _mutex?.Dispose(); }
        catch { /* best-effort */ }
        base.OnExit(e);
    }

    private void CreateAndShowMainWindow()
    {
        if (_services is null) return;

        if (_mainWindow is not null)
        {
            DesktopGlassBehavior.NotifyAppHiding(_mainWindow, false);
            _mainWindow.Show();

            // 托盘/快捷键把面板重新叫出来时顺手复查一次跨天：万一午夜定时器
            // 和系统事件都没赶上（例如长时间休眠），打开面板也不会停在昨天。
            ((MainViewModel?)_mainWindow.DataContext)?.CheckDayRollover();
            return;
        }

        var vm = new MainViewModel(_services);
        var w = new MainWindow { DataContext = vm };

        vm.RequestOpenSettings += ShowSettingsWindow;
        vm.RequestHide += () =>
        {
            DesktopGlassBehavior.NotifyAppHiding(w, true);
            w.Hide();
        };
        vm.RequestNewItem += ShowItemEditor;
        vm.RequestEditItem += item =>
            ShowItemEditor(item.Event);

        DesktopGlassBehavior.Attach(w);
        // NOTE: no DwmSetWindowAttribute/SetWindowCompositionAttribute here —
        // DWM blur compositing renders as an opaque black rectangle over RDP.
        // The panel's blurred backdrop is wallpaper-snapshot based (see
        // WallpaperBackdrop + MainWindow.RefreshWallpaper), which is safe on
        // every compositor.

        w.LocationChanged += (_, _) =>
        {
            if (!WindowGeometry.OnMovedOrResized(w, _services.LastView))
                ScheduleGeometrySave(w);
        };
        w.SizeChanged += (_, _) => ScheduleGeometrySave(w);
        w.Closing += (_, _) => SaveWindowGeometry(w);

        if (!TryRestoreWindowGeometry(w))
        {
            // No saved geometry (e.g. first run on a new machine): size the
            // panel to the current desktop resolution and dock it bottom-right.
            w.SourceInitialized += (_, _) => WindowGeometry.ApplyDefaultPlacement(w);
        }

        _mainWindow = w;
        MainWindow = w;
        w.Show();
    }

    private void ShowItemEditor() => ShowItemEditor(existing: null);

    private void ShowItemEditor(ScheduleEvent? existing)
    {
        if (_mainWindow is null || _services is null) return;

        var vm = (MainViewModel)_mainWindow.DataContext;
        var width = _mainWindow.ActualWidth > 0 ? _mainWindow.ActualWidth : 340;
        var height = _mainWindow.ActualHeight > 0 ? _mainWindow.ActualHeight : 500;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle;
        var center = new System.Windows.Point(
            _mainWindow.Left + width / 2,
            _mainWindow.Top + height / 2);

        var editor = new ItemEditorWindow(vm, width, height, center, existing);
        DesktopGlassBehavior.NotifyTransientWindow(_mainWindow, true);
        editor.Closed += (_, _) => DesktopGlassBehavior.NotifyTransientWindow(_mainWindow, false);
        editor.Show();
    }

    private void ShowSettingsWindow()
    {
        if (_services is null) return;

        var vm = new SettingsViewModel(_services);
        var w = new SettingsWindow { DataContext = vm };

        vm.AppearanceChanged += () => _mainWindow?.Dispatcher.BeginInvoke(() =>
            ((MainViewModel?)_mainWindow?.DataContext)?.ReloadAppearance());
        vm.RequestShowMainPanel += CreateAndShowMainWindow;
        vm.RequestExit += () => Shutdown();
        vm.StorageOpened += CreateAndShowMainWindow;

        _settingsWindow = w;
        if (_mainWindow is not null)
        {
            w.Owner = _mainWindow;
            w.ShowInTaskbar = true;

            // Same treatment as the item editor: during Show Desktop (Win+D)
            // the panel is re-asserted above every normal window every 250ms,
            // which would bury this normal-band window behind it. Topmost=True
            // puts the settings window in the TOPMOST band, and
            // NotifyTransientWindow keeps the Show Desktop state stable while
            // this window holds the foreground.
            DesktopGlassBehavior.NotifyTransientWindow(_mainWindow, true);
            w.Closed += (_, _) => DesktopGlassBehavior.NotifyTransientWindow(_mainWindow, false);
        }
        w.Show();
    }

    private void CreateTrayIcon()
    {
        var icon = new System.Windows.Media.Imaging.BitmapImage();
        icon.BeginInit();
        icon.UriSource = new Uri("pack://application:,,,/Assets/app.ico");
        icon.EndInit();

        _tray = new TaskbarIcon
        {
            IconSource = icon,
            ToolTipText = "日程桌面",
        };

        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(MakeMenuItem("显示面板", () => CreateAndShowMainWindow()));
        menu.Items.Add(MakeMenuItem("隐藏面板", () =>
        {
            if (_mainWindow is null) return;
            DesktopGlassBehavior.NotifyAppHiding(_mainWindow, true);
            _mainWindow.Hide();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("设置", ShowSettingsWindow));
        menu.Items.Add(MakeMenuItem("重置窗口位置", ResetWindowPosition));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("退出应用", () => Shutdown()));
        _tray.ContextMenu = menu;

        _tray.TrayLeftMouseUp += (_, _) => CreateAndShowMainWindow();

        // Explicitly register the tray icon with the OS.
        //
        // TaskbarIcon (H.NotifyIcon.Wpf) only calls ForceCreate() from its own
        // Loaded event. Loaded never fires here because the icon is created as a
        // plain code-behind object and never added to a window's visual tree, so
        // without this the Shell_NotifyIcon(NIM_ADD) registration never runs and
        // no tray icon appears. The false flag keeps Windows 11 Efficiency Mode
        // off so the app's foreground/background behaviour is unchanged. The icon
        // asset loads asynchronously; TrayIcon.UpdateIcon covers both orderings,
        // pushing the real HICON to the tray once it is ready.
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    private static System.Windows.Controls.MenuItem MakeMenuItem(
        string header, Action onClick)
    {
        var item = new System.Windows.Controls.MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ScheduleGeometrySave(Window w)
    {
        _geometryWindow = w;
        _geometrySaveTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _geometrySaveTimer.Stop();
        _geometrySaveTimer.Tick += OnGeometrySaveTick;
        _geometrySaveTimer.Start();
    }

    private void OnGeometrySaveTick(object? sender, EventArgs e)
    {
        _geometrySaveTimer?.Stop();
        if (_geometryWindow is not null)
        {
            SaveWindowGeometry(_geometryWindow);
        }
    }

    private void SaveWindowGeometry(Window w)
    {
        if (_services is null || w.WindowState != WindowState.Normal)
        {
            return;
        }

        WindowGeometry.SaveNow(w, _services.LastView);
    }

    /// <summary>
    /// DisplaySettingsChanged fires on RDP connect/disconnect, monitor hot-plug
    /// and resolution/DPI changes. Windows often reports the monitor info a
    /// little late, so we debounce the reconcile and then run a follow-up check
    /// to catch the settled geometry (mirrors the wallpaper-transition pattern).
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        ScheduleDisplayReconcile();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // RemoteConnect / ConsoleConnect / SessionLogon: the desktop resolution
        // may have changed while the session was away.
        if (e.Reason is SessionSwitchReason.RemoteConnect
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.SessionLogon)
        {
            ScheduleDisplayReconcile();
        }
    }

    /// <summary>
    /// 系统时间/时区被修改（手动改时间、系统校时，以及部分休眠恢复场景）后，
    /// 本地“今天”可能已经变了，而且之前排好的午夜定时器已不对应新的零点：
    /// 让主面板立即复查一次跨天并重排定时器。SystemEvents 回调不一定在 UI
    /// 线程上，必须封送。
    /// </summary>
    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        _mainWindow?.Dispatcher.BeginInvoke(new Action(() =>
            ((MainViewModel?)_mainWindow?.DataContext)?.CheckDayRollover()));
    }

    /// <summary>
    /// 休眠/待机期间 DispatcherTimer 不会触发；恢复时补一次跨天复查，避免
    /// “合盖过夜 → 第二天打开面板仍停在昨天”的情况。
    /// </summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _mainWindow?.Dispatcher.BeginInvoke(new Action(() =>
            ((MainViewModel?)_mainWindow?.DataContext)?.CheckDayRollover()));
    }

    private void ScheduleDisplayReconcile()
    {
        _displayChangedTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _displayChangedTimer.Stop();
        _displayChangedTimer.Tick += OnDisplayChangedTick;
        _displayChangedTimer.Start();

        _displayChangedFollowUpTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _displayChangedFollowUpTimer.Stop();
        _displayChangedFollowUpTimer.Tick += OnDisplayChangedFollowUpTick;
        _displayChangedFollowUpTimer.Start();
    }

    private void OnDisplayChangedTick(object? sender, EventArgs e)
    {
        _displayChangedTimer?.Stop();
        if (_services is null || _mainWindow is null) return;
        WindowGeometry.Reconcile(_mainWindow, _services.LastView);
    }

    private void OnDisplayChangedFollowUpTick(object? sender, EventArgs e)
    {
        _displayChangedFollowUpTimer?.Stop();
        if (_services is null || _mainWindow is null) return;
        WindowGeometry.Reconcile(_mainWindow, _services.LastView);
    }

    private bool TryRestoreWindowGeometry(Window w)
    {
        if (_services is null)
        {
            return false;
        }

        return WindowGeometry.TryRestore(w, _services.LastView);
    }

    private void ResetWindowPosition()
    {
        if (_services is null || _mainWindow is null)
        {
            return;
        }

        WindowGeometry.ResetPosition(_mainWindow, _services.LastView);
    }
}
