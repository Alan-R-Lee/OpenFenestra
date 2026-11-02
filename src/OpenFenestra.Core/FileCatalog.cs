namespace OpenFenestra.Core;

public sealed record DesktopEntry(string Path, string Name, bool IsDirectory)
{
    public bool Exists => DesktopAssignments.IsVirtual(Path) || File.Exists(Path) || Directory.Exists(Path);
}

public static class FileCatalog
{
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".tex" };
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".gif", ".svg", ".webp", ".bmp", ".psd", ".ai" };
    private static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".mp4", ".wav", ".flac", ".mkv", ".avi", ".mov" };

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return null;
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static DesktopEntry Entry(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) name = path;
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url")
            name = System.IO.Path.GetFileNameWithoutExtension(path);
        return new(path, name, Directory.Exists(path));
    }

    // Materialize inside the try: EnumerateFileSystemEntries can throw while iterating.
    public static IReadOnlyList<string> ReadFolder(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path)
                .Where(p => (File.GetAttributes(p) & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderByDescending(Directory.Exists)
                .ThenBy(System.IO.Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .Take(10000).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return []; }
    }

    public static IReadOnlyList<DesktopEntry> ReadTab(FenceTab tab) =>
        (tab.IsPortal ? ReadFolder(tab.FolderPath!) : tab.Items).Select(Entry).ToList();

    public static int AddReferences(FenceTab tab, IEnumerable<string> paths)
    {
        if (tab.IsPortal) return 0;
        var known = tab.Items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths)
        {
            var normalized = NormalizePath(path);
            if (normalized is null || (!File.Exists(normalized) && !Directory.Exists(normalized))) continue;
            if (tab.Items.Count >= 10000) break;
            if (known.Add(normalized)) { tab.Items.Add(normalized); added++; }
        }
        return added;
    }

    public static string Category(string path)
    {
        if (Directory.Exists(path)) return "文件夹";
        var ext = System.IO.Path.GetExtension(path);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) return "应用与快捷方式";
        if (Documents.Contains(ext)) return "文档与笔记";
        if (Images.Contains(ext)) return "图片与设计";
        return Media.Contains(ext) ? "影音与媒体" : "其他项目";
    }

    public static int Organize(Layout layout, IEnumerable<string> desktopPaths)
    {
        var known = layout.Groups.SelectMany(g => g.Tabs).SelectMany(t => t.Items)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in desktopPaths.SelectMany(ReadFolder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!known.Add(path)) continue;
            var category = Category(path);
            var group = layout.Groups.FirstOrDefault(g => g.Name == category && !g.ActiveTab.IsPortal);
            if (group is null)
            {
                if (layout.Groups.Count >= 100) break;
                var index = layout.Groups.Count;
                group = new() { Name = category, X = 90 + index % 3 * 370, Y = 90 + index / 3 * 350,
                    Color = new[] { "#78B9A1", "#8CA9DB", "#C0A2D1", "#D6AE75", "#D98F9D", "#8BBFC8" }[index % 6] };
                layout.Groups.Add(group);
            }
            added += AddReferences(group.ActiveTab, [path]);
        }
        return added;
    }
}
