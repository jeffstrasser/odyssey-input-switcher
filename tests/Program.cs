using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OdysseyInputSwitcher;

// A harmless restart target for the published installer's end-to-end smoke test.
if (args.Length == 3 && args[0] == "--cleanup-update")
{
    UpdateInstaller.Cleanup(int.Parse(args[1]), args[2]);
    File.WriteAllText(Environment.ProcessPath + ".restarted", "Restart and cleanup succeeded.");
    return;
}

int assertions = 0;
void Check(bool passed, string description)
{
    assertions++;
    if (!passed)
        throw new Exception("FAIL: " + description);
}
foreach (var (text, expected) in new (string, uint)[]
{
    ("17", 17), (" 0x11 ", 17), ("0XFFFF", 65535), ("65535", 65535), ("0", 0)
})
    Check(InputValue.TryParse(text, out uint value) && value == expected, "Parse " + text);
foreach (string text in new[] { "", "0x", "-1", "+17", "65536", "0x10000", "1.0", "1 7", "4294967296", "0xGG" })
    Check(!InputValue.TryParse(text, out _), "Reject " + text);
var defaults = InputSettings.Resolve(null);
var saved = InputSettings.Resolve(new Dictionary<string, string?>
{
    ["HDMI"] = " 0x1234 ",
    ["Micro HDMI"] = ""
});
Check(saved["HDMI"] == "0x1234" && saved["DisplayPort"] == defaults["DisplayPort"],
    "Saved custom input overrides its preset; missing inputs retain presets");
Check(saved["Micro HDMI"] == defaults["Micro HDMI"],
    "Blank settings from the original app no longer suppress the working preset");
saved["DisplayPort"] = "99";
Check(InputSettings.Resolve(null)["DisplayPort"] == defaults["DisplayPort"],
    "Restoring the working profile is independent of edited settings");
foreach (string? invalid in new string?[] { null, "-1", "65536", "0xGG" })
{
    try
    {
        InputSettings.Resolve(new Dictionary<string, string?> { ["HDMI"] = invalid });
        Check(false, "Reject corrupt saved configuration");
    }
    catch (InvalidOperationException)
    {
        Check(true, "Reject corrupt saved configuration without silent fallback");
    }
}
var monitor = new FakeMonitor();
InputSwitch.Send(monitor, 0x1234);
Check(monitor.Writes == 1 && monitor.LastValue == 0x1234, "Send precisely one configured write");
Check(monitor.Reads == 0, "Do not read back after switch");
try
{
    InputSwitch.Send(null, 17);
    Check(false, "Require explicit monitor selection");
}
catch (InvalidOperationException)
{
    Check(true, "Require explicit monitor selection");
}
try
{
    InputSwitch.Send(monitor, 65536);
    Check(false, "Reject oversized write");
}
catch (ArgumentOutOfRangeException)
{
    Check(monitor.Writes == 1, "Rejected value does not write");
}
monitor.Fail = true;
try
{
    InputSwitch.Send(monitor, 17);
    Check(false, "Propagate write failure");
}
catch (InvalidOperationException)
{
    Check(monitor.Writes == 2 && monitor.Reads == 0, "No retry or success-shaped fallback on failure");
}
Check(Marshal.SizeOf<NativeMethods.PhysicalMonitor>() == IntPtr.Size + 256, "Native physical monitor layout");
Check(Marshal.SizeOf<NativeMethods.MonitorInfo>() == 104, "Native MONITORINFOEXW layout");
Check(Marshal.SizeOf<NativeMethods.DisplayDevice>() == 840, "Native DISPLAY_DEVICEW layout");
Check(MonitorSelection.Choose([], "saved") == -1, "No selection when disconnected");
Check(MonitorSelection.Choose(["only"], "missing") == 0, "Auto-select the sole monitor");
Check(MonitorSelection.Choose([null], null) == 0, "Sole monitor does not require a persistable identity");
Check(MonitorSelection.Choose(["first", "second"], null) == -1, "Multiple monitors require a default or user choice");
Check(MonitorSelection.Choose(["first", "second"], "second") == 1, "Select the saved default, not the first display");
Check(MonitorSelection.Choose(["second", "first"], "second") == 0, "Default survives enumeration order changes");
Check(MonitorSelection.Choose(["first", "second"], "missing") == -1, "Do not fall back to another display when default is absent");
Check(MonitorSelection.Choose(["same", "SAME"], "same") == -1, "Duplicate identities never select an arbitrary display");
Check(MonitorSelection.FindUnique([null, "second"], "SECOND") == 1, "Windows interface identity matching is case-insensitive");
var persisted = JsonSerializer.Serialize(new Preferences
{
    DefaultMonitorId = "second",
    SuppressSwitchConfirmation = true
});
var restoredPreferences = JsonSerializer.Deserialize<Preferences>(persisted)!;
Check(MonitorSelection.Choose(["second", "first"], restoredPreferences.DefaultMonitorId) == 0,
    "Persisted default is restored by identity rather than list position");
Check(restoredPreferences.SuppressSwitchConfirmation,
    "Input confirmation preference persists with monitor preferences");
Check(!JsonSerializer.Deserialize<Preferences>("{}")!.SuppressSwitchConfirmation,
    "Missing confirmation preference retains the warning by default");
int releases = 0;
var zeroHandle = new PhysicalMonitorHandle(0, handle =>
{
    Check(handle == 0, "Preserve valid zero physical-monitor handle");
    releases++;
    return true;
});
Check(!zeroHandle.IsInvalid, "Zero is a valid physical-monitor handle");
zeroHandle.Dispose();
zeroHandle.Dispose();
Check(releases == 1, "Release even a zero handle exactly once");

void Reject<T>(Action action, string description) where T : Exception
{
    try { action(); Check(false, description); }
    catch (T) { Check(true, description); }
}
string ReleaseJson(string tag = "v1.3.0", bool prerelease = false, bool draft = false,
    string? digest = null, string? url = null, string runtime = "win-x64") =>
    JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease,
        assets = new[] { new
        {
            name = $"OdysseyInputSwitcher-{runtime}.zip",
            browser_download_url = url ?? $"{ReleaseUpdater.RepositoryUrl}/releases/download/{tag}/OdysseyInputSwitcher-{runtime}.zip",
            digest = digest ?? "sha256:" + new string('a', 64)
        } }
    });
var current = new Version(1, 2, 0, 0);
Check(ReleaseUpdater.ParseRelease(ReleaseJson(), current, Architecture.X64)?.Version == new Version(1, 3, 0, 0),
    "Offer a newer stable release with normalized version components");
Check(ReleaseUpdater.ParseRelease(ReleaseJson(runtime: "win-arm64"), current, Architecture.Arm64)!
    .DownloadUrl.AbsolutePath.EndsWith("OdysseyInputSwitcher-win-arm64.zip"), "Select native ARM64 asset");
foreach (string tag in new[] { "v1.2.0", "1.2.0.0", "v1.1.0" })
    Check(ReleaseUpdater.ParseRelease(ReleaseJson(tag), current, Architecture.X64) is null,
        "Do not offer current or older release " + tag);
Check(ReleaseUpdater.ParseRelease(ReleaseJson(prerelease: true), current, Architecture.X64) is null,
    "Do not offer prereleases");
Check(ReleaseUpdater.ParseRelease(ReleaseJson(draft: true), current, Architecture.X64) is null,
    "Do not offer drafts");
Reject<InvalidDataException>(() => ReleaseUpdater.ParseRelease(ReleaseJson("v2.0.0-beta"), current, Architecture.X64),
    "Reject non-stable version tag");
Reject<InvalidDataException>(() => ReleaseUpdater.ParseRelease(ReleaseJson(digest: ""), current, Architecture.X64),
    "Reject missing digest rather than silently skipping verification");
Reject<InvalidDataException>(() => ReleaseUpdater.ParseRelease(ReleaseJson(url: "https://example.com/update.zip"), current, Architecture.X64),
    "Reject assets outside the repository");
Reject<InvalidDataException>(() => ReleaseUpdater.ParseRelease(ReleaseJson(), current, Architecture.Arm64),
    "Reject missing matching architecture instead of installing x64 on ARM64");
Reject<PlatformNotSupportedException>(() => ReleaseUpdater.ParseRelease(ReleaseJson(), current, Architecture.X86),
    "Reject unsupported process architectures");
string testDirectory = UpdateInstaller.CreateDirectory();
Check(StartupRegistration.CreateCommand(@"C:\Program Files\Odyssey\Input Switcher.exe") ==
    "\"C:\\Program Files\\Odyssey\\Input Switcher.exe\" --tray",
    "Startup command quotes paths with spaces and launches directly in tray mode");
Reject<ArgumentException>(() => StartupRegistration.CreateCommand("relative.exe"),
    "Reject relative startup paths");
Reject<ArgumentException>(() => StartupRegistration.CreateCommand("C:\\bad\"path.exe"),
    "Reject quotes that could corrupt the startup command");
try
{
    UpdateInstaller.ValidateDirectory(testDirectory);
    if (!ReleaseUpdater.IsSingleFileBuild())
    {
        Reject<InvalidOperationException>(() => ReleaseUpdater.StartInstaller(testDirectory),
            "Source apphosts cannot be copied as standalone installers");
        Reject<InvalidOperationException>(() => StartupRegistration.SetEnabled(true),
            "Development builds reject startup registration before writing the registry");
    }
    Reject<InvalidDataException>(() => UpdateInstaller.ValidateDirectory(Path.GetTempPath()),
        "Reject cleanup of arbitrary directories");
    string archivePath = Path.Combine(testDirectory, "test.zip");
    void MakeArchive(params (string Name, byte[] Bytes)[] entries)
    {
        File.Delete(archivePath);
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var stream = archive.CreateEntry(entry.Name).Open();
            stream.Write(entry.Bytes);
        }
    }
    byte[] executable = [(byte)'M', (byte)'Z', 1, 2];
    MakeArchive((ReleaseUpdater.ExecutableName, executable), ("../escaped.exe", executable));
    string payload = Path.Combine(testDirectory, "payload.exe");
    ReleaseUpdater.ExtractExecutable(archivePath, payload);
    Check(File.ReadAllBytes(payload).SequenceEqual(executable), "Extract exactly the root executable");
    Check(!File.Exists(Path.Combine(Path.GetDirectoryName(testDirectory)!, "escaped.exe")),
        "Ignore traversal entries instead of extracting them");
    File.Delete(payload);
    MakeArchive(("nested/" + ReleaseUpdater.ExecutableName, executable));
    Reject<InvalidDataException>(() => ReleaseUpdater.ExtractExecutable(archivePath, payload),
        "Reject packages missing the root executable");
    MakeArchive((ReleaseUpdater.ExecutableName, executable), (ReleaseUpdater.ExecutableName, executable));
    Reject<InvalidDataException>(() => ReleaseUpdater.ExtractExecutable(archivePath, payload),
        "Reject duplicate executable entries");
    MakeArchive((ReleaseUpdater.ExecutableName, [1, 2, 3]));
    Reject<InvalidDataException>(() => ReleaseUpdater.ExtractExecutable(archivePath, payload),
        "Reject non-executable payload");
    File.Delete(payload);
    MakeArchive((ReleaseUpdater.ExecutableName, executable));
    byte[] zipBytes = File.ReadAllBytes(archivePath);
    var update = new ReleaseUpdate(new Version(1, 3, 0), new Uri(ReleaseUpdater.RepositoryUrl),
        Convert.ToHexString(SHA256.HashData(zipBytes)));
    using (var updater = new ReleaseUpdater(new HttpClient(new ArchiveHandler(zipBytes))))
    {
        string staged = await updater.StageAsync(update, CancellationToken.None);
        try
        {
            Check(File.ReadAllBytes(Path.Combine(staged, "payload.exe")).SequenceEqual(executable),
                "Stage only SHA-256-verified bytes");
        }
        finally { Directory.Delete(staged, recursive: true); }
        try
        {
            await updater.StageAsync(update with { Sha256 = new string('0', 64) }, CancellationToken.None);
            Check(false, "Reject tampered download");
        }
        catch (InvalidDataException) { Check(true, "Reject tampered download"); }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await updater.StageAsync(update, canceled.Token);
            Check(false, "Cancel update staging");
        }
        catch (OperationCanceledException) { Check(true, "Cancel update staging"); }
    }
    string target = Path.Combine(testDirectory, "installed.exe");
    string backup = target + ".update-backup";
    File.WriteAllText(target, "old");
    File.WriteAllText(payload, "new");
    UpdateInstaller.ReplaceExecutable(payload, target, backup);
    Check(File.ReadAllText(target) == "new" && File.ReadAllText(backup) == "old",
        "Atomic replacement keeps previous executable for rollback");
    Check(!File.Exists(target + ".update-new"), "Replacement removes temporary adjacent file");
    Reject<IOException>(() => UpdateInstaller.ReplaceExecutable(payload, target, backup),
        "Do not overwrite an unresolved backup");
    File.Move(backup, target, overwrite: true);
    using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        Reject<IOException>(() => UpdateInstaller.ReplaceExecutable(payload, target, backup),
            "Locked installation fails instead of corrupting existing executable");
    Check(File.ReadAllText(target) == "old" && !File.Exists(target + ".update-new"),
        "Failed replacement leaves original executable and cleans adjacent file");
    Reject<System.ComponentModel.Win32Exception>(() => UpdateInstaller.Apply(int.MaxValue, target, testDirectory),
        "Restarting an invalid executable fails explicitly");
    Check(File.ReadAllText(target) == "old" && !File.Exists(backup),
        "Failed process restart rolls back the previous executable");
}
finally { Directory.Delete(testDirectory, recursive: true); }
Exception? layoutError = null;
var layoutThread = new Thread(() =>
{
    try
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using (var hidden = new MainForm(startInTray: true))
        {
            hidden.Show();
            Check(!hidden.Visible && hidden.IsHandleCreated,
                "Tray startup creates the message-loop handle without showing the main window");
            var notify = (NotifyIcon)typeof(MainForm).GetField("tray",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(hidden)!;
            Check(notify.Visible, "Tray startup exposes its notification icon");
            typeof(MainForm).GetMethod("ShowMainWindow",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(hidden, null);
            Check(hidden.Visible, "Showing Odyssey exits hidden-startup mode");
            hidden.Hide();
        }
        using var main = new MainForm();
        Check(main.FormBorderStyle == FormBorderStyle.FixedSingle && !main.MaximizeBox,
            "Main window cannot be manually resized or maximized");
        var advanced = (Form)typeof(MainForm).GetField("advanced",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        Check(advanced.FormBorderStyle == FormBorderStyle.FixedSingle && !advanced.MaximizeBox,
            "Advanced window cannot be manually resized or maximized");
        foreach (float size in new[] { 10f, 16f, 24f })
        {
            using var font = new System.Drawing.Font("Segoe UI", size);
            main.Font = font;
            _ = main.Handle;
            typeof(MainForm).GetMethod("SetScaledMinimumSize",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
            main.PerformLayout();
            var mainContent = (TableLayoutPanel)main.Controls[0];
            mainContent.PerformLayout();
            Check(main.Width >= main.MinimumSize.Width && main.ClientSize.Width >= 600 * main.DeviceDpi / 96d,
                $"Content sizing preserves the designed minimum window width at {size}pt");
            Check(mainContent.Bottom <= main.ClientSize.Height || main.VerticalScroll.Visible,
                $"Fixed main window fits content or allows scrolling at {size}pt");
            Check(main.ClientSize.Height - mainContent.Bottom <= 1,
                $"Main window has no unused height below its content at {size}pt");
            int compactHeight = main.ClientSize.Height;
            var setStatus = typeof(MainForm).GetMethod("SetStatus",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            setStatus.Invoke(main, ["Downloading and verifying the update. Please wait for this operation to finish."]);
            main.PerformLayout();
            Check((main.ClientSize.Height > compactHeight || main.VerticalScroll.Visible) &&
                (mainContent.Bottom <= main.ClientSize.Height || main.VerticalScroll.Visible),
                $"Status messages expand the compact window without clipping at {size}pt");
            setStatus.Invoke(main, [""]);
            main.PerformLayout();
            Check(main.ClientSize.Height == compactHeight,
                $"Clearing status restores compact height at {size}pt");
            var (dialog, suppress) = main.CreateInputConfirmation(
                "Samsung Odyssey OLED G9 - a long monitor description", "Micro HDMI", 6);
            using (dialog)
            {
                // Create handles and lay out without showing the dialog or discovering monitors.
                _ = dialog.Handle;
                dialog.PerformAutoScale();
                dialog.PerformLayout();
                var content = (TableLayoutPanel)dialog.Controls[0];
                content.PerformLayout();
                var warning = (Label)content.GetControlFromPosition(0, 1)!;
                Check(warning.Height >= warning.GetPreferredSize(new System.Drawing.Size(warning.Width, 0)).Height,
                    $"Full confirmation warning fits at {size}pt");
                Check(warning.Bottom <= suppress.Top,
                    $"Confirmation warning does not overlap preference at {size}pt");
                Check(content.Bottom <= dialog.ClientSize.Height,
                    $"All confirmation rows fit inside the dialog at {size}pt");
                Check(dialog.AcceptButton is Button button && button.Text == "Cancel",
                    $"Cancel remains default at {size}pt");
            }
        }
    }
    catch (Exception error) { layoutError = error; }
});
layoutThread.SetApartmentState(ApartmentState.STA);
layoutThread.Start();
layoutThread.Join();
if (layoutError is not null)
    throw new Exception("Font scaling regression failed.", layoutError);
Console.WriteLine($"PASS: {assertions} hardware-free assertions. No monitor API calls or writes.");

internal sealed class ArchiveHandler(byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        });
    }
}

internal sealed class FakeMonitor : IMonitorConnection
{
    public string Description => "Test monitor";
    internal int Writes, Reads;
    internal uint LastValue;
    internal bool Fail;
    public uint ReadInput() { Reads++; return LastValue; }
    public string ReadCapabilities() { Reads++; return ""; }
    public void WriteInput(uint value)
    {
        Writes++;
        if (Fail)
            throw new InvalidOperationException("Simulated disconnected control path.");
        LastValue = value;
    }
    public void Dispose() { }
}
