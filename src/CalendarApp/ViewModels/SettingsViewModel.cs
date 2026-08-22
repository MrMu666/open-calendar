using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CalendarApp.Data;
using CalendarApp.Platform;

namespace CalendarApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private bool _suppressSave;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _services.Items.Changed += () =>
        {
            RefreshStatus();
            ReloadAppearance();
        };
        RefreshStatus();
        ReloadAppearance();
        LaunchAtStartup = AutoStartService.IsEnabled();
    }

    /// <summary>Raised after a storage folder has been successfully opened.</summary>
    public event Action? StorageOpened;

    /// <summary>Raised when the background color/opacity was changed and saved.</summary>
    public event Action? AppearanceChanged;

    public event Action? RequestShowMainPanel;

    public event Action? RequestExit;

    [ObservableProperty]
    public partial string CurrentFile { get; set; } = "未打开";

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackgroundColor { get; set; } = "#0D1117";

    [ObservableProperty]
    public partial double BackgroundOpacity { get; set; } = 0.6;

    /// <summary>Accent color (selected day / 新增事项 / 跳转按钮共用).</summary>
    [ObservableProperty]
    public partial string AccentColor { get; set; } = "#4CAF50";

    /// <summary>Solid accent preview brush shown in the settings window.</summary>
    [ObservableProperty]
    public partial Brush AccentPreview { get; set; } = MakeBrush("#4CAF50");

    /// <summary>Composite background brush; rebuilt on every color/opacity change so the UI updates live.</summary>
    [ObservableProperty]
    public partial Brush PanelBackground { get; set; } = MakeBrush("#0D1117", 0.6);

    /// <summary>Whether the app auto-launches at sign-in. Reflects the OS registration state.</summary>
    [ObservableProperty]
    public partial bool LaunchAtStartup { get; set; }

    /// <summary>Opens the storage folder chosen by the user. Returns true on success.</summary>
    public bool OpenFolder(string path) => Apply(path);

    [RelayCommand]
    private void ShowMainPanel() => RequestShowMainPanel?.Invoke();

    [RelayCommand]
    private void Exit() => RequestExit?.Invoke();

    private bool Apply(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            _services.Items.Open(fullPath);

            // The one and only cross-session datum, recorded in the user profile.
            _services.Settings.LastOpenedFile = fullPath;
            _services.Settings.Save();

            Status = $"✓ 已加载日程文件夹：{fullPath}";
            RefreshStatus();
            StorageOpened?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Status = $"✗ 打开失败：{ex.Message}";
            return false;
        }
    }

    private void ReloadAppearance()
    {
        _suppressSave = true;
        try
        {
            BackgroundColor = _services.Settings.BackgroundColor;
            BackgroundOpacity = _services.Settings.BackgroundOpacity;
            AccentColor = _services.Settings.AccentColor;
            RebuildBackground();
            RebuildAccentPreview();
        }
        finally
        {
            _suppressSave = false;
        }
    }

    partial void OnBackgroundColorChanged(string value)
    {
        RebuildBackground();
        SaveAppearance();
    }

    partial void OnBackgroundOpacityChanged(double value)
    {
        RebuildBackground();
        SaveAppearance();
    }

    partial void OnAccentColorChanged(string value)
    {
        RebuildAccentPreview();
        SaveAppearance();
    }

    private void RebuildBackground()
    {
        if (TryParseHexColor(BackgroundColor, out var color))
        {
            PanelBackground = new SolidColorBrush(color) { Opacity = BackgroundOpacity };
        }
    }

    private void RebuildAccentPreview()
    {
        if (TryParseHexColor(AccentColor, out var color))
        {
            AccentPreview = new SolidColorBrush(color);
        }
    }

    private void SaveAppearance()
    {
        if (_suppressSave)
        {
            return;
        }

        _services.Settings.BackgroundColor = BackgroundColor;
        _services.Settings.BackgroundOpacity = BackgroundOpacity;
        _services.Settings.AccentColor = AccentColor;
        _services.Settings.Save();
        AppearanceChanged?.Invoke();
    }

    partial void OnLaunchAtStartupChanged(bool value)
    {
        AutoStartService.SetEnabled(value);
        _services.Settings.LaunchAtStartup = value;
        _services.Settings.Save();
    }

    private void RefreshStatus()
    {
        CurrentFile = _services.Items.RootPath ?? "未打开";
    }

    private static Brush MakeBrush(string hex, double opacity = 1.0)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static bool TryParseHexColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }
}
