using System.Diagnostics;
using Microsoft.Win32;
using Nocturnal.Core;

namespace Nocturnal.App.Services;

public static class StartupManager
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string AppName = "Nocturnal";
    private const string RecoveryValueName = "NocturnalRecovery";

    public static bool IsLaunchAtStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetLaunchAtStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                key.SetValue(AppName, $"\"{exePath}\" --minimized");
            }
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    public static void SetOneShotRecovery(bool armed)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (armed)
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    key.SetValue(RecoveryValueName, $"\"{exePath}\" --minimized");
                }
            }
            else
            {
                key.DeleteValue(RecoveryValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry RunOnce failures must never disrupt power state changes.
        }
    }
}

public sealed class WindowsRecoveryHook : IRecoveryHook
{
    public void SetRecoveryArmed(bool armed) => StartupManager.SetOneShotRecovery(armed);
}
