using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Emby.GitHubRepoPluginInstall.Helpers;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Validation;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

public class ChecksumHelperTests
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Fact]
    public void ParseDigest_ReadsSha256Digest()
    {
        Assert.Equal(Hash, ChecksumHelper.ParseDigest("sha256:" + Hash));
        Assert.Equal(Hash, ChecksumHelper.ParseDigest("SHA256:" + Hash.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1:abc")]
    [InlineData("sha256:not-hex")]
    [InlineData("sha256:abcd")]
    public void ParseDigest_RejectsAnythingElse(string digest)
    {
        Assert.Null(ChecksumHelper.ParseDigest(digest));
    }

    [Fact]
    public void FindHash_BareHashFile()
    {
        Assert.Equal(Hash, ChecksumHelper.FindHashInChecksumFile(Hash + "\n", "Plugin.dll"));
    }

    [Fact]
    public void FindHash_Sha256sumFile_PicksTheRightLine()
    {
        var other = new string('a', 64);
        var text = $"{other}  Other.dll\r\n{Hash} *Plugin.dll\r\n";
        Assert.Equal(Hash, ChecksumHelper.FindHashInChecksumFile(text, "Plugin.dll"));
    }

    [Fact]
    public void FindHash_ReturnsNullWhenFileNotListed()
    {
        var text = new string('a', 64) + "  Other.dll";
        Assert.Null(ChecksumHelper.FindHashInChecksumFile(text, "Plugin.dll"));
    }

    [Fact]
    public void Sha256Hex_MatchesFrameworkHash()
    {
        using var dir = new TempDir();
        var path = dir.File("a.bin", "hello");
        using var sha = SHA256.Create();
        var expected = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("hello"))).Replace("-", "").ToLowerInvariant();
        Assert.Equal(expected, ChecksumHelper.Sha256Hex(path));
    }
}

public class TimeFormatTests
{
    private static readonly DateTime Now = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(2 * 3600, "2 h ago")]
    [InlineData(3 * 86400, "3 d ago")]
    public void Ago_UsesTheRightUnit(int secondsBack, string expected)
    {
        Assert.Equal(expected, TimeFormat.Ago(Now.AddSeconds(-secondsBack), Now));
    }

    [Fact]
    public void Ago_NullIsNever()
    {
        Assert.Equal("never", TimeFormat.Ago(null));
    }

    [Fact]
    public void Ago_FutureTimeDoesNotGoNegative()
    {
        Assert.Equal("just now", TimeFormat.Ago(Now.AddMinutes(5), Now));
    }

    [Theory]
    [InlineData(0.2, "under 1 s")]
    [InlineData(42, "42 s")]
    [InlineData(125, "2 min 5 s")]
    public void Duration_IsReadable(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Duration(seconds));
    }

    [Fact]
    public void Stamp_SaysTodayAndYesterday()
    {
        var local = new DateTime(2025, 6, 1, 3, 0, 0, DateTimeKind.Local);
        Assert.Equal("Today 03:00", TimeFormat.Stamp(local.ToUniversalTime(), local));
        Assert.Equal("Yesterday 03:00", TimeFormat.Stamp(local.AddDays(-1).ToUniversalTime(), local));
    }
}

public class FileOpsTests
{
    [Fact]
    public void OverwriteFile_ReplacesContent()
    {
        using var dir = new TempDir();
        var src = dir.File("new.dll", "new");
        var dst = dir.File("plugin.dll", "old");

        FileOps.OverwriteFile(src, dst);

        Assert.Equal("new", File.ReadAllText(dst));
    }

    [Fact]
    public void OverwriteFile_CreatesMissingFolders()
    {
        using var dir = new TempDir();
        var src = dir.File("new.dll", "new");
        var dst = Path.Combine(dir.Path, "sub", "deeper", "plugin.dll");

        FileOps.OverwriteFile(src, dst);

        Assert.Equal("new", File.ReadAllText(dst));
    }

    // Windows lets a loaded DLL be renamed but not overwritten.
    [Fact]
    public void OverwriteFile_WhenDestinationIsALoadedDll_RenamesItAside()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var src = dir.File("new.dll", "new");

        using (var loaded = new LoadedDll(dir.Path, "plugin.dll"))
        {
            Assert.True(loaded.IsLocked(), "precondition: a loaded DLL can't be overwritten");

            FileOps.OverwriteFile(src, loaded.Path);

            Assert.Equal("new", File.ReadAllText(loaded.Path));
            Assert.True(File.Exists(loaded.Path + FileOps.OldSuffix), "the loaded file should have been parked as .old");
        }
    }

    [Fact]
    public void CleanupOldFiles_RemovesLeftoversOnceTheDllIsUnloaded()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var src = dir.File("new.dll", "new");
        string path;

        using (var loaded = new LoadedDll(dir.Path, "plugin.dll"))
        {
            path = loaded.Path;
            FileOps.OverwriteFile(src, path);
        }

        // .NET releases an unloaded assembly's file lock a little after Unload(), so give it a moment
        for (var attempt = 0; attempt < 50 && File.Exists(path + FileOps.OldSuffix); attempt++)
        {
            FileOps.CleanupOldFiles(dir.Path);
            if (!File.Exists(path + FileOps.OldSuffix)) break;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            System.Threading.Thread.Sleep(100);
        }

        Assert.False(File.Exists(path + FileOps.OldSuffix));
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public void CleanupOldFiles_LeavesBackupsAlone()
    {
        using var dir = new TempDir();
        dir.File("a.dll.old");
        var backup = dir.File("plugin-backups/a/x.dll.old");

        Assert.Equal(1, FileOps.CleanupOldFiles(dir.Path));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public void ReplaceFile_RemovesTheSource()
    {
        using var dir = new TempDir();
        var src = dir.File("new.dll.temp", "new");
        var dst = dir.File("plugin.dll", "old");

        FileOps.ReplaceFile(src, dst);

        Assert.False(File.Exists(src));
        Assert.Equal("new", File.ReadAllText(dst));
    }

    [Fact]
    public void PickTrackedDll_PrefersTheOneTrackedBefore()
    {
        Assert.Equal("Old.dll", FileOps.PickTrackedDll(new[] { "Other.dll", "Old.dll" }, "Whatever", "old.dll"));
    }

    [Fact]
    public void PickTrackedDll_UsesTheOnlyDll()
    {
        Assert.Equal("Only.dll", FileOps.PickTrackedDll(new[] { "Only.dll" }, "Repo", null));
    }

    [Fact]
    public void PickTrackedDll_MatchesRepositoryName()
    {
        var picked = FileOps.PickTrackedDll(new[] { "Newtonsoft.Json.dll", "Emby.Moonfin.dll" }, "Moonfin", null);
        Assert.Equal("Emby.Moonfin.dll", picked);
    }

    [Fact]
    public void PickTrackedDll_NoDlls_ReturnsNull()
    {
        Assert.Null(FileOps.PickTrackedDll(new string[0], "Repo", null));
    }
}

public class AssetFilterHelperTests
{
    [Theory]
    [InlineData("Moonfin.Emby-2.1.0.0.zip", "Moonfin.Emby")]
    [InlineData("plugin.dll", "plugin")]
    [InlineData("Emby.Plugin_v1.2.3-beta.zip", "Emby.Plugin")]
    [InlineData("My Plugin 3.0.dll", "My Plugin")]
    [InlineData("", "")]
    public void SuggestFilter_DropsTheVersion(string asset, string expected)
    {
        Assert.Equal(expected, AssetFilterHelper.SuggestFilter(asset));
    }
}

public class GitHubUrlValidatorTests
{
    [Theory]
    [InlineData("https://github.com/alex/plugin", "https://github.com/alex/plugin")]
    [InlineData("alex/plugin", "https://github.com/alex/plugin")]
    [InlineData("github.com/alex/plugin", "https://github.com/alex/plugin")]
    [InlineData("http://github.com/alex/plugin/", "https://github.com/alex/plugin")]
    [InlineData("https://www.github.com/alex/plugin.git", "https://github.com/alex/plugin")]
    [InlineData("git@github.com:alex/plugin.git", "https://github.com/alex/plugin")]
    [InlineData("https://github.com/alex/plugin/releases/latest", "https://github.com/alex/plugin")]
    [InlineData("  https://github.com/alex/plugin  ", "https://github.com/alex/plugin")]
    public void Normalize_GivesTheCanonicalForm(string input, string expected)
    {
        Assert.Equal(expected, GitHubUrlValidator.Normalize(input));
    }

    [Fact]
    public void ValidateUrl_AcceptsShorthandAndReportsTheCorrection()
    {
        var result = GitHubUrlValidator.ValidateUrl("alex/plugin");

        Assert.True(result.IsValid);
        Assert.Equal("https://github.com/alex/plugin", result.CorrectedUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://gitlab.com/alex/plugin")]
    [InlineData("https://github.com/alex")]
    [InlineData("-bad-/plugin")]
    public void ValidateUrl_RejectsNonsense(string input)
    {
        Assert.False(GitHubUrlValidator.ValidateUrl(input).IsValid);
    }
}

public class ReposToProcessTests
{
    private static ReposToProcess Repo(string url = "https://github.com/Alex/Plugin") => new ReposToProcess { Url = url };

    [Fact]
    public void RepoKey_IgnoresCaseAndUrlSpelling()
    {
        Assert.Equal("alex/plugin", Repo().RepoKey);
        Assert.Equal(Repo().RepoKey, Repo("https://github.com/alex/plugin/").RepoKey);
    }

    [Fact]
    public void Status_NotCheckedWhenNothingKnown()
    {
        Assert.Equal(RepoStatus.NotChecked, Repo().GetStatus());
    }

    [Fact]
    public void Status_NoReleaseWhenItWasCheckedAndNothingWasFound()
    {
        var r = Repo();
        r.LastDateTimeChecked = DateTime.UtcNow;

        Assert.Equal(RepoStatus.NoRelease, r.GetStatus());
        Assert.Equal("No release yet", r.StatusText);
    }

    [Fact]
    public void Status_AnInstalledPluginWhoseCheckFindsNothingStaysUpToDate()
    {
        var r = Repo();
        r.LastDateTimeChecked = DateTime.UtcNow;
        r.LastVersionDownloaded = "v1";

        Assert.Equal(RepoStatus.UpToDate, r.GetStatus());
    }

    [Fact]
    public void Status_UpToDateWhenVersionsMatchIgnoringCase()
    {
        var r = Repo();
        r.LatestVersion = "V1.0"; r.LastVersionDownloaded = "v1.0";
        Assert.Equal(RepoStatus.UpToDate, r.GetStatus());
    }

    [Fact]
    public void Status_UpdateAvailableWhenAutoUpdateIsOn()
    {
        var r = Repo();
        r.LatestVersion = "v2"; r.LastVersionDownloaded = "v1";
        Assert.Equal(RepoStatus.UpdateAvailable, r.GetStatus());
    }

    [Fact]
    public void Status_UpdateWaitingWhenAutoUpdateIsOff()
    {
        var r = Repo();
        r.AutoUpdate = false; r.LatestVersion = "v2"; r.LastVersionDownloaded = "v1";
        Assert.Equal(RepoStatus.UpdateWaiting, r.GetStatus());
    }

    [Fact]
    public void Status_FailedWinsOverEverything()
    {
        var r = Repo();
        r.LatestVersion = "v2"; r.LastVersionDownloaded = "v2"; r.LastError = "boom";
        Assert.Equal(RepoStatus.Failed, r.GetStatus());
    }

    [Fact]
    public void CopyRuntimeState_CopiesOnlyWhatRunsWrite()
    {
        var ui = Repo(); ui.AutoUpdate = false; ui.AssetNameFilter = "keep";
        var fresh = Repo(); fresh.AutoUpdate = true; fresh.LastVersionDownloaded = "v9"; fresh.LatestVersion = "v9"; fresh.LastError = "e";

        ui.CopyRuntimeStateFrom(fresh);

        Assert.Equal("v9", ui.LastVersionDownloaded);
        Assert.Equal("e", ui.LastError);
        Assert.False(ui.AutoUpdate);
        Assert.Equal("keep", ui.AssetNameFilter);
    }

    [Fact]
    public void LatestAssetNames_SplitsTheStoredList()
    {
        var r = Repo(); r.LatestAssets = "a.dll|b.zip";
        Assert.Equal(new[] { "a.dll", "b.zip" }, r.GetLatestAssetNames());
        Assert.Empty(Repo().GetLatestAssetNames());
    }
}
