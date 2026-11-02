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
        var g = layout.Groups[0]; Assert(g.Width == 260 && g.Height == 2000 && g.Opacity == 0.35 && g.Color == "#78B9A1");
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
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"{passed} passed; {failed} failed");
return failed == 0 ? 0 : 1;
