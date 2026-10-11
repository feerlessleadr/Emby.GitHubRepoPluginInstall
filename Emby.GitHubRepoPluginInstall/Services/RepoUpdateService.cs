using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.GithubAPI;
using Emby.GitHubRepoPluginInstall.Helpers;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Storage;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.GitHubRepoPluginInstall.Services;

public enum RunMode
{
    /// <summary>Install updates for repos with auto-update on; only check the rest. What the scheduled task does.</summary>
    Auto,

    /// <summary>Install updates for every repo, including ones with auto-update off.</summary>
    All,

    /// <summary>Look for new releases and record them. Installs nothing and adds nothing to the history.</summary>
    CheckOnly
}

public class RunProgressInfo
{
    public bool     IsRunning  { get; set; }
    public int      Total      { get; set; }
    public int      Done       { get; set; }
    public string   Current    { get; set; }
    public string   Trigger    { get; set; }
    public DateTime StartedUtc { get; set; }
}

/// <summary>
/// The one place that checks GitHub, installs updates, backs up what it replaces, and records what happened.
/// The scheduled task and every button on the plugin page go through here.
/// </summary>
public class RepoUpdateService
{
    public const string TriggerScheduled = "Scheduled";
    public const string TriggerManual    = "Manual";

    private static readonly SemaphoreSlim RunLock      = new SemaphoreSlim(1, 1);
    private static readonly object        ProgressLock = new object();
    private static          RunProgressInfo _progress   = new RunProgressInfo();

    private readonly IApplicationHost            _appHost;
    private readonly ILogger                     _logger;
    private readonly Func<string, GitHubApiClient> _clientFactory;

    /// <param name="clientFactory">Creates the GitHub client from a token. Tests pass one that talks to a fake server.</param>
    public RepoUpdateService(IApplicationHost appHost, ILogger logger, Func<string, GitHubApiClient> clientFactory = null)
    {
        _appHost       = appHost;
        _logger        = logger;
        _clientFactory = clientFactory ?? (token => new GitHubApiClient(token, appHost.Resolve<IJsonSerializer>(), logger));
    }

    public static RunProgressInfo GetProgress()
    {
        lock (ProgressLock)
        {
            return new RunProgressInfo
                   {
                       IsRunning  = _progress.IsRunning,
                       Total      = _progress.Total,
                       Done       = _progress.Done,
                       Current    = _progress.Current,
                       Trigger    = _progress.Trigger,
                       StartedUtc = _progress.StartedUtc
                   };
        }
    }

    private static void SetProgress(Action<RunProgressInfo> change)
    {
        lock (ProgressLock)
        {
            change(_progress);
        }
    }

    /// <summary>
    /// Runs one pass. Throws <see cref="InvalidOperationException"/> if another pass is already running.
    /// </summary>
    public Task<RunRecord> RunAsync(string trigger,
                                    RunMode mode,
                                    string onlyRepoId = null,
                                    IProgress<double> progress = null,
                                    CancellationToken cancellationToken = default)
    {
        var only = onlyRepoId == null ? null : new HashSet<string> { onlyRepoId };
        return RunCoreAsync(trigger, mode, only, progress, cancellationToken);
    }

    /// <summary>
    /// Runs one pass over just the plugins with these ids. Used by "Update plugins with updates", which should touch
    /// only those plugins and leave everything else alone.
    /// </summary>
    public Task<RunRecord> RunForReposAsync(string trigger,
                                            RunMode mode,
                                            IEnumerable<string> repoIds,
                                            IProgress<double> progress = null,
                                            CancellationToken cancellationToken = default)
    {
        if (repoIds == null) throw new ArgumentNullException(nameof(repoIds));

        return RunCoreAsync(trigger, mode, new HashSet<string>(repoIds), progress, cancellationToken);
    }

    private async Task<RunRecord> RunCoreAsync(string trigger,
                                               RunMode mode,
                                               HashSet<string> onlyRepoIds,
                                               IProgress<double> progress,
                                               CancellationToken cancellationToken)
    {
        if (!await RunLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An update run is already in progress.");

        var record = new RunRecord { StartedUtc = DateTime.UtcNow, Trigger = trigger };
        var timer  = Stopwatch.StartNew();

        try
        {
            var pluginName = GitHubRepPluginInstall.PluginName;
            var store      = new SecurePluginOptionsStore(_appHost, _logger, pluginName);
            var state      = new RuntimeStateStore(_appHost, _logger, pluginName);
            var paths      = _appHost.Resolve<IApplicationPaths>();
            var options    = store.GetOptions();

            var repos = options.Repos.Where(r => onlyRepoIds == null || onlyRepoIds.Contains(r.Id)).ToList();

            SetProgress(p =>
                        {
                            p.IsRunning  = true;
                            p.Total      = repos.Count;
                            p.Done       = 0;
                            p.Current    = null;
                            p.Trigger    = trigger;
                            p.StartedUtc = record.StartedUtc;
                        });

            FileOps.CleanupOldFiles(paths.PluginsPath);

            var backup    = new PluginBackupService(paths.PluginsPath, _logger);
            var hasToken  = !string.IsNullOrEmpty(options.GitHubToken);

            using (var client = _clientFactory(options.GitHubToken))
            {
                foreach (var repo in repos)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SetProgress(p => p.Current = repo.Repository);

                    var install = mode == RunMode.All || (mode == RunMode.Auto && repo.AutoUpdate);
                    var result = await ProcessRepoAsync(repo, install, client, backup, state, paths.PluginsPath,
                                                        hasToken, cancellationToken).ConfigureAwait(false);
                    record.Results.Add(result);

                    // Save after every repo so a crash or cancel doesn't lose what was already installed
                    store.SetOptions(options);

                    SetProgress(p => p.Done++);
                    progress?.Report(record.Results.Count * 100.0 / Math.Max(1, repos.Count));
                }
            }

            record.DurationSeconds = timer.Elapsed.TotalSeconds;

            if (mode != RunMode.CheckOnly && record.Results.Count > 0)
            {
                state.AddRun(record);
                CreateSummaryEntry(record);
            }

            if (record.Updated > 0)
            {
                // Unattended runs honour the restart setting. Runs started from the page never restart the server
                // behind the user's back; the page shows a "Restart now" button instead.
                if (trigger == TriggerScheduled && options.RestartServerAfterInstall)
                    _appHost.Restart();
                else
                    _appHost.NotifyPendingRestart();
            }

            return record;
        }
        finally
        {
            SetProgress(p =>
                        {
                            p.IsRunning = false;
                            p.Current   = null;
                        });
            RunLock.Release();
        }
    }

    private async Task<RepoRunResult> ProcessRepoAsync(ReposToProcess repo,
                                                       bool install,
                                                       GitHubApiClient client,
                                                       PluginBackupService backup,
                                                       RuntimeStateStore state,
                                                       string pluginsPath,
                                                       bool hasToken,
                                                       CancellationToken cancellationToken)
    {
        var name = repo.Repository;
        var result = new RepoRunResult
                     {
                         RepoId      = repo.Id,
                         Name        = name,
                         FromVersion = repo.LastVersionDownloaded
                     };

        GitHubRelease release;
        try
        {
            release = await client.GetLatestReleaseAsync(repo, true, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException ex)
        {
            return Fail(repo, result, FriendlyMessage(ex, hasToken), ex.Message);
        }

        repo.LastDateTimeChecked = DateTime.UtcNow;

        if (release == null)
        {
            repo.LastError     = null;
            repo.LatestVersion = null;
            result.Outcome     = RepoOutcome.NoRelease;
            result.Message     = "No release has been published yet.";
            return result;
        }

        repo.LatestVersion      = release.TagName;
        repo.LatestPublishedUtc = release.PublishedAt == default ? (DateTime?)null : release.PublishedAt.UtcDateTime;
        repo.LatestNotes        = TrimNotes(release.Body ?? release.GitHubCommit?.GitHubCommitDetails?.Message);
        repo.LatestAssets       = string.Join("|", (release.Assets ?? new System.Collections.Generic.List<GitHubReleaseAsset>())
                                                     .Where(a => !string.IsNullOrEmpty(a.Name)).Select(a => a.Name));
        repo.LastError          = null;
        result.ToVersion        = release.TagName;

        if (string.Equals(release.TagName, repo.LastVersionDownloaded, StringComparison.OrdinalIgnoreCase))
        {
            result.Outcome = RepoOutcome.UpToDate;
            return result;
        }

        if (!install)
        {
            result.Outcome = RepoOutcome.WaitingManual;
            result.Message = repo.AutoUpdate ? "Not installed in this run." : "Auto-update is off for this plugin.";
            return result;
        }

        var asset = GitHubApiClient.PickAsset(release, repo);
        if (asset == null)
        {
            var filter = string.IsNullOrWhiteSpace(repo.AssetNameFilter) ? "" : $" matching the filter '{repo.AssetNameFilter}'";
            return Fail(repo, result, $"Release {release.TagName} has no .dll or .zip file{filter}.", null);
        }

        try
        {
            // Keep a copy of what is about to be replaced
            var current = !string.IsNullOrEmpty(repo.FileName) ? repo.FileName : (asset.IsDll ? Path.GetFileName(asset.Name) : null);
            if (!string.IsNullOrEmpty(current))
            {
                try
                {
                    await backup.CreateBackupAsync(Path.Combine(pluginsPath, current), repo.LastVersionDownloaded ?? "unknown", cancellationToken)
                                .ConfigureAwait(false);
                }
                catch (Exception backupEx) when (!(backupEx is OperationCanceledException))
                {
                    _logger.Warn($"Could not back up {current} before updating: {backupEx.Message}");
                }
            }

            var previous = repo.LastVersionDownloaded;
            var download = await client.DownloadReleaseAsync(release, pluginsPath, null, cancellationToken, repo).ConfigureAwait(false);

            repo.FileName              = download.FileName;
            repo.LastVersionDownloaded = release.TagName;
            repo.LastUpdatedUtc        = DateTime.UtcNow;
            repo.LastError             = null;

            result.Outcome = RepoOutcome.Updated;
            result.Message = download.Verification;

            state.AddPendingRestart(new PendingRestartItem
                                    {
                                        Name        = name,
                                        FromVersion = previous,
                                        ToVersion   = release.TagName,
                                        FileName    = download.FileName
                                    });

            CreateEntry(new ActivityLogEntry
                        {
                            Name = $"Plugin {name} updated to {release.TagName}",
                            Overview = ActivityLogHelper.CreateSuccessHtml(
                                $"Plugin {name} Successfully Updated",
                                "Plugin has been downloaded and installed. Restart Emby to start using it.",
                                release.TagName,
                                $"Integrity check: {download.Verification}\n\n" +
                                (release.Body ?? release.GitHubCommit?.GitHubCommitDetails?.Message ?? "No release notes available.")),
                            ShortOverview = null,
                            Type          = "PluginInstalled",
                            ItemId        = null,
                            Date          = DateTimeOffset.Now,
                            Severity      = LogSeverity.Info
                        });

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitHubApiException ex)
        {
            return Fail(repo, result, FriendlyMessage(ex, hasToken), ex.Message);
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"Failed to update {name}", ex);
            return Fail(repo, result, ex.Message, ex.ToString());
        }
    }

    private RepoRunResult Fail(ReposToProcess repo, RepoRunResult result, string message, string technical)
    {
        repo.LastError             = message;
        repo.LastDateTimeChecked   = DateTime.UtcNow;
        result.Outcome             = RepoOutcome.Failed;
        result.Message             = message;

        CreateEntry(new ActivityLogEntry
                    {
                        Name          = $"Release {repo.Repository} NOT Updated",
                        Overview      = ActivityLogHelper.CreateErrorHtml(
                            $"Failed to Update Plugin {repo.Repository}",
                            message,
                            technical,
                            null),
                        ShortOverview = null,
                        Type          = "GithubRepoPluginUpdateFailed",
                        ItemId        = null,
                        Date          = DateTimeOffset.Now,
                        Severity      = LogSeverity.Error
                    });

        return result;
    }

    private void CreateSummaryEntry(RunRecord record)
    {
        var parts = $"{record.Updated} updated, {record.UpToDate} up to date, {record.Failed} failed";
        if (record.Waiting > 0) parts += $", {record.Waiting} waiting on you";

        CreateEntry(new ActivityLogEntry
                    {
                        Name          = $"Plugin update run finished: {parts}",
                        Overview      = ActivityLogHelper.CreateRunSummaryHtml(record),
                        ShortOverview = null,
                        Type          = "GithubRepoPluginRun",
                        ItemId        = null,
                        Date          = DateTimeOffset.Now,
                        Severity      = record.Failed > 0 ? LogSeverity.Warn : LogSeverity.Info
                    });
    }

    private void CreateEntry(ActivityLogEntry entry)
    {
        try
        {
            _appHost.Resolve<IActivityManager>().Create(entry);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not write activity log entry '{entry.Name}': {ex.Message}");
        }
    }


    public const int MaxStoredNotes = 2000;

    /// <summary>
    /// Release notes are kept so the page can show them without calling GitHub. Very long ones are cut, at the end of a
    /// line where there is one, so the last bullet isn't left half-finished.
    /// </summary>
    internal static string TrimNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;

        notes = notes.Trim();
        if (notes.Length <= MaxStoredNotes) return notes;

        var window = notes.Substring(0, MaxStoredNotes);
        var lastBreak = window.LastIndexOf('\n');
        var kept = lastBreak > MaxStoredNotes / 2 ? window.Substring(0, lastBreak) : window;
        return kept.TrimEnd() + "\n…";
    }

    /// <summary>A sentence the user can act on, instead of an HTTP status code.</summary>
    public static string FriendlyMessage(GitHubApiException ex, bool hasToken)
    {
        switch (ex.Kind)
        {
            case GitHubErrorKind.Unauthorized:
                return "GitHub rejected the token. It may have expired or been revoked. Click Test Token, and enter a new one if it fails.";

            case GitHubErrorKind.RateLimited:
                var reset = ex.ResetUtc != null ? $" It resets at {ex.ResetUtc.Value.ToLocalTime():HH:mm}." : "";
                var tip = hasToken ? "" : " Adding a token raises the limit from 60 to 5,000 requests per hour.";
                return "GitHub's request limit was reached." + reset + tip;

            case GitHubErrorKind.Forbidden:
                return "GitHub denied access. If the repository is private, the token needs read access to its contents.";

            case GitHubErrorKind.NotFound:
                return "Repository not found. Check the URL, and for a private repository make sure the token can see it.";

            case GitHubErrorKind.Network:
                return $"Couldn't reach GitHub ({ex.Message}). Check the server's internet connection.";

            default:
                return ex.Message;
        }
    }
}
