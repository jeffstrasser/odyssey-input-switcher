namespace OdysseyInputSwitcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool startInTray = args.Length == 1 && args[0] == "--tray";
        string? updateDirectory = null;
        int updateHelperId = 0;
        if (args.Length > 0 && !startInTray)
        {
            try
            {
                if (args.Length == 4 && args[0] == "--apply-update" && int.TryParse(args[1], out int parentId))
                {
                    UpdateInstaller.Apply(parentId, args[2], args[3]);
                    return;
                }
                if (args.Length == 3 && args[0] == "--cleanup-update" && int.TryParse(args[1], out int helperId))
                {
                    UpdateInstaller.ValidateDirectory(args[2]);
                    updateDirectory = args[2];
                    updateHelperId = helperId;
                }
                else
                    throw new ArgumentException("Unrecognized application arguments.");
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Update startup failed: {0}", error);
                if (MessageBox.Show("We couldn't start or complete the update.\r\n\r\nIf installation failed, the previous executable is retained. " +
                    "You can download a release manually from " + ReleaseUpdater.RepositoryUrl + "/releases/latest." +
                    "\r\n\r\nWould you like to see the technical details?",
                    MainForm.AppName + " update failed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    MessageBox.Show(error.ToString(), MainForm.AppName + " technical details",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (args[0] != "--cleanup-update")
                    return;
            }
        }
        using var form = new MainForm(startInTray);
        if (updateDirectory is not null)
        {
            form.InitializationCompleted += async (_, _) =>
            {
                try
                {
                    UpdateInstaller.SignalReady(updateDirectory);
                    await Task.Run(() => UpdateInstaller.Cleanup(updateHelperId, updateDirectory));
                }
                catch (Exception error)
                {
                    form.ReportError(error, ErrorContext.Cleanup, "Update cleanup failed");
                }
            };
        }
        Application.Run(form);
    }
}
