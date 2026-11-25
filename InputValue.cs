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
