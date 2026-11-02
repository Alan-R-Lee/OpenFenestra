using System.Text.Json;
using System.Runtime.InteropServices;
using OpenFenestra.Core;

namespace OpenFenestra.App;

internal sealed record DesktopDrag(string[] Paths)
{
    internal const string Format = "OpenFenestra.Items.v2";
    internal DataObject Data(bool includeFileDrop = true)
    {
        var data = new DataObject();
        data.SetData(Format, JsonSerializer.Serialize(this));
        var physical = Paths.Where(p => !DesktopAssignments.IsVirtual(p)).ToArray();
        if (includeFileDrop && physical.Length > 0) data.SetData(DataFormats.FileDrop, physical);
        return data;
    }
    internal static string[] Read(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(Format, false) && data.GetData(Format, false) is string text && text.Length < 1000000)
            {
                try { return Normalize(JsonSerializer.Deserialize<DesktopDrag>(text)?.Paths ?? []); }
                catch (JsonException) { /* A malformed private format can still have a valid Shell format. */ }
            }
            var shellItems = ShellDataTransfer.Read(data);
            if (shellItems.Length > 0) return shellItems;
            return data.GetData(DataFormats.FileDrop, false) is string[] paths ? Normalize(paths) : [];
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or InvalidOperationException
            or IOException or NotSupportedException or System.Security.SecurityException)
        { return []; }
    }

    private static string[] Normalize(IEnumerable<string> paths) => paths.Take(300)
        .Select(DesktopAssignments.NormalizeItem).Where(path => path is not null).Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
