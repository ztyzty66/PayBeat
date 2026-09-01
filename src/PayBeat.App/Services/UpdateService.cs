using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using PayBeat.App.Helpers;

namespace PayBeat.App.Services;

/// <summary>
/// Minimal in-app update service. Checks GitHub Releases for a newer stable version,
/// downloads the installer, verifies SHA256, and launches it after the current process exits.
/// All network I/O uses an injectable HttpClient for testability.
/// </summary>
public class UpdateService
{
    private readonly HttpClient _http;
    private const string LatestReleaseUrl = "https://api.github.com/repos/ztyzty66/PayBeat/releases/latest";
    private const string UserAgent = "PayBeat-InAppUpdate";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public UpdateService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Result of a version check.</summary>
    public record UpdateResult(bool Available, string? RemoteVersion, string? DownloadUrl, string? Sha256Digest, string? ReleaseNotes);

    /// <summary>Checks GitHub for a newer stable release. Returns null on any network/parse error.</summary>
    public async Task<UpdateResult?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Reject draft / prerelease.
            if (root.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean()) return null;

            // Parse tag.
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrEmpty(tag)) return null;
            var remoteVersion = StripTagPrefix(tag);
            if (!IsValidStableTag(remoteVersion)) return null;

            // Compare versions.
            if (!IsRemoteNewer(AppVersion.Current, remoteVersion)) return null;

            // Find installer asset.
            string? installerUrl = null;
            string? sha256 = null;
            var expectedAssetName = $"PayBeat-{remoteVersion}-setup-win-x64.exe";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : "";
                    if (name != expectedAssetName) continue;

                    installerUrl = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
                    if (asset.TryGetProperty("digest", out var digestEl))
                    {
                        var digest = digestEl.GetString();
                        if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        {
                            sha256 = digest["sha256:".Length..];
                        }
                    }
                    break;
                }
            }

            if (installerUrl is null || !installerUrl.StartsWith("https://github.com/ztyzty66/PayBeat/releases/download/", StringComparison.OrdinalIgnoreCase))
                return null;

            var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : "";

            return new UpdateResult(true, remoteVersion, installerUrl, sha256, notes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads the installer to a temp directory. Reports progress via callback (0-100).
    /// Returns the local file path, or null on failure.</summary>
    public async Task<string?> DownloadInstallerAsync(string url, Action<int>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var version = ExtractVersionFromUrl(url);
            var dir = Path.Combine(Path.GetTempPath(), "PayBeat", "updates", $"v{version}");
            Directory.CreateDirectory(dir);
            var filePath = Path.Combine(dir, Path.GetFileName(new Uri(url).AbsolutePath));

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Invoke((int)(read * 100 / total));
            }
            progress?.Invoke(100);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Verifies the SHA256 of a file against the expected hex digest.</summary>
    public static bool VerifySha256(string filePath, string expectedHex)
    {
        try
        {
            var hash = SHA256.HashData(File.ReadAllBytes(filePath));
            var actual = Convert.ToHexString(hash).ToLowerInvariant();
            return string.Equals(actual, expectedHex.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches a PowerShell helper that waits for the current process to exit,
    /// then starts the installer. Returns true if the helper was successfully started.
    /// </summary>
    public bool LaunchInstallerAfterExit(string installerPath)
    {
        try
        {
            var pid = Environment.ProcessId;
            var ps = $"Wait-Process -Id {pid}; Start-Process -FilePath '{installerPath}' -Verb RunAs";
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{ps}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Strips the 'v' prefix from a tag name.</summary>
    public static string StripTagPrefix(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;

    /// <summary>Returns true if the tag looks like a stable X.Y.Z version.</summary>
    public static bool IsValidStableTag(string version)
    {
        if (string.IsNullOrEmpty(version)) return false;
        var parts = version.Split('.');
        if (parts.Length < 2 || parts.Length > 4) return false;
        return parts.All(p => int.TryParse(p, out _));
    }

    /// <summary>
    /// Parses the numeric core X.Y.Z from a version string that may contain
    /// prerelease suffixes (e.g. "1.0.1-alpha.1") or build metadata ("1.0.1+build").
    /// Returns null if parsing fails.
    /// </summary>
    public static (int Major, int Minor, int Patch)? ParseVersionCore(string version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        // Strip metadata (+...) and prerelease (-...).
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        var minus = version.IndexOf('-');
        if (minus >= 0) version = version[..minus];
        var parts = version.Split('.');
        if (parts.Length < 2 || parts.Length > 3) return null;
        if (!int.TryParse(parts[0], out var major)) return null;
        if (!int.TryParse(parts[1], out var minor)) return null;
        var patch = parts.Length >= 3 && int.TryParse(parts[2], out var p) ? p : 0;
        return (major, minor, patch);
    }

    /// <summary>Returns true if remoteVersion is strictly newer than currentVersion.</summary>
    public static bool IsRemoteNewer(string currentVersion, string remoteVersion)
    {
        var current = ParseVersionCore(currentVersion);
        var remote = ParseVersionCore(remoteVersion);
        if (current is null || remote is null) return false;
        if (remote.Value.Major != current.Value.Major) return remote.Value.Major > current.Value.Major;
        if (remote.Value.Minor != current.Value.Minor) return remote.Value.Minor > current.Value.Minor;
        return remote.Value.Patch > current.Value.Patch;
    }

    private static string ExtractVersionFromUrl(string url)
    {
        // URL: https://github.com/ztyzty66/PayBeat/releases/download/v1.0.2/PayBeat-1.0.2-setup-win-x64.exe
        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        // PayBeat-1.0.2-setup-win-x64.exe → 1.0.2
        var dash = fileName.IndexOf('-');
        var secondDash = fileName.IndexOf('-', dash + 1);
        return dash >= 0 && secondDash > dash ? fileName[(dash + 1)..secondDash] : "unknown";
    }
}
