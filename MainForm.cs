using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace OdysseyInputSwitcher;

internal sealed class MainForm : Form
{
    internal const string AppName = "Connected Display Input Selector";
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
    private readonly Button clearDefault = MakeButton("Clear default monitor");
    private readonly CheckBox confirmSwitch = new()
    {
        Text = "Ask before switching inputs", AutoSize = true, Checked = true
    };
    private readonly Label status = new()
    {
        AutoSize = true, Visible = false, ForeColor = Color.FromArgb(90, 103, 122)
    };
    private readonly Label errorStatus = new()
    {
        AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.FromArgb(150, 45, 35),
        AccessibleName = "Latest application error"
    };
    private readonly TableLayoutPanel errorPanel = new()
    {
        AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Visible = false
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
    private string[] inputNames = ["DisplayPort", "HDMI", "Micro HDMI"];
    private string[] displayNames = ["DisplayPort", "HDMI", "Micro HDMI"];
    private readonly TextBox[] nameFields = new TextBox[3];
    private bool[] visibleInputs = [true, true, true];
    private readonly CheckBox[] visibilityFields = new CheckBox[3];
    private readonly Label allInputsHidden = new()
    {
        Text = "All inputs are hidden. Open Settings > Inputs... to show them again.",
        AutoSize = true, Dock = DockStyle.Fill, Visible = false
    };
    private readonly TextBox[] values = new TextBox[3];
    private Button[] switches = new Button[3];
    private ToolStripMenuItem[] trayInputs = new ToolStripMenuItem[3];
    private TableLayoutPanel mainInputs = null!;
    private readonly DataGridView profileGrid = new()
    {
        Dock = DockStyle.Top, Height = 180, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersVisible = true,
        AccessibleName = "Selected monitor input configuration"
    };
    private readonly Label profileExplanation = new() { AutoSize = true, Dock = DockStyle.Fill };
    private Dictionary<string, MonitorProfile> profiles = new(StringComparer.OrdinalIgnoreCase);
    private MonitorProfile? activeProfile;
    private MonitorProfile? legacyProfile;
    private NativeMonitor? profileMonitor;
    private string? profileIdentity;
    private bool profileStoreAvailable = true;
    private bool preferencesStoreAvailable = true;
    private bool profileEditorReady;
    private bool defaultSetupPending;
    private readonly Dictionary<NativeMonitor, MonitorProfileDiscovery> connectionProfiles = [];
    private readonly HashSet<string> offeredMonitors = new(StringComparer.OrdinalIgnoreCase);
    private string ProfilesPath => Path.Combine(SettingsDirectory, "monitor-profiles.json");
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
    private readonly string windowPositionPath;
    private WindowLocation? normalLocation;
    private bool placementLoaded;
    private bool positioningWindow;
    private bool hasUserPlacement;
    private int displayGeneration;
    private string SettingsDirectory { get; }
    private string SettingsPath => Path.Combine(SettingsDirectory, "inputs.json");
    private string PreferencesPath => Path.Combine(SettingsDirectory, "preferences.json");
    private string InputNamesPath => Path.Combine(SettingsDirectory, "input-names.json");
    private string InputVisibilityPath => Path.Combine(SettingsDirectory, "input-visibility.json");
    private NativeMonitor? SelectedMonitor => !stale ? monitors.SelectedItem as NativeMonitor : null;
    internal event EventHandler? InitializationCompleted;

    internal MainForm(bool startInTray = false, TimeProvider? clock = null,
        Func<ReleaseUpdater>? updaterFactory = null, string? windowPositionPath = null,
        string? settingsDirectory = null)
    {
        this.startInTray = startInTray;
        SettingsDirectory = settingsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OdysseyInputSwitcher");
        this.windowPositionPath = windowPositionPath ?? Path.Combine(SettingsDirectory, "window-position.json");
        updateSchedule = new UpdateCheckSchedule(clock ?? TimeProvider.System);
        createUpdater = updaterFactory ?? (() => new ReleaseUpdater());
        Text = AppName;
        StartPosition = FormStartPosition.Manual;
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
        monitors.SelectedIndexChanged += async (_, _) =>
        {
            if (resourcesDisposed)
                return;
            UpdateControls();
            if (!busy && !stale)
            {
                SetSelectionStatus();
                await PrepareMonitorProfileAsync();
            }
        };
        refresh.Click += (_, _) => QueueRefresh("Manual refresh");
        saveDefault.Click += (_, _) => SaveDefault();
        clearDefault.Click += (_, _) => SaveDefault(clear: true);
        diagnostics.Click += async (_, _) => await DiagnosticsAsync();
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
        refreshTimer.Tick += async (_, _) =>
        {
            if (busy || confirming || exiting)
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
        ResizeEnd += (_, _) => SaveWindowPosition();
        VisibleChanged += (_, _) =>
        {
            if (!Visible)
                SaveWindowPosition();
        };
        FormClosing += (_, e) =>
        {
            SaveWindowPosition();
            if (!exiting)
            {
                e.Cancel = true;
                advanced.Hide();
                Hide();
                if (!explainedTray)
                {
                    explainedTray = true;
                    tray.ShowBalloonTip(4000, AppName + " is still running",
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
        Layout += (_, _) =>
        {
            SetScaledMinimumSize();
            RestoreWindowPosition();
        };
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
        if (!placementLoaded)
        {
            try
            {
                if (File.Exists(windowPositionPath))
                {
                    normalLocation = JsonSerializer.Deserialize<WindowLocation>(File.ReadAllText(windowPositionPath))
                        ?? throw new InvalidOperationException("Saved main-window position is empty.");
                    hasUserPlacement = true;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                JsonException or InvalidOperationException)
            {
                ReportSettingsFailure(error);
            }
            placementLoaded = true;
        }
        RestoreWindowPosition();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RestoreWindowPosition();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (placementLoaded && !positioningWindow && Visible && WindowState == FormWindowState.Normal)
        {
            normalLocation = new WindowLocation { X = Left, Y = Top };
            hasUserPlacement = true;
        }
    }

    private void RestoreWindowPosition()
    {
        if (!placementLoaded || positioningWindow || resourcesDisposed || WindowState != FormWindowState.Normal)
            return;
        positioningWindow = true;
        try
        {
            var areas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
            var primary = (Screen.PrimaryScreen ?? Screen.FromControl(this)).WorkingArea;
            Location = WindowPlacement.Resolve(hasUserPlacement ? normalLocation : null, Size, areas, primary);
            normalLocation = new WindowLocation { X = Left, Y = Top };
        }
        finally { positioningWindow = false; }
    }

    private void SaveWindowPosition()
    {
        if (!placementLoaded || normalLocation is null || resourcesDisposed || positioningWindow)
            return;
        try
        {
            WriteSettings(windowPositionPath, normalLocation);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportSettingsFailure(error);
        }
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
            if (File.Exists(SettingsPath) || File.Exists(InputNamesPath) || File.Exists(InputVisibilityPath))
                legacyProfile = MonitorProfiles.FromLegacy(
                    inputNames.Select((name, i) => (name, i)).ToDictionary(entry => entry.name, entry => values[entry.i].Text),
                    inputNames.Select((name, i) => (name, i)).ToDictionary(entry => entry.name, entry => displayNames[entry.i]),
                    inputNames.Select((name, i) => (name, i)).ToDictionary(entry => entry.name, entry => visibleInputs[entry.i]));
            try
            {
                profiles = MonitorProfiles.Load(ProfilesPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                JsonException or InvalidOperationException)
            {
                profileStoreAvailable = false;
                ReportSettingsFailure(error);
            }
            LoadPreferences();
            RefreshStartupSetting();
            InitializationCompleted?.Invoke(this, EventArgs.Empty);
            updateTimer.Start();
            await RefreshAsync();
            await CheckForUpdatesAsync(manual: false);
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
        RestoreWindowPosition();
    }

    private void SetScaledMinimumSize()
    {
        // ClientSize excludes visible scrollbars as well as the native window frame.
        MinimumSize = new Size((int)Math.Ceiling(440 * DeviceDpi / 96d) + Width - ClientSize.Width,
            SizeFromClientSize(Size.Empty).Height);
        if (mainInputs is not null)
            SizeInputButtons(mainInputs);
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
        mainInputs = inputs;
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
        status.Dock = DockStyle.Fill;
        status.Margin = new Padding(0, 6, 0, 0);
        layout.Controls.Add(status, 0, 5);
        errorPanel.Controls.Add(errorStatus, 0, 0);
        var errorActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        var details = MakeButton("Details...");
        DescribeButton(details, "Open Diagnostics to see the technical details of the error.");
        details.Click += (_, _) => ShowAdvanced(2);
        var dismiss = MakeButton("Dismiss");
        DescribeButton(dismiss, "Hide this error message. Technical details remain in Diagnostics.");
        dismiss.Click += (_, _) => errorPanel.Visible = false;
        errorActions.Controls.AddRange([details, dismiss]);
        errorPanel.Controls.Add(errorActions, 0, 1);
        errorPanel.Layout += (_, _) =>
            errorStatus.MaximumSize = new Size(Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
        layout.Controls.Add(errorPanel, 0, 6);
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
        advanced.Text = AppName + " - Settings";
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

        for (int i = 0; i < inputNames.Length; i++)
        {
            values[i] = new TextBox { Dock = DockStyle.Fill, AccessibleName = inputNames[i] + " write value" };
            values[i].TextChanged += (_, _) => UpdateControls();
        }

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
        tray.Text = AppName;
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowMainWindow();
        tray.Visible = true;
    }

    private void ShowMainWindow()
    {
        startInTray = false;
        WindowState = FormWindowState.Normal;
        RestoreWindowPosition();
        Show();
        Activate();
        PresentMonitorSetup();
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
        items.Add("About " + AppName + "...", null, (_, _) => ShowAbout());
        items.Add(new ToolStripSeparator());
        items.Add("E&xit", null, (_, _) => ExitApplication());
    }

    private void ShowAbout()
    {
        ShowMainWindow();
        MessageBox.Show(this,
            AppName + "\r\n" +
            $"Version: {ReleaseUpdater.FormatVersion(ReleaseUpdater.CurrentVersion)}\r\n" +
            "Creator: Jeffrey Strasser (jeffstrasser)\r\n" +
            $"Architecture: {RuntimeInformation.ProcessArchitecture}\r\n\r\n" +
            "Switch monitor inputs using Windows DDC/CI.\r\n\r\n" +
            ReleaseUpdater.RepositoryUrl,
            "About " + AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        ReportError(error, ErrorContext.Settings, "Settings failed");
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
        profileGrid.Enabled = !busy && !confirming && !exiting && monitor is not null;
        if (profileEditorReady)
            foreach (var button in settingsTabs.TabPages[1].Controls.OfType<TableLayoutPanel>()
                .SelectMany(panel => panel.Controls.OfType<TableLayoutPanel>())
                .SelectMany(panel => panel.Controls.OfType<Button>()))
                button.Enabled = !busy && !confirming && !exiting && monitor is not null;
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
        string trayText = AppName + " - " + (monitor?.DisplayName ?? "no monitor selected");
        tray.Text = trayText[..Math.Min(63, trayText.Length)];
        for (int i = 0; i < switches.Length; i++)
        {
            if (activeProfile is null && values[i] is null)
                continue;
            if (activeProfile is null)
                values[i].Enabled = !busy;
            bool enabled = selected && visibleInputs[i] && InputValue.TryParse(ConfiguredValue(i), out _) &&
                (activeProfile is null || profileMonitor == monitor);
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
                if (!exiting)
                    ReportError(error, ErrorContext.Update, "Automatic update check failed", showDialog: false);
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
        tray.ShowBalloonTip(5000, AppName + " update available",
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
                            AppName + " updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    "Download and install it now? The app will close and restart. Your settings will be kept.",
                    AppName + " update available", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
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
                Append($"Installing version {ReleaseUpdater.FormatVersion(update.Version)}; restarting {AppName}.");
                ExitApplication();
            }
            catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested)
            {
                Append("Update canceled because the app is exiting.");
            }
            catch (Exception error)
            {
                SetStatus("Update failed. Your installed version has not been changed.");
                if (!exiting)
                    ReportError(error, ErrorContext.Update, "Update failed", manual || approved);
            }
            finally
            {
                if (stagedDirectory is not null && !installerStarted)
                {
                    try { Directory.Delete(stagedDirectory, recursive: true); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        if (!exiting)
                            ReportError(error, ErrorContext.Cleanup, "Update staging cleanup failed");
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
        ReportError(error, ErrorContext.Monitor, "Monitor operation failed", showDialog);
    }

    internal void ReportError(Exception error, ErrorContext context, string diagnosticContext,
        bool showDialog = true)
    {
        Append(diagnosticContext + ": " + error);
        errorStatus.Text = AppErrors.Describe(error, context) + "\r\nDetails are available in Settings > Advanced > Diagnostics.";
        errorPanel.Visible = true;
        if (showDialog && !startInTray && (Visible || advanced.Visible))
            MessageBox.Show(advanced.Visible ? advanced : this, errorStatus.Text,
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        connectionProfiles.Clear();
        profileMonitor = null;
        monitors.Items.Clear();
        if (inventory is null)
            return;
        inventory.Dispose();
        if (logMessages)
            foreach (string message in inventory.Messages)
                Append(message);
        inventory = null;
    }

    private async Task RefreshAsync()
    {
        await RunAsync(async () =>
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
        var connected = inventory.Monitors.ToArray();
        var identities = CurrentIdentities();
        var discovered = await Task.Run(() => connected.Select((monitor, index) =>
            MonitorProfiles.GetOrDiscover(monitor, UniqueIdentity(monitor, index, identities), profiles)).ToArray());
        if (exiting || generation != displayGeneration)
        {
            QueueRefresh("Displays changed during input discovery");
            return;
        }
        var updatedProfiles = new Dictionary<string, MonitorProfile>(profiles, StringComparer.OrdinalIgnoreCase);
        bool newMonitor = false;
        for (int index = 0; index < connected.Length; index++)
        {
            var monitor = connected[index];
            var discovery = discovered[index];
            connectionProfiles[monitor] = discovery;
            string? identity = UniqueIdentity(monitor, index, identities);
            string offerKey = identity ?? monitor.DisplayName;
            if (!discovery.Remembered && !offeredMonitors.Contains(offerKey))
                newMonitor = true;
            if (discovery.Error is not null)
                Append("Input discovery unavailable for " + monitor.Description + ": " + discovery.Error);
            else if (!discovery.Remembered && identity is not null)
                updatedProfiles[identity] = discovery.Profile;
        }
        if (updatedProfiles.Count != profiles.Count && profileStoreAvailable)
        {
            try
            {
                WriteSettings(ProfilesPath, updatedProfiles);
                profiles = updatedProfiles;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ReportSettingsFailure(error);
            }
        }
        defaultSetupPending = connected.Length != 0 && preferences.DefaultMonitorId is null &&
            (defaultSetupPending || !preferences.MonitorSetupCompleted || newMonitor);
        monitors.SelectedIndex = MonitorSelection.Choose(CurrentIdentities(), preferences.DefaultMonitorId);
        SetSelectionStatus();
        Append($"Enumerated {inventory.Monitors.Count} physical monitor(s). No input commands sent.");
        }, showErrors: false);
        if (!exiting && !stale)
        {
            await PrepareMonitorProfileAsync();
            PresentMonitorSetup();
        }
    }

    private void SetSelectionStatus()
    {
        SetStatus(monitors.Items.Count == 0 ? "No connected physical monitor found."
            : SelectedMonitor is null ? "Choose a monitor. No unique saved default is connected."
            : "");
    }

    private string ConfiguredValue(int index) =>
        activeProfile is null ? values[index].Text : activeProfile.Inputs[index].Value;

    private static string? UniqueIdentity(NativeMonitor monitor, int index, IReadOnlyList<string?> identities) =>
        monitor.StableId is not null && MonitorSelection.FindUnique(identities, monitor.StableId) == index
            ? monitor.StableId : null;

    private Task PrepareMonitorProfileAsync()
    {
        NativeMonitor? selected = SelectedMonitor;
        if (selected is null || selected == profileMonitor || busy || confirming || exiting)
            return Task.CompletedTask;
        string? identity = UniqueIdentity(selected, monitors.SelectedIndex, CurrentIdentities());
        var profile = identity is not null && profiles.TryGetValue(identity, out var saved)
            ? saved : connectionProfiles.TryGetValue(selected, out var discovery)
                ? discovery.Profile : new MonitorProfile();
        RenderMonitorProfile(profile, identity, selected);
        if (profile.Inputs.Count == 0)
        {
            profileExplanation.Text = "We couldn't find input settings for this monitor. Add input names and values from " +
                "its documentation or a known-working configuration, then choose Save monitor inputs.\r\n" +
                "Don't guess input values. Details are available in Diagnostics.";
            SetStatus("This monitor needs input settings. Open Settings > Inputs... to add them.");
        }
        return Task.CompletedTask;
    }

    private void PresentMonitorSetup()
    {
        if (!defaultSetupPending || busy || confirming || exiting || stale || inventory is null ||
            preferences.DefaultMonitorId is not null || !Visible || startInTray)
            return;
        int generation = displayGeneration;
        var connected = inventory.Monitors.ToArray();
        var identities = CurrentIdentities();
        var options = connected.Select((monitor, index) => new MonitorSetupOption(monitor.DisplayName,
            UniqueIdentity(monitor, index, identities), connectionProfiles[monitor])).ToArray();
        confirming = true;
        UpdateControls();
        DialogResult answer;
        int selectedIndex;
        using (var dialog = new MonitorSetupDialog(options, Font, Icon))
        {
            try
            {
                answer = dialog.ShowDialog(this);
                selectedIndex = dialog.SelectedMonitorIndex;
            }
            finally
            {
                confirming = false;
                UpdateControls();
                if (closeWhenIdle)
                    Close();
            }
        }
        if (exiting || stale || generation != displayGeneration)
            return;
        CompleteMonitorSetup(options, answer, selectedIndex);
    }

    internal void CompleteMonitorSetup(IReadOnlyList<MonitorSetupOption> options, DialogResult answer,
        int selectedIndex)
    {
        defaultSetupPending = false;
        foreach (var option in options)
            offeredMonitors.Add(option.Identity ?? option.Name);
        try
        {
            var updated = new Preferences
            {
                DefaultMonitorId = preferences.DefaultMonitorId,
                SuppressSwitchConfirmation = preferences.SuppressSwitchConfirmation,
                MonitorSetupCompleted = true
            };
            WritePreferences(updated);
            preferences = updated;
            if (selectedIndex >= 0 && answer is DialogResult.OK or DialogResult.Retry)
            {
                monitors.SelectedIndex = selectedIndex;
                if (answer == DialogResult.OK)
                    SaveDefault();
                else
                    ShowAdvanced(1);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportSettingsFailure(error);
        }
    }

    internal void RenderMonitorProfile(MonitorProfile profile, string? identity, NativeMonitor? monitor)
    {
        MonitorProfiles.Validate(profile);
        activeProfile = profile;
        profileMonitor = monitor;
        profileIdentity = identity;
        BuildProfileEditor();
        mainInputs.SuspendLayout();
        try
        {
            foreach (var button in switches)
            {
                mainInputs.Controls.Remove(button);
                button.Dispose();
            }
            foreach (var item in trayInputs)
            {
                trayMenu.Items.Remove(item);
                item.Dispose();
            }
            inputNames = profile.Inputs.Select(input => input.Id).ToArray();
            displayNames = profile.Inputs.Select(input => input.Name).ToArray();
            visibleInputs = profile.Inputs.Select(input => input.Visible).ToArray();
            switches = new Button[profile.Inputs.Count];
            trayInputs = new ToolStripMenuItem[profile.Inputs.Count];
            mainInputs.SetColumnSpan(allInputsHidden, 1);
            mainInputs.ColumnStyles.Clear();
            mainInputs.ColumnCount = Math.Max(1, profile.Inputs.Count);
            int visibleCount = visibleInputs.Count(value => value);
            for (int i = 0; i < switches.Length; i++)
            {
                int index = i;
                bool fill = visibleInputs[i] || (visibleCount == 0 && i == 0);
                mainInputs.ColumnStyles.Add(new ColumnStyle(fill ? SizeType.Percent : SizeType.Absolute,
                    fill ? 100f / Math.Max(1, visibleCount) : 0));
                var button = MakeButton(displayNames[i], primary: true);
                button.UseMnemonic = false;
                button.AccessibleName = "Switch to " + displayNames[i];
                button.Dock = DockStyle.Fill;
                button.MinimumSize = new Size(0, 36);
                DescribeButton(button, "Switch the selected monitor to " + displayNames[i] + ".");
                button.Click += async (_, _) => await SwitchAsync(index);
                button.FontChanged += (_, _) => SizeInputButtons(mainInputs);
                switches[i] = button;
                mainInputs.Controls.Add(button, i, 0);
                button.Visible = visibleInputs[i];
                trayInputs[i] = new ToolStripMenuItem(displayNames[i].Replace("&", "&&"))
                {
                    AccessibleName = "Switch to " + displayNames[i], Visible = visibleInputs[i]
                };
                trayInputs[i].Click += async (_, _) => await SwitchAsync(index);
                trayMenu.Items.Insert(2 + i, trayInputs[i]);
            }
            if (switches.Length == 0)
                mainInputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mainInputs.SetColumnSpan(allInputsHidden, mainInputs.ColumnCount);
            allInputsHidden.Text = switches.Length == 0
                ? "No inputs configured. Open Settings > Inputs... to add them."
                : "All inputs are hidden. Open Settings > Inputs... to show them again.";
            trayAllInputsHidden.Text = switches.Length == 0
                ? "No inputs configured - Settings > Inputs..." : "All inputs hidden - Settings > Inputs...";
            allInputsHidden.Visible = trayAllInputsHidden.Visible = visibleCount == 0;
            SizeInputButtons(mainInputs);
        }
        finally { mainInputs.ResumeLayout(performLayout: true); }
        profileGrid.Rows.Clear();
        foreach (var input in profile.Inputs)
        {
            int row = profileGrid.Rows.Add(input.Name, input.Value, input.Visible);
            profileGrid.Rows[row].Tag = input.Id;
        }
        profileExplanation.Text = "Edit names, VCP 0x60 values (decimal or 0x hex), and Show for this monitor.\r\n" +
            "Use the blank row to add inputs; select a row header and press Delete to remove it.\r\n" +
            "Save monitor inputs applies all changes. Blank values disable switching. Saving does not send commands." +
            (identity is null ? "\r\nNo unique monitor identity: these settings last only for this connection." : "");
        UpdateControls();
        SizeSettingsWindow();
    }

    private void BuildProfileEditor()
    {
        if (profileEditorReady)
            return;
        profileEditorReady = true;
        var namesPage = settingsTabs.TabPages[1];
        foreach (Control control in namesPage.Controls.Cast<Control>().ToArray())
            control.Dispose();
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 3
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(profileExplanation, 0, 0);
        profileGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "InputName", HeaderText = "Input name", MaxInputLength = InputDisplayNames.MaximumLength
        });
        profileGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "VCP 0x60 value" });
        profileGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "ShowInput", HeaderText = "Show" });
        profileGrid.DefaultValuesNeeded += (_, e) => e.Row.Cells[2].Value = true;
        panel.Controls.Add(profileGrid, 0, 1);
        var actions = MakeSettingsActions();
        var saveProfile = MakeButton("Save monitor inputs");
        saveProfile.Click += (_, _) => SaveMonitorProfile();
        AddSettingsAction(actions, saveProfile, "Save names, visibility and values for this monitor. Does not switch inputs.");
        if (legacyProfile is not null)
        {
            var import = MakeButton("Use previous settings");
            import.Click += (_, _) => UseLegacyProfile(usePreset: false);
            AddSettingsAction(actions, import,
                "Import your previous configuration for this selected monitor only. Choose Save monitor inputs to keep it.");
        }
        var preset = MakeButton("Use working Odyssey values");
        preset.Click += (_, _) => UseLegacyProfile(usePreset: true);
        AddSettingsAction(actions, preset,
            "Only for the tested Odyssey setup: fill DisplayPort 0x0F, HDMI 0x05 and Micro HDMI 0x06. Save to keep them.");
        panel.Controls.Add(actions, 0, 2);
        namesPage.Controls.Add(panel);
    }

    private void UseLegacyProfile(bool usePreset)
    {
        if (SelectedMonitor is null || SelectedMonitor != profileMonitor || busy || exiting)
            return;
        var profile = usePreset
            ? MonitorProfiles.FromLegacy(InputSettings.Resolve(null), InputDisplayNames.Resolve(null),
                InputVisibility.Resolve(null))
            : legacyProfile ?? throw new InvalidOperationException("No previous input configuration is available.");
        RenderMonitorProfile(profile, profileIdentity, profileMonitor);
        SetStatus("Configuration imported for this monitor. Choose Save monitor inputs to keep it.");
    }

    internal void SaveMonitorProfile(string? path = null)
    {
        try
        {
            if (activeProfile is null || busy || confirming || exiting || SelectedMonitor is null ||
                SelectedMonitor != profileMonitor)
                throw new InvalidOperationException("Select a connected monitor before saving inputs.");
            if (!profileGrid.EndEdit())
                throw new InvalidOperationException("Finish editing the input configuration before saving.");
            var edited = new MonitorProfile();
            foreach (DataGridViewRow row in profileGrid.Rows)
            {
                if (row.IsNewRow)
                    continue;
                edited.Inputs.Add(new MonitorInput
                {
                    Id = row.Tag as string ?? Guid.NewGuid().ToString("N"),
                    Name = Convert.ToString(row.Cells[0].Value)?.Trim() ?? "",
                    Value = Convert.ToString(row.Cells[1].Value)?.Trim() ?? "",
                    Visible = row.Cells[2].Value is not bool show || show
                });
            }
            MonitorProfiles.Validate(edited);
            if (profileIdentity is not null)
            {
                if (!profileStoreAvailable)
                    throw new InvalidOperationException(
                        "Saved monitor profiles could not be loaded. Repair or back up monitor-profiles.json before saving.");
                var updated = new Dictionary<string, MonitorProfile>(profiles, StringComparer.OrdinalIgnoreCase)
                {
                    [profileIdentity] = edited
                };
                WriteSettings(path ?? ProfilesPath, updated);
                profiles = updated;
            }
            RenderMonitorProfile(edited, profileIdentity, profileMonitor);
            if (profileMonitor is not null)
                connectionProfiles[profileMonitor] = new(edited, true, null);
            SetStatus(profileIdentity is null
                ? "Input configuration applied for this connection only; no unique monitor identity is available."
                : "Monitor inputs saved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportSettingsFailure(error);
        }
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
            !InputValue.TryParse(ConfiguredValue(index), out uint value) ||
            (activeProfile is not null && profileMonitor != selected))
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
                        SuppressSwitchConfirmation = confirmation.SuppressFuture,
                        MonitorSetupCompleted = preferences.MonitorSetupCompleted
                    };
                    WritePreferences(updated);
                    preferences = updated;
                    Append("Saved input confirmation preference.");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ReportSettingsFailure(error);
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
            RestoreWindowPosition();
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
            ReportSettingsFailure(error);
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
        SizeInputButtons(mainInputs);
    }

    internal void SaveInputNames(string path)
    {
        if (activeProfile is not null)
        {
            SaveMonitorProfile();
            return;
        }
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
        var inputs = mainInputs;
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
        if (activeProfile is not null)
        {
            SaveMonitorProfile();
            return;
        }
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
            preferencesStoreAvailable = false;
            ReportSettingsFailure(error);
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

    private void WritePreferences(Preferences updated)
    {
        if (!preferencesStoreAvailable)
            throw new InvalidOperationException(
                "Saved preferences could not be loaded. Back up and repair preferences.json before saving.");
        WriteSettings(PreferencesPath, updated);
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
                SuppressSwitchConfirmation = preferences.SuppressSwitchConfirmation,
                MonitorSetupCompleted = preferences.MonitorSetupCompleted
            };
            WritePreferences(updated);
            preferences = updated;
            Append(clear ? "Cleared the default monitor." : "Saved default monitor interface: " + identity);
            SetStatus(clear ? "Default monitor cleared." : "Default monitor saved.");
            UpdateControls();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportSettingsFailure(error);
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
                SuppressSwitchConfirmation = !confirmSwitch.Checked,
                MonitorSetupCompleted = preferences.MonitorSetupCompleted
            };
            WritePreferences(updated);
            preferences = updated;
            Append(confirmSwitch.Checked
                ? "Enabled input switch confirmations."
                : "Disabled input switch confirmations.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            confirmSwitch.Checked = !preferences.SuppressSwitchConfirmation;
            ReportSettingsFailure(error);
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
            foreach (var value in values)
                value.Dispose();
            buttonHelp.Dispose();
            Icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
