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
    private readonly Button saveDefault = MakeButton("Save as default");
    private readonly Button advancedButton = MakeButton("Advanced...");
    private readonly Button diagnostics = MakeButton("Read monitor diagnostics");
    private readonly Button save = MakeButton("Save input values");
    private readonly Button restore = MakeButton("Use working Odyssey values");
    private readonly Button clearDefault = MakeButton("Clear default monitor");
    private readonly Label status = new() { AutoSize = true, ForeColor = Color.FromArgb(90, 103, 122) };
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
    private int displayGeneration;
    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OdysseyInputSwitcher");
    private static string SettingsPath => Path.Combine(SettingsDirectory, "inputs.json");
    private static string PreferencesPath => Path.Combine(SettingsDirectory, "preferences.json");
    private NativeMonitor? SelectedMonitor => !stale ? monitors.SelectedItem as NativeMonitor : null;

    internal MainForm()
    {
        Text = "Odyssey Input Switcher";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        BackColor = Canvas;
        ForeColor = Ink;
        ClientSize = new Size(550, 440);
        MinimumSize = new Size(550, 480);
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
        clearDefault.Click += (_, _) => SaveDefault(clear: true);
        advancedButton.Click += (_, _) => ShowAdvanced();
        diagnostics.Click += async (_, _) => await DiagnosticsAsync();
        save.Click += (_, _) => SaveSettings();
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
        Shown += (_, _) =>
        {
            initialized = true;
            LoadSettings();
            LoadPreferences();
            QueueRefresh("Startup");
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
                SetStatus("Waiting for the current monitor call before exiting...");
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

    private void BuildMainWindow()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7
        };
        for (int i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "Odyssey", Font = new Font(Font.FontFamily, 25, FontStyle.Bold),
            AutoSize = true, Margin = new Padding(0, 0, 0, 0)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Choose where your monitor gets its picture.",
            AutoSize = true, ForeColor = status.ForeColor, Margin = new Padding(2, 0, 0, 16)
        }, 0, 1);
        var selection = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        monitors.Margin = new Padding(0, 8, 10, 8);
        selection.Controls.Add(monitors, 0, 0);
        selection.Controls.Add(refresh, 1, 0);
        layout.Controls.Add(selection, 0, 2);
        var defaults = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        defaults.Controls.Add(saveDefault);
        defaultLabel.Margin = new Padding(4, 6, 0, 6);
        defaults.Controls.Add(defaultLabel);
        layout.Controls.Add(defaults, 0, 3);
        var inputs = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 70, RowCount = 1, ColumnCount = 3,
            Margin = new Padding(0, 10, 0, 10)
        };
        inputs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < inputNames.Length; i++)
        {
            int index = i;
            inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            switches[i] = MakeButton(inputNames[i], primary: true);
            switches[i].AutoSize = false;
            switches[i].AccessibleName = "Switch to " + inputNames[i];
            switches[i].Dock = DockStyle.Fill;
            switches[i].MinimumSize = new Size(0, 60);
            switches[i].Click += async (_, _) => await SwitchAsync(index);
            inputs.Controls.Add(switches[i], i, 0);
        }
        layout.Controls.Add(inputs, 0, 4);
        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top };
        footer.Controls.Add(advancedButton);
        var hide = MakeButton("Run in tray");
        hide.Click += (_, _) => { advanced.Hide(); Hide(); };
        footer.Controls.Add(hide);
        layout.Controls.Add(footer, 0, 5);
        status.MaximumSize = new Size(490, 0);
        status.Margin = new Padding(2, 12, 0, 0);
        layout.Controls.Add(status, 0, 6);
        Controls.Add(layout);
    }

    private void BuildAdvancedWindow()
    {
        advanced.Text = "Odyssey - Advanced";
        advanced.Font = Font;
        advanced.BackColor = Canvas;
        advanced.ForeColor = Ink;
        advanced.AutoScaleMode = AutoScaleMode.Dpi;
        advanced.ClientSize = new Size(750, 560);
        advanced.MinimumSize = new Size(650, 500);
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
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 8) };
        var diagnosticTab = new TabPage("Diagnostics") { BackColor = Canvas, Padding = new Padding(16) };
        var settingsTab = new TabPage("Inputs & default monitor") { BackColor = Canvas, Padding = new Padding(16) };
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
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 6
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
        settingsLayout.Controls.Add(clearDefault, 0, 5);
        settingsLayout.SetColumnSpan(clearDefault, 2);
        settingsTab.Controls.Add(settingsLayout);
        tabs.TabPages.Add(diagnosticTab);
        tabs.TabPages.Add(settingsTab);
        advanced.Controls.Add(tabs);
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
        trayMenu.Items.Add("Advanced...", null, (_, _) => ShowAdvanced());
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
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ShowAdvanced()
    {
        ShowMainWindow();
        advanced.Show(this);
        advanced.Activate();
    }

    private void ExitApplication()
    {
        exiting = true;
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
        diagnostics.Enabled = selected;
        save.Enabled = restore.Enabled = !busy && !exiting;
        clearDefault.Enabled = !busy && preferences.DefaultMonitorId is not null;
        saveDefault.Visible = monitors.Items.Count > 1;
        saveDefault.Enabled = selected && monitor?.StableId is not null &&
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

    private void Append(string message) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
    private void SetStatus(string message) => status.Text = message;

    private void ReportFailure(Exception error, bool showDialog = true)
    {
        SetStatus("Operation failed. Open Advanced for details.");
        Append(error.ToString());
        tray.ShowBalloonTip(5000, "Odyssey operation failed", error.Message, ToolTipIcon.Error);
        if (showDialog)
            MessageBox.Show(advanced.Visible ? advanced : Visible ? this : null, error.Message +
                "\r\n\r\nSee Advanced for details. The DDC/CI connection may be unavailable.",
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
            : "Ready to switch inputs.");
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
                : result.failed ? "Some reads failed. See Advanced for details."
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
            answer = MessageBox.Show(Visible ? this : null,
                $"{selected.DisplayName}\r\n\r\nSwitch to {inputNames[index]} (VCP 0x60 = 0x{value:X4})?\r\n\r\n" +
                "Odyssey input values may differ on other monitors. Switching may disconnect this PC. " +
                "Use the monitor's Source control to return if needed.",
                "Confirm input switch", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
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
            var updated = new Preferences { DefaultMonitorId = identity };
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            exiting = true;
            refreshTimer.Dispose();
            ReleaseInventory(logMessages: false);
            tray.Visible = false;
            tray.Dispose();
            trayMenu.Dispose();
            advanced.Dispose();
            Icon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
