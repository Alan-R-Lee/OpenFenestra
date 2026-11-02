using System.Text.Json;

namespace OpenFenestra.App;

internal sealed record DesktopDrag(string[] Paths)
{
    internal const string Format = "OpenFenestra.Items.v2";
    internal DataObject Data()
    {
        var data = new DataObject();
        data.SetData(Format, JsonSerializer.Serialize(this));
        var physical = Paths.Where(p => !OpenFenestra.Core.DesktopAssignments.IsVirtual(p)).ToArray();
        if (physical.Length > 0) data.SetData(DataFormats.FileDrop, physical);
        return data;
    }
    internal static string[] Read(IDataObject data)
    {
        if (data.GetDataPresent(Format) && data.GetData(Format) is string text && text.Length < 1000000)
        {
            try { return JsonSerializer.Deserialize<DesktopDrag>(text)?.Paths?.Take(300).ToArray() ?? []; }
            catch (JsonException) { return []; }
        }
        return data.GetData(DataFormats.FileDrop) is string[] paths ? paths.Take(300).ToArray() : [];
    }
}
