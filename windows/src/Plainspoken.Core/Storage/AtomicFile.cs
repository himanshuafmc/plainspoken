namespace Plainspoken.Core.Storage;

public static class AtomicFile
{
    /// <summary>Writes to a temp file then renames over the target, so a crash never leaves half a file.</summary>
    public static void WriteAllText(string path, string contents) =>
        Write(path, tmp => File.WriteAllText(tmp, contents));

    public static void WriteAllBytes(string path, byte[] contents) =>
        Write(path, tmp => File.WriteAllBytes(tmp, contents));

    private static void Write(string path, Action<string> writeTemp)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        writeTemp(tmp);
        File.Move(tmp, path, overwrite: true);
    }
}
