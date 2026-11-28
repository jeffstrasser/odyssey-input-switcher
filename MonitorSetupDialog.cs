namespace OdysseyInputSwitcher;

internal sealed record MonitorSetupOption(string Name, string? Identity, MonitorProfileDiscovery Discovery)
{
    public override string ToString() => Name;
}

internal sealed class MonitorSetupDialog : Form
{
    internal ComboBox MonitorChoice { get; } = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top,
        AccessibleName = "Choose your default monitor"
    };
    internal Button SaveDefault { get; } = MainForm.MakeButton("Use as default", primary: true);
    internal Button ConfigureInputs { get; } = MainForm.MakeButton("Edit inputs...");
    internal Label InputSummary { get; } = new() { AutoSize = true, Dock = DockStyle.Top };
    internal int SelectedMonitorIndex => MonitorChoice.SelectedIndex;

    internal MonitorSetupDialog(IReadOnlyList<MonitorSetupOption> options, Font font, Icon? icon)
    {
        Text = "Choose your default monitor";
        Font = font;
        Icon = icon;
        BackColor = Color.White;
        ForeColor = MainForm.Ink;
        SaveDefault.Enabled = ConfigureInputs.Enabled = false;
        SaveDefault.DialogResult = DialogResult.OK;
        ConfigureInputs.DialogResult = DialogResult.Retry;
        SaveDefault.AccessibleName = "Save selected monitor as default";
        ConfigureInputs.AccessibleName = "Configure selected monitor inputs";
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            Padding = new Padding(20), Width = 520
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(new Label
        {
            Text = "We found your connected monitors. Choose the one you usually want to control.\r\n" +
                "We'll remember its inputs and settings. This won't switch any inputs.",
            AutoSize = true, Dock = DockStyle.Top
        }, 0, 0);
        foreach (var option in options)
            MonitorChoice.Items.Add(option);
        content.Controls.Add(MonitorChoice, 0, 1);
        InputSummary.Text = "Choose a monitor above to see its inputs. You can also set a default later in Settings > General.";
        content.Controls.Add(InputSummary, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var skip = MainForm.MakeButton("Not now");
        skip.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([SaveDefault, ConfigureInputs, skip]);
        content.Controls.Add(actions, 0, 3);
        Controls.Add(content);
        AcceptButton = SaveDefault;
        CancelButton = skip;
        MonitorChoice.SelectedIndexChanged += (_, _) =>
        {
            var choice = options[MonitorChoice.SelectedIndex];
            SaveDefault.Enabled = choice.Identity is not null;
            ConfigureInputs.Enabled = true;
            string inputs = choice.Discovery.Profile.Inputs.Count == 0
                ? "We couldn't find input settings for this monitor. Choose Edit inputs to add them from its documentation."
                : (choice.Discovery.Remembered ? "Saved inputs: " : "Detected inputs: ") +
                    string.Join(", ", choice.Discovery.Profile.Inputs.Select(input => input.Name)) +
                    ". Choose Edit inputs if anything needs changing.";
            InputSummary.Text = inputs + (choice.Identity is null
                ? "\r\nWindows couldn't uniquely identify this monitor, so a default cannot be saved. " +
                    "You can still configure it for this connection."
                : "");
        };
        Layout += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min((int)Math.Ceiling(520 * DeviceDpi / 96d), area.Width - 40);
            content.Width = Math.Max(200, width);
            foreach (var label in content.Controls.OfType<Label>())
                label.MaximumSize = new Size(Math.Max(1, content.Width - content.Padding.Horizontal - 8), 0);
            MaximumSize = new Size(area.Width, area.Height);
            AutoScroll = PreferredSize.Height > area.Height;
        };
    }
}
