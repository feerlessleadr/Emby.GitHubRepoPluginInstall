using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Security;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.Storage;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

public class SecureStorageTests
{
    private static SecureStorage Make(TempDir dir) => new SecureStorage(new TestLogger(), Path.Combine(dir.Path, "key.bin"));

    [Fact]
    public void RoundTrip_ReturnsTheOriginalToken()
    {
        using var dir = new TempDir();
        var storage = Make(dir);

        var protectedText = storage.Protect("ghp_exampletoken123");

        Assert.Equal("ghp_exampletoken123", storage.Unprotect(protectedText));
    }

    [Fact]
    public void Protect_UsesTheNewFormatAndHidesTheToken()
    {
        using var dir = new TempDir();
        var protectedText = Make(dir).Protect("ghp_exampletoken123");

        Assert.True(SecureStorage.IsCurrentFormat(protectedText));
        Assert.DoesNotContain("ghp_", protectedText);
    }

    [Fact]
    public void Protect_GivesADifferentResultEachTime()
    {
        using var dir = new TempDir();
        var storage = Make(dir);

        Assert.NotEqual(storage.Protect("same"), storage.Protect("same"));
    }

    [Fact]
    public void Key_IsStoredOutsideTheProtectedValue()
    {
        using var dir = new TempDir();
        var storage = Make(dir);
        storage.Protect("x");

        Assert.True(File.Exists(Path.Combine(dir.Path, "key.bin")));
    }

    [Fact]
    public void AnotherKeyFile_CannotReadTheToken()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var protectedText = Make(a).Protect("secret-token");

        Assert.Null(Make(b).Unprotect(protectedText));
    }

    [Fact]
    public void MissingKeyFile_ReturnsNullInsteadOfTheCiphertext()
    {
        using var dir = new TempDir();
        var storage = Make(dir);
        var protectedText = storage.Protect("secret-token");
        File.Delete(Path.Combine(dir.Path, "key.bin"));

        Assert.Null(Make(dir).Unprotect(protectedText));
    }

    [Fact]
    public void TamperedValue_ReturnsNull()
    {
        using var dir = new TempDir();
        var storage = Make(dir);
        var protectedText = storage.Protect("secret-token");
        var bytes = Convert.FromBase64String(protectedText.Substring(3));
        bytes[bytes.Length - 1] ^= 0xFF;

        Assert.Null(storage.Unprotect("v2:" + Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void EmptyValues_PassThrough()
    {
        using var dir = new TempDir();
        var storage = Make(dir);

        Assert.Null(storage.Protect(null));
        Assert.Equal("", storage.Unprotect(""));
    }

    // Tokens saved by earlier versions must still work so nobody has to re-enter theirs.
    [Fact]
    public void LegacyXorValue_StillReadable_AndIsNotTheCurrentFormat()
    {
        using var dir = new TempDir();
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "EmbyGitHubPlugin2024"));
        var plain = Encoding.UTF8.GetBytes("ghp_legacytoken");
        var xored = plain.Select((b, i) => (byte)(b ^ key[i % key.Length])).ToArray();
        var legacy = Convert.ToBase64String(xored);

        Assert.False(SecureStorage.IsCurrentFormat(legacy));
        Assert.Equal("ghp_legacytoken", Make(dir).Unprotect(legacy));
    }

    [Fact]
    public void LegacyPlainBase64_StillReadable()
    {
        using var dir = new TempDir();
        var legacy = Convert.ToBase64String(Encoding.UTF8.GetBytes("ghp_plainbase64"));

        Assert.Equal("ghp_plainbase64", Make(dir).Unprotect(legacy));
    }

    [Fact]
    public void LegacyPlainText_IsReturnedAsIs()
    {
        using var dir = new TempDir();
        Assert.Equal("ghp_not base64!!", Make(dir).Unprotect("ghp_not base64!!"));
    }
}

public class PluginBackupServiceTests
{
    private static PluginBackupService Make(TempDir dir) => new PluginBackupService(dir.Path, new TestLogger());

    [Fact]
    public async Task CreateBackup_CopiesTheFileAndListsIt()
    {
        using var dir = new TempDir();
        var plugin = dir.File("My.Plugin.dll", "v1 bytes");
        var svc = Make(dir);

        var path = await svc.CreateBackupAsync(plugin, "1.0.0");
        var list = await svc.GetBackupsAsync("My.Plugin.dll");

        Assert.NotNull(path);
        Assert.EndsWith(".bak", path);
        Assert.Single(list);
        Assert.Equal("1.0.0", list[0].Version);
        Assert.Equal("My.Plugin.dll", list[0].OriginalFileName);
        Assert.Equal("v1 bytes", File.ReadAllText(list[0].BackupPath));
    }

    [Fact]
    public async Task CreateBackup_ReturnsNullWhenThereIsNothingToBackUp()
    {
        using var dir = new TempDir();
        Assert.Null(await Make(dir).CreateBackupAsync(Path.Combine(dir.Path, "missing.dll"), "1"));
        Assert.Null(await Make(dir).CreateBackupAsync(null, "1"));
    }

    [Fact]
    public async Task Backups_AreNotDllFiles_SoEmbyWontLoadThem()
    {
        using var dir = new TempDir();
        var plugin = dir.File("P.dll");
        await Make(dir).CreateBackupAsync(plugin, "1");

        var all = Directory.GetFiles(dir.Path, "*.dll", SearchOption.AllDirectories);
        Assert.Equal(new[] { plugin }, all);
    }

    [Fact]
    public async Task Restore_PutsTheNewestBackupBack()
    {
        using var dir = new TempDir();
        var plugin = dir.File("P.dll", "one");
        var svc = Make(dir);
        await svc.CreateBackupAsync(plugin, "1.0");
        File.WriteAllText(plugin, "two");
        await svc.CreateBackupAsync(plugin, "2.0");
        File.WriteAllText(plugin, "three");

        var restored = await svc.RestoreBackupAsync("P.dll");

        Assert.Equal("2.0", restored.Version);
        Assert.Equal("two", File.ReadAllText(plugin));
    }

    [Fact]
    public async Task Restore_CanPickASpecificVersion()
    {
        using var dir = new TempDir();
        var plugin = dir.File("P.dll", "one");
        var svc = Make(dir);
        await svc.CreateBackupAsync(plugin, "1.0");
        File.WriteAllText(plugin, "two");
        await svc.CreateBackupAsync(plugin, "2.0");

        await svc.RestoreBackupAsync("P.dll", "1.0");

        Assert.Equal("one", File.ReadAllText(plugin));
    }

    [Fact]
    public async Task Restore_ReturnsNullWhenThereIsNoBackup()
    {
        using var dir = new TempDir();
        Assert.Null(await Make(dir).RestoreBackupAsync("Nothing.dll"));
    }

    [Fact]
    public async Task OldBackups_AreTrimmedToThree()
    {
        using var dir = new TempDir();
        var plugin = dir.File("P.dll");
        var svc = Make(dir);
        for (var i = 1; i <= 5; i++)
        {
            File.WriteAllText(plugin, "v" + i);
            await svc.CreateBackupAsync(plugin, i + ".0");
        }

        var list = await svc.GetBackupsAsync("P.dll");

        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { "5.0", "4.0", "3.0" }, list.Select(b => b.Version).ToArray());
    }

    [Fact]
    public async Task OddNamesAndVersions_SurviveTheRoundTrip()
    {
        using var dir = new TempDir();
        var plugin = dir.File("My_Plugin~beta.dll", "x");
        var svc = Make(dir);

        await svc.CreateBackupAsync(plugin, "v1.2~rc/1");
        var list = await svc.GetBackupsAsync("My_Plugin~beta.dll");

        Assert.Single(list);
        Assert.Equal("My_Plugin~beta.dll", list[0].OriginalFileName);
        Assert.DoesNotContain('~', list[0].Version);
        Assert.DoesNotContain('/', list[0].Version);
    }

    [Fact]
    public async Task BackupsOfDifferentPlugins_AreKeptApart()
    {
        using var dir = new TempDir();
        var svc = Make(dir);
        await svc.CreateBackupAsync(dir.File("A.dll"), "1");
        await svc.CreateBackupAsync(dir.File("B.dll"), "1");

        Assert.Single(await svc.GetBackupsAsync("A.dll"));
        Assert.Single(await svc.GetBackupsAsync("B.dll"));
    }
}

public class RuntimeStateStoreTests
{
    private static RuntimeStateStore Make(TempDir dir) => new RuntimeStateStore(dir.Path, new TestJson(), new TestLogger(), "Test Plugin");

    private static RunRecord Run(string trigger = "Scheduled") => new RunRecord
    {
        StartedUtc = DateTime.UtcNow,
        Trigger    = trigger,
        Results    = { new RepoRunResult { Name = "A", Outcome = RepoOutcome.Updated, FromVersion = "1", ToVersion = "2" } }
    };

    [Fact]
    public void EmptyStore_LoadsEmptyState()
    {
        using var dir = new TempDir();
        var state = Make(dir).Load();

        Assert.Empty(state.History);
        Assert.Empty(state.PendingRestart);
    }

    [Fact]
    public void History_KeepsTheNewestFirst_AndSurvivesReload()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        store.AddRun(Run("Scheduled"));
        store.AddRun(Run("Manual"));

        var history = Make(dir).Load().History;

        Assert.Equal(new[] { "Manual", "Scheduled" }, history.Select(h => h.Trigger).ToArray());
        Assert.Equal("2", history[0].Results[0].ToVersion);
    }

    [Fact]
    public void History_IsCappedAtTwenty()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        for (var i = 0; i < RuntimeStateStore.MaxHistory + 5; i++) store.AddRun(Run());

        Assert.Equal(RuntimeStateStore.MaxHistory, store.Load().History.Count);
    }

    [Fact]
    public void PendingRestart_IsRecordedAndCanBeDismissed()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = "1", ToVersion = "2", FileName = "A.dll" });

        Assert.Single(store.Load().PendingRestart);

        store.ClearPendingRestart();
        Assert.Empty(store.Load().PendingRestart);
    }

    [Fact]
    public void PendingRestart_SecondUpdateOfTheSamePlugin_KeepsTheOriginalFromVersion()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = "1", ToVersion = "2", FileName = "A.dll" });
        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = "2", ToVersion = "3", FileName = "A.dll" });

        var item = Assert.Single(store.Load().PendingRestart);
        Assert.Equal("1", item.FromVersion);
        Assert.Equal("3", item.ToVersion);
    }

    [Fact]
    public void PendingRestart_GoingBackToTheRunningVersion_RemovesTheEntry()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = "1", ToVersion = "2", FileName = "A.dll" });
        store.AddPendingRestart(new PendingRestartItem { Name = "B", FromVersion = "5", ToVersion = "6", FileName = "B.dll" });

        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = "2", ToVersion = "1", FileName = "A.dll" });

        var left = Assert.Single(store.Load().PendingRestart);
        Assert.Equal("B", left.Name);
    }

    // Found on a real Emby server: a plugin first installed by this tool has no "from" version, and its backup is
    // labelled "unknown". Rolling back to it is still back to what is running.
    [Fact]
    public void PendingRestart_RollingBackToAnUnknownOriginalVersion_RemovesTheEntry()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = null, ToVersion = "2026.08.27", FileName = "A.dll" });

        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = "2026.08.27", ToVersion = "unknown", FileName = "A.dll" });

        Assert.Empty(store.Load().PendingRestart);
    }

    [Fact]
    public void PendingRestart_WhenTheLastEntryCancelsOut_LeavesNothingPending()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = "1", ToVersion = "2", FileName = "A.dll" });
        store.AddPendingRestart(new PendingRestartItem { Name = "A", FromVersion = "2", ToVersion = "1", FileName = "A.dll" });

        var state = store.Load();

        Assert.Empty(state.PendingRestart);
        Assert.Null(state.PendingSinceUtc);
    }

    [Fact]
    public void PendingRestart_IsClearedOnceTheServerHasRestarted()
    {
        using var dir = new TempDir();
        var started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var stale = new PluginRuntimeState
        {
            PendingSinceUtc = started.AddHours(-1),
            PendingRestart  = { new PendingRestartItem { Name = "A", ToVersion = "2", FileName = "A.dll" } }
        };
        new TestJson().SerializeToFile(stale, Path.Combine(dir.Path, "Test Plugin.state.json"));

        Assert.Empty(Make(dir).Load().PendingRestart);
    }

    [Fact]
    public void CorruptFile_IsTreatedAsEmpty()
    {
        using var dir = new TempDir();
        dir.File("Test Plugin.state.json", "{ this is not json");

        var state = Make(dir).Load();

        Assert.Empty(state.History);
    }

    [Fact]
    public void RunRecord_CountsOutcomes()
    {
        var run = new RunRecord
        {
            Results =
            {
                new RepoRunResult { Outcome = RepoOutcome.Updated },
                new RepoRunResult { Outcome = RepoOutcome.UpToDate },
                new RepoRunResult { Outcome = RepoOutcome.NoRelease },
                new RepoRunResult { Outcome = RepoOutcome.Failed },
                new RepoRunResult { Outcome = RepoOutcome.WaitingManual }
            }
        };

        Assert.Equal(1, run.Updated);
        Assert.Equal(2, run.UpToDate);
        Assert.Equal(1, run.Failed);
        Assert.Equal(1, run.Waiting);
    }
}
