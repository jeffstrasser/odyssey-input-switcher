namespace OdysseyInputSwitcher;

internal static class MonitorSelection
{
    internal static int Choose(IReadOnlyList<string?> identities, string? defaultIdentity) =>
        identities.Count == 1 ? 0 : FindUnique(identities, defaultIdentity);

    internal static int FindUnique(IReadOnlyList<string?> identities, string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return -1;
        int result = -1;
        for (int i = 0; i < identities.Count; i++)
        {
            if (!string.Equals(identities[i], identity, StringComparison.OrdinalIgnoreCase))
                continue;
            if (result >= 0)
                return -1;
            result = i;
        }
        return result;
    }
}

internal sealed class Preferences
{
    public string? DefaultMonitorId { get; set; }
    public bool SuppressSwitchConfirmation { get; set; }
}
