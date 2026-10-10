using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

/// <summary>
/// Whole update runs against a fake server with real folders: what gets installed, what gets backed up,
/// what ends up in the history, and when the server is asked to restart.
/// </summary>
[Collection("UpdateRuns")]
public class RepoUpdateServiceTests
{
    private static Task<RunRecord> Run(TestEnv env, string trigger = RepoUpdateService.TriggerScheduled, RunMode mode = RunMode.Auto, string only = null)
        => env.Service.RunAsync(trigger, mode, only);

    // ---- Installing -------------------------------------------------------------------------------------------

    [Fact]
    public async Task NewRelease_IsInstalled_AndEverythingIsRecorded()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old build");
        var bytes = env.Publish("Cool.Plugin", "v1.1.0");

        var run = await Run(env);

        var result = Assert.Single(run.Results);
        Assert.Equal(RepoOutcome.Updated, result.Outcome);
        Assert.Equal("v1.0.0", result.FromVersion);
        Assert.Equal("v1.1.0", result.ToVersion);
        Assert.StartsWith("verified", result.Message);

        // installed
        Assert.Equal(bytes, File.ReadAllBytes(env.PluginFile("Cool.Plugin.dll")));

        // remembered in the settings
        var repo = env.Repo("Cool.Plugin");
        Assert.Equal("v1.1.0", repo.LastVersionDownloaded);
        Assert.Equal("Cool.Plugin.dll", repo.FileName);
        Assert.Equal(RepoStatus.UpToDate, repo.GetStatus());
        Assert.Null(repo.LastError);
        Assert.NotNull(repo.LastUpdatedUtc);

        // the token survived all the saving
        Assert.Equal("ghp_testtoken", env.Options.GitHubToken);
    }

    [Fact]
    public async Task TheOldVersion_IsBackedUpBeforeItIsReplaced()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old build");
        env.Publish("Cool.Plugin", "v1.1.0");

        await Run(env);

        var backups = await new PluginBackupService(env.Plugins, env.Logger).GetBackupsAsync("Cool.Plugin.dll");
        var backup = Assert.Single(backups);
        Assert.Equal("v1.0.0", backup.Version);
        Assert.Equal("old build", File.ReadAllText(backup.BackupPath));
    }

    [Fact]
    public async Task AnUpdate_CanBeRolledBack_FromTheBackup()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old build");
        env.Publish("Cool.Plugin", "v1.1.0");
        await Run(env);

        var restored = await new PluginBackupService(env.Plugins, env.Logger).RestoreBackupAsync("Cool.Plugin.dll");

        Assert.Equal("v1.0.0", restored.Version);
        Assert.Equal("old build", File.ReadAllText(env.PluginFile("Cool.Plugin.dll")));
    }

    [Fact]
    public async Task AFirstInstall_HasNothingToBackUp_AndStillWorks()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");
        env.Publish("Cool.Plugin", "v1.0.0");

        var run = await Run(env);

        Assert.Equal(RepoOutcome.Updated, run.Results[0].Outcome);
        Assert.Empty(await new PluginBackupService(env.Plugins, env.Logger).GetBackupsAsync("Cool.Plugin.dll"));
    }

    [Fact]
    public async Task AlreadyUpToDate_DownloadsNothing()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        env.Publish("Cool.Plugin", "v1.0.0");

        var run = await Run(env);

        Assert.Equal(RepoOutcome.UpToDate, run.Results[0].Outcome);
        Assert.DoesNotContain(env.Handler.Requests, r => r.Contains("/assets/"));
        Assert.Empty(env.HostState.Calls);
    }

    [Fact]
    public async Task ARunRemembersWhatItLearned_EvenWhenNothingWasInstalled()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        env.Publish("Cool.Plugin", "v1.0.0");

        await Run(env);

        var repo = env.Repo("Cool.Plugin");
        Assert.Equal("v1.0.0", repo.LatestVersion);
        Assert.NotNull(repo.LastDateTimeChecked);
        Assert.Contains("notes for v1.0.0", repo.LatestNotes);
        Assert.Contains("Cool.Plugin.dll", repo.GetLatestAssetNames());
    }

    // ---- Restart behaviour ------------------------------------------------------------------------------------

    [Fact]
    public async Task ScheduledRun_DoesNotRestartByDefault_ItOnlyFlagsAPendingRestart()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");
        env.Publish("Cool.Plugin", "v1.0.0");

        await Run(env);

        Assert.Equal(new[] { "NotifyPendingRestart" }, env.HostState.Calls.ToArray());
    }

    [Fact]
    public async Task ScheduledRun_RestartsTheServer_WhenTheSettingIsOn()
    {
        using var env = new TestEnv();
        env.SetRestartAfterInstall(true);
        env.AddRepo("Cool.Plugin");
        env.Publish("Cool.Plugin", "v1.0.0");

        await Run(env);

        Assert.Equal(new[] { "Restart" }, env.HostState.Calls.ToArray());
    }

    [Fact]
    public async Task ARunStartedFromThePage_NeverRestartsTheServerBehindTheUsersBack()
    {
        using var env = new TestEnv();
        env.SetRestartAfterInstall(true);
        env.AddRepo("Cool.Plugin");
        env.Publish("Cool.Plugin", "v1.0.0");

        await Run(env, RepoUpdateService.TriggerManual);

        Assert.Equal(new[] { "NotifyPendingRestart" }, env.HostState.Calls.ToArray());
    }

    [Fact]
    public async Task NoRestartIsRequested_WhenNothingChanged()
    {
        using var env = new TestEnv();
        env.SetRestartAfterInstall(true);
        env.AddRepo("Cool.Plugin", installed: "v1.0.0");
        env.Publish("Cool.Plugin", "v1.0.0");

        await Run(env);

        Assert.Empty(env.HostState.Calls);
    }

    [Fact]
    public async Task UpdatedPlugins_AreListedAsWaitingForARestart()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old");
        env.Publish("Cool.Plugin", "v1.1.0");

        await Run(env);

        var pending = Assert.Single(env.NewState().Load().PendingRestart);
        Assert.Equal("Cool.Plugin", pending.Name);
        Assert.Equal("v1.0.0", pending.FromVersion);
        Assert.Equal("v1.1.0", pending.ToVersion);
    }

    // ---- Which repos get installed ---------------------------------------------------------------------------

    [Fact]
    public async Task AutoMode_ChecksButDoesNotInstall_WhenAutoUpdateIsOff()
    {
        using var env = new TestEnv();
        env.AddRepo("Manual.Plugin", auto: false, installed: "v1.0.0", fileName: "Manual.Plugin.dll");
        File.WriteAllText(env.PluginFile("Manual.Plugin.dll"), "old");
        env.Publish("Manual.Plugin", "v2.0.0");

        var run = await Run(env);

        Assert.Equal(RepoOutcome.WaitingManual, run.Results[0].Outcome);
        Assert.Equal("old", File.ReadAllText(env.PluginFile("Manual.Plugin.dll")));

        var repo = env.Repo("Manual.Plugin");
        Assert.Equal("v2.0.0", repo.LatestVersion);
        Assert.Equal(RepoStatus.UpdateWaiting, repo.GetStatus());
        Assert.Empty(env.HostState.Calls);
    }

    [Fact]
    public async Task AllMode_InstallsEvenWhenAutoUpdateIsOff()
    {
        using var env = new TestEnv();
        env.AddRepo("Manual.Plugin", auto: false, installed: "v1.0.0", fileName: "Manual.Plugin.dll");
        env.Publish("Manual.Plugin", "v2.0.0");

        var run = await Run(env, RepoUpdateService.TriggerManual, RunMode.All);

        Assert.Equal(RepoOutcome.Updated, run.Results[0].Outcome);
        Assert.Equal("v2.0.0", env.Repo("Manual.Plugin").LastVersionDownloaded);
    }

    [Fact]
    public async Task OnlyTheRequestedRepo_IsTouched()
    {
        using var env = new TestEnv();
        var a = env.AddRepo("A.Plugin");
        env.AddRepo("B.Plugin");
        env.Publish("A.Plugin", "v1.0.0");
        env.Publish("B.Plugin", "v1.0.0");

        var run = await Run(env, RepoUpdateService.TriggerManual, RunMode.All, a.Id);

        Assert.Equal(new[] { "A.Plugin" }, run.Results.Select(r => r.Name).ToArray());
        Assert.Null(env.Repo("B.Plugin").LastVersionDownloaded);
        Assert.DoesNotContain(env.Handler.Requests, r => r.Contains("B.Plugin"));
    }

    [Fact]
    public async Task CheckOnly_InstallsNothing_AndLeavesNoHistory()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0");
        env.Publish("Cool.Plugin", "v2.0.0");

        var run = await Run(env, RepoUpdateService.TriggerManual, RunMode.CheckOnly);

        Assert.Equal(RepoOutcome.WaitingManual, run.Results[0].Outcome);
        Assert.Equal("v2.0.0", env.Repo("Cool.Plugin").LatestVersion);
        Assert.Equal("v1.0.0", env.Repo("Cool.Plugin").LastVersionDownloaded);
        Assert.Empty(env.NewState().Load().History);
        Assert.DoesNotContain(env.Activity.Entries, e => e.Type == "GithubRepoPluginRun");
        Assert.Empty(env.HostState.Calls);
    }

    // ---- Failures ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OneBrokenRepo_DoesNotStopTheOthers()
    {
        using var env = new TestEnv();
        env.AddRepo("Gone.Plugin");
        env.AddRepo("Good.Plugin");
        env.Handler.OnJson("/repos/alex/Gone.Plugin/releases", "{}", HttpStatusCode.NotFound);
        env.Publish("Good.Plugin", "v1.0.0");

        var run = await Run(env);

        Assert.Equal(RepoOutcome.Failed, run.Results.Single(r => r.Name == "Gone.Plugin").Outcome);
        Assert.Equal(RepoOutcome.Updated, run.Results.Single(r => r.Name == "Good.Plugin").Outcome);
        Assert.Equal("v1.0.0", env.Repo("Good.Plugin").LastVersionDownloaded);
    }

    [Fact]
    public async Task AFailure_IsExplainedInPlainLanguage_AndShownOnTheRepo()
    {
        using var env = new TestEnv();
        env.AddRepo("Gone.Plugin");
        env.Handler.OnJson("/repos/alex/Gone.Plugin/releases", "{}", HttpStatusCode.NotFound);

        var run = await Run(env);

        Assert.Contains("Repository not found", run.Results[0].Message);
        var repo = env.Repo("Gone.Plugin");
        Assert.Equal(RepoStatus.Failed, repo.GetStatus());
        Assert.Contains("Check the URL", repo.LastError);
    }

    [Fact]
    public async Task ARecoveredRepo_ClearsItsOldError()
    {
        using var env = new TestEnv();
        env.AddRepo("Flaky.Plugin");
        env.Handler.OnJson("/repos/alex/Flaky.Plugin/releases", "{}", HttpStatusCode.NotFound);
        await Run(env);
        Assert.NotNull(env.Repo("Flaky.Plugin").LastError);

        env.Publish("Flaky.Plugin", "v1.0.0");
        await Run(env);

        Assert.Null(env.Repo("Flaky.Plugin").LastError);
    }

    [Fact]
    public async Task ABadChecksum_IsRefused_AndTheWorkingPluginIsLeftAlone()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "working build");
        env.Publish("Cool.Plugin", "v1.1.0", goodDigest: false);

        var run = await Run(env);

        Assert.Equal(RepoOutcome.Failed, run.Results[0].Outcome);
        Assert.Contains("Checksum mismatch", run.Results[0].Message);
        Assert.Equal("working build", File.ReadAllText(env.PluginFile("Cool.Plugin.dll")));
        Assert.Equal("v1.0.0", env.Repo("Cool.Plugin").LastVersionDownloaded);
        Assert.Empty(env.HostState.Calls);
        Assert.Empty(env.NewState().Load().PendingRestart);
    }

    [Fact]
    public async Task AReleaseWithNothingToInstall_FailsWithAClearReason()
    {
        using var env = new TestEnv();
        env.AddRepo("Docs.Only");
        env.Handler.OnJson("/repos/alex/Docs.Only/releases", "[" + Gh.Release("v1", "[" + Gh.Asset("Docs.Only", "readme.txt") + "]") + "]");

        var run = await Run(env);

        Assert.Equal(RepoOutcome.Failed, run.Results[0].Outcome);
        Assert.Contains("no .dll or .zip", run.Results[0].Message);
    }

    [Fact]
    public async Task ARepoWithNoReleases_IsNotAnError()
    {
        using var env = new TestEnv();
        env.AddRepo("Empty.Plugin");
        env.Handler.OnJson("/repos/alex/Empty.Plugin/releases", "[]");

        var run = await Run(env);

        Assert.Equal(RepoOutcome.NoRelease, run.Results[0].Outcome);
        Assert.Null(env.Repo("Empty.Plugin").LastError);
    }

    // ---- History and the activity log -------------------------------------------------------------------------

    [Fact]
    public async Task EachRun_IsAddedToTheHistory_WithItsTriggerAndCounts()
    {
        using var env = new TestEnv();
        env.AddRepo("A.Plugin");
        env.AddRepo("B.Plugin", installed: "v1.0.0");
        env.Publish("A.Plugin", "v1.0.0");
        env.Publish("B.Plugin", "v1.0.0");

        await Run(env, RepoUpdateService.TriggerScheduled);
        await Run(env, RepoUpdateService.TriggerManual, RunMode.All);

        var history = env.NewState().Load().History;
        Assert.Equal(new[] { "Manual", "Scheduled" }, history.Select(h => h.Trigger).ToArray());
        Assert.Equal(1, history[1].Updated);
        Assert.Equal(2, history[0].UpToDate);
        Assert.True(history[1].DurationSeconds >= 0);
    }

    [Fact]
    public async Task TheActivityLog_GetsInstallEntries_AFailureEntry_AndOneSummary()
    {
        using var env = new TestEnv();
        env.AddRepo("Good.Plugin");
        env.AddRepo("Gone.Plugin");
        env.Publish("Good.Plugin", "v1.0.0");
        env.Handler.OnJson("/repos/alex/Gone.Plugin/releases", "{}", HttpStatusCode.NotFound);

        await Run(env);

        var types = env.Activity.Entries.Select(e => e.Type).OrderBy(t => t).ToArray();
        Assert.Equal(new[] { "GithubRepoPluginRun", "GithubRepoPluginUpdateFailed", "PluginInstalled" }, types);

        // Entries tied to a user are listed under Activity, the rest under Alerts. Keep a run's entries together.
        Assert.All(env.Activity.Entries, e => Assert.Null(e.UserId));

        var summary = env.Activity.Entries.Single(e => e.Type == "GithubRepoPluginRun");
        Assert.Contains("1 updated", summary.Name);
        Assert.Contains("1 failed", summary.Name);
        Assert.Equal(MediaBrowser.Model.Logging.LogSeverity.Warn, summary.Severity);
    }

    [Fact]
    public async Task HealthyChecks_DoNotFillTheActivityLogWithEntries()
    {
        using var env = new TestEnv();
        env.AddRepo("A.Plugin", installed: "v1.0.0");
        env.AddRepo("B.Plugin", installed: "v1.0.0");
        env.Publish("A.Plugin", "v1.0.0");
        env.Publish("B.Plugin", "v1.0.0");

        await Run(env);

        var entry = Assert.Single(env.Activity.Entries);
        Assert.Equal("GithubRepoPluginRun", entry.Type);
        Assert.Equal(MediaBrowser.Model.Logging.LogSeverity.Info, entry.Severity);
    }

    // ---- Housekeeping -----------------------------------------------------------------------------------------

    [Fact]
    public async Task LeftoverOldFiles_AreCleanedUpAtTheStartOfARun()
    {
        using var env = new TestEnv();
        var stale = Path.Combine(env.Plugins, "Cool.Plugin.dll.old");
        File.WriteAllText(stale, "left over from a locked update");
        env.AddRepo("Cool.Plugin", installed: "v1.0.0");
        env.Publish("Cool.Plugin", "v1.0.0");

        await Run(env);

        Assert.False(File.Exists(stale));
    }

    [Fact]
    public async Task ProgressIsReported_AndTheRunIsNotLeftMarkedAsRunning()
    {
        using var env = new TestEnv();
        env.AddRepo("A.Plugin", installed: "v1");
        env.AddRepo("B.Plugin", installed: "v1");
        env.Publish("A.Plugin", "v1");
        env.Publish("B.Plugin", "v1");
        var reports = new System.Collections.Generic.List<double>();

        await env.Service.RunAsync(RepoUpdateService.TriggerScheduled, RunMode.Auto, null, new Progress<double>(reports.Add));
        await Task.Delay(50);

        Assert.False(RepoUpdateService.GetProgress().IsRunning);
        Assert.Contains(100.0, reports);
    }

    [Fact]
    public async Task ASecondRun_WhileOneIsInProgress_IsRefused_AndTheFirstStillFinishes()
    {
        using var env = new TestEnv();
        env.AddRepo("Slow.Plugin", installed: "v1");
        using var gate = new ManualResetEventSlim(false);
        env.Handler.On("/repos/alex/Slow.Plugin/releases", _ =>
        {
            gate.Wait(TimeSpan.FromSeconds(10));
            return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent("[" + Gh.Release("v1", "[]") + "]")
            };
        });

        var first = Task.Run(() => Run(env));
        RunRecord run;
        try
        {
            // The run marks itself as started first and names the plugin it is on a moment later
            for (var i = 0; i < 500 && RepoUpdateService.GetProgress().Current != "Slow.Plugin"; i++) await Task.Delay(20);

            var progress = RepoUpdateService.GetProgress();
            Assert.True(progress.IsRunning);
            Assert.Equal(1, progress.Total);
            Assert.Equal("Slow.Plugin", progress.Current);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Run(env));
        }
        finally
        {
            // Always let the first run finish, so a failed assertion can't leave the lock held for other tests
            gate.Set();
            run = await first;
        }

        Assert.Equal(RepoOutcome.UpToDate, run.Results[0].Outcome);
        Assert.False(RepoUpdateService.GetProgress().IsRunning);
    }

    [Fact]
    public async Task ACancelledRun_StopsCleanly_AndReleasesTheLock()
    {
        using var env = new TestEnv();
        env.AddRepo("A.Plugin", installed: "v1");
        env.Publish("A.Plugin", "v1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => env.Service.RunAsync(RepoUpdateService.TriggerManual, RunMode.Auto, null, null, cts.Token));

        Assert.False(RepoUpdateService.GetProgress().IsRunning);
        var run = await Run(env);
        Assert.Single(run.Results);
    }
}
