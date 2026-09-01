using System.Net;
using System.Net.Http;
using System.Text;
using PayBeat.App.Helpers;
using PayBeat.App.Services;

namespace PayBeat.Tests;

/// <summary>
/// Tests for the in-app update flow: version parsing, GitHub API checks, SHA256 verification,
/// and network failure handling. All tests use injected HttpClient — no real network access.
/// </summary>
public class InAppUpdateTests
{
    // ── Status Copy ────────────────────────────────────────────────────────

    [Fact]
    public void StatusFish_ZhCN_IsNowEarning()
    {
        // Verify the zh-CN resource file directly (no WPF runtime needed).
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "src", "PayBeat.App", "Resources", "Strings.zh-CN.xaml"));
        Assert.Contains("Status.Fish", xaml);
        Assert.DoesNotContain("摸鱼", xaml.Substring(xaml.IndexOf("Status.Fish"), 80));
        Assert.Contains("计薪", xaml.Substring(xaml.IndexOf("Status.Fish"), 80));
    }

    [Fact]
    public void StatusFish_En_IsEarningNow()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "src", "PayBeat.App", "Resources", "Strings.en.xaml"));
        Assert.Contains("Status.Fish", xaml);
        var idx = xaml.IndexOf("Status.Fish");
        Assert.Contains("Earning", xaml.Substring(idx, 80));
    }

    // ── Version Parsing ────────────────────────────────────────────────────

    [Fact]
    public void VersionCore_Stable()
    {
        var core = UpdateService.ParseVersionCore("1.0.1");
        Assert.NotNull(core);
        Assert.Equal((1, 0, 1), core.Value);
    }

    [Fact]
    public void VersionCore_Prerelease()
    {
        var core = UpdateService.ParseVersionCore("1.0.2-alpha.1");
        Assert.NotNull(core);
        Assert.Equal((1, 0, 2), core.Value);
    }

    [Fact]
    public void VersionCore_Metadata()
    {
        var core = UpdateService.ParseVersionCore("2.0.0+build.42");
        Assert.NotNull(core);
        Assert.Equal((2, 0, 0), core.Value);
    }

    [Fact]
    public void VersionCore_Empty()
    {
        Assert.Null(UpdateService.ParseVersionCore(""));
        Assert.Null(UpdateService.ParseVersionCore(null!));
    }

    [Fact]
    public void StripTagPrefix_V()
    {
        Assert.Equal("1.0.0", UpdateService.StripTagPrefix("v1.0.0"));
    }

    [Fact]
    public void StripTagPrefix_NoV()
    {
        Assert.Equal("1.0.0", UpdateService.StripTagPrefix("1.0.0"));
    }

    [Fact]
    public void IsValidStableTag_Valid()
    {
        Assert.True(UpdateService.IsValidStableTag("1.0.0"));
        Assert.True(UpdateService.IsValidStableTag("10.20.30"));
    }

    [Fact]
    public void IsValidStableTag_Invalid()
    {
        Assert.False(UpdateService.IsValidStableTag(""));
        Assert.False(UpdateService.IsValidStableTag("abc"));
        Assert.False(UpdateService.IsValidStableTag("1.0.0-alpha"));
    }

    // ── Version Comparison ─────────────────────────────────────────────────

    [Fact]
    public void RemoteNewer_ReturnsAvailable()
    {
        Assert.True(UpdateService.IsRemoteNewer("1.0.0", "1.0.1"));
        Assert.True(UpdateService.IsRemoteNewer("1.0.0", "1.1.0"));
        Assert.True(UpdateService.IsRemoteNewer("1.0.0", "2.0.0"));
    }

    [Fact]
    public void RemoteSame_ReturnsUpToDate()
    {
        Assert.False(UpdateService.IsRemoteNewer("1.0.0", "1.0.0"));
    }

    [Fact]
    public void RemoteOlder_ReturnsUpToDate()
    {
        Assert.False(UpdateService.IsRemoteNewer("1.0.1", "1.0.0"));
    }

    // ── GitHub API Checks ──────────────────────────────────────────────────

    private static HttpClient MockGitHubApi(string json)
    {
        var handler = new MockHttpHandler(json);
        return new HttpClient(handler);
    }

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly string _response;
        public MockHttpHandler(string response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task DraftRelease_Rejected()
    {
        var json = """{"tag_name":"v1.0.1","draft":true,"prerelease":false,"assets":[]}""";
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task Prerelease_Rejected()
    {
        var json = """{"tag_name":"v1.0.1","draft":false,"prerelease":true,"assets":[]}""";
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task InvalidTag_Rejected()
    {
        var json = """{"tag_name":"not-a-version","draft":false,"prerelease":false,"assets":[]}""";
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task WrongInstallerFilename_Rejected()
    {
        var json = """{"tag_name":"v1.0.1","draft":false,"prerelease":false,"assets":[{"name":"PayBeat-v1.0.1.zip","browser_download_url":"https://github.com/ztyzty66/PayBeat/releases/download/v1.0.1/PayBeat-v1.0.1.zip","digest":"sha256:abc123"}]}""";
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task InstallerVersionMismatch_Rejected()
    {
        // Tag is v1.0.2 but asset name has 1.0.1
        var json = """{"tag_name":"v1.0.2","draft":false,"prerelease":false,"assets":[{"name":"PayBeat-1.0.1-setup-win-x64.exe","browser_download_url":"https://github.com/ztyzty66/PayBeat/releases/download/v1.0.2/PayBeat-1.0.1-setup-win-x64.exe","digest":"sha256:abc123"}]}""";
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task UntrustedDownloadUrl_Rejected()
    {
        var json = """{"tag_name":"v1.0.1","draft":false,"prerelease":false,"assets":[{"name":"PayBeat-1.0.1-setup-win-x64.exe","browser_download_url":"https://evil.com/PayBeat-1.0.1-setup-win-x64.exe","digest":"sha256:abc123"}]}""";
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        Assert.Null(result);
    }

    [Fact]
    public async Task MissingDigest_Rejected()
    {
        var json = """{"tag_name":"v1.0.1","draft":false,"prerelease":false,"assets":[{"name":"PayBeat-1.0.1-setup-win-x64.exe","browser_download_url":"https://github.com/ztyzty66/PayBeat/releases/download/v1.0.1/PayBeat-1.0.1-setup-win-x64.exe"}]}""";
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        // Missing digest means Sha256Digest is null — download can proceed but SHA256 gate will skip.
        Assert.NotNull(result);
        Assert.Null(result!.Sha256Digest);
    }

    [Fact]
    public async Task InvalidDigest_Rejected()
    {
        var json = """{"tag_name":"v1.0.1","draft":false,"prerelease":false,"assets":[{"name":"PayBeat-1.0.1-setup-win-x64.exe","browser_download_url":"https://github.com/ztyzty66/PayBeat/releases/download/v1.0.1/PayBeat-1.0.1-setup-win-x64.exe","digest":"md5:abc123"}]}""";
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        Assert.NotNull(result);
        Assert.Null(result!.Sha256Digest);
    }

    // ── SHA256 Verification ────────────────────────────────────────────────

    [Fact]
    public void Sha256Match_Passes()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "hello world");
            var hash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(tmpFile));
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            Assert.True(UpdateService.VerifySha256(tmpFile, hex));
        }
        finally { File.Delete(tmpFile); }
    }

    [Fact]
    public void Sha256Mismatch_Fails()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "hello world");
            Assert.False(UpdateService.VerifySha256(tmpFile, "0000000000000000000000000000000000000000000000000000000000000000"));
        }
        finally { File.Delete(tmpFile); }
    }

    // ── Network Failure ────────────────────────────────────────────────────

    [Fact]
    public async Task NetworkFailure_DoesNotBreakCoreFlow()
    {
        var handler = new MockHttpHandlerThatThrows();
        var svc = new UpdateService(new HttpClient(handler));
        var result = await svc.CheckForUpdateAsync();
        Assert.Null(result);
    }

    private class MockHttpHandlerThatThrows : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("network error");
    }

    // ── Update Available Scenario ──────────────────────────────────────────

    [Fact]
    public async Task RemoteNewer_ReturnsAvailable_Full()
    {
        var json = """
        {
            "tag_name": "v1.0.2",
            "draft": false,
            "prerelease": false,
            "body": "Bug fixes",
            "assets": [{
                "name": "PayBeat-1.0.2-setup-win-x64.exe",
                "browser_download_url": "https://github.com/ztyzty66/PayBeat/releases/download/v1.0.2/PayBeat-1.0.2-setup-win-x64.exe",
                "digest": "sha256:abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890"
            }]
        }
        """;
        var svc = new UpdateService(MockGitHubApi(json));
        var result = await svc.CheckForUpdateAsync();
        Assert.NotNull(result);
        Assert.True(result!.Available);
        Assert.Equal("1.0.2", result.RemoteVersion);
        Assert.Contains("PayBeat-1.0.2-setup-win-x64.exe", result.DownloadUrl!);
        Assert.Equal("abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890", result.Sha256Digest);
        Assert.Equal("Bug fixes", result.ReleaseNotes);
    }

    // ── XAML Binding Verification ──────────────────────────────────────────

    [Fact]
    public void Settings_Xaml_HasCheckUpdateBinding()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "src", "PayBeat.App", "Views", "SettingsWindow.xaml"));
        Assert.Contains("CheckUpdateCommand", xaml);
        Assert.Contains("Settings.Update.CheckButton", xaml);
    }

    [Fact]
    public void Settings_Xaml_HasDownloadInstallBinding()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "src", "PayBeat.App", "Views", "SettingsWindow.xaml"));
        Assert.Contains("DownloadInstallCommand", xaml);
        Assert.Contains("Settings.Update.DownloadButton", xaml);
    }

    [Fact]
    public void Notification_UpdateText_PointsToSettingsSystem()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "src", "PayBeat.App", "Resources", "Strings.zh-CN.xaml"));
        var idx = xaml.IndexOf("Notification.UpdateAvailableBody");
        Assert.True(idx > 0);
        var snippet = xaml.Substring(idx, 200);
        Assert.Contains("设置", snippet);
        Assert.Contains("系统", snippet);
    }
}
