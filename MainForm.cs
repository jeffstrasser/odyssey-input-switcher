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
    private readonly Button saveDefault = MakeButton("Save selected monitor as default");
    private readonly MenuStrip menuBar = new() { Dock = DockStyle.Top };
    private readonly ToolStripMenuItem settingsMenu = new("&Settings");
    private readonly CheckBox runAtStartup = new() { Text = "Run at Windows startup", AutoSize = true };
    private readonly ToolStripMenuItem settingsUpdate = new("Check for updates...");
    private readonly TabControl settingsTabs = new() { Dock = DockStyle.Fill, Padding = new Point(10, 4) };
    private readonly ToolTip buttonHelp = new() { AutoPopDelay = 15000 };
    private bool settingsReady;
    private bool sizingSettings;
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
    private readonly Label settingsMonitor = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label diagnosticSummary = new() { AutoSize = true };
    private readonly TextBox log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Dock = DockStyle.Fill, AccessibleName = "Diagnostic log",
        Font = new Font("Consolas", 9), BackColor = Color.White
    };
    private readonly string[] inputNames = ["DisplayPort", "HDMI", "Micro HDMI"];
    private readonly string[] displayNames = ["DisplayPort", "HDMI", "Micro HDMI"];
    private readonly TextBox[] nameFields = new TextBox[3];
    private readonly bool[] visibleInputs = [true, true, true];
    private readonly CheckBox[] visibilityFields = new CheckBox[3];
    private readonly Label allInputsHidden = new()
    {
        Text = "All inputs are hidden. Open Settings > Inputs... to show them again.",
        AutoSize = true, Dock = DockStyle.Fill, Visible = false
    };
    private readonly TextBox[] values = new TextBox[3];
    private readonly Button[] switches = new Button[3];
    private readonly ToolStripMenuItem[] trayInputs = new ToolStripMenuItem[3];
    private readonly ToolStripMenuItem trayAllInputsHidden = new("All inputs hidden - Settings > Inputs...")
    {
        Enabled = false, Visible = false
    };
    private readonly ToolStripMenuItem trayMonitor = new("Discovering monitors...") { Enabled = false };
    private readonly CancellationTokenSource updateCancellation = new();
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip trayMenu = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 650 };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60_000 };
    private readonly UpdateCheckSchedule updateSchedule;
    private readonly Func<ReleaseUpdater> createUpdater;
    private bool checkingForUpdates;
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
    private static string InputNamesPath => Path.Combine(SettingsDirectory, "input-names.json");
    private static string InputVisibilityPath => Path.Combine(SettingsDirectory, "input-visibility.json");
    private NativeMonitor? SelectedMonitor => !stale ? monitors.SelectedItem as NativeMonitor : null;
    internal event EventHandler? InitializationCompleted;

    internal MainForm(bool startInTray = false, TimeProvider? clock = null,
        Func<ReleaseUpdater>? updaterFactory = null)
    {
        this.startInTray = startInTray;
        updateSchedule = new UpdateCheckSchedule(clock ?? TimeProvider.System);
        createUpdater = updaterFactory ?? (() => new ReleaseUpdater());
        Text = "Odyssey Input Switcher";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        BackColor = Canvas;
        ForeColor = Ink;
        ClientSize = new Size(440, 1);
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
        ApplyInputNames(InputDisplayNames.Resolve(null));
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
        clearDefault.Click += (_, _) => SaveDefault(clear: true);
        diagnostics.Click += async (_, _) => await DiagnosticsAsync();
        save.Click += (_, _) => SaveSettings();
        confirmSwitch.CheckedChanged += (_, _) => SaveConfirmationPreference();
        runAtStartup.Click += (_, _) =>
        {
            try
            {
                StartupRegistration.SetEnabled(runAtStartup.Checked);
                RefreshStartupSetting();
                Append(runAtStartup.Checked ? "Enabled Windows startup in the system tray."
                    : "Disabled Windows startup.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                System.Security.SecurityException or InvalidOperationException)
            {
                runAtStartup.Checked = !runAtStartup.Checked;
                ReportSettingsFailure(error);
            }
        };
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
        updateTimer.Tick += async (_, _) => await CheckForUpdatesIfDueAsync();
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
                        "Right-click the monitor icon to switch inputs or Quit. Double-click it for Settings.", ToolTipIcon.Info);
                }

            }
            else if (busy || confirming)
            {
                e.Cancel = true;
                closeWhenIdle = true;
                SetStatus("Waiting for the current operation before exiting...");
            }
        };
        Layout += (_, _) => SetScaledMinimumSize();
        UpdateControls();
    }

    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink,
        Padding = new Padding(8, 3, 8, 3), Cursor = Cursors.Hand,
        FlatAppearance = { BorderSize = 0, MouseOverBackColor = primary
            ? Color.FromArgb(24, 72, 184) : Color.FromArgb(230, 236, 247) }
    };

    private static TableLayoutPanel MakeSettingsActions()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 2,
            Margin = new Padding(0, 10, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        panel.Layout += (_, _) =>
        {
            foreach (Control control in panel.Controls)
            {
                float fraction = panel.GetColumn(control) == 0 ? 0.45f : 0.55f;
                int minimumWidth = control is Button ? control.Padding.Horizontal + 8 : 1;
                control.MaximumSize = new Size(
                    Math.Max(minimumWidth, (int)(panel.ClientSize.Width * fraction) - control.Margin.Horizontal), 0);
            }
        };
        return panel;
    }

    private void DescribeButton(Button button, string description)
    {
        button.AccessibleDescription = description;
        buttonHelp.SetToolTip(button, description);
    }

    private void AddSettingsAction(TableLayoutPanel panel, Button button, string description)
    {
        DescribeButton(button, description);
        int row = panel.RowCount++;
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        button.Dock = DockStyle.Top;
        panel.Controls.Add(button, 0, row);
        panel.Controls.Add(new Label
        {
            Text = description, AutoSize = true, Dock = DockStyle.Top,
            ForeColor = status.ForeColor, Margin = new Padding(10, 6, 3, 6)
        }, 1, row);
    }

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
            LoadInputNames(InputNamesPath);
            LoadInputVisibility(InputVisibilityPath);
            LoadPreferences();
            RefreshStartupSetting();
            InitializationCompleted?.Invoke(this, EventArgs.Empty);
            updateTimer.Start();
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
        // ClientSize excludes visible scrollbars as well as the native window frame.
        MinimumSize = new Size((int)Math.Ceiling(440 * DeviceDpi / 96d) + Width - ClientSize.Width,
            SizeFromClientSize(Size.Empty).Height);
        if (switches[0]?.Parent is TableLayoutPanel inputs)
            SizeInputButtons(inputs);
    }

    private void BuildMainWindow()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14), ColumnCount = 1, RowCount = 7
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoScroll = true;
        for (int i = 0; i < 7; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            Text = "Monitor", Font = new Font(Font, FontStyle.Bold),
            AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 2)
        }, 0, 0);
        var selection = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2
        };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        monitors.Margin = new Padding(0, 6, 10, 8);
        selection.Controls.Add(monitors, 0, 0);
        selection.Controls.Add(refresh, 1, 0);
        DescribeButton(refresh, "Find connected monitors again. Does not switch inputs.");
        layout.Controls.Add(selection, 0, 1);
        defaultLabel.Dock = DockStyle.Fill;
        defaultLabel.Margin = new Padding(0, 2, 0, 4);
        layout.Controls.Add(defaultLabel, 0, 2);
        layout.Controls.Add(new Label
        {
            Text = "Switch input", AutoSize = true,
            Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2)
        }, 0, 3);
        var inputs = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            RowCount = 2, ColumnCount = 3,
            Margin = new Padding(0, 4, 0, 6)
        };
        inputs.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        inputs.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < inputNames.Length; i++)
        {
            int index = i;
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            switches[i] = MakeButton(inputNames[i], primary: true);
            switches[i].UseMnemonic = false;
            switches[i].AccessibleName = "Switch to " + inputNames[i];
            switches[i].Dock = DockStyle.Fill;
            switches[i].MinimumSize = new Size(0, 36);
            DescribeButton(switches[i], "Switch the selected monitor to " + inputNames[i] + ".");
            switches[i].Click += async (_, _) => await SwitchAsync(index);
            inputs.Controls.Add(switches[i], i, 0);
            switches[i].FontChanged += (_, _) => SizeInputButtons(inputs);
        }
        inputs.Controls.Add(allInputsHidden, 0, 1);
        inputs.SetColumnSpan(allInputsHidden, 3);
        inputs.SizeChanged += (_, _) => SizeInputButtons(inputs);
        layout.Controls.Add(inputs, 0, 4);
        var footer = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top
        };
        var hide = MakeButton("Run in tray");
        DescribeButton(hide, "Hide this window and keep Odyssey running. Double-click the tray icon to reopen.");
        hide.Click += (_, _) => { advanced.Hide(); Hide(); };
        footer.Controls.Add(hide);
        layout.Controls.Add(footer, 0, 5);
        status.Dock = DockStyle.Fill;
        status.Margin = new Padding(0, 6, 0, 0);
        layout.Controls.Add(status, 0, 6);
        Controls.Add(layout);
        menuBar.Font = Font;
        menuBar.Items.Add(settingsMenu);
        MainMenuStrip = menuBar;
        Controls.Add(menuBar);
        FontChanged += (_, _) => menuBar.Font = Font;
    }

    private void SizeInputButtons(TableLayoutPanel inputs)
    {
        int count = visibleInputs.Count(visible => visible);
        if (count == 0)
        {
            inputs.RowStyles[0].Height = 0;
            return;
        }
        int width = inputs.ClientSize.Width / count;
        if (width <= 0 || switches.Any(button => button is null))
            return;
        int height = (int)Math.Ceiling(36 * DeviceDpi / 96d);
        for (int i = 0; i < switches.Length; i++)
        {
            if (!visibleInputs[i])
                continue;
            var button = switches[i];
            button.MaximumSize = new Size(Math.Max(1, width - button.Margin.Horizontal), 0);
            int textWidth = Math.Max(1, width - button.Margin.Horizontal - button.Padding.Horizontal - 8);
            var textSize = TextRenderer.MeasureText(button.Text, button.Font,
                new Size(textWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
            height = Math.Max(height, Math.Max(button.MinimumSize.Height,
                textSize.Height + button.Padding.Vertical + 8) + button.Margin.Vertical);
        }
        if (inputs.RowStyles[0].Height != height)
            inputs.RowStyles[0].Height = height;
    }

    private void BuildAdvancedWindow()
    {
        advanced.Text = "Odyssey - Settings";
        advanced.Font = Font;
        advanced.BackColor = Canvas;
        advanced.ForeColor = Ink;
        advanced.AutoScaleMode = AutoScaleMode.Dpi;
        advanced.AutoScaleDimensions = new SizeF(96, 96);
        advanced.ClientSize = new Size(650, 480);
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
            BackColor = Canvas, Padding = new Padding(12)
        };
        var settingsTab = new TabPage("Input values")
        {
            BackColor = Canvas, Padding = new Padding(12)
        };
        var diagnosticLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 3
        };
        diagnosticLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        diagnosticLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        diagnosticLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        diagnosticLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));
        diagnosticSummary.Dock = DockStyle.Fill;
        diagnosticSummary.Margin = new Padding(3, 0, 3, 12);
        diagnosticLayout.Controls.Add(diagnosticSummary, 0, 0);
        var diagnosticActions = MakeSettingsActions();
        AddSettingsAction(diagnosticActions, diagnostics,
            "Read the current input and monitor capabilities. Does not switch inputs.");
        diagnosticLayout.Controls.Add(diagnosticActions, 0, 1);
        diagnosticLayout.Controls.Add(log, 0, 2);
        diagnosticTab.Controls.Add(diagnosticLayout);

        var settingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 5
        };
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var explanation = new Label
        {
            Text = "VCP 0x60 input values (decimal or 0x hex).\r\n" +
                "Working Odyssey profile: DisplayPort 0x0F, HDMI 0x05, Micro HDMI 0x06.\r\n" +
                "Changing these settings does not send monitor commands.",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 12)
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
        var actions = MakeSettingsActions();
        AddSettingsAction(actions, restore, "Fill in the tested Odyssey values. Choose Save input values to keep them.");
        AddSettingsAction(actions, save, "Save these command values for future switches. Does not switch inputs.");
        settingsLayout.Controls.Add(actions, 0, 4);
        settingsLayout.SetColumnSpan(actions, 2);
        settingsTab.Controls.Add(settingsLayout);

        var generalTab = new TabPage("General")
        {
            BackColor = Canvas, Padding = new Padding(12)
        };
        var generalLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 5
        };
        generalLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        generalLayout.Controls.Add(new Label
        {
            Text = "Startup and confirmation changes are saved immediately.\r\n" +
                "Choose a monitor in the main window before saving it as your default.",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 12)
        }, 0, 0);
        generalLayout.Controls.Add(runAtStartup, 0, 1);
        generalLayout.Controls.Add(confirmSwitch, 0, 2);
        settingsMonitor.Margin = new Padding(3, 16, 3, 12);
        generalLayout.Controls.Add(settingsMonitor, 0, 3);
        var monitorActions = MakeSettingsActions();
        AddSettingsAction(monitorActions, saveDefault, "Remember the selected monitor for automatic selection on startup.");
        AddSettingsAction(monitorActions, clearDefault, "Forget the saved monitor. A sole connected monitor is still selected automatically.");
        generalLayout.Controls.Add(monitorActions, 0, 4);
        generalTab.Controls.Add(generalLayout);
        settingsTabs.TabPages.Add(generalTab);

        var namesTab = new TabPage("Inputs")
        {
            BackColor = Canvas, Padding = new Padding(12)
        };
        var namesLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, RowCount = 5
        };
        namesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        namesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        namesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var namesExplanation = new Label
        {
            Text = $"Name each input after its connected device (1-{InputDisplayNames.MaximumLength} characters).\r\n" +
                "Use Show to choose which inputs appear in the main window and tray.\r\n" +
                "Names and visibility are saved separately; neither switches inputs.",
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 12)
        };
        namesLayout.Controls.Add(namesExplanation, 0, 0);
        namesLayout.SetColumnSpan(namesExplanation, 3);
        for (int i = 0; i < inputNames.Length; i++)
        {
            namesLayout.Controls.Add(new Label
            {
                Text = inputNames[i], AutoSize = true, Margin = new Padding(3, 6, 18, 10)
            }, 0, i + 1);
            nameFields[i] = new TextBox
            {
                Dock = DockStyle.Fill, MaxLength = InputDisplayNames.MaximumLength,
                AccessibleName = inputNames[i] + " display name"
            };
            namesLayout.Controls.Add(nameFields[i], 1, i + 1);
            visibilityFields[i] = new CheckBox
            {
                Text = "Show", AutoSize = true, Checked = true,
                AccessibleName = "Show " + inputNames[i] + " input",
                Margin = new Padding(12, 6, 3, 10)
            };
            namesLayout.Controls.Add(visibilityFields[i], 2, i + 1);
        }
        var nameActions = MakeSettingsActions();
        var restoreNames = MakeButton("Restore default names");
        restoreNames.Click += (_, _) =>
        {
            for (int i = 0; i < inputNames.Length; i++)
                nameFields[i].Text = inputNames[i];
        };
        var saveNames = MakeButton("Save input names");
        saveNames.Click += (_, _) => SaveInputNames(InputNamesPath);
        AddSettingsAction(nameActions, restoreNames,
            "Fill in the original port names. Choose Save input names to keep them.");
        AddSettingsAction(nameActions, saveNames,
            "Apply and remember these names in the main window, tray, and confirmations.");
        var saveVisibility = MakeButton("Save visible inputs");
        saveVisibility.Click += (_, _) => SaveInputVisibility(InputVisibilityPath);
        AddSettingsAction(nameActions, saveVisibility,
            "Apply and remember the Show checkboxes. Hidden inputs disappear from the main window and tray.");
        namesLayout.Controls.Add(nameActions, 0, 4);
        namesLayout.SetColumnSpan(nameActions, 3);
        namesTab.Controls.Add(namesLayout);
        settingsTabs.TabPages.Add(namesTab);
        var advancedTab = new TabPage("Advanced") { BackColor = Canvas };
        var advancedTabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(10, 4) };
        advancedTabs.TabPages.Add(settingsTab);
        advancedTabs.TabPages.Add(diagnosticTab);
        advancedTab.Controls.Add(advancedTabs);
        settingsTabs.TabPages.Add(advancedTab);
        advanced.Controls.Add(settingsTabs);
        settingsReady = true;
        advanced.Layout += (_, _) => SizeSettingsWindow();
        advanced.VisibleChanged += (_, _) => SizeSettingsWindow();
        settingsTabs.SelectedIndexChanged += (_, _) => SizeSettingsWindow();
        advancedTabs.SelectedIndexChanged += (_, _) => SizeSettingsWindow();
        advanced.FontChanged += (_, _) => SizeSettingsWindow();
        advanced.DpiChanged += (_, _) => SizeSettingsWindow();
        settingsMonitor.TextChanged += (_, _) => SizeSettingsWindow();
        diagnosticSummary.TextChanged += (_, _) => SizeSettingsWindow();
        advanced.PerformAutoScale();
        SizeSettingsWindow();
        PopulateSettingsMenu(settingsMenu.DropDownItems, settingsUpdate);
        settingsMenu.DropDownOpening += (_, _) => { RefreshStartupSetting(); UpdateControls(); };
    }

    private void SizeSettingsWindow()
    {
        if (!settingsReady || sizingSettings || advanced.IsDisposed)
            return;
        sizingSettings = true;
        try
        {
            var workingArea = Screen.FromControl(advanced).WorkingArea;
            int frameWidth = advanced.Width - advanced.ClientSize.Width;
            int frameHeight = advanced.Height - advanced.ClientSize.Height;
            double scale = advanced.DeviceDpi / 96d;
            int width = Math.Min((int)Math.Ceiling(650 * scale * advanced.Font.Size / 9),
                workingArea.Width - frameWidth);
            if (advanced.ClientSize.Width != width)
                advanced.ClientSize = new Size(width, advanced.ClientSize.Height);
            advanced.PerformLayout();
            int height = SettingsContentHeight(settingsTabs);
            int availableHeight = workingArea.Height - frameHeight;
            if (height > availableHeight)
                height = SettingsContentHeight(settingsTabs, selectedOnly: true);
            var page = settingsTabs.SelectedTab!;
            if (page.Controls.OfType<TabControl>().FirstOrDefault() is { } nested)
                page = nested.SelectedTab!;
            page.AutoScroll = height > availableHeight;
            if (!page.AutoScroll)
                page.AutoScrollPosition = Point.Empty;
            height = Math.Min(height, availableHeight);
            if (advanced.ClientSize.Height != height)
                advanced.ClientSize = new Size(advanced.ClientSize.Width, height);
            if (advanced.Visible)
                advanced.Location = new Point(
                    Math.Clamp(advanced.Left, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - advanced.Width)),
                    Math.Clamp(advanced.Top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - advanced.Height)));
        }
        finally { sizingSettings = false; }
    }

    private static int SettingsContentHeight(TabControl tabs, bool selectedOnly = false)
    {
        tabs.PerformLayout();
        int height = 0;
        foreach (TabPage page in tabs.TabPages)
        {
            if (selectedOnly && page != tabs.SelectedTab)
                continue;
            page.PerformLayout();
            int contentHeight;
            if (page.Controls.OfType<TabControl>().FirstOrDefault() is { } nested)
                contentHeight = SettingsContentHeight(nested, selectedOnly);
            else
            {
                var panel = page.Controls.OfType<TableLayoutPanel>().Single();
                int width = Math.Max(1, tabs.DisplayRectangle.Width - page.Padding.Horizontal);
                panel.Width = width;
                panel.PerformLayout();
                contentHeight = panel.GetPreferredSize(new Size(width, 0)).Height;
            }
            height = Math.Max(height, contentHeight + page.Padding.Vertical);
        }
        return height + tabs.Height - tabs.DisplayRectangle.Height;
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
        trayMenu.Items.Add(trayAllInputsHidden);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("&Quit", null, (_, _) => ExitApplication());
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
        RefreshStartupSetting();
        UpdateControls();
        settingsTabs.SelectedIndex = tab;
        advanced.Show(this);
        SizeSettingsWindow();
        var area = Screen.FromControl(this).WorkingArea;
        advanced.Location = new Point(
            Math.Clamp(Left + (Width - advanced.Width) / 2, area.Left, Math.Max(area.Left, area.Right - advanced.Width)),
            Math.Clamp(Top + (Height - advanced.Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - advanced.Height)));
        advanced.Activate();
    }

    private void PopulateSettingsMenu(ToolStripItemCollection items, ToolStripMenuItem update)
    {
        items.Add("&General...", null, (_, _) => ShowAdvanced(0));
        items.Add("&Inputs...", null, (_, _) => ShowAdvanced(1));
        items.Add("&Advanced...", null, (_, _) => ShowAdvanced(2));
        items.Add(new ToolStripSeparator());
        update.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        items.Add(update);
        items.Add("About Odyssey...", null, (_, _) => ShowAbout());
        items.Add(new ToolStripSeparator());
        items.Add("E&xit", null, (_, _) => ExitApplication());
    }

    private void ShowAbout()
    {
        ShowMainWindow();
        MessageBox.Show(this,
            "Odyssey Input Switcher\r\n" +
            $"Version: {ReleaseUpdater.FormatVersion(ReleaseUpdater.CurrentVersion)}\r\n" +
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
            runAtStartup.Checked = StartupRegistration.IsEnabled();
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
            MessageBox.Show(advanced.Visible ? advanced : this, error.Message,
                "Odyssey settings failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void ExitApplication()
    {
        exiting = true;
        updateCancellation.Cancel();
        updateTimer.Stop();
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
        settingsUpdate.Enabled = !busy && !confirming && !exiting && !checkingForUpdates;
        runAtStartup.Enabled = !busy && !exiting;
        diagnostics.Enabled = selected;
        save.Enabled = restore.Enabled = !busy && !exiting;
        confirmSwitch.Enabled = !busy && !exiting;
        clearDefault.Enabled = !busy && preferences.DefaultMonitorId is not null;
        saveDefault.Enabled = selected && monitor?.StableId is not null &&
            MonitorSelection.FindUnique(CurrentIdentities(), monitor.StableId) == monitors.SelectedIndex;
        defaultLabel.Text = monitor is null ? "" : monitors.Items.Count == 1 ? "Selected automatically"
            : monitor.StableId is null ? "Select manually after display changes."
            : MonitorSelection.FindUnique(CurrentIdentities(), preferences.DefaultMonitorId) == monitors.SelectedIndex
                ? "Your default monitor" : "Save this monitor as your default in Settings > General.";
        settingsMonitor.Text = $"Selected: {monitor?.DisplayName ?? "none"}\r\n" +
            $"Saved default: {preferences.DefaultMonitorId ?? "none"}";
        trayMonitor.Text = monitor is null ? "No monitor selected" : monitor.DisplayName;
        string trayText = monitor is null ? "Odyssey - no monitor selected" : "Odyssey - " + monitor.DisplayName;
        tray.Text = trayText[..Math.Min(63, trayText.Length)];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is null)
                continue;
            values[i].Enabled = !busy;
            bool enabled = selected && visibleInputs[i] && InputValue.TryParse(values[i].Text, out _);
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

    internal async Task CheckForUpdatesIfDueAsync()
    {
        if (busy || confirming || exiting || checkingForUpdates || !updateSchedule.IsDue)
            return;
        checkingForUpdates = true;
        updateSchedule.CheckStarted();
        UpdateControls();
        try
        {
            using var updater = createUpdater();
            var update = await updater.CheckAsync(updateCancellation.Token);
            if (!exiting)
            {
                if (update is not null)
                    NotifyUpdateAvailable(update);
                else
                    Append($"No newer stable release. Installed version: {ReleaseUpdater.FormatVersion(ReleaseUpdater.CurrentVersion)}.");
            }
        }
        catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
        {
            if (!resourcesDisposed)
                Append("Update canceled because the app is exiting.");
        }
        catch (Exception error)
        {
            if (!resourcesDisposed)
            {
                Append("Automatic update check failed: " + error);
                if (!exiting)
                    tray.ShowBalloonTip(5000, "Update check failed",
                        error.Message + " Use Check for updates to retry.", ToolTipIcon.Warning);
            }
        }
        finally
        {
            checkingForUpdates = false;
            UpdateControls();
        }
    }

    private void NotifyUpdateAvailable(ReleaseUpdate update)
    {
        Append($"Version {ReleaseUpdater.FormatVersion(update.Version)} is available. Open Settings > Check for updates to upgrade.");
        tray.ShowBalloonTip(5000, "Odyssey update available",
            $"Version {ReleaseUpdater.FormatVersion(update.Version)} is available. Open Settings > Check for updates.", ToolTipIcon.Info);
    }

    internal async Task CheckForUpdatesAsync(bool manual)
    {
        if (busy || confirming || exiting || checkingForUpdates)
            return;
        checkingForUpdates = true;
        updateSchedule.CheckStarted();
        try
        {
            await PerformUpdateCheckAsync(manual);
        }
        finally
        {
            checkingForUpdates = false;
            UpdateControls();
        }
    }

    private Task PerformUpdateCheckAsync(bool manual) =>
        RunAsync(async () =>
        {
            string? stagedDirectory = null;
            bool installerStarted = false;
            bool approved = false;
            try
            {
                SetStatus("Checking for updates...");
                using var updater = createUpdater();
                var update = await updater.CheckAsync(updateCancellation.Token);
                if (exiting)
                    return;
                if (update is null)
                {
                    Append($"No newer stable release. Installed version: {ReleaseUpdater.FormatVersion(ReleaseUpdater.CurrentVersion)}.");
                    SetStatus("No newer stable release is available.");
                    if (manual)
                        MessageBox.Show(this, "No newer stable release is available.",
                            "Odyssey updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (startInTray && !manual)
                {
                    NotifyUpdateAvailable(update);
                    return;
                }
                ShowMainWindow();
                if (MessageBox.Show(this,
                    $"Version {ReleaseUpdater.FormatVersion(update.Version)} is available (installed: {ReleaseUpdater.FormatVersion(ReleaseUpdater.CurrentVersion)}).\r\n\r\n" +
                    "Download and install it now? Odyssey will close and restart. Your settings will be kept.",
                    "Odyssey update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes || exiting)
                {
                    SetStatus("Update declined. Use Settings > Check for updates... to check again.");
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
                Append($"Installing version {ReleaseUpdater.FormatVersion(update.Version)}; restarting Odyssey.");
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

    private void Append(string message) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
    private void SetStatus(string message)
    {
        status.Text = message;
        status.Visible = message.Length != 0;
    }

    private void ReportFailure(Exception error, bool showDialog = true)
    {
        SetStatus("Operation failed. Open Settings > Advanced > Diagnostics for details.");
        Append(error.ToString());
        tray.ShowBalloonTip(5000, "Odyssey operation failed", error.Message, ToolTipIcon.Error);
        if (showDialog)
            MessageBox.Show(advanced.Visible ? advanced : Visible ? this : null, error.Message +
                "\r\n\r\nSee Settings > Advanced > Diagnostics for details. The DDC/CI connection may be unavailable.",
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
                : result.failed ? "Some reads failed. See Settings > Advanced > Diagnostics for details."
                : "Diagnostics complete. No input commands sent.");
        });
    }

    private async Task SwitchAsync(int index)
    {
        NativeMonitor? selected = SelectedMonitor;
        if (!visibleInputs[index] || busy || confirming || exiting || selected is null ||
            !InputValue.TryParse(values[index].Text, out uint value))
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
                var confirmation = ConfirmInputSwitch(selected, index, value);
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
        NativeMonitor monitor, int index, uint value)
    {
        var (dialog, suppress) = CreateInputConfirmation(monitor.DisplayName, index, value);
        using (dialog)
        {
            DialogResult result = dialog.ShowDialog(Visible ? this : null);
            return (result, suppress.Checked);
        }
    }

    internal (Form Dialog, CheckBox Suppress) CreateInputConfirmation(
        string displayName, int index, uint value)
    {
        string inputName = displayNames[index];
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
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(new Label
        {
            Text = $"Switch to {inputName}?",
            UseMnemonic = false,
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Fill,
            MaximumSize = new Size((int)Math.Ceiling(400 * Font.SizeInPoints / 10), 0),
            Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        content.Controls.Add(new Label
        {
            Text = $"{displayName}\r\nVCP 0x60 = 0x{value:X4}\r\n\r\n" +
                "Switching may disconnect this PC. Use the monitor's Source control to return if needed.",
            AutoSize = true,
            Dock = DockStyle.Fill,
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
        dialog.Layout += (_, _) =>
        {
            int width = content.GetPreferredSize(Size.Empty).Width + dialog.Width - dialog.ClientSize.Width;
            if (dialog.MinimumSize.Width != width)
                dialog.MinimumSize = new Size(width, 0);
        };
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

    internal void LoadInputNames(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(path))
                ?? throw new InvalidOperationException("Saved input names are empty.");
            ApplyInputNames(InputDisplayNames.Resolve(stored));
            Append("Loaded input names from " + path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException)
        {
            ReportSettingsFailure(error);
        }
    }

    private void ApplyInputNames(Dictionary<string, string> names)
    {
        for (int i = 0; i < inputNames.Length; i++)
        {
            displayNames[i] = names[inputNames[i]];
            nameFields[i].Text = displayNames[i];
            switches[i].Text = displayNames[i];
            switches[i].AccessibleName = "Switch to " + displayNames[i];
            DescribeButton(switches[i], "Switch the selected monitor to " + displayNames[i] +
                " (" + inputNames[i] + ").");
            trayInputs[i].Text = displayNames[i].Replace("&", "&&");
            trayInputs[i].AccessibleName = "Switch to " + displayNames[i];
        }
        SizeInputButtons((TableLayoutPanel)switches[0].Parent!);
    }

    internal void SaveInputNames(string path)
    {
        try
        {
            var edited = new Dictionary<string, string?>();
            for (int i = 0; i < inputNames.Length; i++)
                edited[inputNames[i]] = nameFields[i].Text;
            var names = InputDisplayNames.Resolve(edited);
            WriteSettings(path, names);
            ApplyInputNames(names);
            Append("Saved input names to " + path);
            SetStatus("Input names saved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportSettingsFailure(error);
        }
    }

    internal void LoadInputVisibility(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var stored = JsonSerializer.Deserialize<Dictionary<string, bool?>>(File.ReadAllText(path))
                ?? throw new InvalidOperationException("Saved input visibility is empty.");
            ApplyInputVisibility(InputVisibility.Resolve(stored));
            Append("Loaded input visibility from " + path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException)
        {
            ReportSettingsFailure(error);
        }
    }

    private void ApplyInputVisibility(Dictionary<string, bool> visibility)
    {
        var inputs = (TableLayoutPanel)switches[0].Parent!;
        inputs.SuspendLayout();
        try
        {
            int count = visibility.Values.Count(visible => visible);
            for (int i = 0; i < inputNames.Length; i++)
            {
                bool visible = visibility[inputNames[i]];
                visibleInputs[i] = visibilityFields[i].Checked = visible;
                switches[i].Visible = trayInputs[i].Visible = visible;
                var column = inputs.ColumnStyles[i];
                bool fill = visible || (count == 0 && i == 0);
                column.SizeType = fill ? SizeType.Percent : SizeType.Absolute;
                column.Width = fill ? 100f / Math.Max(1, count) : 0;
            }
            allInputsHidden.Visible = count == 0;
            trayAllInputsHidden.Visible = count == 0;
            SizeInputButtons(inputs);
        }
        finally { inputs.ResumeLayout(performLayout: true); }
        UpdateControls();
    }

    internal void SaveInputVisibility(string path)
    {
        try
        {
            var visibility = new Dictionary<string, bool>();
            for (int i = 0; i < inputNames.Length; i++)
                visibility[inputNames[i]] = visibilityFields[i].Checked;
            WriteSettings(path, visibility);
            ApplyInputVisibility(visibility);
            Append("Saved input visibility to " + path);
            SetStatus("Input visibility saved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportSettingsFailure(error);
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            updateTimer.Dispose();
            ReleaseInventory(logMessages: false);
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            advanced.Dispose();
            buttonHelp.Dispose();
            Icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
