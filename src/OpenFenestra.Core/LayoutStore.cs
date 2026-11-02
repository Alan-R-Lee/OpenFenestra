namespace OpenFenestra.Core;

public sealed class LayoutStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string LayoutPath => Path.Combine(DirectoryPath, "layout.json");
    public string SnapshotDirectory => Path.Combine(DirectoryPath, "snapshots");
    public bool RecoveredFromBackup { get; private set; }

    public Layout Load()
    {
        if (!File.Exists(LayoutPath)) return new();
        try { return Read(LayoutPath); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            if (!File.Exists(LayoutPath + ".bak")) throw new InvalidDataException("布局无法读取；原文件已保留。", e);
            var recovered = Read(LayoutPath + ".bak");
            // Preserve the damaged file before the next successful save updates the backup.
            File.Copy(LayoutPath, LayoutPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
            RecoveredFromBackup = true;
            return recovered;
        }
    }

    public static Layout Read(string path)
    {
        if (new FileInfo(path).Length > LayoutCodec.MaxBytes) throw new InvalidDataException("布局文件超过 8 MB。");
        return LayoutCodec.Decode(File.ReadAllText(path));
    }

    public void Save(Layout layout)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = LayoutCodec.Encode(layout);
        // Validate before replacing a known-good layout.
        LayoutCodec.Decode(json);
        AtomicWrite(LayoutPath, json, !RecoveredFromBackup);
        RecoveredFromBackup = false;
    }

    public string Snapshot(Layout layout)
    {
        Directory.CreateDirectory(SnapshotDirectory);
        var path = Path.Combine(SnapshotDirectory, $"layout-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.json");
        AtomicWrite(path, LayoutCodec.Encode(layout), false);
        return path;
    }

    public IReadOnlyList<string> Snapshots() => Directory.Exists(SnapshotDirectory)
        ? Directory.GetFiles(SnapshotDirectory, "layout-*.json").OrderDescending(StringComparer.Ordinal).ToList() : [];

    public static void Export(string path, Layout layout) => AtomicWrite(path, LayoutCodec.Encode(layout), false);

    private static void AtomicWrite(string path, string json, bool backup)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, backup ? path + ".bak" : null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
