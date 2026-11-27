using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OdysseyInputSwitcher;

// A harmless restart target for the published installer's end-to-end smoke test.
if (args.Length == 3 && args[0] == "--cleanup-update")
{
    if (File.Exists(Path.Combine(args[2], "crash-before-ready")))
        Environment.Exit(23);
    UpdateInstaller.SignalReady(args[2]);
    UpdateInstaller.Cleanup(int.Parse(args[1]), args[2]);
    File.WriteAllText(Environment.ProcessPath + ".restarted", "Restart and cleanup succeeded.");
    return;
}

if (args.Length == 4 && args[0] == "--verify-package")
{
    string staged = UpdateInstaller.CreateDirectory();
    try
    {
        using (var archive = ZipFile.OpenRead(args[1]))
            if (!archive.Entries.Select(entry => entry.FullName).Order()
                .SequenceEqual(new[] { ReleaseUpdater.ExecutableName, "README.md" }.Order()))
                throw new InvalidDataException("The release ZIP must contain exactly the app executable and README at its root.");
        string executable = Path.Combine(staged, "payload.exe");
        ReleaseUpdater.ExtractExecutable(args[1], executable);
        ReleaseUpdater.ValidateExecutable(executable, ReleaseUpdater.ParseVersion(args[3]), Enum.Parse<Architecture>(args[2]));
        Console.WriteLine($"PASS: {args[2]} release ZIP shape, executable headers, architecture, and version {args[3]}.");
    }
    finally { Directory.Delete(staged, recursive: true); }
    return;
}

int assertions = 0;
void Check(bool passed, string description)
{
    assertions++;
    if (!passed)
        throw new Exception("FAIL: " + description);
}
if (args.Contains("--daily-updates-only") || args.Contains("--layout-only"))
    goto UiTests;
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
Check(InputVisibility.Resolve(null).Values.All(visible => visible),
    "All inputs are visible by default for existing installations");
var visibilitySettings = InputVisibility.Resolve(new Dictionary<string, bool?> { ["HDMI"] = false });
Check(!visibilitySettings["HDMI"] && visibilitySettings["DisplayPort"] && visibilitySettings["Micro HDMI"],
    "Explicitly hidden inputs stay hidden and missing visibility keys default to shown");
try
{
    InputVisibility.Resolve(new Dictionary<string, bool?> { ["HDMI"] = null });
    Check(false, "Reject corrupt visibility rather than silently hiding an input");
}
catch (InvalidOperationException)
{
    Check(true, "Reject corrupt visibility rather than silently hiding an input");
}
var customNames = InputDisplayNames.Resolve(new Dictionary<string, string?>
{
    ["HDMI"] = "  Work PC & console  "
});
Check(customNames["HDMI"] == "Work PC & console" && customNames["DisplayPort"] == "DisplayPort",
    "Names are trimmed and missing identities retain original labels");
Check(InputSettings.Resolve(new Dictionary<string, string?> { ["HDMI"] = "0x1234" })["HDMI"] == "0x1234",
    "Input mappings remain keyed by stable identity, not custom labels");
Check(InputDisplayNames.Resolve(new Dictionary<string, string?> { ["HDMI"] = new string('W', 48) })["HDMI"].Length == 48,
    "Accept the maximum-length display name");
foreach (string? invalid in new string?[] { null, "", "   ", new string('W', 49), "PC\r\nconsole", "PC\tconsole", "PC\0" })
{
    try
    {
        InputDisplayNames.Resolve(new Dictionary<string, string?> { ["HDMI"] = invalid });
        Check(false, "Reject invalid display name");
    }
    catch (InvalidOperationException)
    {
        Check(true, "Reject invalid display name");
    }
}
Check(InputDisplayNames.Resolve(new Dictionary<string, string?> { ["HDMI"] = "Caf\u00e9 PC" })["HDMI"] == "Caf\u00e9 PC",
    "Allow Unicode device names");
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
Check(ReleaseUpdater.ParseRelease(ReleaseJson("v2"), new Version(1, 4, 1), Architecture.X64)?.Version ==
    new Version(2, 0, 0, 0), "Offer the first whole-number release after a legacy release");
Check(ReleaseUpdater.ParseRelease(ReleaseJson("v3", runtime: "win-arm64"), new Version(2, 0, 0, 0),
    Architecture.Arm64)?.Version == new Version(3, 0, 0, 0), "Offer subsequent whole-number ARM64 releases");
foreach (string tag in new[] { "v2", "2", "v1", "v1.4.1" })
    Check(ReleaseUpdater.ParseRelease(ReleaseJson(tag), new Version(2, 0, 0, 0), Architecture.X64) is null,
        "Do not offer current or older releases after migration: " + tag);
foreach (string tag in new[] { "v0", "v02", "v2.1", "v-2", "v2-beta", "v2147483648" })
    Reject<InvalidDataException>(() => ReleaseUpdater.ParseVersion(tag), "Reject malformed whole-number version " + tag);
Check(ReleaseUpdater.FormatVersion(new Version(2, 0, 0, 0)) == "2" &&
    ReleaseUpdater.FormatVersion(ReleaseUpdater.ParseVersion("v3")) == "3",
    "Display whole-number versions without binary-version padding");
Check(ReleaseUpdater.FormatVersion(new Version(1, 4, 1, 0)) == "1.4.1" &&
    ReleaseUpdater.FormatVersion(new Version(1, 4, 1, 2)) == "1.4.1.2",
    "Preserve legacy version components for display");
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
    Reject<InvalidDataException>(() => ReleaseUpdater.ValidateExecutable(payload, new Version(1, 3, 0),
        RuntimeInformation.ProcessArchitecture), "Reject MZ-only payload before installation");
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
    string fixture = Environment.ProcessPath!;
    var fixtureInfo = FileVersionInfo.GetVersionInfo(fixture);
    var fixtureVersion = new Version(fixtureInfo.FileMajorPart, fixtureInfo.FileMinorPart,
        fixtureInfo.FileBuildPart, fixtureInfo.FilePrivatePart);
    byte[] fixtureBytes = File.ReadAllBytes(fixture);
    MakeArchive((ReleaseUpdater.ExecutableName, fixtureBytes));
    byte[] zipBytes = File.ReadAllBytes(archivePath);
    var update = new ReleaseUpdate(fixtureVersion, new Uri(ReleaseUpdater.RepositoryUrl),
        Convert.ToHexString(SHA256.HashData(zipBytes)));
    using (var updater = new ReleaseUpdater(new HttpClient(new ArchiveHandler(zipBytes))))
    {
        string staged = await updater.StageAsync(update, CancellationToken.None);
        try
        {
            Check(File.ReadAllBytes(Path.Combine(staged, "payload.exe")).SequenceEqual(fixtureBytes),
                "Stage only SHA-256-verified bytes with valid architecture and version");
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
    MakeArchive((ReleaseUpdater.ExecutableName, executable));
    byte[] invalidZip = File.ReadAllBytes(archivePath);
    using (var updater = new ReleaseUpdater(new HttpClient(new ArchiveHandler(invalidZip))))
    {
        try
        {
            await updater.StageAsync(update with { Sha256 = Convert.ToHexString(SHA256.HashData(invalidZip)) },
                CancellationToken.None);
            Check(false, "Reject invalid executable even when archive digest is correct");
        }
        catch (InvalidDataException) { Check(true, "Reject invalid executable even when archive digest is correct"); }
    }
    using (var updater = new ReleaseUpdater(new HttpClient(new ArchiveHandler(zipBytes))))
    {
        try
        {
            await updater.StageAsync(update with { Version = new Version(fixtureVersion.Major + 1, 0, 0) },
                CancellationToken.None);
            Check(false, "Reject version-mismatched executable during staging");
        }
        catch (InvalidDataException) { Check(true, "Reject version-mismatched executable during staging"); }
    }
    ReleaseUpdater.ValidateExecutable(fixture, fixtureVersion, RuntimeInformation.ProcessArchitecture);
    Reject<InvalidDataException>(() => ReleaseUpdater.ValidateExecutable(fixture,
        new Version(fixtureVersion.Major + 1, 0, 0), RuntimeInformation.ProcessArchitecture),
        "Reject executable whose embedded version differs from its release");
    Reject<InvalidDataException>(() => ReleaseUpdater.ValidateExecutable(fixture, fixtureVersion,
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.X64 : Architecture.Arm64),
        "Reject executable with the wrong process architecture");
    File.WriteAllBytes(payload, fixtureBytes[..(fixtureBytes.Length / 2)]);
    Reject<InvalidDataException>(() => ReleaseUpdater.ValidateExecutable(payload, fixtureVersion,
        RuntimeInformation.ProcessArchitecture), "Reject truncated executable section data");
    using (var process = Process.GetCurrentProcess())
    {
        File.WriteAllText(Path.Combine(testDirectory, "ready"), "-1");
        Reject<TimeoutException>(() => UpdateInstaller.WaitForReady(process, testDirectory,
            TimeSpan.FromMilliseconds(150)), "Ignore readiness signals from another process");
        UpdateInstaller.SignalReady(testDirectory);
        UpdateInstaller.WaitForReady(process, testDirectory, TimeSpan.FromSeconds(1));
        Check(true, "Accept atomic initialization readiness signal for the expected process");
        File.Delete(Path.Combine(testDirectory, "ready"));
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
    string crashTarget = Path.Combine(AppContext.BaseDirectory, "crash-test-" + Guid.NewGuid().ToString("N") + ".exe");
    try
    {
        File.WriteAllText(crashTarget, "old");
        File.Copy(fixture, payload, overwrite: true);
        File.WriteAllText(Path.Combine(testDirectory, "crash-before-ready"), "");
        Reject<InvalidOperationException>(() => UpdateInstaller.Apply(int.MaxValue, crashTarget, testDirectory),
            "Immediate post-launch crash is treated as failed initialization");
        Check(File.ReadAllText(crashTarget) == "old",
            "Restore original executable after an updated process crashes before readiness");
    }
    finally { File.Delete(crashTarget); }
}
finally { Directory.Delete(testDirectory, recursive: true); }
UiTests:
Exception? layoutError = null;
var layoutThread = new Thread(() =>
{
    try
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            Environment.Exit(1);
        };
        Application.SetHighDpiMode(args.Contains("--dpi-unaware")
            ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        var updateClock = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var schedule = new UpdateCheckSchedule(updateClock);
        Check(schedule.IsDue, "Startup update check is initially due");
        schedule.CheckStarted();
        updateClock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        Check(!schedule.IsDue, "Daily check does not run before exactly 24 hours");
        updateClock.Advance(TimeSpan.FromTicks(1));
        Check(schedule.IsDue, "Daily check becomes due at the 24-hour boundary");
        schedule.CheckStarted();
        updateClock.Advance(TimeSpan.FromDays(4));
        Check(schedule.IsDue, "A check missed during sleep is due on resume");
        schedule.CheckStarted();
        Check(!schedule.IsDue, "Resumed checks start a fresh interval instead of catching up in a burst");
        string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        Action<CancellationToken>? duringRequest = null;
        bool failUpdate = false;
        using var updateHandler = new CallbackReleaseHandler((request, token) =>
        {
            Check(request.RequestUri!.AbsolutePath.EndsWith("/releases/latest"),
                "Daily checks fetch metadata only, never download an update");
            duringRequest?.Invoke(token);
            return new HttpResponseMessage(failUpdate ? System.Net.HttpStatusCode.BadGateway : System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseJson("v99.0.0", runtime: runtime))
            };
        });
        using (var daily = new MainForm(startInTray: true, clock: updateClock,
            updaterFactory: () => new ReleaseUpdater(new HttpClient(updateHandler, disposeHandler: false))))
        {
            var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var state = typeof(MainForm).GetMethod("SetStatus", fields)!;
            var message = (Label)typeof(MainForm).GetField("status", fields)!.GetValue(daily)!;
            var updateItem = (ToolStripMenuItem)typeof(MainForm).GetField("settingsUpdate", fields)!.GetValue(daily)!;
            var refreshButton = (Button)typeof(MainForm).GetField("refresh", fields)!.GetValue(daily)!;
            daily.CheckForUpdatesAsync(manual: false).GetAwaiter().GetResult();
            Check(updateHandler.Requests == 1 && !daily.Visible && !daily.IsHandleCreated,
                "Startup checking remains supported in tray mode");
            state.Invoke(daily, ["Monitor operation status"]);
            updateClock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
            daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            Check(updateHandler.Requests == 1, "Timer polling does not request metadata before the deadline");
            updateClock.Advance(TimeSpan.FromTicks(1));
            typeof(MainForm).GetField("busy", fields)!.SetValue(daily, true);
            daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            Check(updateHandler.Requests == 1, "Due checks defer while a monitor operation is busy");
            typeof(MainForm).GetField("busy", fields)!.SetValue(daily, false);
            typeof(MainForm).GetField("confirming", fields)!.SetValue(daily, true);
            daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            Check(updateHandler.Requests == 1, "Due checks defer while an input confirmation is active");
            typeof(MainForm).GetField("confirming", fields)!.SetValue(daily, false);
            duringRequest = _ =>
            {
                Check(!updateItem.Enabled && refreshButton.Enabled &&
                    !(bool)typeof(MainForm).GetField("busy", fields)!.GetValue(daily)!,
                    "Background requests disable overlapping update checks but not monitor controls");
                bool operationRan = false;
                var operation = (Task)typeof(MainForm).GetMethod("RunAsync", fields)!.Invoke(daily,
                    [(Func<Task>)(() => { operationRan = true; return Task.CompletedTask; }), false])!;
                operation.GetAwaiter().GetResult();
                Check(operationRan, "Monitor operations can run during a daily metadata request");
                daily.CheckForUpdatesAsync(manual: true).GetAwaiter().GetResult();
                updateClock.Advance(TimeSpan.FromHours(24));
                daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
                updateClock.Advance(-TimeSpan.FromHours(24));
            };
            daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            Check(updateHandler.Requests == 2 && updateItem.Enabled,
                "Deferred daily checks run when idle and overlapping checks are suppressed");
            Check(!daily.Visible && !daily.IsHandleCreated && message.Text == "Monitor operation status",
                "Daily update offers preserve tray-only operation and monitor status");
            duringRequest = null;
            updateClock.Advance(TimeSpan.FromDays(3));
            failUpdate = true;
            daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            var diagnosticLog = (TextBox)typeof(MainForm).GetField("log", fields)!.GetValue(daily)!;
            Check(updateHandler.Requests == 3 && diagnosticLog.Text.Contains("Automatic update check failed") &&
                updateItem.Enabled && message.Text == "Monitor operation status",
                "Network failures are reported without blocking controls or overwriting monitor status");
            daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            Check(updateHandler.Requests == 3, "Failed checks do not cause a request burst on every timer tick");
            updateClock.Advance(TimeSpan.FromHours(24));
            failUpdate = false;
            duringRequest = token => { daily.Dispose(); token.ThrowIfCancellationRequested(); };
            daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            daily.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            Check(daily.IsDisposed && updateHandler.Requests == 4,
                "Shutdown cancels background requests and prevents further checks without accessing disposed UI");
        }
        if (args.Contains("--daily-updates-only"))
            return;
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
            ((ToolStripMenuItem)hidden.MainMenuStrip!.Items[0]).DropDownItems
                .OfType<ToolStripMenuItem>().Single(item => item.Text == "E&xit").PerformClick();
            Check(hidden.IsDisposed, "Settings Exit stops the hidden app rather than hiding it again");
        }
        foreach (string pendingOperation in new[] { "busy", "confirming" })
        {
            using var waiting = new MainForm(startInTray: true);
            _ = waiting.Handle;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var pending = typeof(MainForm).GetField(pendingOperation, flags)!;
            pending.SetValue(waiting, true);
            var exit = ((ToolStripMenuItem)waiting.MainMenuStrip!.Items[0]).DropDownItems
                .OfType<ToolStripMenuItem>().Single(item => item.Text == "E&xit");
            exit.PerformClick();
            Check(!waiting.IsDisposed && (bool)typeof(MainForm).GetField("closeWhenIdle", flags)!.GetValue(waiting)!,
                $"Settings Exit waits for an outstanding {pendingOperation} operation");
            pending.SetValue(waiting, false);
            exit.PerformClick();
            Check(waiting.IsDisposed, $"Settings Exit completes once {pendingOperation} is cleared");
        }
        // Forms must be disposed before the shared font used during their paint/teardown.
        using var originalFont = new System.Drawing.Font("Segoe UI", 9);
        using var main = new MainForm();
        var menu = main.MainMenuStrip ?? throw new Exception("Standard menu bar is missing.");
        var settingsMenu = (ToolStripMenuItem)menu.Items[0];
        string[] expectedMenu = ["&General...", "&Inputs...", "&Advanced...", "Check for updates...", "About Odyssey...", "E&xit"];
        Check(settingsMenu.Text == "&Settings" && settingsMenu.DropDownItems.OfType<ToolStripMenuItem>()
            .Select(item => item.Text).SequenceEqual(expectedMenu),
            "Standard Settings menu exposes preferences, logging, updates, About and Exit with keyboard mnemonics");
        var mainLayout = main.Controls.OfType<TableLayoutPanel>().Single();
        var footer = (FlowLayoutPanel)mainLayout.GetControlFromPosition(0, 5)!;
        Check(footer.Controls.OfType<Button>().Select(button => button.Text).SequenceEqual(["Run in tray"]),
            "Footer retains Run in tray but no Settings button");
        var trayMenu = (ContextMenuStrip)typeof(MainForm).GetField("trayMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var trayMonitor = (ToolStripMenuItem)typeof(MainForm).GetField("trayMonitor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var menuInputs = (ToolStripMenuItem[])typeof(MainForm).GetField("trayInputs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var trayHiddenMessage = (ToolStripMenuItem)typeof(MainForm).GetField("trayAllInputsHidden",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        Check(trayMenu.Items.Count == 6 && trayMenu.Items[0] == trayMonitor &&
            trayMenu.Items[1] is ToolStripSeparator &&
            trayMenu.Items.Cast<ToolStripItem>().Skip(2).SequenceEqual(
                menuInputs.Cast<ToolStripItem>().Append(trayHiddenMessage)) &&
            trayMenu.Items.OfType<ToolStripMenuItem>().All(item => !item.HasDropDownItems),
            "Tray menu contains only the monitor heading, input choices and all-hidden guidance");
        Check(main.FormBorderStyle == FormBorderStyle.FixedSingle && !main.MaximizeBox,
            "Main window cannot be manually resized or maximized");
        var advanced = (Form)typeof(MainForm).GetField("advanced",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        Check(advanced.FormBorderStyle == FormBorderStyle.FixedSingle && !advanced.MaximizeBox,
            "Advanced window cannot be manually resized or maximized");
        var settingsTabs = advanced.Controls.OfType<TabControl>().Single();
        Check(settingsTabs.TabPages.Cast<TabPage>().Select(tab => tab.Text)
            .SequenceEqual(["General", "Inputs", "Advanced"]),
            "Settings separates everyday preferences, input presentation, and technical settings");
        var generalPanel = settingsTabs.TabPages[0].Controls.OfType<TableLayoutPanel>().Single();
        Check(generalPanel.Controls.OfType<CheckBox>().Select(control => control.Text)
            .SequenceEqual(["Run at Windows startup", "Ask before switching inputs"]) &&
            generalPanel.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<Button>()
                .Select(button => button.Text).SequenceEqual(["Save selected monitor as default", "Clear default monitor"]),
            "General groups startup, confirmation, and both default-monitor actions");
        var technicalTabs = settingsTabs.TabPages[2].Controls.OfType<TabControl>().Single();
        Check(technicalTabs.TabPages.Cast<TabPage>().Select(tab => tab.Text)
            .SequenceEqual(["Input values", "Diagnostics"]),
            "Advanced keeps VCP editing separate from diagnostic reads");
        IEnumerable<Control> Descendants(Control parent) =>
            parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
        var help = (ToolTip)typeof(MainForm).GetField("buttonHelp",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        Check(Descendants(main).Concat(Descendants(advanced)).OfType<Button>().All(button =>
            !string.IsNullOrWhiteSpace(button.AccessibleDescription) &&
            help.GetToolTip(button) == button.AccessibleDescription),
            "Every main and settings button has accessible hover help");
        Check(Descendants(advanced).OfType<Button>().All(button =>
            button.Parent!.Controls.OfType<Label>().Any(label => label.Text == button.AccessibleDescription)),
            "Every settings button has an always-visible description");
        var nameFields = (TextBox[])typeof(MainForm).GetField("nameFields",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var switches = (Button[])typeof(MainForm).GetField("switches",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var inputValues = (TextBox[])typeof(MainForm).GetField("values",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        string[] valuesBeforeNavigation = inputValues.Select(field => field.Text).ToArray();
        for (int index = 0; index < 3; index++)
        {
            settingsMenu.DropDownItems[index].PerformClick();
            Check(advanced.Visible && settingsTabs.SelectedIndex == index,
                $"{settingsMenu.Text} shortcut opens the matching settings section {index}");
            advanced.Hide();
            main.Hide();
        }
        Check(inputValues.Select(field => field.Text).SequenceEqual(valuesBeforeNavigation),
            "Opening settings from the main menu preserves staged VCP values");
        main.Show();
        main.PerformLayout();
        Check(main.ClientSize.Width <= 460 * main.DeviceDpi / 96d &&
            main.ClientSize.Height <= 300 * main.DeviceDpi / 96d,
            $"Default main window stays compact (client={main.ClientSize}, DPI={main.DeviceDpi})");
        Check(advanced.ClientSize.Width == Math.Ceiling(650 * advanced.DeviceDpi / 96d) &&
            advanced.ClientSize.Height <= 600 * advanced.DeviceDpi / 96d,
            $"Settings initially fits a DPI-scaled compact window (client={advanced.ClientSize}, DPI={advanced.DeviceDpi})");
        inputValues[1].Text = "0x1234";
        string[] originalValues = inputValues.Select(field => field.Text).ToArray();
        string namesDirectory = Path.Combine(AppContext.BaseDirectory, "names-test-" + Guid.NewGuid().ToString("N"));
        string namesPath = Path.Combine(namesDirectory, "input-names.json");
        string[] labels = [new string('W', 48), "Work PC & console", "Living room gaming PC and streaming device"];
        try
        {
            for (int i = 0; i < nameFields.Length; i++)
                nameFields[i].Text = labels[i];
            main.SaveInputNames(namesPath);
            Check(switches.Select(button => button.Text).SequenceEqual(labels) &&
                switches.All(button => !button.UseMnemonic),
                "Saved custom names appear literally in main buttons");
            Check(trayMenu.Items.OfType<ToolStripMenuItem>().Skip(1).Take(3).Select(item => item.Text)
                .SequenceEqual(labels.Select(label => label.Replace("&", "&&"))),
                "Tray labels preserve custom names including literal ampersands");
            Check(inputValues.Select(field => field.Text).SequenceEqual(originalValues),
                "Saving names does not change the configured VCP values");
            var namesFromDisk = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(namesPath))!;
            Check(namesFromDisk.Keys.Order().SequenceEqual(new[] { "DisplayPort", "HDMI", "Micro HDMI" }.Order()),
                "Names persist under stable input identities");
            using (var restarted = new MainForm())
            {
                restarted.LoadInputNames(namesPath);
                var restoredButtons = (Button[])typeof(MainForm).GetField("switches",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(restarted)!;
                Check(restoredButtons.Select(button => button.Text).SequenceEqual(labels),
                    "Restart restores custom names from disk");
            }
            var tabs = advanced.Controls.OfType<TabControl>().Single();
            var namesLayout = (TableLayoutPanel)tabs.TabPages[1].Controls[0];
            var actions = (TableLayoutPanel)namesLayout.GetControlFromPosition(0, 4)!;
            var restoreNames = actions.Controls.OfType<Button>().Single(button => button.Text == "Restore default names");
            typeof(Button).GetMethod("OnClick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(restoreNames, [EventArgs.Empty]);
            Check(nameFields.Select(field => field.Text).SequenceEqual(["DisplayPort", "HDMI", "Micro HDMI"]) &&
                switches.Select(button => button.Text).SequenceEqual(labels),
                "Restore defaults stages original names without changing saved labels before Save");
            main.SaveInputNames(namesPath);
            Check(switches.Select(button => button.Text).SequenceEqual(["DisplayPort", "HDMI", "Micro HDMI"]) &&
                InputDisplayNames.Resolve(JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(namesPath)))
                    .Values.SequenceEqual(["DisplayPort", "HDMI", "Micro HDMI"]),
                "Saving restored defaults updates buttons and disk");
            for (int i = 0; i < nameFields.Length; i++)
                nameFields[i].Text = labels[i];
            main.SaveInputNames(namesPath);
        }
        finally { if (Directory.Exists(namesDirectory)) Directory.Delete(namesDirectory, recursive: true); }
        var visibilityFields = (CheckBox[])typeof(MainForm).GetField("visibilityFields",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var trayInputs = (ToolStripMenuItem[])typeof(MainForm).GetField("trayInputs",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var hiddenMessage = (Label)typeof(MainForm).GetField("allInputsHidden",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(main)!;
        var inputPanel = (TableLayoutPanel)switches[0].Parent!;
        string visibilityDirectory = Path.Combine(AppContext.BaseDirectory, "visibility-test-" + Guid.NewGuid().ToString("N"));
        string visibilityPath = Path.Combine(visibilityDirectory, "input-visibility.json");
        string combinedNamesPath = Path.Combine(visibilityDirectory, "input-names.json");
        main.Show();
        try
        {
            main.SaveInputNames(combinedNamesPath);
            string originalNamesFile = File.ReadAllText(combinedNamesPath);
            foreach (float size in new[] { 10f, 16f, 24f })
            {
                using var font = new System.Drawing.Font("Segoe UI", size);
                main.Font = font;
                for (int mask = 0; mask < 8; mask++)
                {
                    bool[] expected = Enumerable.Range(0, 3).Select(i => (mask & (1 << i)) != 0).ToArray();
                    for (int i = 0; i < visibilityFields.Length; i++)
                        visibilityFields[i].Checked = expected[i];
                    main.SaveInputVisibility(visibilityPath);
                    main.PerformLayout();
                    inputPanel.PerformLayout();
                    Check(switches.Select(button => button.Visible).SequenceEqual(expected) &&
                        trayInputs.Select(item => item.Available).SequenceEqual(expected),
                        $"Visibility updates main and tray actions for mask {mask} at {size}pt");
                    int[] widths = inputPanel.GetColumnWidths();
                    int count = expected.Count(visible => visible);
                    Check(count == 0 ? inputPanel.RowStyles[0].Height == 0 :
                        Enumerable.Range(0, 3).All(i => expected[i] ? widths[i] > 0 : widths[i] == 0) &&
                        widths.Sum() == inputPanel.ClientSize.Width,
                        $"Visible buttons reflow without empty slots for mask {mask} at {size}pt");
                    Check(hiddenMessage.Visible == (count == 0) &&
                        trayMenu.Items.OfType<ToolStripMenuItem>().Single(item =>
                            item.Text == "All inputs hidden - Settings > Inputs...").Available == (count == 0) &&
                        (count != 0 || (hiddenMessage.Text.Contains("Settings > Inputs") &&
                        hiddenMessage.Height >= hiddenMessage.GetPreferredSize(new System.Drawing.Size(hiddenMessage.Width, 0)).Height &&
                        hiddenMessage.Right <= inputPanel.ClientSize.Width)),
                        $"All-hidden state directs users to Settings without clipping at {size}pt");
                    Check(switches.Select(button => button.Text).SequenceEqual(labels) &&
                        inputValues.Select(field => field.Text).SequenceEqual(originalValues),
                        $"Visibility preserves custom names and VCP values for mask {mask} at {size}pt");
                    Check(Enumerable.Range(0, 3).Where(i => expected[i]).All(i =>
                        switches[i].Bottom <= inputPanel.RowStyles[0].Height &&
                        switches[i].Height >= TextRenderer.MeasureText(switches[i].Text, switches[i].Font,
                            new System.Drawing.Size(Math.Max(1, switches[i].Width - switches[i].Padding.Horizontal - 8), int.MaxValue),
                            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height +
                            switches[i].Padding.Vertical),
                        $"Reflowed custom labels fit for mask {mask} at {size}pt");
                }
                main.Font = originalFont;
            }
            visibilityFields[0].Checked = visibilityFields[2].Checked = false;
            main.SaveInputVisibility(visibilityPath);
            Check(File.ReadAllText(combinedNamesPath) == originalNamesFile,
                "Saving visibility leaves the persisted custom-name file unchanged");
            using (var restarted = new MainForm())
            {
                restarted.LoadInputNames(combinedNamesPath);
                restarted.LoadInputVisibility(visibilityPath);
                var restoredFields = (CheckBox[])typeof(MainForm).GetField("visibilityFields",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(restarted)!;
                var restoredTray = (ToolStripMenuItem[])typeof(MainForm).GetField("trayInputs",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(restarted)!;
                Check(restoredFields.Select(field => field.Checked).SequenceEqual([false, true, false]) &&
                    restoredTray.Select(item => item.Available).SequenceEqual([false, true, false]) &&
                    restoredTray.Select(item => item.Text).SequenceEqual(labels.Select(label => label.Replace("&", "&&"))),
                    "Restart restores saved visibility independently of custom names and VCP mappings");
            }
            foreach (var field in visibilityFields)
                field.Checked = true;
            main.SaveInputVisibility(visibilityPath);
            Check(switches.All(button => button.Visible) && !hiddenMessage.Visible,
                "All hidden inputs remain available in Settings and can be restored");
        }
        finally
        {
            main.Hide();
            if (Directory.Exists(visibilityDirectory)) Directory.Delete(visibilityDirectory, recursive: true);
        }
        typeof(MainForm).GetMethod("SetStatus",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, [""]);
        main.Show();
        foreach (float size in new[] { 10f, 16f, 24f, 10f })
        {
            using var font = new System.Drawing.Font("Segoe UI", size);
            try
            {
                main.Font = font;
                _ = main.Handle;
                typeof(MainForm).GetMethod("SetScaledMinimumSize",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
                main.PerformLayout();
                var mainContent = mainLayout;
                mainContent.PerformLayout();
                Check(menu.Bottom <= mainContent.Top,
                    $"Top menu does not overlap main content at {size}pt");
                Check(menu.Font.Size == main.Font.Size,
                    $"Menu follows the main window font at {size}pt");
                advanced.Font = font;
                var namesTabs = advanced.Controls.OfType<TabControl>().Single();
                namesTabs.SelectedIndex = 1;
                _ = advanced.Handle;
                _ = namesTabs.Handle;
                _ = namesTabs.TabPages[1].Handle;
                advanced.PerformAutoScale();
                advanced.PerformLayout();
                namesTabs.PerformLayout();
                namesTabs.TabPages[1].PerformLayout();
                var namesPanel = (TableLayoutPanel)namesTabs.TabPages[1].Controls[0];
                namesPanel.PerformLayout();
                typeof(MainForm).GetMethod("SizeSettingsWindow",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
                Check(nameFields.All(field => field.Width > 0 && field.Right <= namesPanel.ClientSize.Width) &&
                    visibilityFields.All(field => field.Right <= namesPanel.ClientSize.Width &&
                        field.Height >= field.GetPreferredSize(System.Drawing.Size.Empty).Height) &&
                    namesPanel.Controls.OfType<Label>().All(label => label.Right <= namesPanel.ClientSize.Width),
                    $"Custom-name settings fit without clipping at {size}pt " +
                    $"(panel={namesPanel.ClientSize}, labels={string.Join("; ", namesPanel.Controls.OfType<Label>().Select(label => label.Bounds))})");
                foreach (var page in new[] { settingsTabs.TabPages[0], settingsTabs.TabPages[1],
                technicalTabs.TabPages[0], technicalTabs.TabPages[1] })
                {
                    settingsTabs.SelectedIndex = settingsTabs.TabPages.Contains(page)
                        ? settingsTabs.TabPages.IndexOf(page) : 2;
                    technicalTabs.SelectedIndex = technicalTabs.TabPages.Contains(page)
                        ? technicalTabs.TabPages.IndexOf(page) : 0;
                    _ = page.Handle;
                    advanced.PerformLayout();
                    settingsTabs.PerformLayout();
                    technicalTabs.PerformLayout();
                    page.PerformLayout();
                    typeof(MainForm).GetMethod("SizeSettingsWindow",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(main, null);
                    var panel = page.Controls.OfType<TableLayoutPanel>().Single();
                    panel.PerformLayout();
                    foreach (var actions in panel.Controls.OfType<TableLayoutPanel>())
                        actions.PerformLayout();
                    Check(panel.Controls.Cast<Control>().All(control =>
                        control.Right <= panel.ClientSize.Width &&
                        (control is not Label && control is not CheckBox ||
                            control.Height >= control.GetPreferredSize(new System.Drawing.Size(control.Width, 0)).Height)) &&
                        panel.Controls.OfType<TableLayoutPanel>().SelectMany(actions => actions.Controls.Cast<Control>())
                            .All(control => control.Right <= control.Parent!.ClientSize.Width &&
                                control.Bottom <= control.Parent.ClientSize.Height &&
                                control.Height >= control.GetPreferredSize(new System.Drawing.Size(control.Width, 0)).Height) &&
                        panel.Top >= page.Padding.Top &&
                        (panel.Bottom <= page.ClientSize.Height - page.Padding.Bottom &&
                            !page.AutoScroll && !page.VerticalScroll.Visible && !page.HorizontalScroll.Visible ||
                            advanced.Height == Screen.FromControl(advanced).WorkingArea.Height &&
                            panel.GetPreferredSize(new System.Drawing.Size(panel.Width, 0)).Height +
                                page.Padding.Vertical > page.ClientSize.Height &&
                            page.AutoScroll && page.VerticalScroll.Visible),
                        $"{page.Text} preferences, descriptions, and actions fit without scrolling at {size}pt " +
                        $"(page={page.ClientSize}, panel={panel.Bounds}, scroll={page.VerticalScroll.Visible}, " +
                        $"controls={string.Join("; ", panel.Controls.Cast<Control>().Select(control => $"{control.Text}: {control.Bounds}, preferred={control.GetPreferredSize(new System.Drawing.Size(control.Width, 0))}"))}, " +
                        $"buttons={string.Join("; ", panel.Controls.OfType<TableLayoutPanel>().SelectMany(actions => actions.Controls.Cast<Control>()).Select(control => $"{control.Text}: {control.Bounds}, parent={control.Parent!.ClientSize}"))})");
                }
                technicalTabs.SelectedIndex = 1;
                var diagnosticPage = technicalTabs.SelectedTab!;
                diagnosticPage.PerformLayout();
                var diagnosticPanel = diagnosticPage.Controls.OfType<TableLayoutPanel>().Single();
                diagnosticPanel.PerformLayout();
                Check((!diagnosticPage.VerticalScroll.Visible && !diagnosticPage.HorizontalScroll.Visible ||
                    advanced.Height == Screen.FromControl(advanced).WorkingArea.Height &&
                    diagnosticPanel.GetPreferredSize(new System.Drawing.Size(diagnosticPanel.Width, 0)).Height +
                        diagnosticPage.Padding.Vertical > diagnosticPage.ClientSize.Height &&
                    diagnosticPage.AutoScroll && diagnosticPage.VerticalScroll.Visible) &&
                    diagnosticPanel.Controls.Cast<Control>().All(control =>
                        control.Right <= diagnosticPanel.ClientSize.Width &&
                        control.Bottom <= diagnosticPanel.ClientSize.Height) &&
                    advanced.Width <= Screen.FromControl(advanced).WorkingArea.Width &&
                    advanced.Height <= Screen.FromControl(advanced).WorkingArea.Height,
                    $"Diagnostics controls fit and settings stay within the screen at {size}pt " +
                    $"(window={advanced.Size}, area={Screen.FromControl(advanced).WorkingArea}, " +
                    $"panel={diagnosticPanel.ClientSize}, controls={string.Join("; ", diagnosticPanel.Controls.Cast<Control>().Select(control => control.Bounds))})");
                Check(main.Width >= main.MinimumSize.Width && main.ClientSize.Width >= 440 * main.DeviceDpi / 96d,
                    $"Content sizing preserves the designed minimum window width at {size}pt " +
                    $"(width={main.Width}, minimum={main.MinimumSize}, client={main.ClientSize}, DPI={main.DeviceDpi})");
                Check(mainContent.Bottom <= main.ClientSize.Height || main.VerticalScroll.Visible,
                    $"Fixed main window fits content or allows scrolling at {size}pt");
                Check(mainContent.Controls.Cast<Control>().Where(control => control.Visible).All(control =>
                    control.Right <= mainContent.ClientSize.Width - mainContent.Padding.Right),
                    $"Compact main rows stay within the window width at {size}pt");
                Check(main.ClientSize.Height - mainContent.Bottom <= 1,
                    $"Main window has no unused height below its content at {size}pt");
                foreach (var button in switches)
                {
                    var required = TextRenderer.MeasureText(button.Text, button.Font,
                        new System.Drawing.Size(Math.Max(1, button.Width - button.Padding.Horizontal - 8), int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                    Check(button.Height >= required.Height + button.Padding.Vertical &&
                        button.Right <= button.Parent!.ClientSize.Width && button.Bottom <= button.Parent.ClientSize.Height,
                        $"Maximum-length custom button names wrap without clipping at {size}pt");
                }
                int compactHeight = main.ClientSize.Height;
                string compactLayout = $"client={main.ClientSize}, content={mainContent.Bounds}, " +
                    $"rows={string.Join(",", mainContent.GetRowHeights())}, inputs={inputPanel.Bounds}, " +
                    $"scroll={main.VerticalScroll.Visible}";
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
                    $"Clearing status restores compact height at {size}pt " +
                    $"(before={compactLayout}, after={main.ClientSize}, content={mainContent.Bounds}, " +
                    $"rows={string.Join(",", mainContent.GetRowHeights())}, inputs={inputPanel.Bounds}, scroll={main.VerticalScroll.Visible})");
                foreach (var button in Descendants(main).OfType<Button>().Where(button => button.Visible))
                {
                    using var bitmap = new System.Drawing.Bitmap(button.Width, button.Height);
                    button.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size));
                }
                var (dialog, suppress) = main.CreateInputConfirmation(
                    "Samsung Odyssey OLED G9 - a long monitor description", 0, 15);
                using (dialog)
                {
                    // Create handles and lay out without showing the dialog or discovering monitors.
                    _ = dialog.Handle;
                    dialog.PerformAutoScale();
                    dialog.PerformLayout();
                    var content = (TableLayoutPanel)dialog.Controls[0];
                    content.PerformLayout();
                    var warning = (Label)content.GetControlFromPosition(0, 1)!;
                    var heading = (Label)content.GetControlFromPosition(0, 0)!;
                    Check(heading.Text == $"Switch to {labels[0]}?" && !heading.UseMnemonic &&
                        heading.Height >= heading.GetPreferredSize(new System.Drawing.Size(heading.Width, 0)).Height &&
                        heading.Right <= content.ClientSize.Width && heading.Bottom <= warning.Top,
                        $"Confirmation uses the saved name and wraps without overlap at {size}pt " +
                        $"(text={heading.Text}, heading={heading.Bounds}, preferred={heading.GetPreferredSize(new System.Drawing.Size(heading.Width, 0))}, " +
                        $"content={content.ClientSize}, warning={warning.Bounds})");
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
            finally
            {
                main.Font = originalFont;
                advanced.Font = originalFont;
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

internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    public override DateTimeOffset GetUtcNow() => current;
    internal void Advance(TimeSpan elapsed) => current += elapsed;
}

internal sealed class CallbackReleaseHandler(
    Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
{
    internal int Requests;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(respond(request, cancellationToken));
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
