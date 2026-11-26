namespace OdysseyInputSwitcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool startInTray = args.Length == 1 && args[0] == "--tray";
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
                    UpdateInstaller.Cleanup(helperId, args[2]);
                else
                    throw new ArgumentException("Unrecognized application arguments.");
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message + "\r\n\r\nIf installation failed, the previous executable is retained. " +
                    "You can download a release manually from " + ReleaseUpdater.RepositoryUrl + "/releases/latest.",
                    "Odyssey update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (args[0] != "--cleanup-update")
                    return;
            }
        }
        Application.Run(new MainForm(startInTray));
    }
}
