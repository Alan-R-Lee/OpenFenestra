using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace OpenFenestra.App;

internal static class StartupRegistration
{
    internal const string ValueName = "OpenFenestra";
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly Lazy<string> AccountSid = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    });
    private static string UserSid => AccountSid.Value;
    private static string TaskName => "OpenFenestra-" + UserSid;
    private static readonly Lazy<string> ExecutablePath = new(() => RuntimePaths.PhysicalFile(Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序安装位置。")));
    private static string Executable => ExecutablePath.Value;
    private static bool? _cachedEnabled;
    private static long _checkedAt;
    private static string Arguments => "--tray --autostart" + (App.HasCustomDataDirectory ? " --data-dir \"" + App.DataDirectory + "\"" : App.IsPortable ? " --portable" : "");
    internal static string? LastFallbackReason { get; private set; }

    internal static bool IsEnabled()
    {
        var now = Environment.TickCount64;
        if (_cachedEnabled is { } cached && now - _checkedAt < 2000) return cached;
        var enabled = ReadEnabled();
        _cachedEnabled = enabled; _checkedAt = now;
        return enabled;
    }
    private static bool ReadEnabled()
    {
        try { if (WithFolder(folder => TaskMatches(folder, Executable, Arguments))) return true; }
        catch (Exception e) when (SchedulerUnavailable(e)) { }
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) is string command && command == "\"" + Executable + "\" " + Arguments;
    }

    internal static void SetEnabled(bool enabled)
    {
        _cachedEnabled = null;
        LastFallbackReason = null;
        if (enabled)
        {
            try
            {
                WithFolder(folder =>
                {
                    if (!TaskMatches(folder, Executable, Arguments))
                    {
                        object task = folder.RegisterTask(TaskName, BuildTaskXml(Executable, Arguments, UserSid), 6, UserSid, null, 3, null);
                        Marshal.FinalReleaseComObject(task);
                    }
                    return true;
                });
                using var old = Registry.CurrentUser.OpenSubKey(KeyPath, true);
                old?.DeleteValue(ValueName, false);
                return;
            }
            catch (Exception e) when (SchedulerUnavailable(e)) { LastFallbackReason = e.Message; }
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
            key.SetValue(ValueName, "\"" + Executable + "\" " + Arguments, RegistryValueKind.String);
        }
        else
        {
            WithFolder(folder =>
            {
                try { folder.DeleteTask(TaskName, 0); }
                catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { }
                return true;
            });
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true);
            key?.DeleteValue(ValueName, false);
        }
    }

    internal static string BuildTaskXml(string executable, string arguments, string sid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object value) => new(ns + name, value);
        return new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "OpenFenestra: restore desktop groups at user logon.")),
            E("Triggers", E("LogonTrigger", new[] { E("Enabled", "true"), E("UserId", sid) })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"), E("RunOnlyIfNetworkAvailable", "false"),
                E("Enabled", "true"), E("RunOnlyIfIdle", "false"), E("ExecutionTimeLimit", "PT0S") }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), E("Exec", new[] {
                E("Command", executable), E("Arguments", arguments), E("WorkingDirectory", Path.GetDirectoryName(executable)!) })))
            .ToString();
    }

    private static bool TaskMatches(dynamic folder, string executable, string arguments)
    {
        object? task = null;
        try
        {
            task = folder.GetTask(TaskName);
            dynamic registered = task;
            var xml = XElement.Parse((string)registered.Xml);
            XNamespace ns = xml.Name.Namespace;
            var action = xml.Element(ns + "Actions")?.Element(ns + "Exec");
            return (bool)registered.Enabled && string.Equals((string?)action?.Element(ns + "Command"), executable, StringComparison.OrdinalIgnoreCase)
                && (string?)action?.Element(ns + "Arguments") == arguments;
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { return false; }
        finally { if (task is not null) Marshal.FinalReleaseComObject(task); }
    }

    private static bool WithFolder(Func<dynamic, bool> action)
    {
        object? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            dynamic scheduler = service;
            scheduler.Connect(); folder = scheduler.GetFolder(@"\");
            return action(folder);
        }
        finally
        {
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            if (service is not null) Marshal.FinalReleaseComObject(service);
        }
    }
    private static bool SchedulerUnavailable(Exception e) => e is COMException or UnauthorizedAccessException or System.Security.SecurityException;
}
