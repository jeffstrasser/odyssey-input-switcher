using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OdysseyInputSwitcher;

internal sealed class MainForm : Form
{
    private static readonly Color Canvas = Color.FromArgb(245, 247, 251);
    private static readonly Color Ink = Color.FromArgb(28, 39, 58);
    private static readonly Color Accent = Color.FromArgb(36, 91, 219);
    private readonly ComboBox monitors = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill,
        AccessibleName = "Monitor", DropDownWidth = 650
    };
    private readonly Button refresh = MakeButton("Refresh");
    private readonly ToolStripMenuItem saveDefault = new("Save selected monitor as default");
    private readonly ToolStripMenuItem traySaveDefault = new("Save selected monitor as default");
    private readonly Button settingsButton = MakeButton("Settings");
    private readonly ContextMenuStrip settingsMenu = new();
    private readonly ToolStripMenuItem startupItem = new("Run at Windows startup");
    private readonly ToolStripMenuItem trayStartupItem = new("Run at Windows startup");
    private readonly ToolStripMenuItem settingsUpdate = new("Check for updates...");
    private readonly TabControl settingsTabs = new() { Dock = DockStyle.Fill, Padding = new Point(16, 8) };
    private readonly Button diagnostics = MakeButton("Read monitor diagnostics");
    private readonly Button save = MakeButton("Save input values");
    private readonly Button restore = MakeButton("Use working Odyssey values");
    private readonly Button clearDefault = MakeButton("Clear default monitor");
    private readonly CheckBox confirmSwitch = new()
    {
        Text = "Ask before switching inputs", AutoSize = true, Checked = true
    };
    private readonly Label status = new()
    {
        AutoSize = true, Visible = false, ForeColor = Color.FromArgb(90, 103, 122)
    };
    private readonly Label defaultLabel = new() { AutoSize = true, ForeColor = Color.FromArgb(90, 103, 122) };
    private readonly Label diagnosticSummary = new() { AutoSize = true };
    private readonly TextBox log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Dock = DockStyle.Fill, AccessibleName = "Diagnostic log",
        Font = new Font("Consolas", 9), BackColor = Color.White
    };
    private readonly string[] inputNames = ["DisplayPort", "HDMI", "Micro HDMI"];
    private readonly TextBox[] values = new TextBox[3];
    private readonly Button[] switches = new Button[3];
    private readonly ToolStripMenuItem[] trayInputs = new ToolStripMenuItem[3];
    private readonly ToolStripMenuItem trayMonitor = new("Discovering monitors...") { Enabled = false };
    private readonly ToolStripMenuItem trayRefresh = new("Refresh monitors");
    private readonly ToolStripMenuItem trayUpdate = new("Check for updates...");
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip trayMenu = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 650 };
    private readonly Form advanced = new();
    private MonitorInventory? inventory;
    private Preferences preferences = new();
    private bool busy;
    private bool confirming;
    private bool stale = true;
    private bool refreshPending;
    private bool exiting;
    private bool closeWhenIdle;
    private bool initialized;
    private bool resourcesDisposed;
    private bool explainedTray;
    private bool startInTray;
    private bool initializationQueued;
    private int displayGeneration;
    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OdysseyInputSwitcher");
    private static string SettingsPath => Path.Combine(SettingsDirectory, "inputs.json");
    private static string PreferencesPath => Path.Combine(SettingsDirectory, "preferences.json");
    private NativeMonitor? SelectedMonitor => !stale ? monitors.SelectedItem as NativeMonitor : null;
    internal event EventHandler? InitializationCompleted;

    internal MainForm(bool startInTray = false)
    {
        this.startInTray = startInTray;
        Text = "Odyssey Input Switcher";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        BackColor = Canvas;
        ForeColor = Ink;
        ClientSize = new Size(600, 1);
        MinimumSize = SizeFromClientSize(ClientSize);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        using var iconStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OdysseyInputSwitcher.assets.monitor.ico")
            ?? throw new InvalidOperationException("The monitor icon resource is missing.");
        using var sourceIcon = new Icon(iconStream);
        Icon = (Icon)sourceIcon.Clone();

        BuildMainWindow();
        BuildAdvancedWindow();
        BuildTray();
        ApplySettings(InputSettings.Resolve(null));
        monitors.SelectedIndexChanged += (_, _) =>
        {
            if (resourcesDisposed)
                return;
            UpdateControls();
            if (!busy && !stale)
                SetSelectionStatus();
        };
        refresh.Click += (_, _) => QueueRefresh("Manual refresh");
        saveDefault.Click += (_, _) => SaveDefault();
        traySaveDefault.Click += (_, _) => SaveDefault();
        clearDefault.Click += (_, _) => SaveDefault(clear: true);
        settingsButton.Click += (_, _) => settingsMenu.Show(settingsButton, new Point(0, settingsButton.Height));
        diagnostics.Click += async (_, _) => await DiagnosticsAsync();
        save.Click += (_, _) => SaveSettings();
        confirmSwitch.CheckedChanged += (_, _) => SaveConfirmationPreference();
        restore.Click += (_, _) =>
        {
            ApplySettings(InputSettings.Resolve(null));
            Append("Restored working profile: DisplayPort 0x0F, HDMI 0x05, Micro HDMI 0x06.");
            SetStatus("Working input values restored. Save to keep them.");
        };
        refreshTimer.Tick += async (_, _) =>
        {
            if (busy || exiting)
                return;
            refreshTimer.Stop();
            refreshPending = false;
            await RefreshAsync();
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                Hide();
        };
        FormClosing += (_, e) =>
        {
            if (!exiting)
            {
                e.Cancel = true;
                advanced.Hide();
                Hide();
                if (!explainedTray)
                {
                    explainedTray = true;
                    tray.ShowBalloonTip(4000, "Odyssey is still running",
                        "Right-click the monitor icon to switch inputs or exit.", ToolTipIcon.Info);
                }

            }
            else if (busy || confirming)
            {
                e.Cancel = true;
                closeWhenIdle = true;
                SetStatus("Waiting for the current operation before exiting...");
            }
        };
        UpdateControls();
    }

    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
        Padding = new Padding(12, 7, 12, 7), Cursor = Cursors.Hand,
        FlatAppearance = { BorderSize = 0, MouseOverBackColor = primary
            ? Color.FromArgb(24, 72, 184) : Color.FromArgb(230, 236, 247) }
    };

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SetScaledMinimumSize();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (initializationQueued)
            return;
        initializationQueued = true;
        BeginInvoke(async () =>
        {
            if (exiting)
                return;
            initialized = true;
            LoadSettings();
            LoadPreferences();
            RefreshStartupSetting();
            InitializationCompleted?.Invoke(this, EventArgs.Empty);
            await CheckForUpdatesAsync(manual: false);
            QueueRefresh("Startup");
        });
    }

    protected override void SetVisibleCore(bool value)
    {
        if (value && startInTray)
        {
            if (!IsHandleCreated)
                CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        SetScaledMinimumSize();
    }

    private void SetScaledMinimumSize()
    {
        MinimumSize = SizeFromClientSize(new Size(
            (int)Math.Ceiling(600 * DeviceDpi / 96d), 0));
    }

    private void BuildMainWindow()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(28), ColumnCount = 1, RowCount = 9
        };
        AutoScroll = true;
        for (int i = 0; i < 9; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Odyssey Input Switcher", Font = new Font(Font.FontFamily, 23, FontStyle.Bold),
            AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 2)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Choose where your monitor gets its picture.",
            AutoSize = true, Dock = DockStyle.Top,
            ForeColor = status.ForeColor, Margin = new Padding(2, 0, 0, 22)
        }, 0, 1);
        layout.Controls.Add(new Label
        {
            Text = "MONITOR", AutoSize = true, ForeColor = Accent,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(2, 0, 0, 2)
        }, 0, 2);
        var selection = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        monitors.Margin = new Padding(0, 6, 10, 8);
        selection.Controls.Add(monitors, 0, 0);
        selection.Controls.Add(refresh, 1, 0);
        layout.Controls.Add(selection, 0, 3);
        var defaults = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        defaultLabel.Margin = new Padding(4, 6, 0, 6);
        defaults.Controls.Add(defaultLabel);
        layout.Controls.Add(defaults, 0, 4);
        layout.Controls.Add(new Label
        {
            Text = "SWITCH INPUT", AutoSize = true, ForeColor = Accent,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(2, 18, 0, 2)
        }, 0, 5);
        var inputs = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, RowCount = 1, ColumnCount = 3,
            Margin = new Padding(0, 6, 0, 10)
        };
        inputs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < inputNames.Length; i++)
        {
            int index = i;
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            switches[i] = MakeButton(inputNames[i], primary: true);
            switches[i].AccessibleName = "Switch to " + inputNames[i];
            switches[i].Dock = DockStyle.Fill;
            switches[i].MinimumSize = new Size(0, 60);
            switches[i].Click += async (_, _) => await SwitchAsync(index);
            inputs.Controls.Add(switches[i], i, 0);
        }
        layout.Controls.Add(inputs, 0, 6);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        footer.Controls.Add(settingsButton);
        var hide = MakeButton("Run in tray");
        hide.Click += (_, _) => { advanced.Hide(); Hide(); };
        footer.Controls.Add(hide);
        layout.Controls.Add(footer, 0, 7);
        status.MaximumSize = new Size(540, 0);
        status.Margin = new Padding(2, 12, 0, 0);
        layout.Controls.Add(status, 0, 8);
        Controls.Add(layout);
    }

    private void BuildAdvancedWindow()
    {
        advanced.Text = "Odyssey - Settings & diagnostics";
        advanced.Font = Font;
        advanced.BackColor = Canvas;
        advanced.ForeColor = Ink;
        advanced.AutoScaleMode = AutoScaleMode.Dpi;
        advanced.ClientSize = new Size(750, 560);
        advanced.FormBorderStyle = FormBorderStyle.FixedSingle;
        advanced.MaximizeBox = false;
        advanced.StartPosition = FormStartPosition.CenterParent;
        advanced.Icon = Icon;
        advanced.FormClosing += (_, e) =>
        {
            if (!exiting)
            {
                e.Cancel = true;
                advanced.Hide();
            }
        };
        var diagnosticTab = new TabPage("Diagnostics")
        {
            BackColor = Canvas, Padding = new Padding(16), AutoScroll = true
        };
        var settingsTab = new TabPage("Inputs & default monitor")
        {
            BackColor = Canvas, Padding = new Padding(16), AutoScroll = true
        };
        var diagnosticLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        diagnosticLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        diagnosticLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        diagnosticLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        diagnosticSummary.MaximumSize = new Size(680, 0);
        diagnosticSummary.Margin = new Padding(3, 0, 3, 12);
        diagnosticLayout.Controls.Add(diagnosticSummary, 0, 0);
        diagnosticLayout.Controls.Add(diagnostics, 0, 1);
        diagnosticLayout.Controls.Add(log, 0, 2);
        diagnosticTab.Controls.Add(diagnosticLayout);

        var settingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 7
        };
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var explanation = new Label
        {
            Text = "VCP 0x60 input values (decimal or 0x hex).\r\n" +
                "Working Odyssey profile: DisplayPort 0x0F, HDMI 0x05, Micro HDMI 0x06.\r\n" +
                "Changing these settings does not send monitor commands.",
            AutoSize = true, Margin = new Padding(3, 0, 3, 20)
        };
        settingsLayout.Controls.Add(explanation, 0, 0);
        settingsLayout.SetColumnSpan(explanation, 2);
        for (int i = 0; i < inputNames.Length; i++)
        {
            values[i] = new TextBox { Dock = DockStyle.Fill, AccessibleName = inputNames[i] + " write value" };
            values[i].TextChanged += (_, _) => UpdateControls();
            settingsLayout.Controls.Add(new Label
            {
                Text = inputNames[i], AutoSize = true, Margin = new Padding(3, 6, 18, 10)
            }, 0, i + 1);
            settingsLayout.Controls.Add(values[i], 1, i + 1);
        }
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        actions.Controls.Add(restore);
        actions.Controls.Add(save);
        settingsLayout.Controls.Add(actions, 0, 4);
        settingsLayout.SetColumnSpan(actions, 2);
        settingsLayout.Controls.Add(confirmSwitch, 0, 5);
        settingsLayout.SetColumnSpan(confirmSwitch, 2);
        settingsLayout.Controls.Add(clearDefault, 0, 6);
        settingsLayout.SetColumnSpan(clearDefault, 2);
        settingsTab.Controls.Add(settingsLayout);
        settingsTabs.TabPages.Add(diagnosticTab);
        settingsTabs.TabPages.Add(settingsTab);
        advanced.Controls.Add(settingsTabs);
        PopulateSettingsMenu(settingsMenu.Items, startupItem, settingsUpdate);
        settingsMenu.Items.Add(saveDefault);
        settingsMenu.Opening += (_, _) => { RefreshStartupSetting(); UpdateControls(); };
    }

    private void BuildTray()
    {
        trayMenu.Items.Add(trayMonitor);
        trayMenu.Items.Add(new ToolStripSeparator());
        for (int i = 0; i < inputNames.Length; i++)
        {
            int index = i;
            trayInputs[i] = new ToolStripMenuItem(inputNames[i]);
            trayInputs[i].Click += async (_, _) => await SwitchAsync(index);
            trayMenu.Items.Add(trayInputs[i]);
        }
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Show Odyssey", null, (_, _) => ShowMainWindow());
        trayRefresh.Click += (_, _) => QueueRefresh("Tray refresh");
        trayMenu.Items.Add(trayRefresh);
        var traySettings = new ToolStripMenuItem("Settings");
        PopulateSettingsMenu(traySettings.DropDownItems, trayStartupItem, trayUpdate);
        traySettings.DropDownItems.Add(traySaveDefault);
        traySettings.DropDownOpening += (_, _) => { RefreshStartupSetting(); UpdateControls(); };
        trayMenu.Items.Add(traySettings);
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());
        trayMenu.Opening += (_, _) => UpdateControls();
        tray.Icon = Icon;
        tray.Text = "Odyssey Input Switcher";
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowMainWindow();
        tray.Visible = true;
    }

    private void ShowMainWindow()
    {
        startInTray = false;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ShowAdvanced(int tab = 0)
    {
        ShowMainWindow();
        settingsTabs.SelectedIndex = tab;
        advanced.Show(this);
        advanced.Activate();
    }

    private void PopulateSettingsMenu(ToolStripItemCollection items,
        ToolStripMenuItem startup, ToolStripMenuItem update)
    {
        startup.Click += (_, _) =>
        {
            try
            {
                StartupRegistration.SetEnabled(!startup.Checked);
                RefreshStartupSetting();
                Append(startup.Checked ? "Enabled Windows startup in the system tray."
                    : "Disabled Windows startup.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or InvalidOperationException)
            {
                ReportSettingsFailure(error);
            }
        };
        items.Add(startup);
        items.Add(new ToolStripSeparator());
        items.Add("Input & monitor settings...", null, (_, _) => ShowAdvanced(1));
        items.Add("Diagnostics & logging...", null, (_, _) => ShowAdvanced(0));
        update.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        items.Add(update);
        items.Add(new ToolStripSeparator());
        items.Add("About Odyssey...", null, (_, _) => ShowAbout());
    }

    private void ShowAbout()
    {
        ShowMainWindow();
        MessageBox.Show(this,
            "Odyssey Input Switcher\r\n" +
            $"Version: {ReleaseUpdater.CurrentVersion.ToString(3)}\r\n" +
            "Creator: Jeffrey Strasser (jeffstrasser)\r\n" +
            $"Architecture: {RuntimeInformation.ProcessArchitecture}\r\n\r\n" +
            "Switch monitor inputs using Windows DDC/CI.\r\n\r\n" +
            ReleaseUpdater.RepositoryUrl,
            "About Odyssey Input Switcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RefreshStartupSetting()
    {
        try
        {
            startupItem.Checked = trayStartupItem.Checked = StartupRegistration.IsEnabled();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or InvalidOperationException)
        {
            ReportSettingsFailure(error);
        }
    }

    private void ReportSettingsFailure(Exception error)
    {
        Append("Settings failed: " + error);
        SetStatus("Settings could not be updated.");
        if (startInTray)
            tray.ShowBalloonTip(5000, "Odyssey settings failed", error.Message, ToolTipIcon.Error);
        else
            MessageBox.Show(this, error.Message, "Odyssey settings failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ExitApplication()
    {
        exiting = true;
        updateCancellation.Cancel();
        refreshTimer.Stop();
        refreshPending = false;
        Close();
    }

    private void UpdateControls()
    {
        if (resourcesDisposed)
            return;
        NativeMonitor? monitor = SelectedMonitor;
        bool selected = !busy && !confirming && !exiting && monitor is not null;
        monitors.Enabled = !busy && !stale;
        refresh.Enabled = !busy && !exiting;
        trayRefresh.Enabled = refresh.Enabled;
        trayUpdate.Enabled = !busy && !confirming && !exiting;
        settingsUpdate.Enabled = trayUpdate.Enabled;
        startupItem.Enabled = trayStartupItem.Enabled = !busy && !exiting;
        diagnostics.Enabled = selected;
        save.Enabled = restore.Enabled = !busy && !exiting;
        confirmSwitch.Enabled = !busy && !exiting;
        clearDefault.Enabled = !busy && preferences.DefaultMonitorId is not null;
        saveDefault.Visible = traySaveDefault.Visible = monitors.Items.Count > 1;
        saveDefault.Enabled = traySaveDefault.Enabled = selected && monitor?.StableId is not null &&
            MonitorSelection.FindUnique(CurrentIdentities(), monitor.StableId) == monitors.SelectedIndex;
        defaultLabel.Text = monitor is null ? "" : monitors.Items.Count == 1 ? "Selected automatically"
            : monitor.StableId is null ? "Select manually after display changes."
            : MonitorSelection.FindUnique(CurrentIdentities(), preferences.DefaultMonitorId) == monitors.SelectedIndex
                ? "Your default monitor" : "Save this monitor as your default.";
        trayMonitor.Text = monitor is null ? "No monitor selected" : monitor.DisplayName;
        string trayText = monitor is null ? "Odyssey - no monitor selected" : "Odyssey - " + monitor.DisplayName;
        tray.Text = trayText[..Math.Min(63, trayText.Length)];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is null)
                continue;
            values[i].Enabled = !busy;
            bool enabled = selected && InputValue.TryParse(values[i].Text, out _);
            switches[i].Enabled = enabled;
            if (trayInputs[i] is not null)
                trayInputs[i].Enabled = enabled;
        }
        diagnosticSummary.Text = $"Process: {RuntimeInformation.ProcessArchitecture} / Windows {Environment.OSVersion.Version}\r\n" +
            $"Selected: {monitor?.Description ?? "none"}\r\n" +
            $"Monitor interface: {monitor?.StableId ?? "unavailable"}\r\n" +
            $"Saved default: {preferences.DefaultMonitorId ?? "none"}";
    }

    private string?[] CurrentIdentities() =>
        inventory?.Monitors.Select(monitor => monitor.StableId).ToArray() ?? [];

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (busy || confirming || exiting)
            return;
        await RunAsync(async () =>
        {
            string? stagedDirectory = null;
            bool installerStarted = false;
            bool approved = false;
            try
            {
                SetStatus("Checking for updates...");
                using var updater = new ReleaseUpdater();
                var update = await updater.CheckAsync(updateCancellation.Token);
                if (exiting)
                    return;
                if (update is null)
                {
                    Append($"No newer stable release. Installed version: {ReleaseUpdater.CurrentVersion}.");
                    SetStatus("No newer stable release is available.");
                    if (manual)
                        MessageBox.Show(this, "No newer stable release is available.",
                            "Odyssey updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (startInTray && !manual)
                {
                    Append($"Version {update.Version} is available. Open Settings > Check for updates to upgrade.");
                    tray.ShowBalloonTip(5000, "Odyssey update available",
                        $"Version {update.Version} is available. Open Settings > Check for updates.", ToolTipIcon.Info);
                    return;
                }
                ShowMainWindow();
                if (MessageBox.Show(this,
                    $"Version {update.Version} is available (installed: {ReleaseUpdater.CurrentVersion}).\r\n\r\n" +
                    "Download and install it now? Odyssey will close and restart. Your settings will be kept.",
                    "Odyssey update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes || exiting)
                {
                    SetStatus("Update declined. Use the tray menu to check again.");
                    return;
                }
                approved = true;
                if (!ReleaseUpdater.IsSingleFileBuild())
                    throw new InvalidOperationException("In-place upgrades require a published single-file release. " +
                        "Download one from " + ReleaseUpdater.RepositoryUrl + "/releases/latest.");
                SetStatus("Downloading and verifying the update...");
                stagedDirectory = await updater.StageAsync(update, updateCancellation.Token);
                if (exiting)
                    return;
                ReleaseUpdater.StartInstaller(stagedDirectory);
                installerStarted = true;
                Append($"Installing version {update.Version}; restarting Odyssey.");
                ExitApplication();
            }
            catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
            {
                Append("Update canceled because the app is exiting.");
            }
            catch (Exception error)
            {
                Append("Update failed: " + error);
                SetStatus("Update failed. Your installed version has not been changed.");
                if (!exiting)
                {
                    if (manual || approved)
                        MessageBox.Show(this, error.Message, "Odyssey update failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else
                        tray.ShowBalloonTip(5000, "Update check failed",
                            error.Message + " Use Check for updates to retry.", ToolTipIcon.Warning);
                }
            }
            finally
            {
                if (stagedDirectory is not null && !installerStarted)
                {
                    try { Directory.Delete(stagedDirectory, recursive: true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        Append("Update staging cleanup failed: " + error);
                        if (!exiting)
                            MessageBox.Show(this, "Temporary update files could not be removed:\r\n" + error.Message,
                                "Odyssey update cleanup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        });
    }

    private void Append(string message) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
    private void SetStatus(string message)
    {
        status.Text = message;
        status.Visible = message.Length != 0;
    }

    private void ReportFailure(Exception error, bool showDialog = true)
    {
        SetStatus("Operation failed. Open Settings > Diagnostics & logging for details.");
        Append(error.ToString());
        tray.ShowBalloonTip(5000, "Odyssey operation failed", error.Message, ToolTipIcon.Error);
        if (showDialog)
            MessageBox.Show(advanced.Visible ? advanced : Visible ? this : null, error.Message +
                "\r\n\r\nSee Settings > Diagnostics & logging for details. The DDC/CI connection may be unavailable.",
                "Odyssey Input Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private async Task RunAsync(Func<Task> operation, bool showErrors = true)
    {
        if (busy || exiting)
            return;
        busy = true;
        UpdateControls();
        try
        {
            await operation();
        }
        catch (Exception error)
        {
            ReportFailure(error, showErrors);
        }
        finally
        {
            busy = false;
            UpdateControls();
            if (closeWhenIdle)
                Close();
            else if (refreshPending)
                refreshTimer.Start();
        }
    }

    private void QueueRefresh(string reason)
    {
        if (exiting || resourcesDisposed)
            return;
        stale = true;
        refreshPending = true;
        monitors.SelectedIndex = -1;
        Append($"Refresh queued: {reason}. No input commands sent.");
        SetStatus("Discovering connected monitors...");
        UpdateControls();
        refreshTimer.Stop();
        refreshTimer.Start();
    }

    private void ReleaseInventory(bool logMessages = true)
    {
        monitors.Items.Clear();
        if (inventory is null)
            return;
        inventory.Dispose();
        if (logMessages)
            foreach (string message in inventory.Messages)
                Append(message);
        inventory = null;
    }

    private Task RefreshAsync() => RunAsync(async () =>
    {
        SetStatus("Discovering connected monitors...");
        ReleaseInventory();
        int generation = displayGeneration;
        inventory = await Task.Run(MonitorInventory.Enumerate);
        foreach (string message in inventory.Messages)
            Append(message);
        inventory.Messages.Clear();
        stale = generation != displayGeneration;
        if (stale)
        {
            QueueRefresh("Displays changed during enumeration");
            return;
        }
        foreach (var monitor in inventory.Monitors)
        {
            monitors.Items.Add(monitor);
            Append($"{monitor.Description}\r\nMonitor interface: {monitor.StableId ?? "unavailable"}");
        }
        monitors.SelectedIndex = MonitorSelection.Choose(CurrentIdentities(), preferences.DefaultMonitorId);
        SetSelectionStatus();
        Append($"Enumerated {inventory.Monitors.Count} physical monitor(s). No input commands sent.");
    }, showErrors: false);

    private void SetSelectionStatus()
    {
        SetStatus(monitors.Items.Count == 0 ? "No connected physical monitor found."
            : SelectedMonitor is null ? "Choose a monitor. No unique saved default is connected."
            : "");
    }

    private Task DiagnosticsAsync()
    {
        NativeMonitor? selected = SelectedMonitor;
        if (selected is null)
            return Task.CompletedTask;
        return RunAsync(async () =>
        {
            SetStatus("Reading monitor diagnostics...");
            Append("Read-only diagnostics for " + selected.Description);
            var result = await Task.Run(() =>
            {
                var lines = new List<string>();
                bool failed = false;
                try
                {
                    uint current = selected.ReadInput();
                    lines.Add($"VCP 0x60 current value: {current} (0x{current:X4}). " +
                        "A read does not establish the write mapping.");
                }
                catch (Win32Exception ex) { failed = true; lines.Add(ex.Message); }
                catch (InvalidOperationException ex) { failed = true; lines.Add(ex.Message); }
                try
                {
                    lines.Add("Raw capabilities (may be misleading):\r\n" + selected.ReadCapabilities());
                }
                catch (Win32Exception ex) { failed = true; lines.Add(ex.Message); }
                catch (InvalidOperationException ex) { failed = true; lines.Add(ex.Message); }
                return (lines, failed);
            });
            foreach (string line in result.lines)
                Append(line);
            SetStatus(stale ? "Monitor connection changed. Refreshing..."
                : result.failed ? "Some reads failed. See Settings > Diagnostics & logging for details."
                : "Diagnostics complete. No input commands sent.");
        });
    }

    private async Task SwitchAsync(int index)
    {
        NativeMonitor? selected = SelectedMonitor;
        if (busy || confirming || exiting || selected is null || !InputValue.TryParse(values[index].Text, out uint value))
            return;
        int generation = displayGeneration;
        DialogResult answer;
        confirming = true;
        UpdateControls();
        try
        {
            if (preferences.SuppressSwitchConfirmation)
            {
                answer = DialogResult.Yes;
            }
            else
            {
                var confirmation = ConfirmInputSwitch(selected, inputNames[index], value);
                answer = confirmation.Result;
                if (confirmation.SuppressFuture != preferences.SuppressSwitchConfirmation)
                {
                    var updated = new Preferences
                    {
                        DefaultMonitorId = preferences.DefaultMonitorId,
                        SuppressSwitchConfirmation = confirmation.SuppressFuture
                    };
                    WriteSettings(PreferencesPath, updated);
                    preferences = updated;
                    Append("Saved input confirmation preference.");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportFailure(error);
            return;
        }
        finally
        {
            confirming = false;
            UpdateControls();
            if (exiting && closeWhenIdle)
                Close();
        }
        if (answer != DialogResult.Yes || exiting)
            return;
        if (stale || generation != displayGeneration || SelectedMonitor != selected)
        {
            QueueRefresh("Connection changed during confirmation");
            return;
        }
        await RunAsync(async () =>
        {
            SetStatus("Sending input command...");
            Append($"Sending VCP 0x60 = {value} (0x{value:X4}) to {selected.Description}.");
            try
            {
                await Task.Run(() => InputSwitch.Send(selected, value));
                Append("Windows accepted the command. Physical switching is NOT verified. No readback or retry.");
            }
            finally
            {
                QueueRefresh("Input switch attempt completed");
            }
        });
    }

    private (DialogResult Result, bool SuppressFuture) ConfirmInputSwitch(
        NativeMonitor monitor, string inputName, uint value)
    {
        var (dialog, suppress) = CreateInputConfirmation(monitor.DisplayName, inputName, value);
        using (dialog)
        {
            DialogResult result = dialog.ShowDialog(Visible ? this : null);
            return (result, suppress.Checked);
        }
    }

    internal (Form Dialog, CheckBox Suppress) CreateInputConfirmation(
        string displayName, string inputName, uint value)
    {
        var dialog = new Form
        {
            Text = "Confirm input switch",
            Font = Font,
            BackColor = Color.White,
            ForeColor = Ink,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = Visible ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
            Icon = Icon
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 4
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(new Label
        {
            Text = $"Switch to {inputName}?",
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        content.Controls.Add(new Label
        {
            Text = $"{displayName}\r\nVCP 0x60 = 0x{value:X4}\r\n\r\n" +
                "Switching may disconnect this PC. Use the monitor's Source control to return if needed.",
            AutoSize = true,
            MaximumSize = new Size((int)Math.Ceiling(400 * Font.SizeInPoints / 10), 0),
            ForeColor = status.ForeColor,
            Margin = new Padding(0, 0, 0, 14)
        }, 0, 1);
        var suppress = new CheckBox
        {
            Text = "Do not show me this again",
            AutoSize = true,
            AccessibleName = "Do not show input switch confirmation again",
            Margin = new Padding(0, 0, 0, 14)
        };
        content.Controls.Add(suppress, 0, 2);
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty
        };
        var yes = MakeButton("Switch", primary: true);
        yes.DialogResult = DialogResult.Yes;
        var no = MakeButton("Cancel");
        no.DialogResult = DialogResult.No;
        actions.Controls.Add(yes);
        actions.Controls.Add(no);
        content.Controls.Add(actions, 0, 3);
        dialog.Controls.Add(content);
        dialog.AcceptButton = no;
        dialog.CancelButton = no;

        return (dialog, suppress);
    }

    protected override void WndProc(ref Message message)
    {
        const int WmDisplayChange = 0x007E;
        const int WmPowerBroadcast = 0x0218;
        bool resumed = message.Msg == WmPowerBroadcast && message.WParam.ToInt64() is 7 or 18;
        if (message.Msg == WmDisplayChange || resumed)
        {
            displayGeneration++;
            if (initialized)
                QueueRefresh(resumed ? "Windows resumed" : "Windows display change");
        }
        base.WndProc(ref message);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(SettingsPath))
                ?? throw new InvalidOperationException("Saved input settings are empty.");
            ApplySettings(InputSettings.Resolve(stored));
            Append("Loaded input values from " + SettingsPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException)
        {
            foreach (var field in values)
                field.Clear();
            ReportFailure(error);
        }
    }

    private void LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
                return;
            preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath))
                ?? throw new InvalidOperationException("Saved preferences are empty.");
            Append("Loaded monitor preferences from " + PreferencesPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException)
        {
            preferences = new Preferences();
            ReportFailure(error);
        }
        finally
        {
            confirmSwitch.Checked = !preferences.SuppressSwitchConfirmation;
        }
    }

    private void ApplySettings(Dictionary<string, string> stored)
    {
        for (int i = 0; i < values.Length; i++)
            values[i].Text = stored[inputNames[i]];
    }

    private static void WriteSettings(string path, object settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void SaveDefault(bool clear = false)
    {
        try
        {
            string? identity = null;
            if (!clear)
            {
                NativeMonitor? monitor = SelectedMonitor;
                if (monitor?.StableId is null ||
                    MonitorSelection.FindUnique(CurrentIdentities(), monitor.StableId) != monitors.SelectedIndex)
                    throw new InvalidOperationException("This monitor has no unique Windows interface identity. Select it manually.");
                identity = monitor.StableId;
            }
            var updated = new Preferences
            {
                DefaultMonitorId = identity,
                SuppressSwitchConfirmation = preferences.SuppressSwitchConfirmation
            };
            WriteSettings(PreferencesPath, updated);
            preferences = updated;
            Append(clear ? "Cleared the default monitor." : "Saved default monitor interface: " + identity);
            SetStatus(clear ? "Default monitor cleared." : "Default monitor saved.");
            UpdateControls();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure(error);
        }
    }

    private void SaveSettings()
    {
        try
        {
            var stored = new Dictionary<string, string>();
            for (int i = 0; i < values.Length; i++)
            {
                string text = values[i].Text.Trim();
                if (text.Length != 0 && !InputValue.TryParse(text, out _))
                    throw new InvalidOperationException($"{inputNames[i]} needs a value from 0 to 65535, decimal or 0x hex.");
                stored[inputNames[i]] = text;
            }
            WriteSettings(SettingsPath, stored);
            Append("Saved input values to " + SettingsPath);
            SetStatus("Input values saved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure(error);
        }
    }

    private void SaveConfirmationPreference()
    {
        if (!initialized || confirmSwitch.Checked == !preferences.SuppressSwitchConfirmation)
            return;
        try
        {
            var updated = new Preferences
            {
                DefaultMonitorId = preferences.DefaultMonitorId,
                SuppressSwitchConfirmation = !confirmSwitch.Checked
            };
            WriteSettings(PreferencesPath, updated);
            preferences = updated;
            Append(confirmSwitch.Checked
                ? "Enabled input switch confirmations."
                : "Disabled input switch confirmations.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            confirmSwitch.Checked = !preferences.SuppressSwitchConfirmation;
            ReportFailure(error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            exiting = true;
            updateCancellation.Cancel();
            updateCancellation.Dispose();
            refreshTimer.Dispose();
            ReleaseInventory(logMessages: false);
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            settingsMenu.Dispose();
            advanced.Dispose();
            Icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
