using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

namespace OdysseyInputSwitcher;

internal sealed class MainForm : Form
{
    private readonly ComboBox monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button refresh = new() { Text = "Refresh monitors", AutoSize = true };
    private readonly Button diagnostics = new() { Text = "Read diagnostics (no writes)", AutoSize = true };
    private readonly Button save = new() { Text = "Save input values", AutoSize = true };
    private readonly TextBox log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Dock = DockStyle.Fill, AccessibleName = "Diagnostic log"
    };
    private readonly Label status = new() { AutoSize = true, Text = "Choose a physical monitor." };
    private readonly string[] inputNames = ["DisplayPort", "HDMI", "Micro HDMI"];
    private readonly TextBox[] values = new TextBox[3];
    private readonly Button[] switches = new Button[3];
    private MonitorInventory? inventory;
    private bool busy;
    private bool stale;
    private bool closeWhenIdle;
    private bool initialized;
    private int displayGeneration;
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OdysseyInputSwitcher", "inputs.json");

    internal MainForm()
    {
        Text = "Odyssey Input Switcher";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 640);
        MinimumSize = new Size(680, 580);
        using var iconStream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("OdysseyInputSwitcher.assets.monitor.ico")
            ?? throw new InvalidOperationException("The monitor icon resource is missing.");
        using var sourceIcon = new Icon(iconStream);
        Icon = (Icon)sourceIcon.Clone();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16),
            ColumnCount = 1, RowCount = 7
        };
        for (int i = 0; i < 6; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = "Samsung Odyssey OLED G9 G93SD - LS49DG934SNXGO",
            AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10)
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = "Select the physical monitor yourself. Input mappings for this exact model are not verified.\r\n" +
                "Values start blank. Diagnostics only read; switching sends one confirmed VCP 0x60 write.",
            AutoSize = true, Margin = new Padding(0, 0, 0, 12)
        }, 0, 1);
        var selection = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2 };
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        selection.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        selection.Controls.Add(monitors, 0, 0);
        selection.Controls.Add(refresh, 1, 0);
        layout.Controls.Add(selection, 0, 2);
        var inputs = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3 };
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputs.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputs.Controls.Add(new Label
        {
            Text = "Input label", AutoSize = true, Margin = new Padding(3, 10, 3, 6)
        }, 0, 0);
        inputs.Controls.Add(new Label
        {
            Text = "Your write value (decimal or 0x hex)", AutoSize = true,
            Margin = new Padding(3, 10, 3, 6)
        }, 1, 0);
        for (int i = 0; i < inputNames.Length; i++)
        {
            int index = i;
            values[i] = new TextBox { Dock = DockStyle.Fill, AccessibleName = inputNames[i] + " write value" };
            switches[i] = new Button { Text = "Switch to " + inputNames[i], AutoSize = true };
            values[i].TextChanged += (_, _) => UpdateControls();
            switches[i].Click += async (_, _) => await SwitchAsync(index);
            inputs.Controls.Add(new Label
            {
                Text = inputNames[i], AutoSize = true, Anchor = AnchorStyles.Left
            }, 0, i + 1);
            inputs.Controls.Add(values[i], 1, i + 1);
            inputs.Controls.Add(switches[i], 2, i + 1);
        }
        layout.Controls.Add(inputs, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        actions.Controls.Add(diagnostics);
        actions.Controls.Add(save);
        layout.Controls.Add(actions, 0, 4);
        status.Margin = new Padding(3, 8, 3, 10);
        layout.Controls.Add(status, 0, 5);
        layout.Controls.Add(log, 0, 6);
        Controls.Add(layout);

        monitors.SelectedIndexChanged += (_, _) => UpdateControls();
        refresh.Click += async (_, _) => await RefreshAsync();
        diagnostics.Click += async (_, _) => await DiagnosticsAsync();
        save.Click += (_, _) => SaveSettings();
        Shown += async (_, _) =>
        {
            initialized = true;
            LoadSettings();
            await RefreshAsync();
        };
        FormClosing += (_, e) =>
        {
            if (!busy)
                return;
            e.Cancel = true;
            closeWhenIdle = true;
            status.Text = "Waiting for the current Windows monitor call before closing...";
        };
        FormClosed += (_, _) =>
        {
            ReleaseInventory();
            Icon?.Dispose();
        };
        UpdateControls();
    }

    private void UpdateControls()
    {
        bool selected = !busy && !stale && monitors.SelectedItem is NativeMonitor;
        monitors.Enabled = !busy && !stale;
        refresh.Enabled = !busy;
        diagnostics.Enabled = selected;
        save.Enabled = !busy;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] is null)
                continue;
            values[i].Enabled = !busy;
            switches[i].Enabled = selected && InputValue.TryParse(values[i].Text, out _);
        }
    }

    private void Append(string message) =>
        log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");

    private void ReportFailure(Exception error)
    {
        status.Text = "Operation failed; see the diagnostic log.";
        Append(error.ToString());
        MessageBox.Show(this, error.Message +
            "\r\n\r\nDDC/CI may be unsupported, disabled, blocked by a dock/adapter, or unavailable on this input.",
            "Odyssey Input Switcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (busy)
            return;
        busy = true;
        UpdateControls();
        try
        {
            await operation();
        }
        catch (Exception error)
        {
            // Event handlers cannot propagate exceptions safely; expose the complete error instead.
            ReportFailure(error);
        }
        finally
        {
            busy = false;
            UpdateControls();
            if (closeWhenIdle)
                Close();
        }
    }

    private void ReleaseInventory()
    {
        monitors.Items.Clear();
        if (inventory is null)
            return;
        inventory.Dispose();
        foreach (string message in inventory.Messages)
            Append(message);
        inventory = null;
    }

    private Task RefreshAsync() => RunAsync(async () =>
    {
        status.Text = "Enumerating physical monitors (no input reads or writes)...";
        ReleaseInventory();
        int generation = displayGeneration;
        inventory = await Task.Run(MonitorInventory.Enumerate);
        foreach (string message in inventory.Messages)
            Append(message);
        inventory.Messages.Clear();
        stale = generation != displayGeneration;
        if (stale)
        {
            status.Text = "Displays changed during enumeration. Refresh again.";
            return;
        }
        foreach (var monitor in inventory.Monitors)
            monitors.Items.Add(monitor);
        monitors.SelectedIndex = -1;
        status.Text = inventory.Monitors.Count == 0
            ? "No physical monitors found. Check the display connection and diagnostic log."
            : "Choose a physical monitor; none is selected automatically.";
        Append($"Enumerated {inventory.Monitors.Count} physical monitor(s). No VCP writes sent.");
    });

    private Task DiagnosticsAsync()
    {
        var selected = monitors.SelectedItem as NativeMonitor;
        if (stale || selected is null)
            return Task.CompletedTask;
        return RunAsync(async () =>
        {
            status.Text = "Reading DDC/CI diagnostics; Windows calls may take several seconds...";
            Append("Read-only diagnostics for " + selected.Description);
            var result = await Task.Run(() =>
            {
                var lines = new List<string>();
                bool failed = false;
                try
                {
                    uint current = selected.ReadInput();
                    lines.Add($"VCP 0x60 reported current value: {current} (0x{current:X4}). " +
                        "A read value does NOT establish the write mapping.");
                }
                catch (Win32Exception ex)
                {
                    failed = true;
                    lines.Add(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    failed = true;
                    lines.Add(ex.Message);
                }
                try
                {
                    lines.Add("Raw capabilities (may be incomplete or misleading):\r\n" + selected.ReadCapabilities());
                }
                catch (Win32Exception ex)
                {
                    failed = true;
                    lines.Add(ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    failed = true;
                    lines.Add(ex.Message);
                }
                return (lines, failed);
            });
            foreach (string line in result.lines)
                Append(line);
            status.Text = stale ? "Displays changed. Refresh and reselect before another operation."
                : result.failed ? "Some diagnostic reads failed; see the log. No writes were sent."
                : "Read-only diagnostics completed. No writes were sent.";
        });
    }

    private async Task SwitchAsync(int index)
    {
        if (busy || stale || monitors.SelectedItem is not NativeMonitor selected ||
            !InputValue.TryParse(values[index].Text, out uint value))
            return;
        int generation = displayGeneration;
        var answer = MessageBox.Show(this,
            $"Selected physical monitor:\r\n{selected.Description}\r\n\r\n" +
            $"Send ONE input command (VCP 0x60) for {inputNames[index]}: {value} (0x{value:X4})?\r\n\r\n" +
            "This model's write mapping is unverified. An incorrect value may be ignored or behave unexpectedly. " +
            "The screen may go black and this PC may lose control of the monitor. " +
            "Have the monitor's physical Source control available to switch back.",
            "Confirm input switch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes)
            return;
        if (stale || generation != displayGeneration || monitors.SelectedItem != selected)
        {
            status.Text = "Displays changed during confirmation. Refresh and select again.";
            return;
        }
        await RunAsync(async () =>
        {
            status.Text = "Sending one input command...";
            Append($"Sending VCP 0x60 = {value} (0x{value:X4}) to {selected.Description}.");
            try
            {
                await Task.Run(() => InputSwitch.Send(selected, value));
                Append("Windows accepted the command. The physical input change is NOT verified; " +
                    "no readback or retry was attempted.");
                status.Text = "Command accepted, switch not verified. Refresh and reselect to continue.";
            }
            finally
            {
                stale = true;
                monitors.SelectedIndex = -1;
                Append("Monitor selection invalidated after the switch attempt. Use Refresh monitors.");
            }
        });
    }

    protected override void WndProc(ref Message message)
    {
        const int WmDisplayChange = 0x007E;
        if (message.Msg == WmDisplayChange)
        {
            displayGeneration++;
            stale = true;
            if (initialized)
            {
                monitors.SelectedIndex = -1;
                Append("Windows reported a display change. Refresh and reselect; no automatic commands sent.");
                status.Text = "Display topology changed. Refresh monitors before continuing.";
                UpdateControls();
            }
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
            for (int i = 0; i < values.Length; i++)
            {
                if (!stored.TryGetValue(inputNames[i], out string? text))
                    continue;
                if (text is null || (text.Length != 0 && !InputValue.TryParse(text, out _)))
                    throw new InvalidOperationException($"Saved value for {inputNames[i]} is invalid.");
                values[i].Text = text;
            }
            Append("Loaded user-configured values from " + SettingsPath + ". These are not verified model defaults.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            JsonException or InvalidOperationException)
        {
            foreach (var field in values)
                field.Clear();
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
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, SettingsPath, true);
            Append("Saved user-configured input values to " + SettingsPath);
            status.Text = "Values saved. No input commands were sent.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure(error);
        }
    }
}
