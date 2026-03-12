using Microsoft.Win32;

namespace YtmUrlSharp;

/// <summary>
/// Manages Windows startup registration via the CurrentUser registry.
/// </summary>
public static class StartupManager
{
    private const string RegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "YtmUrlSharp";

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            return key?.GetValue(AppName) != null;
        }
    }

    public static void Register()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;

        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
        key?.SetValue(AppName, $"\"{exePath}\"");
    }

    public static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, true);
        key?.DeleteValue(AppName, false);
    }

    public static void Toggle()
    {
        if (IsRegistered)
            Unregister();
        else
            Register();
    }
}
