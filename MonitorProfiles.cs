using System.Globalization;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OdysseyInputSwitcher;

internal sealed class MonitorInput
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Visible { get; set; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter<InputConfigurationSource>))]
internal enum InputConfigurationSource { Unknown, Advertised, UserConfigured }

internal sealed class MonitorProfile
{
    public InputConfigurationSource Source { get; set; }
    public List<MonitorInput> Inputs { get; set; } = [];
    internal bool Configured => Source == InputConfigurationSource.UserConfigured;
}

internal sealed record MonitorProfileDiscovery(MonitorProfile Profile, bool Remembered, Exception? Error);

internal static class MonitorProfiles
{
    internal static MonitorProfileDiscovery GetOrDiscover(IMonitorConnection monitor, string? identity,
        IReadOnlyDictionary<string, MonitorProfile> profiles)
    {
        if (identity is not null && profiles.TryGetValue(identity, out var saved))
            return new(saved, true, null);
        try { return new(Discover(monitor), false, null); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return new(new MonitorProfile(), false, error);
        }
    }

    internal static MonitorProfile Discover(IMonitorConnection monitor) =>
        FromCapabilities(monitor.ReadCapabilities());

    internal static MonitorProfile FromCapabilities(string capabilities)
    {
        string[] tokens = Regex.Matches(capabilities, @"[()]|[^\s()]+")
            .Select(match => match.Value).ToArray();
        int depth = 0;
        int vcpDepth = -1;
        List<uint>? inputs = null;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token == "(")
            {
                depth++;
                continue;
            }
            if (token == ")")
            {
                if (depth == vcpDepth)
                    vcpDepth = -1;
                if (--depth < 0)
                    throw new InvalidOperationException("Monitor capabilities have unbalanced parentheses.");
                continue;
            }
            if (depth <= 1 && token.Equals("vcp", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < tokens.Length && tokens[i + 1] == "(")
                vcpDepth = depth + 1;
            if (depth != vcpDepth || !token.Equals("60", StringComparison.OrdinalIgnoreCase))
                continue;
            if (inputs is not null)
                throw new InvalidOperationException("Monitor capabilities advertise multiple input-source lists.");
            inputs = [];
            if (i + 1 >= tokens.Length || tokens[i + 1] != "(")
                continue;
            i += 2;
            for (; i < tokens.Length && tokens[i] != ")"; i++)
            {
                if (!uint.TryParse(tokens[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                    out uint value) || value > ushort.MaxValue)
                    throw new InvalidOperationException("Monitor capabilities contain an invalid input-source value.");
                if (!inputs.Contains(value))
                    inputs.Add(value);
            }
            if (i >= tokens.Length)
                throw new InvalidOperationException("Monitor capabilities have an incomplete input-source list.");
        }
        if (depth != 0)
            throw new InvalidOperationException("Monitor capabilities have unbalanced parentheses.");
        if (inputs is null || inputs.Count == 0)
            throw new InvalidOperationException(
                "The monitor did not advertise input-source values. DDC/CI support does not guarantee input switching.");
        return new MonitorProfile
        {
            Source = InputConfigurationSource.Advertised,
            Inputs = inputs.Select(value => new MonitorInput
            {
                Id = $"0x{value:X4}", Name = $"Input 0x{value:X4}", Value = $"0x{value:X4}"
            }).ToList()
        };
    }

    internal static string ReviewExplanation(MonitorProfile profile) => profile.Inputs.Count == 0
        ? "This monitor didn't report usable input settings. Open Settings > Inputs... and add names and values " +
            "from its documentation or a known-working configuration. Don't guess values."
        : profile.Source == InputConfigurationSource.Advertised
        ? "The monitor advertised these values, but they may not match its physical inputs. " +
            "Review and name them in Settings > Inputs..., then save before switching."
        : "These input values haven't been reviewed for this monitor. Older profiles may have been discovered " +
            "automatically; imported presets may not apply. Review them in Settings > Inputs..., then save before switching.";

    internal static string Summary(MonitorProfile profile) => profile.Source switch
    {
        InputConfigurationSource.UserConfigured => "User-configured inputs (not hardware-verified)",
        InputConfigurationSource.Advertised => "Advertised values (not reviewed or hardware-verified)",
        _ => "Unknown input provenance (review required)"
    };

    internal static void Validate(MonitorProfile profile)
    {
        if (!Enum.IsDefined(profile.Source))
            throw new InvalidOperationException("Saved monitor input provenance is invalid.");
        if (profile.Inputs is null)
            throw new InvalidOperationException("Saved monitor inputs are missing.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in profile.Inputs)
        {
            if (input is null || string.IsNullOrWhiteSpace(input.Id) || !identities.Add(input.Id))
                throw new InvalidOperationException("Monitor inputs need unique nonempty identities.");
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > InputDisplayNames.MaximumLength ||
                input.Name.Any(char.IsControl))
                throw new InvalidOperationException(
                    $"Input names need 1-{InputDisplayNames.MaximumLength} characters without control characters.");
            if (input.Value is null || (input.Value.Trim().Length != 0 && !InputValue.TryParse(input.Value, out _)))
                throw new InvalidOperationException($"{input.Name} needs a value from 0 to 65535, decimal or 0x hex.");
        }
    }

    internal static Dictionary<string, MonitorProfile> Load(string path)
    {
        var profiles = new Dictionary<string, MonitorProfile>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return profiles;
        var saved = JsonSerializer.Deserialize<Dictionary<string, MonitorProfile?>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Saved monitor profiles are empty.");
        foreach (var (identity, profile) in saved)
        {
            if (string.IsNullOrWhiteSpace(identity) || profile is null)
                throw new InvalidOperationException("Saved monitor profile identity or configuration is missing.");
            Validate(profile);
            if (!profiles.TryAdd(identity, profile))
                throw new InvalidOperationException("Saved monitor profile identities are ambiguous.");
        }
        return profiles;
    }

    internal static MonitorProfile FromLegacy(Dictionary<string, string> values,
        Dictionary<string, string> names, Dictionary<string, bool> visibility) => new()
    {
        Source = InputConfigurationSource.UserConfigured,
        Inputs = values.Select(entry => new MonitorInput
        {
            Id = entry.Key, Name = names[entry.Key], Value = entry.Value, Visible = visibility[entry.Key]
        }).ToList()
    };
}
