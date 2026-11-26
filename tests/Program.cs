using System.Runtime.InteropServices;
using System.Text.Json;
using OdysseyInputSwitcher;

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
Console.WriteLine($"PASS: {assertions} hardware-free assertions. No monitor API calls or writes.");

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
