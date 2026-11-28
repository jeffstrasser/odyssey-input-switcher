using System.Runtime.InteropServices;

namespace OdysseyInputSwitcher;

internal sealed record WindowsDisplayTarget(string Display, string InterfacePath, string Name,
    uint NameFlags = 0, ushort Manufacturer = 0, ushort Product = 0);

internal sealed record MonitorIdentity(string? StableId, string Name, bool Identified, string Diagnostics);

internal static class WindowsMonitorIdentity
{
    internal static MonitorIdentity Resolve(string display, string? interfacePath, string deviceName,
        string physicalName, uint physicalCount, IReadOnlyList<WindowsDisplayTarget> targets, bool complete)
    {
        var candidates = targets.Where(target =>
            target.Display.Equals(display, StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = interfacePath is null ? candidates : candidates.Where(target =>
            target.InterfacePath.Equals(interfacePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        var target = physicalCount == 1 && matches.Length == 1 &&
            (interfacePath is not null || complete) ? matches[0] : null;
        bool conflict = candidates.Length != 0 && target is null;
        string? stableId = physicalCount != 1 || conflict ? null
            : target is not null ? Nonempty(target.InterfacePath) : interfacePath;
        string windowsName = physicalCount == 1 && !conflict ? deviceName : "";
        var names = new[]
        {
            (Name: target?.Name ?? "", Source: "Windows display configuration / EDID"),
            (Name: windowsName, Source: "Windows EnumDisplayDevices"),
            (Name: physicalName, Source: "DDC/CI physical-monitor description")
        };
        var chosen = names.FirstOrDefault(item => !IsGeneric(item.Name));
        bool identified = chosen.Name is not null;
        string name = identified ? chosen.Name!.Trim() : "Unidentified monitor";
        string diagnostics = $"Physical-monitor description: {physicalName}\r\n" +
            $"Windows device description: {deviceName}\r\n" +
            $"Windows display target: {target?.Name ?? "unavailable or ambiguous"}\r\n" +
            $"Display name source: {(identified ? chosen.Source : "no model name available")}\r\n" +
            $"Identity match: {(stableId is null ? "unavailable or ambiguous" : "unique Windows monitor interface")}";
        if (target is not null)
            diagnostics += $"\r\nWindows target-name flags: 0x{target.NameFlags:X}; " +
                $"EDID manufacturer/product: {((target.NameFlags & 4) != 0
                    ? $"0x{target.Manufacturer:X4}/0x{target.Product:X4}" : "not reported")}";
        return new(stableId, name, identified, diagnostics);
    }

    private static string? Nonempty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    internal static bool IsGeneric(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
            return true;
        string normalized = string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return normalized is "genericpnpmonitor" or "genericnonpnpmonitor" or "genericmonitor"
            or "pnpmonitor" or "monitor" or "defaultmonitor";
    }

    internal static (List<WindowsDisplayTarget> Targets, List<string> Errors) ReadActiveTargets()
    {
        const uint activePaths = 2;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int error = IdentityNative.GetDisplayConfigBufferSizes(activePaths, out uint pathCount, out uint modeCount);
            if (error != 0)
                throw NativeMethods.Error("Read Windows display-configuration sizes", error);
            if (pathCount > 256 || modeCount > 1024)
                throw new InvalidOperationException("Windows reported implausible display-configuration sizes.");
            var paths = new IdentityNative.PathInfo[pathCount];
            var modes = new IdentityNative.ModeInfo[modeCount];
            error = IdentityNative.QueryDisplayConfig(activePaths, ref pathCount, paths, ref modeCount, modes, 0);
            // Display topology can change between buffer sizing and the query.
            if (error == 122)
                continue;
            if (error != 0)
                throw NativeMethods.Error("Read active Windows display configuration", error);
            List<WindowsDisplayTarget> targets = [];
            List<string> errors = [];
            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new IdentityNative.SourceName
                {
                    Header = new()
                    {
                        Type = 1, Size = (uint)Marshal.SizeOf<IdentityNative.SourceName>(),
                        Adapter = path.Source.Adapter, Id = path.Source.Id
                    }
                };
                var target = new IdentityNative.TargetName
                {
                    Header = new()
                    {
                        Type = 2, Size = (uint)Marshal.SizeOf<IdentityNative.TargetName>(),
                        Adapter = path.Target.Adapter, Id = path.Target.Id
                    }
                };
                int sourceError = IdentityNative.GetSourceName(ref source);
                int targetError = IdentityNative.GetTargetName(ref target);
                if (sourceError != 0 || targetError != 0)
                {
                    errors.Add($"Windows display-name inquiry failed: source error {sourceError}, target error {targetError}.");
                    continue;
                }
                targets.Add(new(source.Display ?? "", target.InterfacePath ?? "", target.Name ?? "",
                    target.Flags, target.Manufacturer, target.Product));
            }
            return (targets, errors);
        }
        throw NativeMethods.Error("Windows display configuration kept changing during identification", 122);
    }

    private static class IdentityNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid { internal uint Low; internal int High; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SourceInfo
        {
            internal Luid Adapter;
            internal uint Id, ModeIndex, Status;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TargetInfo
        {
            internal Luid Adapter;
            internal uint Id, ModeIndex, Technology, Rotation, Scaling;
            internal uint RefreshNumerator, RefreshDenominator, Scanline;
            internal int Available;
            internal uint Status;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PathInfo
        {
            internal SourceInfo Source;
            internal TargetInfo Target;
            internal uint Flags;
        }

        // DISPLAYCONFIG_MODE_INFO includes a 48-byte video-signal union; mode contents are not needed here.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct ModeInfo { [FieldOffset(0)] internal uint Type; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Header
        {
            internal uint Type, Size;
            internal Luid Adapter;
            internal uint Id;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct SourceName
        {
            internal Header Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string? Display;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct TargetName
        {
            internal Header Header;
            internal uint Flags, Technology;
            internal ushort Manufacturer, Product;
            internal uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            internal string? Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string? InterfacePath;
        }

        [DllImport("user32.dll")]
        internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);

        [DllImport("user32.dll")]
        internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
            ref uint modeCount, [Out] ModeInfo[] modes, nint topology);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int GetSourceName(ref SourceName name);

        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
        internal static extern int GetTargetName(ref TargetName name);
    }
}
