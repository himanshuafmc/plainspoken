using Plainspoken.Core;

namespace Plainspoken.App.Platform;

internal static class AppPaths
{
    public static string RoamingDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);

    public static string LocalDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name);

    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");

    public static string HistoryFile => Path.Combine(LocalDir, "history.json");

    public static string PendingDir => Path.Combine(LocalDir, "pending");

    public static string LogsDir => Path.Combine(LocalDir, "logs");

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Ignore: opening Explorer is a convenience.
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Ignore.
        }
    }
}
