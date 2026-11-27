using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Net;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OdysseyInputSwitcher;

internal sealed record ReleaseUpdate(Version Version, Uri DownloadUrl, string Sha256);

internal sealed class UpdateCheckSchedule(TimeProvider clock)
{
    private DateTimeOffset nextCheck = clock.GetUtcNow();

    internal bool IsDue => clock.GetUtcNow() >= nextCheck;

    internal void CheckStarted() => nextCheck = clock.GetUtcNow().AddHours(24);
}

internal sealed class ReleaseUpdater : IDisposable
{
    internal const string RepositoryUrl = "https://github.com/jeffstrasser/odyssey-input-switcher";
    internal const string ExecutableName = "OdysseyInputSwitcher.exe";
    internal static Version CurrentVersion => typeof(ReleaseUpdater).Assembly.GetName().Version
        ?? throw new InvalidOperationException("The application version is missing.");
    private readonly HttpClient client;

    internal ReleaseUpdater() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(90) })
    {
    }

    internal ReleaseUpdater(HttpClient client)
    {
        this.client = client;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"OdysseyInputSwitcher/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    internal async Task<ReleaseUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync(
            "https://api.github.com/repos/jeffstrasser/odyssey-input-switcher/releases/latest",
            timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(timeout.Token);
        return ParseRelease(json, CurrentVersion, RuntimeInformation.ProcessArchitecture);
    }

    internal static ReleaseUpdate? ParseRelease(string json, Version currentVersion, Architecture architecture)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version <= NormalizeVersion(currentVersion))
            return null;
        string runtime = architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException("Updates support x64 and ARM64 processes only.")
        };
        string assetName = $"OdysseyInputSwitcher-{runtime}.zip";
        var assets = release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == assetName).ToArray();
        if (assets.Length != 1)
            throw new InvalidDataException($"The latest release must contain exactly one {assetName}.");
        var asset = assets[0];
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        string expectedUrl = $"{RepositoryUrl}/releases/download/{tag}/{assetName}";
        if (url != expectedUrl)
            throw new InvalidDataException("The update download is not a release asset from this repository.");
        string digest = asset.TryGetProperty("digest", out var digestValue)
            ? digestValue.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("The release asset is missing a valid SHA-256 digest.");
        return new ReleaseUpdate(version, new Uri(url), digest[7..]);
    }

    private static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    internal static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^v?([1-9]\d*|\d+\.\d+\.\d+(\.\d+)?)$"))
            throw new InvalidDataException("The release has an unsupported version tag.");
        string number = value.TrimStart('v');
        if (!number.Contains('.'))
            number += ".0.0";
        if (!Version.TryParse(number, out var version))
            throw new InvalidDataException("The release has an unsupported version tag.");
        return NormalizeVersion(version);
    }

    internal static string FormatVersion(Version version)
    {
        version = NormalizeVersion(version);
        return version.Minor == 0 && version.Build == 0 && version.Revision == 0
            ? version.Major.ToString()
            : version.ToString(version.Revision == 0 ? 3 : 4);
    }

    internal async Task<string> StageAsync(ReleaseUpdate update, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        cancellationToken = timeout.Token;
        string directory = UpdateInstaller.CreateDirectory();
        try
        {
            string archivePath = Path.Combine(directory, "download.zip");
            using var response = await client.GetAsync(update.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = File.Create(archivePath))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += count;
                    if (total > 150 * 1024 * 1024)
                        throw new InvalidDataException("The update archive exceeds the 150 MB limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
            }
            await using (var archive = File.OpenRead(archivePath))
            {
                byte[] hash = await SHA256.HashDataAsync(archive, cancellationToken);
                if (!Convert.ToHexString(hash).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The update archive failed SHA-256 verification.");
            }
            await Task.Run(() => ExtractExecutable(archivePath, Path.Combine(directory, "payload.exe")),
                cancellationToken);
            ValidateExecutable(Path.Combine(directory, "payload.exe"), update.Version,
                RuntimeInformation.ProcessArchitecture);
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(archivePath);
            return directory;
        }
        catch (Exception error)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException("Update staging failed and its temporary files could not be removed.",
                    error, cleanupError);
            }
            throw;
        }
    }

    internal static void ExtractExecutable(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries.Where(entry => entry.FullName == ExecutableName).ToArray();
        if (entries.Length != 1 || entries[0].Length is < 2 or > 300 * 1024 * 1024)
            throw new InvalidDataException("The update must contain exactly one root application executable (up to 300 MB).");
        // Extract only the named payload, never paths supplied by the archive.
        entries[0].ExtractToFile(destination);
        using var executable = File.OpenRead(destination);
        if (executable.ReadByte() != 'M' || executable.ReadByte() != 'Z')
            throw new InvalidDataException("The release payload is not a Windows executable.");
    }

    internal static void StartInstaller(string directory)
    {
        UpdateInstaller.ValidateDirectory(directory);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The running executable path is unavailable.");
        // A copied development apphost cannot run without its adjacent DLL/runtime files.
        if (!IsSingleFileBuild())
            throw new InvalidOperationException("In-place updates require a published single-file build. Download a release from " +
                RepositoryUrl + "/releases/latest.");
        string helper = Path.Combine(directory, "installer.exe");
        File.Copy(executable, helper);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false };
        foreach (string argument in new[] { "--apply-update", Environment.ProcessId.ToString(), executable, directory })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The update installer could not be started.");
    }

    internal static void ValidateExecutable(string path, Version expectedVersion, Architecture architecture)
    {
        Machine expectedMachine = architecture switch
        {
            Architecture.X64 => Machine.Amd64,
            Architecture.Arm64 => Machine.Arm64,
            _ => throw new PlatformNotSupportedException("Updates support x64 and ARM64 processes only.")
        };
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            if (headers.PEHeader is null || headers.PEHeader.Magic != PEMagic.PE32Plus ||
                !headers.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage) ||
                headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) ||
                headers.SectionHeaders.Length == 0 ||
                headers.SectionHeaders.Any(section => section.PointerToRawData < 0 || section.SizeOfRawData < 0 ||
                    (long)section.PointerToRawData + section.SizeOfRawData > stream.Length))
                throw new InvalidDataException("The update payload is not a complete Windows executable.");
            if (headers.CoffHeader.Machine != expectedMachine)
                throw new InvalidDataException("The update executable architecture does not match this app.");
        }
        catch (BadImageFormatException error)
        {
            throw new InvalidDataException("The update payload has invalid Windows executable headers.", error);
        }
        var info = FileVersionInfo.GetVersionInfo(path);
        var actualVersion = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        if (actualVersion != NormalizeVersion(expectedVersion))
            throw new InvalidDataException($"The update executable version {actualVersion} does not match release {expectedVersion}.");
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000",
        Justification = "An empty assembly location is used only to detect single-file publishing.")]
    internal static bool IsSingleFileBuild() => string.IsNullOrEmpty(typeof(ReleaseUpdater).Assembly.Location);

    public void Dispose() => client.Dispose();
}
