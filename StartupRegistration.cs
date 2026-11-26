using Microsoft.Win32;

namespace OdysseyInputSwitcher;

internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "OdysseyInputSwitcher";

    internal static string CreateCommand(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"') ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Startup requires an absolute executable path.", nameof(executable));
        return $"\"{executable}\" --tray";
    }

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command &&
            command.Equals(CreateCommand(Environment.ProcessPath
                ?? throw new InvalidOperationException("The application path is unavailable.")),
                StringComparison.OrdinalIgnoreCase);
    }

    internal static void SetEnabled(bool enabled)
    {
        if (enabled && !ReleaseUpdater.IsSingleFileBuild())
            throw new InvalidOperationException("Run at Windows startup requires a published release build.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new IOException("The Windows startup registry key could not be opened.");
        if (enabled)
        {
            key.SetValue(ValueName, CreateCommand(Environment.ProcessPath
                ?? throw new InvalidOperationException("The application path is unavailable.")), RegistryValueKind.String);
        }
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
