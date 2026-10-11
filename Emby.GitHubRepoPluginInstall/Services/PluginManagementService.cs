using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.GithubAPI;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Storage;
using Emby.GitHubRepoPluginInstall.Validation;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;

namespace Emby.GitHubRepoPluginInstall.Services;

public enum ManageOutcome
{
    Ok,
    NotFound,
    Invalid,
    Duplicate,
    Refused,
    Failed
}

/// <summary>What a management action did. <see cref="Message"/> is a plain sentence that can be shown to a person.</summary>
public class ManageResult
{
    public ManageOutcome  Outcome { get; set; }
    public string         Message { get; set; }
    public ReposToProcess Repo    { get; set; }

    /// <summary>True when the change means Emby has to be restarted before it takes full effect.</summary>
    public bool RestartNeeded { get; set; }

    public bool Ok => Outcome == ManageOutcome.Ok;

    public static ManageResult Success(string message, ReposToProcess repo = null, bool restart = false) =>
        new ManageResult { Outcome = ManageOutcome.Ok, Message = message, Repo = repo, RestartNeeded = restart };

    public static ManageResult Fail(ManageOutcome outcome, string message) =>
        new ManageResult { Outcome = outcome, Message = message };
}

/// <summary>The settings for adding or editing a plugin entry.</summary>
public class RepoSettings
{
    /// <summary>Set when editing an existing entry; empty when adding.</summary>
    public string Id              { get; set; }
    public string Url             { get; set; }
    public bool   AllowPreRelease { get; set; }
    public bool   AutoUpdate      { get; set; }
    public string AssetNameFilter { get; set; }
}

public class RegistrySettings
{
    /// <summary>Set when editing an existing registry; empty when adding.</summary>
    public string Id      { get; set; }
    public string Name    { get; set; }
    public string RawUrl  { get; set; }
    public bool   Enabled { get; set; } = true;
}

/// <summary>
/// Adding, editing, removing and restoring plugins and registries. The plugin page and the phone API both go through
/// this class, so they can't disagree about how a change is made. It works on whichever options object it is given and
/// calls <c>save</c> after each change.
/// </summary>
public class PluginManagementService
{
    public const string BuiltInRegistryId = "embedded-default";

    private readonly IApplicationHost                _appHost;
    private readonly ILogger                         _logger;
    private readonly Func<PluginUIOptions>           _options;
    private readonly Action                          _save;
    private readonly Action                          _reload;
    private readonly Func<string, GitHubApiClient>   _clientFactory;

    /// <param name="options">Gives the options object to change.</param>
    /// <param name="save">Writes that object to disk.</param>
    /// <param name="reload">Called after an update run has written to the options file, so the object can pick that up. Optional.</param>
    public PluginManagementService(IApplicationHost appHost,
                                   ILogger logger,
                                   Func<PluginUIOptions> options,
                                   Action save,
                                   Action reload = null,
                                   Func<string, GitHubApiClient> clientFactory = null)
    {
        _appHost       = appHost;
        _logger        = logger;
        _options       = options;
        _save          = save;
        _reload        = reload ?? (() => { });
        _clientFactory = clientFactory;
    }

    private PluginUIOptions Options => _options();

    private RuntimeStateStore StateStore => new RuntimeStateStore(_appHost, _logger, GitHubRepPluginInstall.PluginName);

    public ReposToProcess FindRepo(string repoId) =>
        string.IsNullOrEmpty(repoId) ? null : Options.Repos?.Find(x => x.Id == repoId);

    // ---- Plugins ----------------------------------------------------------------------------------------------

    /// <summary>Turns auto-update on or off for a plugin. With <paramref name="value"/> null it flips the current setting.</summary>
    public ManageResult SetAutoUpdate(string repoId, bool? value = null)
    {
        var repo = FindRepo(repoId);
        if (repo == null)
            return ManageResult.Fail(ManageOutcome.NotFound, "That plugin is not in the list.");

        repo.AutoUpdate = value ?? !repo.AutoUpdate;
        _save();
        return ManageResult.Success($"Auto-update is now {(repo.AutoUpdate ? "on" : "off")} for {repo.Repository}.", repo);
    }

    /// <summary>Changes the settings of a plugin that is already in the list. Only the fields given are changed.</summary>
    public ManageResult UpdateSettings(string repoId, bool? autoUpdate, bool? allowPreRelease, string assetNameFilter)
    {
        var repo = FindRepo(repoId);
        if (repo == null)
            return ManageResult.Fail(ManageOutcome.NotFound, "That plugin is not in the list.");

        if (autoUpdate.HasValue) repo.AutoUpdate = autoUpdate.Value;
        if (allowPreRelease.HasValue) repo.GetPreRelease = allowPreRelease.Value;
        if (assetNameFilter != null) repo.AssetNameFilter = assetNameFilter;

        _save();
        return ManageResult.Success($"Saved the settings for {repo.Repository}.", repo);
    }

    /// <summary>Deletes the plugin's file (if any) and takes it off the list.</summary>
    public ManageResult RemoveRepo(string repoId)
    {
        var item = FindRepo(repoId);
        if (item == null)
            return ManageResult.Fail(ManageOutcome.NotFound, "That plugin is not in the list.");

        try
        {
            var fileDeleted      = false;
            var applicationPaths = _appHost.Resolve<IApplicationPaths>();

            if (!string.IsNullOrEmpty(item.FileName))
            {
                var fullPath = Path.Combine(applicationPaths.PluginsPath, item.FileName);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    fileDeleted = true;
                    _logger.Info($"Deleted plugin file: {fullPath}");
                }
            }

            Options.Repos.Remove(item);

            // Only a deleted plugin file needs a restart
            if (fileDeleted)
                _appHost.NotifyPendingRestart();

            _save();

            return ManageResult.Success(fileDeleted
                                            ? $"Removed {item.Repository} and deleted its file. Restart Emby to finish."
                                            : $"Removed {item.Repository} from the list.",
                                        item,
                                        fileDeleted);
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error during repository removal", ex);
            return ManageResult.Fail(ManageOutcome.Failed, $"Could not remove the plugin: {UI.MainPageView.Brief(ex.Message)}");
        }
    }

    /// <summary>Puts the newest backup of the plugin's file back, and turns auto-update off for it.</summary>
    public async Task<ManageResult> RestoreAsync(string repoId)
    {
        var repo = FindRepo(repoId);
        if (repo == null)
            return ManageResult.Fail(ManageOutcome.NotFound, "That plugin is not in the list.");

        if (string.IsNullOrEmpty(repo.FileName))
            return ManageResult.Fail(ManageOutcome.Refused, $"Nothing to restore: no installed file is recorded for {repo.Repository}.");

        try
        {
            var backups = new PluginBackupService(_appHost, _logger);
            var backup  = await backups.RestoreBackupAsync(repo.FileName).ConfigureAwait(false);

            if (backup == null)
                return ManageResult.Fail(ManageOutcome.Refused, $"No backup of {repo.Repository} yet. One is made whenever it is updated.");

            var from = repo.LastVersionDownloaded;
            repo.LastVersionDownloaded = backup.Version == "unknown" ? null : backup.Version;
            repo.AutoUpdate            = false;
            repo.LastError             = null;

            // If the restored file matches what is still running (restoring right after an update, before a restart),
            // no restart is needed and nothing stays pending.
            var state = StateStore;
            state.AddPendingRestart(new PendingRestartItem
                                    {
                                        Name        = repo.Repository,
                                        FromVersion = from,
                                        ToVersion   = backup.Version,
                                        FileName    = repo.FileName
                                    });
            var restart = state.Load().PendingRestart.Count > 0;
            if (restart)
                _appHost.NotifyPendingRestart();

            _save();

            var which = backup.Version == "unknown" ? "the previous version of" : backup.Version + " of";
            return ManageResult.Success($"Restored {which} {repo.Repository}. Auto-update is now off for it.", repo, restart);
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"Restore failed for {repo.Repository}", ex);
            return ManageResult.Fail(ManageOutcome.Failed, $"Could not restore {repo.Repository}: {UI.MainPageView.Brief(ex.Message)}");
        }
    }

    /// <summary>
    /// Adds a plugin to the list, or changes the one named by <see cref="RepoSettings.Id"/>, then looks it up on GitHub
    /// so its card has a real status straight away.
    /// </summary>
    public async Task<ManageResult> ApplyRepoAsync(RepoSettings ui)
    {
        // Accepts owner/repo, ssh addresses, .git suffixes and /releases links, and stores one canonical form
        var validation = GitHubUrlValidator.ValidateUrl(ui.Url);
        if (!validation.IsValid)
            return ManageResult.Fail(ManageOutcome.Invalid,
                                     "Not saved. Use an address like owner/repository or https://github.com/owner/repository.");

        var url = validation.CorrectedUrl ?? ui.Url;
        var key = GitHubApiClient.RegistryKey(url);

        if (Options.Repos == null)
            Options.Repos = new List<ReposToProcess>();

        bool IsDuplicate(string currentId) =>
            Options.Repos.Any(x => x.RepoKey == key && x.Id != currentId);

        ReposToProcess saved;

        if (!string.IsNullOrEmpty(ui.Id))
        {
            saved = Options.Repos.Find(x => x.Id == ui.Id);
            if (saved == null)
                return ManageResult.Fail(ManageOutcome.NotFound, "That plugin is not in the list.");

            if (IsDuplicate(saved.Id))
                return ManageResult.Fail(ManageOutcome.Duplicate, $"{url} is already in the list.");

            saved.Url             = url;
            saved.GetPreRelease   = ui.AllowPreRelease;
            saved.AutoUpdate      = ui.AutoUpdate;
            saved.AssetNameFilter = ui.AssetNameFilter ?? "";
        }
        else
        {
            if (IsDuplicate(null))
                return ManageResult.Fail(ManageOutcome.Duplicate, $"{url} is already in the list.");

            saved = new ReposToProcess
                    {
                        Url             = url,
                        GetPreRelease   = ui.AllowPreRelease,
                        AutoUpdate      = ui.AutoUpdate,
                        AssetNameFilter = ui.AssetNameFilter ?? ""
                    };
            Options.Repos.Add(saved);
        }

        _save();

        // Look the repository up straight away so its card shows a real status (or the reason it can't be reached)
        try
        {
            var service = new RepoUpdateService(_appHost, _logger, _clientFactory);
            await service.RunAsync(RepoUpdateService.TriggerManual, RunMode.CheckOnly, saved.Id).ConfigureAwait(false);
            _reload();
        }
        catch (Exception ex)
        {
            _logger.Debug($"Initial check of {url} was skipped: {UI.MainPageView.Brief(ex.Message)}");
        }

        return ManageResult.Success(validation.Message ?? $"Saved {saved.Repository}.", saved);
    }

    // ---- Registries -------------------------------------------------------------------------------------------

    public ManageResult ToggleRegistry(string registryId, bool? enabled = null)
    {
        var registry = Options.PluginRegistries?.Find(x => x.Id == registryId);
        if (registry == null)
            return ManageResult.Fail(ManageOutcome.NotFound, "That registry is not in the list.");

        registry.Enabled = enabled ?? !registry.Enabled;
        _save();
        return ManageResult.Success($"{registry.Name} is now {(registry.Enabled ? "on" : "off")}.");
    }

    public ManageResult RemoveRegistry(string registryId)
    {
        var registry = Options.PluginRegistries?.Find(x => x.Id == registryId);
        if (registry == null)
            return ManageResult.Fail(ManageOutcome.NotFound, "That registry is not in the list.");

        if (registry.Id == BuiltInRegistryId)
            return ManageResult.Fail(ManageOutcome.Refused, "The built-in registry can't be removed.");

        Options.PluginRegistries.Remove(registry);
        _save();
        return ManageResult.Success($"Removed registry: {registry.Name}");
    }

    /// <summary>Adds a registry, or changes the one named by <see cref="RegistrySettings.Id"/>.</summary>
    public ManageResult UpsertRegistry(RegistrySettings settings)
    {
        if (Options.PluginRegistries == null)
            Options.PluginRegistries = new List<PluginRegistry>();

        var existing = string.IsNullOrEmpty(settings.Id)
                           ? null
                           : Options.PluginRegistries.FirstOrDefault(r => r.Id == settings.Id);

        if (existing != null)
        {
            if (existing.Id == BuiltInRegistryId)
                return ManageResult.Fail(ManageOutcome.Refused, "The built-in registry can't be edited.");

            existing.Name    = settings.Name;
            existing.RawUrl  = settings.RawUrl;
            existing.Enabled = settings.Enabled;
        }
        else
        {
            Options.PluginRegistries.Add(new PluginRegistry
                                         {
                                             Id      = string.IsNullOrEmpty(settings.Id) ? Guid.NewGuid().ToString() : settings.Id,
                                             Name    = settings.Name,
                                             RawUrl  = settings.RawUrl,
                                             Enabled = settings.Enabled
                                         });
        }

        _save();
        return ManageResult.Success($"Saved registry: {settings.Name}");
    }
}
