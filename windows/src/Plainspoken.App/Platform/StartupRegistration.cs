using Microsoft.Win32;
using Plainspoken.Core;
using Plainspoken.Core.Logging;

namespace Plainspoken.App.Platform;

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string AutostartArgument = "--autostart";

    /// <summary>Adds/updates or removes the Run entry. Also fixes the path if the exe was moved.</summary>
    public static void Apply(bool enabled, ILog log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    return;
                }

                var value = $"\"{exe}\" {AutostartArgument}";
                if (!string.Equals(key.GetValue(AppInfo.Name) as string, value, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(AppInfo.Name, value, RegistryValueKind.String);
                    log.Info("start with Windows: registered");
                }
            }
            else if (key.GetValue(AppInfo.Name) is not null)
            {
                key.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
                log.Info("start with Windows: removed");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log.Warn($"start with Windows: could not update registry ({ex.GetType().Name})");
        }
    }
}
