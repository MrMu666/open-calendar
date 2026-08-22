using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CalendarApp.ViewModels;
using Microsoft.Win32;

namespace CalendarApp.Views;

public partial class SettingsWindow : Window
{
    // 预设色板：灰阶 / Material 500 / 深色背景（适合玻璃面板）
    private static readonly string[] PresetColors =
    [
        "#FFFFFF", "#F5F5F5", "#E0E0E0", "#9E9E9E", "#616161", "#212121",
        "#F44336", "#E91E63", "#9C27B0", "#673AB7", "#3F51B5", "#2196F3",
        "#03A9F4", "#00BCD4", "#009688", "#4CAF50", "#8BC34A", "#FFEB3B",
        "#FFC107", "#FF9800", "#FF5722", "#795548", "#607D8B", "#1A2333",
        "#0D1117", "#1F2430", "#232946", "#1B1B2F", "#101820", "#2E3440",
    ];

    public SettingsWindow()
    {
        InitializeComponent();
        BuildPalette();
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    // 调色板当前作用于哪个颜色项（"background" 或 "accent"）
    private string _paletteTarget = "background";

    private void BuildPalette()
    {
        foreach (var hex in PresetColors)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(5),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#33000000")),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Tag = hex,
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            swatch.MouseLeftButtonDown += OnPresetColorClicked;
            PaletteGrid.Children.Add(swatch);
        }
    }

    /// <summary>Left-click on the color preview block opens the palette popup.</summary>
    private void OnColorPreviewPressed(object? sender, MouseButtonEventArgs e)
    {
        if (sender is Border c)
        {
            _paletteTarget = c.Tag as string == "accent" ? "accent" : "background";
            PalettePopup.PlacementTarget = c;
            PalettePopup.IsOpen = true;
        }
    }

    /// <summary>Clicking a swatch applies its color and closes the palette.</summary>
    private void OnPresetColorClicked(object? sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string hex } && ViewModel is { } vm)
        {
            if (_paletteTarget == "accent")
            {
                vm.AccentColor = hex;
            }
            else
            {
                vm.BackgroundColor = hex;
            }

            PalettePopup.IsOpen = false;
        }
    }

    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择日程存储文件夹",
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.OpenFolder(dialog.FolderName);
        }
    }
}
