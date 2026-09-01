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
    private const string TrustedDownloadPrefix = "https://github.com/ztyzty66/PayBeat/releases/download/";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public UpdateService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Check result status.</summary>
    public enum UpdateCheckStatus { Available, UpToDate, Error }

    /// <summary>Result of a version check.</summary>
    public record UpdateCheckResult(UpdateCheckStatus Status, string? RemoteVersion, string? DownloadUrl, string Sha256Digest, string? ReleaseNotes);

    /// <summary>
    /// Checks GitHub for a newer stable release. Deterministic: takes currentVersion
    /// as parameter so tests are not coupled to AppVersion.Current.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return EvaluateReleaseJson(json, currentVersion);
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, null, null, "", null);
        }
    }

    /// <summary>
    /// Pure function: evaluates a GitHub release JSON response against a current version.
    /// No network, no AppVersion dependency — fully deterministic for testing.
    /// </summary>
    public static UpdateCheckResult EvaluateReleaseJson(string json, string currentVersion)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Reject draft / prerelease.
            if (root.TryGetProperty("draft", out var draftEl) && draftEl.GetBoolean())
                return new(UpdateCheckStatus.Error, null, null, "", null);
            if (root.TryGetProperty("prerelease", out var preEl) && preEl.GetBoolean())
                return new(UpdateCheckStatus.Error, null, null, "", null);

            // Parse tag — must be strict vX.Y.Z.
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("v"))
                return new(UpdateCheckStatus.Error, null, null, "", null);
            var remoteVersion = tag[1..];
            if (!IsValidStableVersion(remoteVersion))
                return new(UpdateCheckStatus.Error, null, null, "", null);

            // Compare versions.
            if (!IsRemoteNewer(currentVersion, remoteVersion))
                return new(UpdateCheckStatus.UpToDate, remoteVersion, null, "", null);

            // Find installer asset with valid SHA256 digest.
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
                        if (IsValidSha256Digest(digest))
                        {
                            sha256 = digest!["sha256:".Length..];
                        }
                    }
                    break;
                }
            }

            // Installer URL must be trusted.
            if (installerUrl is null || !IsTrustedDownloadUrl(installerUrl))
                return new(UpdateCheckStatus.Error, remoteVersion, null, "", null);

            // SHA256 is mandatory.
            if (sha256 is null)
                return new(UpdateCheckStatus.Error, remoteVersion, installerUrl, "", null);

            var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : "";
            return new UpdateCheckResult(UpdateCheckStatus.Available, remoteVersion, installerUrl, sha256, notes);
        }
        catch
        {
            return new(UpdateCheckStatus.Error, null, null, "", null);
        }
    }

    /// <summary>Validates that a digest string is exactly sha256: followed by 64 hex chars.</summary>
    public static bool IsValidSha256Digest(string? digest)
    {
        if (string.IsNullOrEmpty(digest)) return false;
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return false;
        var hex = digest["sha256:".Length..];
        if (hex.Length != 64) return false;
        return hex.All(c => "0123456789abcdefABCDEF".Contains(c));
    }

    /// <summary>Validates that a download URL is from the trusted GitHub release path.</summary>
    public static bool IsTrustedDownloadUrl(string url)
    {
        if (!url.StartsWith(TrustedDownloadPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        return fileName.StartsWith("PayBeat-") && fileName.EndsWith("-setup-win-x64.exe");
    }

    /// <summary>Downloads the installer to a trusted temp directory. Reports progress via callback (0-100).
    /// Returns the local file path, or null on failure.</summary>
    public async Task<string?> DownloadInstallerAsync(string url, Action<int>? progress = null, CancellationToken ct = default)
    {
        // Fail-closed: validate URL at download entry.
        if (!IsTrustedDownloadUrl(url)) return null;

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
    /// Validates that the installer path is within the trusted update root and the file exists.
    /// </summary>
    public static bool IsTrustedInstallerPath(string installerPath)
    {
        if (string.IsNullOrEmpty(installerPath)) return false;
        var fullPath = Path.GetFullPath(installerPath);
        var updateRoot = Path.Combine(Path.GetTempPath(), "PayBeat", "updates");
        if (!fullPath.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = Path.GetFileName(fullPath);
        return fileName.StartsWith("PayBeat-") && fileName.EndsWith("-setup-win-x64.exe") && File.Exists(fullPath);
    }

    /// <summary>
    /// Launches a PowerShell helper that waits for the current process to exit,
    /// then starts the installer. Uses ArgumentList for safe parameter passing.
    /// </summary>
    public bool LaunchInstallerAfterExit(string installerPath)
    {
        if (!IsTrustedInstallerPath(installerPath)) return false;
        try
        {
            var pid = Environment.ProcessId;
            var script = $"Wait-Process -Id {pid}; Start-Process -FilePath '{installerPath}' -Verb RunAs";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Strips the 'v' prefix from a tag name.</summary>
    public static string StripVPrefix(string tag) =>
        tag.StartsWith("v") ? tag[1..] : tag;

    /// <summary>Returns true if the version is a strict X.Y.Z with non-negative parts.</summary>
    public static bool IsValidStableVersion(string version)
    {
        if (string.IsNullOrEmpty(version)) return false;
        var parts = version.Split('.');
        if (parts.Length != 3) return false;
        return parts.All(p => int.TryParse(p, out var v) && v >= 0);
    }

    /// <summary>
    /// Parses the numeric core X.Y.Z from a version string that may contain
    /// prerelease suffixes or build metadata.
    /// </summary>
    public static (int Major, int Minor, int Patch)? ParseVersionCore(string version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        var minus = version.IndexOf('-');
        if (minus >= 0) version = version[..minus];
        var parts = version.Split('.');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var major) || major < 0) return null;
        if (!int.TryParse(parts[1], out var minor) || minor < 0) return null;
        if (!int.TryParse(parts[2], out var patch) || patch < 0) return null;
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
        var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        var dash = fileName.IndexOf('-');
        var secondDash = fileName.IndexOf('-', dash + 1);
        return dash >= 0 && secondDash > dash ? fileName[(dash + 1)..secondDash] : "unknown";
    }
}
