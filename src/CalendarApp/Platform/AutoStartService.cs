using System.Diagnostics;
using Microsoft.Win32;

namespace CalendarApp.Platform;

/// <summary>
/// Registers/unregisters the app to launch at user sign-in.
///
/// Windows: writes/removes the executable path under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (per-user, no
/// elevation required). macOS/Linux: not implemented yet — <see cref="IsSupported"/>
/// returns false there and all methods no-op, mirroring DesktopGlassBehavior.
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CalendarApp";

    /// <summary>Whether this platform supports auto-start registration.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// True when the Run key currently points at this app's executable.
    /// Always false on unsupported platforms.
    /// </summary>
    public static bool IsEnabled()
    {
        if (!IsSupported)
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value &&
                   string.Equals(value, ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStart] 读取开机启动状态失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>Adds or removes the Run key entry. No-op on unsupported platforms.</summary>
    public static void SetEnabled(bool enabled)
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                key.SetValue(ValueName, ExecutablePath);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStart] 设置开机启动失败: {ex.Message}");
        }
    }

    private static string ExecutablePath =>
        Environment.ProcessPath ??
        throw new InvalidOperationException("无法定位当前可执行文件路径。");
}
