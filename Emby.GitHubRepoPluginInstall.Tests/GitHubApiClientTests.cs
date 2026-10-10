using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.GithubAPI;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

public class GitHubApiClientTests
{
    private static readonly byte[] DllBytes = Encoding.UTF8.GetBytes("pretend this is a plugin dll");

    private static string Sha(byte[] bytes) => BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant();

    private static GitHubApiClient Client(FakeHandler handler, string token = "ghp_token") =>
        new GitHubApiClient(token, new HttpClient(handler), new TestJson(), new TestLogger());

    private static ReposToProcess Repo(string url = "https://github.com/alex/Cool.Plugin") => new ReposToProcess { Url = url };

    // One release in GitHub's JSON shape. `assets` is raw JSON for the asset array.
    private static string ReleaseJson(string tag, string assets, bool prerelease = false, string published = "2025-05-01T00:00:00Z") =>
        $@"{{""tag_name"":""{tag}"",""draft"":false,""prerelease"":{prerelease.ToString().ToLowerInvariant()},
            ""published_at"":""{published}"",""target_commitish"":""main"",""body"":""notes for {tag}"",""assets"":{assets}}}";

    private static string DllAsset(string name = "Cool.Plugin.dll", string digest = null, int id = 1) =>
        $@"{{""id"":{id},""name"":""{name}"",""url"":""https://api.github.com/repos/alex/Cool.Plugin/releases/assets/{id}"",
             ""browser_download_url"":""https://github.com/alex/Cool.Plugin/releases/download/v1/{name}""
             {(digest == null ? "" : $@",""digest"":""{digest}""")}}}";

    private static FakeHandler WithRelease(string assetsJson, string tag = "v1.2.0")
    {
        return new FakeHandler().OnJson("/repos/alex/Cool.Plugin/releases", "[" + ReleaseJson(tag, assetsJson) + "]");
    }

    // ---- Release lookup ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LatestRelease_PicksTheNewestStableRelease()
    {
        var json = "[" + ReleaseJson("v3.0.0-beta", "[]", prerelease: true, published: "2025-07-01T00:00:00Z") + "," +
                   ReleaseJson("v2.0.0", "[]", published: "2025-06-01T00:00:00Z") + "," +
                   ReleaseJson("v1.0.0", "[]", published: "2025-01-01T00:00:00Z") + "]";
        var handler = new FakeHandler().OnJson("/releases", json);

        var release = await Client(handler).GetLatestReleaseAsync(Repo(), true);

        Assert.Equal("v2.0.0", release.TagName);
    }

    [Fact]
    public async Task LatestRelease_IncludesPrereleasesWhenAsked()
    {
        var json = "[" + ReleaseJson("v3.0.0-beta", "[]", prerelease: true, published: "2025-07-01T00:00:00Z") + "," +
                   ReleaseJson("v2.0.0", "[]", published: "2025-06-01T00:00:00Z") + "]";
        var repo = Repo();
        repo.GetPreRelease = true;

        var release = await Client(new FakeHandler().OnJson("/releases", json)).GetLatestReleaseAsync(repo, true);

        Assert.Equal("v3.0.0-beta", release.TagName);
    }

    [Fact]
    public async Task LatestRelease_NoReleases_ReturnsNull()
    {
        var release = await Client(new FakeHandler().OnJson("/releases", "[]")).GetLatestReleaseAsync(Repo(), true);
        Assert.Null(release);
    }

    [Fact]
    public async Task LatestRelease_ACommitLookupFailureDoesNotFailTheCheck()
    {
        // the fake answers 404 for the /commits call
        var release = await Client(WithRelease("[]")).GetLatestReleaseAsync(Repo(), true);
        Assert.Equal("v1.2.0", release.TagName);
    }

    [Fact]
    public async Task Error_401_IsUnauthorized()
    {
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            Client(new FakeHandler().OnJson("/releases", "{}", HttpStatusCode.Unauthorized)).GetLatestReleaseAsync(Repo(), true));

        Assert.Equal(GitHubErrorKind.Unauthorized, ex.Kind);
    }

    [Fact]
    public async Task Error_404_IsNotFound_NotSilentlyNull()
    {
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            Client(new FakeHandler().OnJson("/releases", "{}", HttpStatusCode.NotFound)).GetLatestReleaseAsync(Repo(), true));

        Assert.Equal(GitHubErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Error_403WithNoRequestsLeft_IsRateLimited_WithTheResetTime()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(20).ToUnixTimeSeconds();
        var handler = new FakeHandler().OnJson("/releases", "{}", HttpStatusCode.Forbidden, r =>
        {
            r.Headers.Add("X-RateLimit-Remaining", "0");
            r.Headers.Add("X-RateLimit-Limit", "60");
            r.Headers.Add("X-RateLimit-Reset", reset.ToString());
        });

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Client(handler, null).GetLatestReleaseAsync(Repo(), true));

        Assert.Equal(GitHubErrorKind.RateLimited, ex.Kind);
        Assert.NotNull(ex.ResetUtc);
        Assert.Equal(0, GitHubApiClient.LastRateLimit.Remaining);
    }

    [Fact]
    public async Task Error_403WithRequestsLeft_IsForbidden()
    {
        var handler = new FakeHandler().OnJson("/releases", "{}", HttpStatusCode.Forbidden, r =>
        {
            r.Headers.Add("X-RateLimit-Remaining", "4000");
            r.Headers.Add("X-RateLimit-Limit", "5000");
            r.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
        });

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Client(handler).GetLatestReleaseAsync(Repo(), true));

        Assert.Equal(GitHubErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public async Task NetworkFailure_IsReportedAsNetwork()
    {
        var handler = new FakeHandler().On("/releases", _ => throw new HttpRequestException("name resolution failed"));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => Client(handler).GetLatestReleaseAsync(Repo(), true));

        Assert.Equal(GitHubErrorKind.Network, ex.Kind);
    }

    // ---- Choosing the file ------------------------------------------------------------------------------------

    [Fact]
    public void PickAsset_PrefersADllOverAZip_AndSkipsSourceArchives()
    {
        var release = new GitHubRelease
        {
            Assets = new List<GitHubReleaseAsset>
            {
                new GitHubReleaseAsset { Name = "source-code.zip" },
                new GitHubReleaseAsset { Name = "Plugin.zip" },
                new GitHubReleaseAsset { Name = "Plugin.dll" }
            }
        };

        Assert.Equal("Plugin.dll", GitHubApiClient.PickAsset(release, Repo()).Name);

        release.Assets.RemoveAt(2);
        Assert.Equal("Plugin.zip", GitHubApiClient.PickAsset(release, Repo()).Name);

        release.Assets.RemoveAt(1);
        Assert.Null(GitHubApiClient.PickAsset(release, Repo()));
    }

    [Fact]
    public void PickAsset_HonoursTheNameFilter()
    {
        var release = new GitHubRelease
        {
            Assets = new List<GitHubReleaseAsset>
            {
                new GitHubReleaseAsset { Name = "Plugin.Server-1.0.zip" },
                new GitHubReleaseAsset { Name = "Plugin.Emby-1.0.zip" }
            }
        };
        var repo = Repo();
        repo.AssetNameFilter = "emby";

        Assert.Equal("Plugin.Emby-1.0.zip", GitHubApiClient.PickAsset(release, repo).Name);
    }

    [Fact]
    public void IsChecksumAssetFor_RecognisesCommonChecksumFiles()
    {
        var asset = new GitHubReleaseAsset { Name = "Plugin.dll" };

        Assert.True(GitHubApiClient.IsChecksumAssetFor(new GitHubReleaseAsset { Name = "Plugin.dll.sha256" }, asset));
        Assert.True(GitHubApiClient.IsChecksumAssetFor(new GitHubReleaseAsset { Name = "SHA256SUMS" }, asset));
        Assert.True(GitHubApiClient.IsChecksumAssetFor(new GitHubReleaseAsset { Name = "checksums.txt" }, asset));
        Assert.False(GitHubApiClient.IsChecksumAssetFor(new GitHubReleaseAsset { Name = "Other.dll.sha256" }, asset));
        Assert.False(GitHubApiClient.IsChecksumAssetFor(new GitHubReleaseAsset { Name = "Plugin.dll" }, asset));
    }

    // ---- Downloading ------------------------------------------------------------------------------------------

    private static async Task<(DownloadResult result, string folder)> Download(FakeHandler handler, TempDir dir, ReposToProcess repo = null)
    {
        using var client = Client(handler);
        var release = await client.GetLatestReleaseAsync(repo ?? Repo(), true);
        var result = await client.DownloadReleaseAsync(release, dir.Path, null, default, repo ?? Repo());
        return (result, dir.Path);
    }

    [Fact]
    public async Task Download_Dll_WithAGoodGitHubDigest_InstallsAndSaysVerified()
    {
        using var dir = new TempDir();
        var handler = WithRelease("[" + DllAsset(digest: "sha256:" + Sha(DllBytes)) + "]").OnBytes("/assets/1", DllBytes);

        var (result, folder) = await Download(handler, dir);

        Assert.Equal("Cool.Plugin.dll", result.FileName);
        Assert.StartsWith("verified", result.Verification);
        Assert.Equal(DllBytes, File.ReadAllBytes(Path.Combine(folder, "Cool.Plugin.dll")));
        Assert.Empty(Directory.GetFiles(folder, "*.temp"));
    }

    [Fact]
    public async Task Download_Dll_WithABadDigest_Throws_AndKeepsTheExistingPlugin()
    {
        using var dir = new TempDir();
        var existing = dir.File("Cool.Plugin.dll", "the working version");
        var handler = WithRelease("[" + DllAsset(digest: "sha256:" + new string('0', 64)) + "]").OnBytes("/assets/1", DllBytes);

        var ex = await Assert.ThrowsAsync<Exception>(() => Download(handler, dir));

        Assert.Contains("Checksum mismatch", ex.InnerException?.Message ?? ex.Message);
        Assert.Equal("the working version", File.ReadAllText(existing));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.temp"));
    }

    [Fact]
    public async Task Download_Dll_UsesASidecarChecksumFile()
    {
        using var dir = new TempDir();
        var assets = "[" + DllAsset() + "," + DllAsset("Cool.Plugin.dll.sha256", null, 2) + "]";
        var handler = WithRelease(assets)
                      .OnBytes("/assets/1", DllBytes)
                      .OnBytes("/assets/2", Encoding.UTF8.GetBytes($"{Sha(DllBytes)}  Cool.Plugin.dll\n"));

        var (result, _) = await Download(handler, dir);

        Assert.Contains("Cool.Plugin.dll.sha256", result.Verification);
    }

    [Fact]
    public async Task Download_Dll_WithAWrongSidecarChecksum_Throws()
    {
        using var dir = new TempDir();
        var assets = "[" + DllAsset() + "," + DllAsset("Cool.Plugin.dll.sha256", null, 2) + "]";
        var handler = WithRelease(assets)
                      .OnBytes("/assets/1", DllBytes)
                      .OnBytes("/assets/2", Encoding.UTF8.GetBytes(new string('1', 64)));

        await Assert.ThrowsAsync<Exception>(() => Download(handler, dir));
        Assert.False(File.Exists(Path.Combine(dir.Path, "Cool.Plugin.dll")));
    }

    [Fact]
    public async Task Download_WithNoPublishedChecksum_StillInstalls_AndSaysNotVerified()
    {
        using var dir = new TempDir();
        var handler = WithRelease("[" + DllAsset() + "]").OnBytes("/assets/1", DllBytes);

        var (result, _) = await Download(handler, dir);

        Assert.StartsWith("not verified", result.Verification);
    }

    [Fact]
    public async Task Download_AnAssetNameThatTriesToLeaveTheFolder_StaysInside()
    {
        using var dir = new TempDir();
        var plugins = Path.Combine(dir.Path, "plugins");
        Directory.CreateDirectory(plugins);
        var handler = WithRelease("[" + DllAsset("../evil.dll") + "]").OnBytes("/assets/1", DllBytes);

        using var client = Client(handler);
        var release = await client.GetLatestReleaseAsync(Repo(), true);
        var result = await client.DownloadReleaseAsync(release, plugins, null, default, Repo());

        Assert.Equal("evil.dll", result.FileName);
        Assert.True(File.Exists(Path.Combine(plugins, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "evil.dll")));
    }

    [Fact]
    public async Task Download_NoUsableAsset_ExplainsWhy()
    {
        using var dir = new TempDir();
        var handler = WithRelease("[" + DllAsset("readme.txt") + "]");

        using var client = Client(handler);
        var release = await client.GetLatestReleaseAsync(Repo(), true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadReleaseAsync(release, dir.Path, null, default, Repo()));
        Assert.Contains("No DLL or suitable ZIP", ex.Message);
    }

    [Fact]
    public async Task Download_FailedRequest_ReportsTheKindOfFailure()
    {
        using var dir = new TempDir();
        var handler = WithRelease("[" + DllAsset() + "]")
                      .OnJson("/assets/1", "{}", HttpStatusCode.NotFound);

        using var client = Client(handler);
        var release = await client.GetLatestReleaseAsync(Repo(), true);

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.DownloadReleaseAsync(release, dir.Path, null, default, Repo()));
        Assert.Equal(GitHubErrorKind.NotFound, ex.Kind);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Download_Zip_ExtractsEverythingAndTracksTheMainDll()
    {
        using var dir = new TempDir();
        var zip = MakeZip(("Cool.Plugin/Cool.Plugin.dll", "main"), ("Cool.Plugin/Helper.dll", "helper"), ("Cool.Plugin/web/index.html", "<html/>"));
        var handler = WithRelease("[" + DllAsset("Cool.Plugin-1.0.zip") + "]").OnBytes("/assets/1", zip);

        var (result, folder) = await Download(handler, dir);

        Assert.Equal("Cool.Plugin.dll", result.FileName);
        Assert.Equal("main", File.ReadAllText(Path.Combine(folder, "Cool.Plugin.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "Helper.dll")));
        Assert.True(File.Exists(Path.Combine(folder, "web", "index.html")));
        Assert.Empty(Directory.GetFiles(folder, "*.temp"));
    }

    [Fact]
    public async Task Download_Zip_KeepsTrackingTheSameDllOnLaterUpdates()
    {
        using var dir = new TempDir();
        var zip = MakeZip(("Alpha.dll", "a"), ("Beta.dll", "b"));
        var handler = WithRelease("[" + DllAsset("Bundle.zip") + "]").OnBytes("/assets/1", zip);
        var repo = Repo();
        repo.FileName = "Beta.dll";

        var (result, _) = await Download(handler, dir, repo);

        Assert.Equal("Beta.dll", result.FileName);
    }

    [Fact]
    public async Task Download_Zip_WithoutADll_Fails_AndLeavesNothingBehind()
    {
        using var dir = new TempDir();
        var zip = MakeZip(("readme.md", "hi"));
        var handler = WithRelease("[" + DllAsset("Docs.zip") + "]").OnBytes("/assets/1", zip);

        await Assert.ThrowsAsync<Exception>(() => Download(handler, dir));
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Download_ReplacesAPluginFileThatIsLocked()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var handler = WithRelease("[" + DllAsset() + "]").OnBytes("/assets/1", DllBytes);

        string path;
        using (var loaded = new LoadedDll(dir.Path, "Cool.Plugin.dll"))
        {
            path = loaded.Path;
            Assert.True(loaded.IsLocked(), "precondition: a loaded DLL can't be overwritten");

            var (result, _) = await Download(handler, dir);

            Assert.Equal("Cool.Plugin.dll", result.FileName);
            Assert.Equal(DllBytes, File.ReadAllBytes(path));
        }
    }

    private static byte[] MakeZip(params (string name, string content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    // ---- Token test -------------------------------------------------------------------------------------------

    [Fact]
    public async Task TestConnection_WithAWorkingToken_ReportsRemainingRequests()
    {
        var handler = new FakeHandler().OnJson("/rate_limit", "{}", HttpStatusCode.OK, r =>
        {
            r.Headers.Add("X-RateLimit-Limit", "5000");
            r.Headers.Add("X-RateLimit-Remaining", "4990");
            r.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
        });

        var result = await Client(handler).TestConnectionAsync();

        Assert.True(result.Ok);
        Assert.Contains("4990 of 5000", result.Message);
    }

    [Fact]
    public async Task TestConnection_WarnsAboutBroadClassicTokens()
    {
        var handler = new FakeHandler().OnJson("/rate_limit", "{}", HttpStatusCode.OK, r =>
        {
            r.Headers.Add("X-RateLimit-Limit", "5000");
            r.Headers.Add("X-RateLimit-Remaining", "4990");
            r.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
            r.Headers.Add("X-OAuth-Scopes", "repo, read:org");
        });

        var result = await Client(handler).TestConnectionAsync();

        Assert.True(result.Ok);
        Assert.Contains("fine-grained", result.Message);
    }

    [Fact]
    public async Task TestConnection_RejectedToken_SaysSo()
    {
        var result = await Client(new FakeHandler().OnJson("/rate_limit", "{}", HttpStatusCode.Unauthorized)).TestConnectionAsync();

        Assert.False(result.Ok);
        Assert.Contains("rejected", result.Message);
    }

    [Fact]
    public async Task TestConnection_WithoutAToken_ExplainsTheLowerLimit()
    {
        var handler = new FakeHandler().OnJson("/rate_limit", "{}", HttpStatusCode.OK, r =>
        {
            r.Headers.Add("X-RateLimit-Limit", "60");
            r.Headers.Add("X-RateLimit-Remaining", "60");
            r.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
        });

        var result = await Client(handler, null).TestConnectionAsync();

        Assert.True(result.Ok);
        Assert.Contains("60 requests per hour", result.Message);
    }

    // ---- Registries -------------------------------------------------------------------------------------------

    [Fact]
    public void RegistryKey_TreatsDifferentSpellingsOfTheSameRepoAsOne()
    {
        Assert.Equal(GitHubApiClient.RegistryKey("https://github.com/Alex/Plugin"), GitHubApiClient.RegistryKey("alex/plugin.git"));
        Assert.Equal("alex/plugin", GitHubApiClient.RegistryKey("https://github.com/Alex/Plugin/"));
    }

    [Fact]
    public async Task Registries_AreDedupedByRepository_NotByUrlText()
    {
        var one = @"{""name"":""One"",""plugins"":[{""name"":""P"",""url"":""https://github.com/Alex/Plugin""}]}";
        var two = @"{""name"":""Two"",""plugins"":[{""name"":""P again"",""url"":""https://github.com/alex/plugin/""},{""name"":""Q"",""url"":""https://github.com/alex/q""}]}";
        var handler = new FakeHandler().OnJson("one.example", one).OnJson("two.example", two);
        var registries = new List<PluginRegistry>
        {
            new PluginRegistry { Name = "One", RawUrl = "https://one.example/plugins.json" },
            new PluginRegistry { Name = "Two", RawUrl = "https://two.example/plugins.json" }
        };

        var plugins = await Client(handler).GetAllRegistryPluginsAsync(registries);

        Assert.Equal(new[] { "P", "Q" }, plugins.Select(p => p.Name).ToArray());
        Assert.Equal("One", plugins[0].RegistrySource);
    }

    [Fact]
    public async Task DefaultRegistry_FallsBackToTheCopyInsideTheDll_WhenGitHubIsUnreachable()
    {
        var handler = new FakeHandler().On("raw.githubusercontent.com", _ => throw new HttpRequestException("offline"));
        var registries = new List<PluginRegistry>
        {
            new PluginRegistry { Name = "Built-in", RawUrl = GitHubApiClient.EmbeddedRegistryUrl }
        };

        var plugins = await Client(handler).GetAllRegistryPluginsAsync(registries);

        Assert.NotEmpty(plugins);
    }
}

public class FriendlyMessageTests
{
    [Fact]
    public void RateLimit_MentionsTheResetTime_AndSuggestsATokenWhenThereIsNone()
    {
        var ex = new GitHubApiException(GitHubErrorKind.RateLimited, "x", DateTime.UtcNow.AddMinutes(10));

        var withoutToken = RepoUpdateService.FriendlyMessage(ex, false);
        var withToken    = RepoUpdateService.FriendlyMessage(ex, true);

        Assert.Contains("resets at", withoutToken);
        Assert.Contains("5,000", withoutToken);
        Assert.DoesNotContain("5,000", withToken);
    }

    [Theory]
    [InlineData(GitHubErrorKind.Unauthorized, "Test Token")]
    [InlineData(GitHubErrorKind.Forbidden, "read access")]
    [InlineData(GitHubErrorKind.NotFound, "Check the URL")]
    [InlineData(GitHubErrorKind.Network, "internet connection")]
    public void EveryKind_SaysWhatToDoNext(GitHubErrorKind kind, string expected)
    {
        Assert.Contains(expected, RepoUpdateService.FriendlyMessage(new GitHubApiException(kind, "boom"), true));
    }
}
