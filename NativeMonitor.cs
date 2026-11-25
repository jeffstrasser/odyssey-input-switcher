using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OdysseyInputSwitcher;

internal sealed class PhysicalMonitorHandle : SafeHandle
{
    private readonly Func<nint, bool> release;
    internal int ReleaseError { get; private set; }
    // Windows can return zero as a valid physical-monitor handle (observed locally).
    public override bool IsInvalid => false;

    internal PhysicalMonitorHandle(nint handle) : this(handle, NativeMethods.DestroyPhysicalMonitor) { }

    internal PhysicalMonitorHandle(nint handle, Func<nint, bool> release) : base(0, true)
    {
        this.release = release;
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        if (release(handle))
            return true;
        ReleaseError = Marshal.GetLastWin32Error();
        Trace.TraceError($"DestroyPhysicalMonitor failed: {ReleaseError}");
        return false;
    }
}

internal sealed class NativeMonitor : IMonitorConnection
{
    private readonly PhysicalMonitorHandle handle;
    public string Description { get; }
    internal int ReleaseError => handle.ReleaseError;

    internal NativeMonitor(nint handle, string description)
    {
        this.handle = new PhysicalMonitorHandle(handle);
        Description = description;
    }

    public uint ReadInput()
    {
        if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, 0x60, out _, out uint current, out _))
            throw NativeMethods.Error("Read VCP 0x60");
        return current;
    }

    public string ReadCapabilities()
    {
        if (!NativeMethods.GetCapabilitiesStringLength(handle, out uint length))
            throw NativeMethods.Error("Get capabilities length");
        if (length == 0 || length > 65536)
            throw new InvalidOperationException($"Invalid capabilities length: {length}.");
        byte[] buffer = new byte[length];
        if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(handle, buffer, length))
            throw NativeMethods.Error("Read capabilities");
        int end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
            throw new InvalidOperationException("Capabilities reply has no terminating NUL.");
        return Encoding.ASCII.GetString(buffer, 0, end);
    }

    public void WriteInput(uint value)
    {
        if (!NativeMethods.SetVCPFeature(handle, 0x60, value))
            throw NativeMethods.Error("Send VCP 0x60 (the monitor may already have disconnected)");
    }

    public void Dispose() => handle.Dispose();

    public override string ToString() => Description;
}

internal sealed class MonitorInventory : IDisposable
{
    internal List<NativeMonitor> Monitors { get; } = [];
    internal List<string> Messages { get; } = [];

    internal static MonitorInventory Enumerate()
    {
        var inventory = new MonitorInventory();
        // Never let managed exceptions cross the unmanaged callback boundary.
        Exception? callbackError = null;
        NativeMethods.MonitorEnumProc callback = (logical, _, _, _) =>
        {
            try
            {
                inventory.AddLogicalMonitor(logical);
                return true;
            }
            catch (Exception ex)
            {
                callbackError = ex;
                return false;
            }
        };
        try
        {
            bool success = NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
            if (callbackError is not null)
                throw new InvalidOperationException("Monitor enumeration callback failed.", callbackError);
            if (!success)
                throw NativeMethods.Error("Enumerate displays");
            return inventory;
        }
        catch
        {
            inventory.Dispose();
            throw;
        }
    }

    private void AddLogicalMonitor(nint logical)
    {
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(logical, ref info))
        {
            Messages.Add(NativeMethods.Error("Get display identity").Message);
            return;
        }
        string identity = $"{info.Device} [{info.Monitor.Left},{info.Monitor.Top}]";
        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logical, out uint count))
        {
            Messages.Add($"{identity}: {NativeMethods.Error("Get physical monitor count").Message}");
            return;
        }
        if (count == 0)
        {
            Messages.Add($"{identity}: no physical monitor handles (virtual displays may not expose DDC/CI).");
            return;
        }
        if (count > 256)
            throw new InvalidOperationException($"{identity}: implausible physical monitor count {count}.");
        var physical = new NativeMethods.PhysicalMonitor[count];
        if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logical, count, physical))
        {
            Messages.Add($"{identity}: {NativeMethods.Error("Get physical monitor handles").Message}");
            return;
        }
        var claimed = new bool[physical.Length];
        try
        {
            for (int index = 0; index < physical.Length; index++)
            {
                var monitor = new NativeMonitor(physical[index].Handle,
                    $"{identity} / physical {index + 1}: {physical[index].Description}");
                claimed[index] = true;
                try
                {
                    Monitors.Add(monitor);
                }
                catch
                {
                    monitor.Dispose();
                    throw;
                }
            }
        }
        finally
        {
            for (int index = 0; index < physical.Length; index++)
            {
                if (!claimed[index] &&
                    !NativeMethods.DestroyPhysicalMonitor(physical[index].Handle))
                    Messages.Add(NativeMethods.Error("Release unclaimed physical monitor handle").Message);
            }
        }
    }

    public void Dispose()
    {
        foreach (var monitor in Monitors)
        {
            monitor.Dispose();
            if (monitor.ReleaseError != 0)
                Messages.Add($"Release {monitor.Description}: Windows error {monitor.ReleaseError}.");
        }
        Monitors.Clear();
    }
}

internal static class NativeMethods
{
    internal delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PhysicalMonitor
    {
        internal nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Description;
    }

    internal static Win32Exception Error(string operation)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code,
            $"{operation}: {new Win32Exception(code).Message} (Windows error {code}).");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);

    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count,
        [Out] PhysicalMonitor[] physicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyPhysicalMonitor(nint monitor);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVCPFeatureAndVCPFeatureReply(PhysicalMonitorHandle monitor, byte code,
        out uint type, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetVCPFeature(PhysicalMonitorHandle monitor, byte code, uint value);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCapabilitiesStringLength(PhysicalMonitorHandle monitor, out uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CapabilitiesRequestAndCapabilitiesReply(PhysicalMonitorHandle monitor,
        [Out] byte[] reply, uint length);
}
