using System.Reflection;

namespace Plainspoken.Core;

/// <summary>
/// App identity in one place so the working name "Plainspoken" is easy to change later.
/// Folder names, registry values and mutex names are all derived from <see cref="Name"/>.
/// </summary>
public static class AppInfo
{
    public const string Name = "Plainspoken";

    /// <summary>Version from Directory.Build.props, e.g. "0.1.0".</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        // Strip any "+commit" suffix.
        var plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? info[..plus] : info;
    }
}
