using System.Diagnostics;
using System.Text.Json;

namespace OpenFenestra.App;

internal sealed record DesktopRecoveryRecord(string Token, int ProcessId, long StartTicks, bool WasVisible);

internal static class DesktopRecovery
{
    internal static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, "desktop-recovery.json");
    internal static DesktopRecoveryRecord? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<DesktopRecoveryRecord>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Debug.WriteLine(e); return null; }
    }
    internal static bool IsAlive(DesktopRecoveryRecord record)
    {
        try { using var process = Process.GetProcessById(record.ProcessId); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.StartTicks; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    internal static void RecoverStale(string path)
    {
        var record = Read(path);
        if (record is null || IsAlive(record)) return;
        NativeDesktop.SetNativeIconsVisible(record.WasVisible); File.Delete(path);
    }
    internal static void RunGuard(string path, string token)
    {
        while (true)
        {
            var record = Read(path);
            if (record is null || record.Token != token) return;
            if (!IsAlive(record))
            {
                if (Read(path)?.Token == token)
                { NativeDesktop.SetNativeIconsVisible(record.WasVisible); File.Delete(path); }
                return;
            }
            System.Threading.Thread.Sleep(500);
        }
    }
}
