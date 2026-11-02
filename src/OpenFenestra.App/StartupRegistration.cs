using Microsoft.Win32;

namespace OpenFenestra.App;

internal static class StartupRegistration
{
    internal const string ValueName = "OpenFenestra";
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string command && Environment.ProcessPath is string executable
            && command.StartsWith("\"" + executable + "\"", StringComparison.OrdinalIgnoreCase);
    }
    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序安装位置。");
            var command = "\"" + executable + "\" --tray";
            if (App.HasCustomDataDirectory) command += " --data-dir \"" + App.DataDirectory + "\"";
            else if (App.IsPortable) command += " --portable";
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, false);
    }
}
