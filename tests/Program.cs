using System.Runtime.InteropServices;
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
