using Plainspoken.Core.Logging;
using Plainspoken.Core.Storage;

namespace Plainspoken.Core.Settings;

/// <summary>Loads and saves settings.json. A corrupt file is kept aside and defaults are used.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly ILog _log;

    public SettingsStore(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    public string FilePath => _path;

    public bool Exists => File.Exists(_path);

    /// <param name="createDefaults">Called when there is no usable file, to seed a fresh settings object.</param>
    public PlainspokenSettings Load(Func<PlainspokenSettings> createDefaults)
    {
        ArgumentNullException.ThrowIfNull(createDefaults);
        if (!File.Exists(_path))
        {
            return createDefaults().Normalize();
        }

        try
        {
            return SettingsSerializer.Deserialize(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            var aside = _path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            _log.Error($"Settings file unreadable ({ex.GetType().Name}); moved to {Path.GetFileName(aside)} and using defaults.");
            try
            {
                File.Move(_path, aside, overwrite: true);
            }
            catch (IOException)
            {
                // Keep going with defaults.
            }

            return createDefaults().Normalize();
        }
    }

    public void Save(PlainspokenSettings settings) =>
        AtomicFile.WriteAllText(_path, SettingsSerializer.Serialize(settings.Normalize()));
}
