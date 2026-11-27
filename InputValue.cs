using System.Globalization;

namespace OdysseyInputSwitcher;

internal static class InputValue
{
    internal static bool TryParse(string text, out uint value)
    {
        text = text.Trim();
        bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex)
            text = text[2..];
        return uint.TryParse(text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out value) && value <= ushort.MaxValue;
    }
}

internal static class InputSettings
{
    internal static Dictionary<string, string> Resolve(Dictionary<string, string?>? saved)
    {
        // Extracted from the user's working app; confirmed by the user on their monitor.
        var values = new Dictionary<string, string>
        {
            ["DisplayPort"] = "0x0F",
            ["HDMI"] = "0x05",
            ["Micro HDMI"] = "0x06"
        };
        if (saved is null)
            return values;
        foreach (string name in values.Keys.ToArray())
        {
            if (!saved.TryGetValue(name, out string? text))
                continue;
            if (text is null || (text.Trim().Length != 0 && !InputValue.TryParse(text, out _)))
                throw new InvalidOperationException($"Saved value for {name} is invalid.");
            if (text.Trim().Length != 0)
                values[name] = text.Trim();
        }
        return values;
    }
}

internal static class InputDisplayNames
{
    internal const int MaximumLength = 48;

    internal static Dictionary<string, string> Resolve(Dictionary<string, string?>? saved)
    {
        var names = new Dictionary<string, string>
        {
            ["DisplayPort"] = "DisplayPort",
            ["HDMI"] = "HDMI",
            ["Micro HDMI"] = "Micro HDMI"
        };
        if (saved is null)
            return names;
        foreach (string identity in names.Keys.ToArray())
        {
            if (!saved.TryGetValue(identity, out string? text))
                continue;
            string name = text?.Trim() ?? "";
            if (text is null || name.Length == 0 || name.Length > MaximumLength || text.Any(char.IsControl))
                throw new InvalidOperationException(
                    $"{identity} needs a name of 1 to {MaximumLength} characters without line breaks or control characters.");
            names[identity] = name;
        }
        return names;
    }
}

internal interface IMonitorConnection : IDisposable
{
    string Description { get; }
    uint ReadInput();
    string ReadCapabilities();
    void WriteInput(uint value);
}

internal static class InputSwitch
{
    internal static void Send(IMonitorConnection? monitor, uint value)
    {
        if (monitor is null)
            throw new InvalidOperationException("Select a physical monitor first.");
        if (value > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Input values must fit in 16 bits.");
        // A switch can remove the DDC/CI path. Never retry or infer success from a later read.
        monitor.WriteInput(value);
    }
}
