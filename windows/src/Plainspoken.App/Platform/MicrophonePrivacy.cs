using System.Diagnostics;
using Microsoft.Win32;

namespace Plainspoken.App.Platform;

/// <summary>Windows privacy switches for the microphone (Settings → Privacy &amp; security → Microphone).</summary>
internal static class MicrophonePrivacy
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>True if the device-wide, per-user or desktop-app switch is set to Deny.</summary>
    public static bool IsBlockedForDesktopApps()
    {
        return IsDenied(Registry.LocalMachine, ConsentStore) ||
               IsDenied(Registry.CurrentUser, ConsentStore) ||
               IsDenied(Registry.CurrentUser, ConsentStore + @"\NonPackaged");
    }

    public static void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Nothing else we can do; the message already names the setting.
        }
    }

    private static bool IsDenied(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
