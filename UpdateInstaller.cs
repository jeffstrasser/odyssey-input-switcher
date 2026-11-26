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
        File.Delete(Path.Combine(directory, "ready"));
        string backup = target + ".update-backup";
        ReplaceExecutable(Path.Combine(directory, "payload.exe"), target, backup);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(target) { UseShellExecute = false };
            start.ArgumentList.Add("--cleanup-update");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add(directory);
            process = Process.Start(start)
                ?? throw new InvalidOperationException("The updated app could not be restarted.");
            WaitForReady(process, directory, TimeSpan.FromSeconds(120));
        }
        catch (Exception error)
        {
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5000))
                        throw new TimeoutException("The failed update process could not be stopped for rollback.");
                }
                File.Move(backup, target, overwrite: true);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Update initialization failed and rollback could not complete. " +
                    "Keep the backup at " + backup + ".", error, rollbackError);
            }
            throw;
        }
        finally { process?.Dispose(); }
        File.Delete(backup);
    }

    internal static void SignalReady(string directory)
    {
        ValidateDirectory(directory);
        string marker = Path.Combine(directory, "ready");
        File.WriteAllText(marker + ".tmp", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(marker + ".tmp", marker, overwrite: true);
    }

    internal static void WaitForReady(Process process, string directory, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        string marker = Path.Combine(directory, "ready");
        string expected = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        while (elapsed.Elapsed < timeout)
        {
            if (process.HasExited)
                throw new InvalidOperationException("The updated app exited before initialization completed. The previous executable will be restored.");
            if (File.Exists(marker) && File.ReadAllText(marker) == expected)
                return;
            process.WaitForExit(100);
        }
        throw new TimeoutException("The updated app did not confirm initialization within two minutes. The previous executable will be restored.");
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
