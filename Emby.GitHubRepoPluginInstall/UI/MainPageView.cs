using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.GithubAPI;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.Storage;
using Emby.GitHubRepoPluginInstall.UIBaseClasses.Views;
using Emby.GitHubRepoPluginInstall.Validation;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;

namespace Emby.GitHubRepoPluginInstall.UI;

internal class MainPageView : PluginPageView
{
    private readonly ILogger                  _logger;
    private readonly SecurePluginOptionsStore _store;
    private readonly IApplicationHost         _appHost;
    private readonly IJsonSerializer          _jsonSerializer;
    private readonly PluginInfo               _pluginInfo;
    private readonly Func<string, GitHubApiClient> _clientFactory;
    private          DateTime                  _seenOptionsStamp;

    public MainPageView(PluginInfo pluginInfo,
                        SecurePluginOptionsStore store,
                        ILogger logger,
                        IApplicationHost appHost,
                        IJsonSerializer jsonSerializer,
                        Func<string, GitHubApiClient> clientFactory = null) : base(pluginInfo.Id)
    {
        _pluginInfo     = pluginInfo;
        _store          = store;
        _logger         = logger;
        _appHost        = appHost;
        _jsonSerializer = jsonSerializer;
        _clientFactory  = clientFactory ?? (token => new GitHubApiClient(token, jsonSerializer, logger));
        ShowSave        = false;
        var data = store.GetOptions();
        _seenOptionsStamp = OptionsFileStamp();

        ContentData = data;

        // Initialize default registry if none exist
        InitializeDefaultRegistry();

        // Built from stored data, so opening the page never waits on GitHub
        RefreshView();
    }

    public PluginUIOptions PluginUiOptions => ContentData as PluginUIOptions;

    private RuntimeStateStore StateStore => new RuntimeStateStore(_appHost, _logger, GitHubRepPluginInstall.PluginName);

    private RepoUpdateService UpdateService => new RepoUpdateService(_appHost, _logger, _clientFactory);

    public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
    {
        SaveOptions();
        return base.OnSaveCommand(itemId, commandId, data);
    }

    public override bool IsCommandAllowed(string commandKey)
    {
        switch (commandKey)
        {
            case "Add":
            case "Remove":
            case "Edit":
            case "Save":
            case "Download":
            case "UpdateAll":
            case "RunNow":
            case "CheckAll":
            case "RefreshView":
            case "RestartNow":
            case "DismissRestart":
            case "TestToken":
            case "RestoreBackup":
            case "AddRegistry":
            case "EditRegistry":
            case "RemoveRegistry":
            case "ToggleAuto":
            case "ToggleRegistry":
                return true;
        }

        return base.IsCommandAllowed(commandKey);
    }

    public override async Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
    {
        switch (commandId)
        {
            case "Download":
                return await UpdateOneAsync(itemId);

            case "Save":
                return SaveSettings();

            case "TestToken":
                return await TestTokenAsync();

            case "RunNow":
                return await StartBackgroundRunAsync(RunMode.Auto);

            case "UpdateAll":
                return await StartBackgroundRunAsync(RunMode.All);

            case "CheckAll":
                return await CheckAllAsync();

            case "RefreshView":
                SyncFromStore();
                Say("");   // the "run started" note is out of date by now
                RefreshView();
                return this;

            case "RestartNow":
                return RestartNow();

            case "DismissRestart":
                StateStore.ClearPendingRestart();
                RefreshView();
                return this;

            case "Add":
                return await ShowRepoDialogAsync(null);

            case "Edit":
                return await EditRepoAsync(itemId);

            case "Remove":
                return RemoveRepo(itemId);

            case "RestoreBackup":
                return await RestoreRepoAsync(itemId);

            case "AddRegistry":
                return new RegistryConfigDialogView(_pluginInfo.Id);

            case "EditRegistry":
                return EditRegistry(itemId);

            case "RemoveRegistry":
                return RemoveRegistry(itemId);

            case "ToggleAuto":
                return ToggleAutoUpdate(itemId);

            case "ToggleRegistry":
                return ToggleRegistry(itemId);
        }

        return await base.RunCommand(itemId, commandId, data);
    }

    // ---- Settings ---------------------------------------------------------------------------------------------

    private IPluginUIView SaveSettings()
    {
        // An empty token field means the user cleared it
        if (string.IsNullOrEmpty(PluginUiOptions.GitHubToken))
            PluginUiOptions.EncryptedGitHubToken = null;

        SaveOptions();
        PluginUiOptions.TokenStatus = new GenericItemList();
        Say("Settings saved.");
        RefreshView();
        return this;
    }

    private async Task<IPluginUIView> TestTokenAsync()
    {
        try
        {
            using (var client = _clientFactory(PluginUiOptions.GitHubToken))
            {
                var result = await client.TestConnectionAsync();
                ShowTokenResult(result.Ok, result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Token test failed", ex);
            ShowTokenResult(false, $"The test could not run: {ex.Message}");
        }

        return this;
    }

    // The message can be long, and a caption would cut it off, so it goes in a list item
    private void ShowTokenResult(bool ok, string message)
    {
        PluginUiOptions.TokenStatus = new GenericItemList
                                      {
                                          new GenericListItem
                                          {
                                              PrimaryText = StatusLists.Encode(message),
                                              Status      = ok ? ItemStatus.Succeeded : ItemStatus.Failed,
                                              Icon        = ok ? IconNames.check_circle_outline : IconNames.error_outline,
                                              IconMode    = ItemListIconMode.LargeRegular
                                          }
                                      };
    }

    // ---- Updating ---------------------------------------------------------------------------------------------

    private async Task<IPluginUIView> UpdateOneAsync(string repoId)
    {
        var repo = PluginUiOptions.Repos.Find(x => x.Id == repoId);
        if (repo == null)
        {
            Say("Could not find that plugin. Refresh the page and try again.");
            return this;
        }

        try
        {
            SyncFromStore();
            SaveOptions();

            var run    = await UpdateService.RunAsync(RepoUpdateService.TriggerManual, RunMode.All, repoId);
            var result = run.Results.FirstOrDefault();

            SyncFromStore();

            if (result == null)
                Say($"Nothing happened for {repo.Repository}.");
            else if (result.Outcome == RepoOutcome.Updated)
                Say($"{repo.Repository} updated to {result.ToVersion}. Restart Emby to start using it.");
            else if (result.Outcome == RepoOutcome.Failed)
                Say($"{repo.Repository} was not updated. The reason is on its card below.");
            else
                Say($"{repo.Repository} is already up to date ({result.ToVersion}).");
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"Error updating {repo.Repository}", ex);
            Say($"{repo.Repository} was not updated: {Brief(ex.Message)}");
        }

        RefreshView();
        return this;
    }

    // Runs in the background so the page doesn't hang while many plugins download. The summary card shows progress.
    private async Task<IPluginUIView> StartBackgroundRunAsync(RunMode mode)
    {
        if (RepoUpdateService.GetProgress().IsRunning)
        {
            Say("An update run is already in progress. Click Refresh to follow it.");
            RefreshView();
            return this;
        }

        SyncFromStore();
        SaveOptions();

        var service = UpdateService;
        _ = Task.Run(async () =>
                     {
                         try
                         {
                             await service.RunAsync(RepoUpdateService.TriggerManual, mode).ConfigureAwait(false);
                         }
                         catch (InvalidOperationException)
                         {
                             // another run won the race; nothing to do
                         }
                         catch (Exception ex)
                         {
                             _logger.ErrorException("Background update run failed", ex);
                         }
                     });

        // Give the run a moment to register so the card shows it as started
        await Task.Delay(400);

        Say(mode == RunMode.All
                ? "Updating every plugin. The Last run card shows progress; click Refresh to update it."
                : "Update run started. The Last run card shows progress; click Refresh to update it.");
        RefreshView();
        return this;
    }

    private async Task<IPluginUIView> CheckAllAsync()
    {
        try
        {
            SyncFromStore();
            SaveOptions();

            await UpdateService.RunAsync(RepoUpdateService.TriggerManual, RunMode.CheckOnly);
            SyncFromStore();

            var waiting = PluginUiOptions.Repos.Count(r => r.GetStatus() == RepoStatus.UpdateAvailable ||
                                                           r.GetStatus() == RepoStatus.UpdateWaiting);
            var failed  = PluginUiOptions.Repos.Count(r => r.GetStatus() == RepoStatus.Failed);

            Say($"Checked {PluginUiOptions.Repos.Count} plugin(s): {waiting} with an update available, {failed} with a problem.");
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error while checking for updates", ex);
            Say($"The check failed: {Brief(ex.Message)}");
        }

        RefreshView();
        return this;
    }

    private IPluginUIView RestartNow()
    {
        if (!_appHost.CanSelfRestart)
        {
            Say("Emby can't restart itself on this system. Restart it manually to finish updating.");
            return this;
        }

        Say("Restarting Emby…");
        _appHost.Restart();
        return this;
    }

    // ---- Repository list --------------------------------------------------------------------------------------

    private async Task<IPluginUIView> ShowRepoDialogAsync(ReposToProcess item)
    {
        // Plugins already in the list are left out of the registry picker
        List<PluginRegistryEntry> availablePlugins = null;
        try
        {
            using (var gitHubClient = _clientFactory(PluginUiOptions.GitHubToken))
            {
                var allPlugins = await gitHubClient.GetAllRegistryPluginsAsync(PluginUiOptions.PluginRegistries);

                var others = (PluginUiOptions.Repos ?? new List<ReposToProcess>())
                             .Where(r => item == null || r.Id != item.Id)
                             .ToList();
                var existingKeys  = others.Select(r => r.RepoKey).ToHashSet();
                var existingNames = others.Select(r => r.Repository).ToHashSet(StringComparer.OrdinalIgnoreCase);

                // A plugin is also hidden when the list already has one with the same repository name from another
                // owner (a fork or the upstream copy). Two entries would install the same file and undo each other;
                // to switch to a different owner, edit the existing entry's address instead.
                availablePlugins = allPlugins.Where(p =>
                {
                    var key = GitHubApiClient.RegistryKey(p.Url);
                    var name = key.Substring(key.IndexOf('/') + 1);
                    return !existingKeys.Contains(key) && !existingNames.Contains(name);
                }).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to fetch registry plugins: {ex.Message}");
        }

        return new RepoConfigDialogView(_pluginInfo.Id, item, availablePlugins);
    }

    private async Task<IPluginUIView> EditRepoAsync(string repoId)
    {
        SyncFromStore();
        var item = RepoById(repoId);
        if (item == null)
        {
            Say("Could not find that plugin. Refresh the page and try again.");
            return this;
        }

        return await ShowRepoDialogAsync(item);
    }

    private ReposToProcess RepoById(string repoId)
    {
        return string.IsNullOrEmpty(repoId) ? null : PluginUiOptions.Repos.Find(x => x.Id == repoId);
    }

    private IPluginUIView ToggleAutoUpdate(string repoId)
    {
        SyncFromStore();
        var repo = RepoById(repoId);
        if (repo == null)
        {
            Say("Could not find that plugin. Refresh the page and try again.");
            return this;
        }

        repo.AutoUpdate = !repo.AutoUpdate;
        SaveOptions();
        Say($"Auto-update is now {(repo.AutoUpdate ? "on" : "off")} for {repo.Repository}.");
        RefreshView();
        return this;
    }

    private IPluginUIView RemoveRepo(string repoId)
    {
        try
        {
            SyncFromStore();
            var item = RepoById(repoId);
            if (item == null)
            {
                Say("Could not find that plugin. Refresh the page and try again.");
                return this;
            }

            var fileDeleted = false;
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

            PluginUiOptions.Repos.Remove(item);

            // Only trigger restart if we actually deleted a plugin file
            if (fileDeleted)
            {
                _appHost.NotifyPendingRestart();
                PluginUiOptions.RepoLogs = new CaptionItem($"Removed {item.Repository} and deleted its file. Restart Emby to finish.") { IsVisible = true };
            }
            else
            {
                PluginUiOptions.RepoLogs = new CaptionItem($"Removed {item.Repository} from the list.") { IsVisible = true };
            }
        }
        catch (Exception exception)
        {
            _logger.ErrorException("Error during repository removal", exception);
            PluginUiOptions.RepoLogs = new CaptionItem($"Could not remove the plugin: {Brief(exception.Message)}") { IsVisible = true };
        }

        if (PluginUiOptions.Repos == null)
            PluginUiOptions.Repos = new List<ReposToProcess>();

        SaveOptions();
        RefreshView();
        return this;
    }

    private async Task<IPluginUIView> RestoreRepoAsync(string repoId)
    {
        // Start from what the latest run recorded, since this edits version fields
        SyncFromStore();

        var repo = RepoById(repoId);
        if (repo == null)
        {
            Say("Could not find that plugin. Refresh the page and try again.");
            return this;
        }

        if (string.IsNullOrEmpty(repo.FileName))
        {
            PluginUiOptions.RepoLogs = new CaptionItem($"Nothing to restore: no installed file is recorded for {repo.Repository}.") { IsVisible = true };
            return this;
        }

        try
        {
            var backups = new PluginBackupService(_appHost, _logger);
            var backup  = await backups.RestoreBackupAsync(repo.FileName);

            if (backup == null)
            {
                PluginUiOptions.RepoLogs = new CaptionItem($"No backup of {repo.Repository} yet. One is made whenever it is updated.") { IsVisible = true };
                return this;
            }

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
            if (state.Load().PendingRestart.Count > 0)
                _appHost.NotifyPendingRestart();

            SaveOptions();
            // The banner above says whether a restart is needed; this line only needs to say what happened
            var which = backup.Version == "unknown" ? "the previous version of" : backup.Version + " of";
            PluginUiOptions.RepoLogs = new CaptionItem($"Restored {which} {repo.Repository}. Auto-update is now off for it.") { IsVisible = true };
        }
        catch (Exception ex)
        {
            _logger.ErrorException($"Restore failed for {repo.Repository}", ex);
            PluginUiOptions.RepoLogs = new CaptionItem($"Could not restore {repo.Repository}: {Brief(ex.Message)}") { IsVisible = true };
        }

        RefreshView();
        return this;
    }

    // ---- Registries -------------------------------------------------------------------------------------------

    private IPluginUIView ToggleRegistry(string registryId)
    {
        var registry = PluginUiOptions.PluginRegistries.Find(x => x.Id == registryId);
        if (registry == null)
        {
            Say("Could not find that registry. Refresh the page and try again.");
            return this;
        }

        registry.Enabled = !registry.Enabled;
        SaveOptions();
        Say($"{registry.Name} is now {(registry.Enabled ? "on" : "off")}.");
        RefreshView();
        return this;
    }

    private IPluginUIView EditRegistry(string registryId)
    {
        try
        {
            var registry = PluginUiOptions.PluginRegistries.Find(x => x.Id == registryId);
            if (registry != null && registry.Id != "embedded-default")
                return new RegistryConfigDialogView(_pluginInfo.Id, registry);

            PluginUiOptions.RegistryLogs = new CaptionItem(registry?.Id == "embedded-default"
                                                               ? "The built-in registry can't be edited."
                                                               : "Could not find the selected registry.") { IsVisible = true };
        }
        catch (Exception ex)
        {
            _logger.Error($"Error editing registry: {ex.Message}");
            PluginUiOptions.RegistryLogs = new CaptionItem($"Could not edit the registry: {Brief(ex.Message)}") { IsVisible = true };
        }

        return this;
    }

    private IPluginUIView RemoveRegistry(string registryId)
    {
        try
        {
            var registry = PluginUiOptions.PluginRegistries.Find(x => x.Id == registryId);
            if (registry != null && registry.Id != "embedded-default")
            {
                PluginUiOptions.PluginRegistries.Remove(registry);
                PluginUiOptions.RegistryLogs = new CaptionItem($"Removed registry: {registry.Name}") { IsVisible = true };
                SaveOptions();
                RefreshView();
            }
            else
            {
                PluginUiOptions.RegistryLogs = new CaptionItem(registry?.Id == "embedded-default"
                                                                   ? "The built-in registry can't be removed."
                                                                   : "Could not find the selected registry.") { IsVisible = true };
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Error removing registry: {ex.Message}");
            PluginUiOptions.RegistryLogs = new CaptionItem($"Could not remove the registry: {Brief(ex.Message)}") { IsVisible = true };
        }

        return this;
    }

    // ---- Dialog results ---------------------------------------------------------------------------------------

    public override void OnDialogResult(IPluginUIView dialogView, bool completedOk, object data)
    {
        try
        {
            _logger.Info($"{completedOk} -- {dialogView.GetType()}");
            if (dialogView is RepoConfigDialogView dialog && completedOk)
                ApplyRepoDialog(dialog);

            if (dialogView is RegistryConfigDialogView registryDialog && completedOk)
            {
                var newRegistry = new PluginRegistry
                {
                    Id = registryDialog.RegistryConfigUi.Id ?? Guid.NewGuid().ToString(),
                    Name = registryDialog.RegistryConfigUi.Name,
                    RawUrl = registryDialog.RegistryConfigUi.RawUrl,
                    Enabled = registryDialog.RegistryConfigUi.Enabled
                };

                if (PluginUiOptions.PluginRegistries == null)
                    PluginUiOptions.PluginRegistries = new List<PluginRegistry>();

                // Check if editing existing
                var existing = PluginUiOptions.PluginRegistries.FirstOrDefault(r => r.Id == newRegistry.Id);
                if (existing != null)
                {
                    existing.Name = newRegistry.Name;
                    existing.RawUrl = newRegistry.RawUrl;
                    existing.Enabled = newRegistry.Enabled;
                }
                else
                {
                    PluginUiOptions.PluginRegistries.Add(newRegistry);
                }

                SaveOptions();
                RefreshView();
                RaiseUIViewInfoChanged();
            }

            base.OnDialogResult(dialogView, completedOk, data);
        }
        catch (Exception e)
        {
            _logger.ErrorException(e.Message, e);
            PluginUiOptions.RepoLogs = new CaptionItem($"Could not save the change: {Brief(e.Message)}") { IsVisible = true };
        }
    }

    private void ApplyRepoDialog(RepoConfigDialogView dialog)
    {
        var ui = dialog.RepoConfigUi;

        // Accepts owner/repo, ssh addresses, .git suffixes and /releases links, and stores one canonical form
        var validation = GitHubUrlValidator.ValidateUrl(ui.Url);
        if (!validation.IsValid)
        {
            PluginUiOptions.RepoLogs = new CaptionItem("Not saved. Use an address like owner/repository or https://github.com/owner/repository.") { IsVisible = true };
            return;
        }

        var url = validation.CorrectedUrl ?? ui.Url;
        var key = GitHubApiClient.RegistryKey(url);

        bool IsDuplicate(string currentId) =>
            PluginUiOptions.Repos?.Any(x => x.RepoKey == key && x.Id != currentId) ?? false;

        ReposToProcess saved;

        if (!string.IsNullOrEmpty(ui.Id))
        {
            // Editing existing entry
            saved = PluginUiOptions.Repos.Find(x => x.Id == ui.Id);
            if (saved == null) return;

            if (IsDuplicate(saved.Id))
            {
                PluginUiOptions.RepoLogs = new CaptionItem($"{url} is already in the list.") { IsVisible = true };
                return;
            }

            saved.Url             = url;
            saved.GetPreRelease   = ui.AllowPreReleaseVersions;
            saved.AutoUpdate      = ui.AutoUpdate;
            saved.AssetNameFilter = ui.AssetNameFilter ?? "";
        }
        else
        {
            if (IsDuplicate(null))
            {
                PluginUiOptions.RepoLogs = new CaptionItem($"{url} is already in the list.") { IsVisible = true };
                return;
            }

            saved = new ReposToProcess
                    {
                        Url             = url,
                        GetPreRelease   = ui.AllowPreReleaseVersions,
                        AutoUpdate      = ui.AutoUpdate,
                        AssetNameFilter = ui.AssetNameFilter ?? ""
                    };

            if (PluginUiOptions.Repos == null)
                PluginUiOptions.Repos = new List<ReposToProcess>();
            PluginUiOptions.Repos.Add(saved);
        }

        SaveOptions();

        // Look the repository up straight away so its card shows a real status (or the reason it can't be reached)
        try
        {
            UpdateService.RunAsync(RepoUpdateService.TriggerManual, RunMode.CheckOnly, saved.Id).GetAwaiter().GetResult();
            SyncFromStore();
        }
        catch (Exception ex)
        {
            _logger.Debug($"Initial check of {url} was skipped: {ex.Message}");
        }

        PluginUiOptions.RepoLogs = new CaptionItem(validation.Message ?? $"Saved {saved.Repository}.") { IsVisible = true };
        RefreshView();
        RaiseUIViewInfoChanged();
    }

    // ---- Shared helpers ---------------------------------------------------------------------------------------

    // Emby shows a caption on one line and cuts it off, so messages that end in an error from elsewhere are shortened
    internal static string Brief(string text, int max = 50)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unexpected error";

        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length <= max) return text;

        var cut = text.LastIndexOf(' ', max);
        return text.Substring(0, cut > max / 2 ? cut : max).TrimEnd(' ', '.', ',', ':') + "…";
    }

    private void Say(string message)
    {
        PluginUiOptions.Logs = new CaptionItem(message) { IsVisible = !string.IsNullOrEmpty(message) };
    }

    // Saving clears the plain token on the options object, so put it back for the page.
    // Every save pulls in what update runs have recorded first, so an old page can't write old versions back.
    private void SaveOptions()
    {
        SyncFromStore();

        var token = PluginUiOptions.GitHubToken;
        _store.SetOptions(PluginUiOptions);
        PluginUiOptions.GitHubToken = token;

        _seenOptionsStamp = OptionsFileStamp();
    }

    // Update runs write what they learn (installed version, latest version, errors) to the options file.
    // Copy that into the page's options so saving from the page can't overwrite it with old values.
    // This only happens when the file changed since this page last read or wrote it, so a change the page
    // is in the middle of making (like restoring a version) isn't overwritten by the older file contents.
    private void SyncFromStore()
    {
        var stamp = OptionsFileStamp();
        if (stamp == _seenOptionsStamp) return;

        var fresh = _store.GetOptions();
        if (!ReferenceEquals(fresh, PluginUiOptions))
        {
            foreach (var repo in PluginUiOptions.Repos)
            {
                var match = fresh.Repos.Find(r => r.Id == repo.Id);
                if (match != null) repo.CopyRuntimeStateFrom(match);
            }
        }

        _seenOptionsStamp = stamp;
    }

    private DateTime OptionsFileStamp()
    {
        try
        {
            return File.GetLastWriteTimeUtc(_store.OptionsFilePath);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void RefreshView()
    {
        var options = PluginUiOptions;
        var state   = StateStore.Load();

        options.RestartBanner = StatusLists.BuildBanner(state);
        options.RestartFlag   = options.RestartBanner.Count > 0 ? "1" : null;
        options.RunSummary    = StatusLists.BuildSummary(state.History.FirstOrDefault(),
                                                         RepoUpdateService.GetProgress(),
                                                         !string.IsNullOrEmpty(options.GitHubToken));
        options.Releases      = StatusLists.BuildPlugins(options.Repos, RunningNote);
        options.RunHistory    = StatusLists.BuildHistory(state.History);
        options.Registries    = StatusLists.BuildRegistries(options.PluginRegistries);
    }

    // After an update the new file is on disk but the old one is still running until Emby restarts
    private string RunningNote(ReposToProcess repo)
    {
        try
        {
            if (string.IsNullOrEmpty(repo.FileName)) return null;

            var loaded = _appHost.Plugins?.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p.AssemblyFilePath), repo.FileName, StringComparison.OrdinalIgnoreCase));
            if (loaded?.Version == null) return null;

            var path = Path.Combine(_appHost.Resolve<IApplicationPaths>().PluginsPath, repo.FileName);
            var onDisk = AssemblyName.GetAssemblyName(path).Version;

            return onDisk != null && onDisk != loaded.Version
                ? $"Running {loaded.Version}, but the installed file is {onDisk}. Restart Emby to apply."
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void InitializeDefaultRegistry()
    {
        // Check if any registries exist
        if (PluginUiOptions.PluginRegistries == null)
        {
            PluginUiOptions.PluginRegistries = new List<PluginRegistry>();
        }

        if (PluginUiOptions.Repos == null)
        {
            PluginUiOptions.Repos = new List<ReposToProcess>();
        }

        var changed = false;

        // Only add default registry if no registries exist
        if (PluginUiOptions.PluginRegistries.Count == 0)
        {
            // Add embedded registry as a default registry source
            PluginUiOptions.PluginRegistries.Add(new PluginRegistry
            {
                Id = "embedded-default",
                Name = "Built-in",
                RawUrl = GitHubApiClient.EmbeddedRegistryUrl,
                Enabled = true
            });
            _logger.Info("Added default embedded registry");
            changed = true;
        }

        // Offer self-update once, including on installs that predate this feature. If the list already has a
        // plugin with this repository's name (from any owner) it is left alone, because two entries writing the
        // same DLL would undo each other. A removed entry is not added back.
        if (!PluginUiOptions.SelfEntryHandled)
        {
            var selfKey  = GitHubApiClient.RegistryKey(GitHubRepPluginInstall.SelfRepoUrl);
            var selfName = selfKey.Substring(selfKey.IndexOf('/') + 1);

            var alreadyListed = PluginUiOptions.Repos.Any(r => string.Equals(r.Repository, selfName, StringComparison.OrdinalIgnoreCase));
            if (!alreadyListed)
            {
                PluginUiOptions.Repos.Add(new ReposToProcess
                {
                    Url = GitHubRepPluginInstall.SelfRepoUrl,
                    GetPreRelease = false,
                    AutoUpdate = true
                });
                _logger.Info("Added self-update entry for GitHub Plugin Installer");
            }

            PluginUiOptions.SelfEntryHandled = true;
            changed = true;
        }

        if (changed)
            SaveOptions();
    }
}
