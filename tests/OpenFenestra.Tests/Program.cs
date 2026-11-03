using OpenFenestra.Core;
using System.Text.Json;

var root = Path.Combine(Path.GetTempPath(), "OpenFenestra.Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
void Test(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e.Message); failed++; }
}
void Assert(bool condition, string? message = null) { if (!condition) throw new Exception(message ?? "assertion failed"); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected exception"); }
var source = Path.Combine(root, "desktop"); Directory.CreateDirectory(source);
var doc = Path.Combine(source, "Report.PDF"); File.WriteAllText(doc, "fixture");
var link = Path.Combine(source, "App.lnk"); File.WriteAllText(link, "fixture");
var image = Path.Combine(source, "Image.png"); File.WriteAllText(image, "fixture");
var folder = Path.Combine(source, "Folder"); Directory.CreateDirectory(folder);
try
{
    Test("Batch collection preserves input order, unrelated items and read-only portals", () =>
    {
        var items = Enumerable.Range(1, 10000).Select(i => "::{" + new Guid(i, 0, 0, new byte[8]).ToString("D").ToUpperInvariant() + "}").ToArray();
        var a = new FenceGroup(); var b = new FenceGroup(); var portal = new FenceTab { FolderPath = source, Items = items.ToList() };
        a.ActiveTab.Items.AddRange(items); a.Tabs.Add(portal);
        var layout = new Layout { Groups = [a, b], DesktopItems = items.ToList() };
        Assert(DesktopAssignments.Collect(layout, b.ActiveTab, items.Reverse().Concat(items.Take(10))) == items.Length);
        Assert(b.ActiveTab.Items.SequenceEqual(items.Reverse()) && a.ActiveTab.Items.Count == 0 && layout.DesktopItems.Count == 0);
        Assert(portal.Items.SequenceEqual(items));
        Assert(DesktopAssignments.ReturnToDesktop(layout, items, new(1, 2)) == items.Length);
        Assert(b.ActiveTab.Items.Count == 0 && layout.DesktopItems.SequenceEqual(items) && portal.Items.SequenceEqual(items));
    });
    Test("Capacity errors leave all source assignments and desktop positions unchanged", () =>
    {
        var full = Enumerable.Range(1, 10000).Select(i => "::{" + new Guid(i, 0, 0, new byte[8]).ToString("D") + "}").ToList();
        var a = new FenceGroup(); var b = new FenceGroup(); a.ActiveTab.Items.Add(doc); b.ActiveTab.Items.AddRange(full);
        var layout = new Layout { Groups = [a, b] };
        var before = LayoutCodec.Encode(layout);
        Throws(() => DesktopAssignments.Collect(layout, b.ActiveTab, [doc]));
        Assert(LayoutCodec.Encode(layout) == before);
        layout.DesktopItems.AddRange(full); before = LayoutCodec.Encode(layout);
        Throws(() => DesktopAssignments.ReturnToDesktop(layout, [doc], new(1, 2)));
        Assert(LayoutCodec.Encode(layout) == before);
    });
    Test("Virtual Shell identities require a valid GUID with exact separators", () =>
    {
        Assert(!DesktopAssignments.IsVirtual(null));
        Assert(!DesktopAssignments.IsVirtual("::{" + new string('A', 36) + "}"));
        Assert(!DesktopAssignments.IsVirtual("::{" + new string('-', 36) + "}"));
        Assert(DesktopAssignments.NormalizeItem("::{" + new string('-', 36) + "}") is null);
        Assert(DesktopAssignments.IsVirtual("::{645ff040-5081-101b-9f08-00aa002f954e}"));
    });
    Test("Folder metadata keeps directories first and shortcut display names intact", () =>
    {
        var entries = FileCatalog.ReadTab(new FenceTab { FolderPath = source });
        Assert(entries[0].Path == folder && entries[0].IsDirectory);
        Assert(entries.Single(e => e.Path == link).Name == "App" && !entries.Single(e => e.Path == link).IsDirectory);
    });
    Test("Organization fills eligible category tabs in order and skips folder portals", () =>
    {
        var paths = Enumerable.Range(1, 10002).Select(i => "::{" + new Guid(i, 0, 0, new byte[8]).ToString("D").ToUpperInvariant() + "}").ToArray();
        var portal = new FenceGroup { Name = "系统入口", Tabs = [new() { FolderPath = source }] };
        var nearlyFull = new FenceGroup { Name = "系统入口" }; nearlyFull.ActiveTab.Items.AddRange(paths.Take(9999));
        var next = new FenceGroup { Name = "系统入口" };
        var layout = new Layout { Groups = [portal, nearlyFull, next] };
        Assert(FileCatalog.OrganizeEntries(layout, paths.Skip(9999).Select(p => new DesktopEntry(p, p, false))) == 3);
        Assert(layout.Groups.Count == 3 && portal.ActiveTab.Items.Count == 0);
        Assert(nearlyFull.ActiveTab.Items.Count == 10000 && nearlyFull.ActiveTab.Items[^1] == paths[9999]);
        Assert(next.ActiveTab.Items.SequenceEqual(paths.Skip(10000)));
    });
    Test("Groups can move upward and downward before or after a target", () =>
    {
        var groups = new[] { "A", "B", "C", "D" }.Select(name => new FenceGroup { Id = name, Name = name }).ToList();
        var layout = new Layout { Groups = groups };
        Assert(layout.ReorderGroup("D", "B", false));
        Assert(string.Join("", layout.Groups.Select(g => g.Id)) == "ADBC");
        Assert(layout.ReorderGroup("A", "C", true));
        Assert(string.Join("", layout.Groups.Select(g => g.Id)) == "DBCA");
        Assert(layout.ReorderGroup("C", "D", false));
        Assert(string.Join("", layout.Groups.Select(g => g.Id)) == "CDBA");
    });
    Test("Cancelled and unchanged group placements leave the order intact", () =>
    {
        var layout = new Layout { Groups = [new() { Id = "A" }, new() { Id = "B" }] };
        Assert(!layout.ReorderGroup("A", "A", false));
        Assert(!layout.ReorderGroup("A", "B", false));
        Assert(!layout.ReorderGroup("B", "A", true));
        Assert(!layout.ReorderGroup("missing", "A", false));
        Assert(!layout.ReorderGroup("A", "missing", true));
        Assert(string.Join("", layout.Groups.Select(g => g.Id)) == "AB");
    });
    Test("Reordering against a filtered target preserves other groups' relative order", () =>
    {
        var layout = new Layout { Groups = new[] { "A", "B", "C", "D", "E" }.Select(id => new FenceGroup { Id = id }).ToList() };
        Assert(layout.ReorderGroup("E", "B", false));
        Assert(string.Join("", layout.Groups.Select(g => g.Id)) == "AEBCD");
        Assert(layout.ReorderGroup("B", "D", true));
        Assert(string.Join("", layout.Groups.Select(g => g.Id)) == "AECDB");
    });
    Test("Group order survives save without changing tabs, geometry or references", () =>
    {
        var first = new FenceGroup { Name = "First", X = 137, Y = 203, Width = 321, Collapsed = true };
        first.ActiveTab.Items.Add(doc);
        var second = new FenceGroup { Name = "Second" };
        var layout = new Layout { Groups = [first, second] };
        Assert(layout.ReorderGroup(second.Id, first.Id, false));
        Assert(ReferenceEquals(layout.Groups[1], first));
        var restored = layout.Clone();
        Assert(restored.Groups.Select(g => g.Id).SequenceEqual(new[] { second.Id, first.Id }));
        var saved = restored.Groups[1];
        Assert(saved.X == 137 && saved.Y == 203 && saved.Width == 321 && saved.Collapsed);
        Assert(saved.ActiveTab.Id == first.ActiveTab.Id && saved.ActiveTab.Items.SequenceEqual(new[] { doc }));
    });
    Test("Drop adds references and leaves original bytes intact", () =>
    {
        var tab = new FenceTab(); Assert(FileCatalog.AddReferences(tab, [doc, link, folder]) == 3);
        Assert(File.ReadAllText(doc) == "fixture" && Directory.Exists(folder));
        tab.Items.Remove(doc); Assert(File.Exists(doc));
    });
    Test("Windows paths deduplicate without case sensitivity", () =>
    {
        var tab = new FenceTab(); FileCatalog.AddReferences(tab, [doc, doc.ToUpperInvariant()]); Assert(tab.Items.Count == 1);
    });
    Test("Missing and relative paths are not added", () =>
    {
        var tab = new FenceTab(); Assert(FileCatalog.AddReferences(tab, ["relative.txt", Path.Combine(root, "missing.txt")]) == 0);
    });
    Test("Folder portals reject drops without changing files", () =>
    {
        var tab = new FenceTab { FolderPath = folder }; Assert(FileCatalog.AddReferences(tab, [doc]) == 0);
        Assert(tab.Items.Count == 0 && File.Exists(doc));
    });
    Test("Desktop categories support case-insensitive extensions", () =>
    { Assert(FileCatalog.Category(doc) == "文档与笔记"); Assert(FileCatalog.Category(image) == "图片与设计"); Assert(FileCatalog.Category(folder) == "文件夹"); });
    Test("Repeated organization is idempotent", () =>
    {
        var layout = new Layout(); Assert(FileCatalog.Organize(layout, [source]) == 4);
        Assert(FileCatalog.Organize(layout, [source, source]) == 0); Assert(layout.Groups.Count == 4);
    });
    Test("Shell organization collects virtual entries once without moving files", () =>
    {
        const string recycle = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        var layout = new Layout { DesktopItems = [recycle, doc] };
        DesktopEntry[] entries = [new(recycle, "回收站", true), new(recycle.ToLowerInvariant(), "回收站", true), FileCatalog.Entry(doc)];
        Assert(FileCatalog.OrganizeEntries(layout, entries) == 2);
        Assert(layout.Groups.Single(g => g.Name == "系统入口").ActiveTab.Items.Single() == recycle);
        Assert(layout.Groups.Single(g => g.Name == "文档与笔记").ActiveTab.Items.Single() == doc);
        Assert(layout.DesktopItems.Count == 0 && FileCatalog.OrganizeEntries(layout, entries) == 0);
        Assert(File.ReadAllText(doc) == "fixture" && File.ReadAllText(link) == "fixture");
    });
    Test("Organization preserves manual assignments and clears redundant desktop pins", () =>
    {
        var manual = new FenceGroup { Name = "我的分类" };
        manual.ActiveTab.Items.Add(doc);
        var layout = new Layout { Groups = [manual], DesktopItems = [doc.ToUpperInvariant(), image] };
        Assert(FileCatalog.OrganizeEntries(layout, [FileCatalog.Entry(doc), FileCatalog.Entry(image)]) == 1);
        Assert(manual.ActiveTab.Items.Single() == doc);
        Assert(layout.Groups.All(group => group.Name != "文档与笔记") && layout.DesktopItems.Count == 0);
    });
    Test("Portal references do not exclude entries from ordinary organization", () =>
    {
        var portal = new FenceGroup { Name = "文档与笔记", Tabs = [new() { FolderPath = source, Items = [doc] }] };
        var layout = new Layout { Groups = [portal] };
        Assert(FileCatalog.OrganizeEntries(layout, [FileCatalog.Entry(doc)]) == 1);
        Assert(portal.ActiveTab.IsPortal && portal.ActiveTab.Items.Single() == doc);
        Assert(layout.Groups.Single(group => !group.ActiveTab.IsPortal).ActiveTab.Items.Single() == doc);
        Assert(DesktopAssignments.Assigned(layout).Count == 1 && File.Exists(doc));
    });
    Test("Shell organization rejects unsafe and missing entries", () =>
    {
        var layout = new Layout();
        Assert(FileCatalog.OrganizeEntries(layout, [new("https://example.com", "web", false),
            new("relative.txt", "relative", false), new(Path.Combine(root, "missing"), "missing", false)]) == 0);
        Assert(layout.Groups.Count == 0);
    });
    Test("Live folder lists changes and tolerates missing directory", () =>
    {
        var tab = new FenceTab { FolderPath = folder }; Assert(FileCatalog.ReadTab(tab).Count == 0);
        File.WriteAllText(Path.Combine(folder, "New.md"), "new"); Assert(FileCatalog.ReadTab(tab).Count == 1);
        Assert(FileCatalog.ReadFolder(Path.Combine(root, "not-found")).Count == 0);
    });
    Test("Layout round trip preserves tabs and missing references", () =>
    {
        var layout = new Layout { Groups = [new() { Name = "研究", Tabs = [new() { Items = [doc, Path.Combine(root, "moved.txt")] }, new() { FolderPath = folder }] }] };
        var clone = layout.Clone(); Assert(clone.Groups[0].Tabs.Count == 2); Assert(clone.Groups[0].Tabs[0].Items.Count == 2);
        clone.Groups[0].Name = "copy"; Assert(layout.Groups[0].Name == "研究");
    });
    Test("Invalid versions and null structures are rejected", () =>
    {
        Throws(() => LayoutCodec.Decode("{\"Version\":99}")); Throws(() => LayoutCodec.Decode("{\"Groups\":null}"));
        Throws(() => LayoutCodec.Decode("{\"Groups\":[null]}"));
        Throws(() => LayoutCodec.Decode("{\"Groups\":[{\"Tabs\":[]}]}"));
    });
    Test("Duplicate IDs are rejected", () =>
    {
        var group = new FenceGroup(); var layout = new Layout { Groups = [group, group] };
        Throws(() => LayoutCodec.Decode(LayoutCodec.Encode(layout)));
    });
    Test("Oversized layouts are rejected", () => Throws(() => LayoutCodec.Decode(new string(' ', LayoutCodec.MaxBytes + 1))));
    Test("Duplicate tab IDs across groups are rejected", () =>
    {
        var tab = new FenceTab();
        Throws(() => LayoutCodec.Decode(LayoutCodec.Encode(new Layout { Groups = [new() { Tabs = [tab] }, new() { Tabs = [tab] }] })));
    });
    Test("Geometry, opacity, and colors are normalized", () =>
    {
        var layout = LayoutCodec.Decode("{\"Groups\":[{\"Width\":-1,\"Height\":99999,\"Opacity\":0,\"Color\":\"bad\"}]}");
        var g = layout.Groups[0]; Assert(g.Width == 260 && g.Height == 2000 && g.Opacity == 0.15 && g.Color == "#78B9A1");
    });
    Test("Atomic saves keep a recoverable previous version", () =>
    {
        var store = new LayoutStore(Path.Combine(root, "atomic"));
        var layout = new Layout { Groups = [new() { Name = "first" }] }; store.Save(layout);
        layout.Groups[0].Name = "second"; store.Save(layout);
        Assert(store.Load().Groups[0].Name == "second");
        Assert(LayoutStore.Read(store.LayoutPath + ".bak").Groups[0].Name == "first");
        Assert(!Directory.GetFiles(store.DirectoryPath, "*.tmp").Any());
    });
    Test("Corrupt primary recovers backup and preserves damaged bytes", () =>
    {
        var store = new LayoutStore(Path.Combine(root, "recover")); var layout = new Layout();
        store.Save(layout); store.Save(layout); File.WriteAllText(store.LayoutPath, "broken");
        Assert(store.Load().Groups.Count == 0 && store.RecoveredFromBackup);
        Assert(Directory.GetFiles(store.DirectoryPath, "*.corrupt-*").Length == 1);
        store.Save(new Layout()); Assert(LayoutStore.Read(store.LayoutPath + ".bak").Groups.Count == 0);
    });
    Test("A newer layout version is preserved even when an old backup exists", () =>
    {
        var store = new LayoutStore(Path.Combine(root, "newer")); store.Save(new Layout()); store.Save(new Layout());
        File.WriteAllText(store.LayoutPath, "{\"Version\":99}"); Throws(() => store.Load());
        Assert(File.ReadAllText(store.LayoutPath) == "{\"Version\":99}" && !store.RecoveredFromBackup);
    });
    Test("Unrecoverable corruption is not silently overwritten", () =>
    {
        var store = new LayoutStore(Path.Combine(root, "broken")); Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.LayoutPath, "broken"); Throws(() => store.Load()); Assert(File.ReadAllText(store.LayoutPath) == "broken");
    });
    Test("Snapshot and export preserve layout independently", () =>
    {
        var store = new LayoutStore(Path.Combine(root, "snapshots")); var layout = new Layout { Groups = [new() { Name = "snapshot" }] };
        var snapshot = store.Snapshot(layout); layout.Groups[0].Name = "changed";
        Assert(LayoutStore.Read(snapshot).Groups[0].Name == "snapshot");
        var export = Path.Combine(root, "export.json"); LayoutStore.Export(export, layout);
        Assert(LayoutStore.Read(export).Groups[0].Name == "changed" && store.Snapshots().Count == 1);
    });
    Test("Version 1 layouts migrate without enabling desktop or startup", () =>
    {
        var migrated = LayoutCodec.Decode("{\"Version\":1,\"Groups\":[{\"Name\":\"existing\"}]}");
        Assert(migrated.Version == 2 && migrated.Groups[0].Name == "existing");
        Assert(!migrated.DesktopIntegrationEnabled && !migrated.StartWithWindows);
    });
    Test("Collected desktop icons are filtered from the desktop display", () =>
    {
        var group = new FenceGroup(); var layout = new Layout { Groups = [group] };
        Assert(DesktopAssignments.Collect(layout, group.ActiveTab, [doc]) == 1);
        Assert(DesktopAssignments.VisibleDesktop(layout, [FileCatalog.Entry(doc), FileCatalog.Entry(image)]).Single().Path == image);
        Assert(File.ReadAllText(doc) == "fixture");
    });
    Test("Dragging a fence item to desktop removes all duplicate assignments", () =>
    {
        var a = new FenceGroup(); var b = new FenceGroup(); a.ActiveTab.Items.Add(doc); b.ActiveTab.Items.Add(doc);
        var layout = new Layout { Groups = [a, b] };
        DesktopAssignments.ReturnToDesktop(layout, [doc], new(100, 150));
        Assert(a.ActiveTab.Items.Count == 0 && b.ActiveTab.Items.Count == 0);
        Assert(DesktopAssignments.VisibleDesktop(layout, [FileCatalog.Entry(doc)]).Count == 1);
        Assert(layout.DesktopPositions[doc] == new DesktopPosition(100, 150) && File.Exists(doc));
    });
    Test("External file references can return to desktop without moving files", () =>
    {
        var layout = new Layout(); DesktopAssignments.ReturnToDesktop(layout, [doc], new(200, 300));
        Assert(DesktopAssignments.VisibleDesktop(layout, []).Single().Path == doc);
        Assert(File.ReadAllText(doc) == "fixture");
    });
    Test("Recollecting an item removes its extra desktop pin", () =>
    {
        var group = new FenceGroup(); var layout = new Layout { Groups = [group] };
        DesktopAssignments.ReturnToDesktop(layout, [doc], new(1, 2));
        DesktopAssignments.Collect(layout, group.ActiveTab, [doc]);
        Assert(layout.DesktopItems.Count == 0 && group.ActiveTab.Items.Single() == doc);
    });
    Test("Group to group transfers are exclusive and preserve file bytes", () =>
    {
        var a = new FenceGroup(); var b = new FenceGroup(); a.ActiveTab.Items.Add(doc);
        var layout = new Layout { Groups = [a, b] }; DesktopAssignments.Collect(layout, b.ActiveTab, [doc]);
        Assert(a.ActiveTab.Items.Count == 0 && b.ActiveTab.Items.Single() == doc && File.ReadAllText(doc) == "fixture");
    });
    Test("Portals remain read-only and do not hide their browsed files", () =>
    {
        var group = new FenceGroup { Tabs = [new() { FolderPath = source }] };
        var layout = new Layout { Groups = [group] };
        Assert(DesktopAssignments.Collect(layout, group.ActiveTab, [doc]) == 0);
        Assert(DesktopAssignments.VisibleDesktop(layout, [FileCatalog.Entry(doc)]).Count == 1);
    });
    Test("Virtual Shell items survive grouping and schema round trips", () =>
    {
        const string recycle = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        var group = new FenceGroup(); var layout = new Layout { Groups = [group] };
        DesktopAssignments.Collect(layout, group.ActiveTab, [recycle]);
        var clone = layout.Clone(); Assert(clone.Groups[0].ActiveTab.Items.Single() == recycle);
        Assert(!DesktopAssignments.IsVirtual("https://example.com"));
    });
    Test("Desktop positions, icon size and collapsed opacity persist", () =>
    {
        var layout = new Layout { StartWithWindows = true, DesktopIntegrationEnabled = true, Groups = [new() { Collapsed = true, CollapsedOpacity = 0.18, IconSize = 64 }] };
        layout.DesktopPositions[doc] = new(-500, 300);
        var clone = layout.Clone(); Assert(clone.StartWithWindows && clone.DesktopIntegrationEnabled && clone.Groups[0].Collapsed);
        Assert(clone.Groups[0].IconSize == 64 && clone.Groups[0].CollapsedOpacity == 0.18 && clone.DesktopPositions[doc].X == -500);
    });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"{passed} passed; {failed} failed");
return failed == 0 ? 0 : 1;
