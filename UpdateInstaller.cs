using System.Diagnostics;

namespace OdysseyInputSwitcher;

internal static class UpdateInstaller
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OdysseyInputSwitcher", "updates");

    internal static string CreateDirectory()
    {
        string directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static void ValidateDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        if (!string.Equals(Path.GetDirectoryName(fullPath), Root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(fullPath), "N", out _) ||
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid update staging directory.");
    }

    internal static void WaitForExit(int processId)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; } // The parent can exit before the helper starts.
        using (process)
        {
            if (!process.WaitForExit(120_000))
                throw new TimeoutException("The app did not exit within two minutes. No update was installed.");
        }
    }

    internal static void Apply(int parentId, string target, string directory)
    {
        ValidateDirectory(directory);
        if (!Path.IsPathFullyQualified(target) ||
            !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid application executable path.");
        WaitForExit(parentId);
        string backup = target + ".update-backup";
        ReplaceExecutable(Path.Combine(directory, "payload.exe"), target, backup);
        try
        {
            var start = new ProcessStartInfo(target) { UseShellExecute = false };
            start.ArgumentList.Add("--cleanup-update");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(directory);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The updated app could not be restarted.");
        }
        catch
        {
            File.Move(backup, target, overwrite: true);
            throw;
        }
        File.Delete(backup);
    }

    internal static void ReplaceExecutable(string payload, string target, string backup)
    {
        if (File.Exists(backup))
            throw new IOException("A previous update backup exists at " + backup +
                ". Keep it safe and remove it from this path before retrying.");
        // Keep the old executable beside the target: replacement and rollback stay on one volume.
        File.Copy(payload, target + ".update-new", overwrite: false);
        try
        {
            File.Replace(target + ".update-new", target, backup, ignoreMetadataErrors: false);
        }
        finally
        {
            File.Delete(target + ".update-new");
        }
    }

    internal static void Cleanup(int helperId, string directory)
    {
        ValidateDirectory(directory);
        WaitForExit(helperId);
        Directory.Delete(directory, recursive: true);
    }
}
