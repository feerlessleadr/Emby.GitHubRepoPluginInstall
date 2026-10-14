using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.GithubAPI;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace Emby.GitHubRepoPluginInstall.Api;

/// <summary>
/// Version of the phone API. Raised only for a breaking change (a removed or renamed field, or a changed meaning).
/// New endpoints, new optional fields and new enum values keep the same number.
/// </summary>
public static class ApiVersions
{
    public const int Current      = 1;
    public const int MinSupported = 1;

    /// <summary>
    /// Level of the alert features (event log, catalog, subscription), the number every pushed event carries. Unlike
    /// ApiVersion it can go up for additions the app should know about.
    /// </summary>
    public const int Level        = 1;
}

// ---- Requests -----------------------------------------------------------------------------------------------------
// Every route needs an administrator's Emby token (or an API key). Which routes also need the switches in the plugin's
// Settings is noted on each one.

public static class Routes
{
    public const string Base = "/Plugins/GitHubRepoPluginInstall";
}

[Route(Routes.Base + "/info", "GET", Summary = "Plugin version, API version and the state of the two switches. Works even when the API is off.")]
[Authenticated(Roles = "Admin")]
public class GetInfo : IReturn<InfoResponse> { }

[Route(Routes.Base + "/Events", "GET", Summary = "Catch up on alerts: the events after a sequence number, oldest first, from a log of the last 500")]
[Authenticated(Roles = "Admin")]
public class GetEvents : IReturn<EventsResponse>
{
    /// <summary>The highest Seq the caller has already seen. Default 0.</summary>
    public long? After { get; set; }

    /// <summary>1 to 500; default 100.</summary>
    public int?  Limit { get; set; }
}

[Route(Routes.Base + "/Notifications/Catalog", "GET", Summary = "The alerts this plugin can send, for the app's notification settings")]
[Authenticated(Roles = "Admin")]
public class GetNotificationCatalog : IReturn<CatalogResponse> { }

[Route(Routes.Base + "/status", "GET", Summary = "Everything a dashboard needs in one call, including live run progress")]
[Authenticated(Roles = "Admin")]
public class GetStatus : IReturn<StatusResponse> { }

[Route(Routes.Base + "/plugins", "GET", Summary = "Every plugin in the list")]
[Authenticated(Roles = "Admin")]
public class GetPlugins : IReturn<PluginListResponse> { }

[Route(Routes.Base + "/plugins/{Id}", "GET", Summary = "One plugin, with its backups")]
[Authenticated(Roles = "Admin")]
public class GetPlugin : IReturn<PluginDto>
{
    [ApiMember(Name = "Id", IsRequired = true, ParameterType = "path")]
    public string Id { get; set; }
}

[Route(Routes.Base + "/history", "GET", Summary = "Recent update runs, newest first")]
[Authenticated(Roles = "Admin")]
public class GetHistory : IReturn<HistoryResponse>
{
    /// <summary>1 to 20; default 10.</summary>
    public int? Limit { get; set; }
}

[Route(Routes.Base + "/registries", "GET", Summary = "Plugin registries")]
[Authenticated(Roles = "Admin")]
public class GetRegistries : IReturn<RegistryListResponse> { }

[Route(Routes.Base + "/registries/plugins", "GET", Summary = "Plugins the enabled registries offer. Asks GitHub, so it can take a while.")]
[Authenticated(Roles = "Admin")]
public class GetAvailablePlugins : IReturn<AvailablePluginsResponse> { }

[Route(Routes.Base + "/settings", "GET", Summary = "Settings (the GitHub token is never returned)")]
[Authenticated(Roles = "Admin")]
public class GetSettings : IReturn<SettingsResponse> { }

[Route(Routes.Base + "/run", "POST", Summary = "Start a run in the background. Mode: Auto, All, Check or Available.")]
[Authenticated(Roles = "Admin")]
public class PostRun : IReturn<RunAcceptedResponse>
{
    /// <summary>Auto = what the daily task does. All = install everything. Check = look only. Available = install only plugins that show an update.</summary>
    public string Mode { get; set; }
}

[Route(Routes.Base + "/plugins/{Id}/update", "POST", Summary = "Install the newest release of one plugin, in the background")]
[Authenticated(Roles = "Admin")]
public class PostUpdatePlugin : IReturn<RunAcceptedResponse>
{
    public string Id { get; set; }
}

[Route(Routes.Base + "/plugins/{Id}/check", "POST", Summary = "Look for a new release of one plugin, in the background")]
[Authenticated(Roles = "Admin")]
public class PostCheckPlugin : IReturn<RunAcceptedResponse>
{
    public string Id { get; set; }
}

[Route(Routes.Base + "/plugins/{Id}/restore", "POST", Summary = "Put the newest backup back and turn auto-update off for the plugin")]
[Authenticated(Roles = "Admin")]
public class PostRestorePlugin : IReturn<ActionResponse>
{
    public string Id { get; set; }
}

[Route(Routes.Base + "/plugins/{Id}/settings", "POST", Summary = "Change a plugin's settings. Only the fields sent are changed.")]
[Authenticated(Roles = "Admin")]
public class PostPluginSettings : IReturn<ActionResponse>
{
    public string Id              { get; set; }
    public bool?  AutoUpdate      { get; set; }
    public bool?  AllowPreRelease { get; set; }
    public string AssetNameFilter { get; set; }
}

[Route(Routes.Base + "/plugins", "POST", Summary = "Add a plugin (needs the add/remove switch)")]
[Authenticated(Roles = "Admin")]
public class PostAddPlugin : IReturn<ActionResponse>
{
    public string Url             { get; set; }
    public bool   AutoUpdate      { get; set; } = true;
    public bool   AllowPreRelease { get; set; }
    public string AssetNameFilter { get; set; }
}

[Route(Routes.Base + "/plugins/{Id}", "DELETE", Summary = "Remove a plugin and delete its file (needs the add/remove switch)")]
[Authenticated(Roles = "Admin")]
public class DeletePlugin : IReturn<ActionResponse>
{
    public string Id { get; set; }
}

[Route(Routes.Base + "/restart", "POST", Summary = "Restart Emby in a moment")]
[Authenticated(Roles = "Admin")]
public class PostRestart : IReturn<ActionResponse> { }

[Route(Routes.Base + "/restart/dismiss", "POST", Summary = "Clear the 'restart needed' notice without restarting")]
[Authenticated(Roles = "Admin")]
public class PostDismissRestart : IReturn<ActionResponse> { }

[Route(Routes.Base + "/settings", "POST", Summary = "Change settings. The two API switches can only be changed on the plugin's own page.")]
[Authenticated(Roles = "Admin")]
public class PostSettings : IReturn<SettingsResponse>
{
    /// <summary>A new GitHub token. Write-only.</summary>
    public string Token                     { get; set; }
    public bool?  ClearToken                { get; set; }
    public bool?  RestartServerAfterInstall { get; set; }
}

[Route(Routes.Base + "/settings/testtoken", "POST", Summary = "Check the saved GitHub token")]
[Authenticated(Roles = "Admin")]
public class PostTestToken : IReturn<TokenTestResponse> { }

[Route(Routes.Base + "/registries", "POST", Summary = "Add a registry (needs the add/remove switch)")]
[Authenticated(Roles = "Admin")]
public class PostAddRegistry : IReturn<ActionResponse>
{
    public string Name    { get; set; }
    public string RawUrl  { get; set; }
    public bool   Enabled { get; set; } = true;
}

[Route(Routes.Base + "/registries/{Id}", "POST", Summary = "Change a registry (needs the add/remove switch)")]
[Authenticated(Roles = "Admin")]
public class PostEditRegistry : IReturn<ActionResponse>
{
    public string Id      { get; set; }
    public string Name    { get; set; }
    public string RawUrl  { get; set; }
    public bool   Enabled { get; set; } = true;
}

[Route(Routes.Base + "/registries/{Id}", "DELETE", Summary = "Remove a registry (needs the add/remove switch)")]
[Authenticated(Roles = "Admin")]
public class DeleteRegistry : IReturn<ActionResponse>
{
    public string Id { get; set; }
}

[Route(Routes.Base + "/registries/{Id}/enabled", "POST", Summary = "Switch a registry on or off")]
[Authenticated(Roles = "Admin")]
public class PostRegistryEnabled : IReturn<ActionResponse>
{
    public string Id      { get; set; }
    public bool   Enabled { get; set; }
}

// ---- The service --------------------------------------------------------------------------------------------------

/// <summary>Served inside Emby's own web server, so Emby's token authentication and administrator check apply first.</summary>
public class PluginApiService : IService, IRequiresRequest
{
    private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9-]{1,64}$", RegexOptions.Compiled);

    /// <summary>Set by Emby for each request.</summary>
    public IRequest Request { get; set; }

    private readonly IServerApplicationHost _host;
    private readonly ILogger                _logger;

    public PluginApiService(IServerApplicationHost host, ILogManager logManager)
    {
        _host   = host;
        _logger = logManager.GetLogger(GitHubRepPluginInstall.PluginName);
    }

    private SecurePluginOptionsStore NewStore() => new SecurePluginOptionsStore(_host, _logger, GitHubRepPluginInstall.PluginName);
    private RuntimeStateStore        NewState() => new RuntimeStateStore(_host, _logger, GitHubRepPluginInstall.PluginName);

    /// <summary>The service for a change: reads the saved settings, applies the change, writes them back.</summary>
    private PluginManagementService Manage(PluginUIOptions options, SecurePluginOptionsStore store) =>
        new PluginManagementService(_host, _logger, () => options, () => store.SetOptions(options));

    // The switches are read fresh each time, so turning one off on the plugin page takes effect on the next request
    private ApiError Gate(PluginUIOptions options, bool needsManage = false, string id = null)
    {
        if (!options.ApiEnabled) return ApiError.Disabled();
        if (needsManage && !options.ApiAllowManage) return ApiError.ManageDisabled();
        if (id != null && !IdPattern.IsMatch(id)) return ApiError.Invalid("The id may only contain letters, digits and dashes.");
        return null;
    }

    private static ApiError Busy() => RepoUpdateService.GetProgress().IsRunning ? ApiError.RunInProgress() : null;

    private static ApiError TooLong(string name, string value, int max) =>
        value != null && value.Length > max ? ApiError.Invalid($"{name} is longer than {max} characters.") : null;

    private void Audit(string message)
    {
        try
        {
            _host.Resolve<IActivityManager>().Create(new ActivityLogEntry
                                                     {
                                                         Name     = message + " (from the phone app)",
                                                         Type     = "GithubRepoPluginApi",
                                                         Date     = DateTimeOffset.Now,
                                                         Severity = LogSeverity.Info
                                                     });
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not write activity log entry: {ex.Message}");
        }
    }

    private static ApiError FromFailure(ManageResult r)
    {
        switch (r.Outcome)
        {
            case ManageOutcome.NotFound:  return ApiError.NotFound(r.Message);
            case ManageOutcome.Invalid:   return ApiError.Invalid(r.Message);
            case ManageOutcome.Duplicate: return ApiError.Conflict("AlreadyAdded", r.Message);
            case ManageOutcome.Refused:   return ApiError.Conflict("Refused", r.Message);
            default:                      return new ApiError(500, "Failed", r.Message);
        }
    }

    private ActionResponse Done(ManageResult r, ISet<string> pending = null) => new ActionResponse
    {
        Message       = r.Message,
        RestartNeeded = r.RestartNeeded,
        Plugin        = r.Repo == null ? null : PluginDto.From(r.Repo, pending ?? PendingFiles())
    };

    private ISet<string> PendingFiles() =>
        new HashSet<string>(NewState().Load().PendingRestart.Select(p => p.FileName).Where(f => !string.IsNullOrEmpty(f)),
                            StringComparer.OrdinalIgnoreCase);

    // ---- Entry points -----------------------------------------------------------------------------------------
    // Each one runs the handler below it and turns an ApiResult into a status code plus JSON body.

    public object Get(GetInfo request) => Reply(DoGetGetInfo(request));
    public object Get(GetStatus request) => Reply(DoGetGetStatus(request));
    public object Get(GetEvents request) => Reply(DoGetGetEvents(request));
    public object Get(GetNotificationCatalog request) => Reply(DoGetGetNotificationCatalog(request));
    public object Get(GetPlugins request) => Reply(DoGetGetPlugins(request));
    public async Task<object> Get(GetPlugin request) => Reply(await DoGetGetPlugin(request));
    public object Get(GetHistory request) => Reply(DoGetGetHistory(request));
    public object Get(GetRegistries request) => Reply(DoGetGetRegistries(request));
    public async Task<object> Get(GetAvailablePlugins request) => Reply(await DoGetGetAvailablePlugins(request));
    public object Get(GetSettings request) => Reply(DoGetGetSettings(request));
    public object Post(PostRun request) => Reply(DoPostPostRun(request));
    public object Post(PostUpdatePlugin request) => Reply(DoPostPostUpdatePlugin(request));
    public object Post(PostCheckPlugin request) => Reply(DoPostPostCheckPlugin(request));
    public async Task<object> Post(PostRestorePlugin request) => Reply(await DoPostPostRestorePlugin(request));
    public object Post(PostPluginSettings request) => Reply(DoPostPostPluginSettings(request));
    public async Task<object> Post(PostAddPlugin request) => Reply(await DoPostPostAddPlugin(request));
    public object Delete(DeletePlugin request) => Reply(DoDeleteDeletePlugin(request));
    public object Post(PostRestart request) => Reply(DoPostPostRestart(request));
    public object Post(PostDismissRestart request) => Reply(DoPostPostDismissRestart(request));
    public object Post(PostSettings request) => Reply(DoPostPostSettings(request));
    public async Task<object> Post(PostTestToken request) => Reply(await DoPostPostTestToken(request));
    public object Post(PostAddRegistry request) => Reply(DoPostPostAddRegistry(request));
    public object Post(PostEditRegistry request) => Reply(DoPostPostEditRegistry(request));
    public object Delete(DeleteRegistry request) => Reply(DoDeleteDeleteRegistry(request));
    public object Post(PostRegistryEnabled request) => Reply(DoPostPostRegistryEnabled(request));

    private object Reply(object result)
    {
        if (!(result is ApiResult api))
            return result;

        var response = Request.Response;
        response.StatusCode = api.Status;

        // Emby leaves the body out of an error reply, so an error's body is written by hand
        if (api.Status >= 400)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(_host.Resolve<IJsonSerializer>().SerializeToString(api.Body));
            response.ContentType = "application/json; charset=utf-8";
            response.SetContentLength(bytes.Length);
            response.OutputWriter.WriteAsync(new ReadOnlyMemory<byte>(bytes)).AsTask().GetAwaiter().GetResult();
            return new byte[0];
        }

        return api.Body;
    }
    // ---- Read -----------------------------------------------------------------------------------------------

    private object DoGetGetInfo(GetInfo request)
    {
        var options = NewStore().GetOptions();
        var state   = NewState().Load();

        return new InfoResponse
               {
                   PluginVersion          = typeof(PluginApiService).Assembly.GetName().Version?.ToString(),
                   ApiVersion             = ApiVersions.Current,
                   MinSupportedApiVersion = ApiVersions.MinSupported,
                   ApiLevel               = ApiVersions.Level,
                   Features               = new[] { "events", "catalog" },
                   ApiEnabled             = options.ApiEnabled,
                   AllowManage            = options.ApiEnabled && options.ApiAllowManage,
                   RunInProgress          = RepoUpdateService.GetProgress().IsRunning,
                   RestartPending         = state.PendingRestart.Count > 0
               };
    }

    private object DoGetGetEvents(GetEvents request)
    {
        var blocked = Gate(NewStore().GetOptions());
        if (blocked != null) return blocked;

        return new EventLogStore(_host, _logger, GitHubRepPluginInstall.PluginName)
            .Read(Math.Max(0, request.After ?? 0), request.Limit ?? EventLogStore.DefaultLimit);
    }

    private object DoGetGetNotificationCatalog(GetNotificationCatalog request)
    {
        var blocked = Gate(NewStore().GetOptions());
        if (blocked != null) return blocked;

        return NotificationCatalog.Build();
    }

    private object DoGetGetStatus(GetStatus request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        var state    = NewState().Load();
        var progress = RepoUpdateService.GetProgress();
        var repos    = options.Repos ?? new List<ReposToProcess>();
        var statuses = repos.Select(r => r.GetStatus()).ToList();

        return new StatusResponse
               {
                   Run = new RunProgressDto
                         {
                             IsRunning  = progress.IsRunning,
                             Total      = progress.Total,
                             Done       = progress.Done,
                             Current    = progress.Current,
                             Trigger    = progress.Trigger,
                             StartedUtc = progress.IsRunning ? progress.StartedUtc : (DateTime?)null
                         },
                   LastRun        = state.History.FirstOrDefault() is RunRecord last ? RunDto.From(last) : null,
                   RestartPending = state.PendingRestart.Count > 0,
                   PendingRestart = state.PendingRestart.Select(p => new PendingRestartDto { Name = p.Name, FromVersion = p.FromVersion, ToVersion = p.ToVersion }).ToList(),
                   CanSelfRestart = _host.CanSelfRestart,
                   Counts = new StatusCounts
                            {
                                Total           = repos.Count,
                                NotChecked      = statuses.Count(s => s == RepoStatus.NotChecked),
                                NoRelease       = statuses.Count(s => s == RepoStatus.NoRelease),
                                UpToDate        = statuses.Count(s => s == RepoStatus.UpToDate),
                                UpdateAvailable = statuses.Count(s => s == RepoStatus.UpdateAvailable),
                                UpdateWaiting   = statuses.Count(s => s == RepoStatus.UpdateWaiting),
                                Failed          = statuses.Count(s => s == RepoStatus.Failed)
                            },
                   UpdatesReady  = statuses.Count(s => s == RepoStatus.UpdateAvailable || s == RepoStatus.UpdateWaiting),
                   ServerTimeUtc = DateTime.UtcNow
               };
    }

    private object DoGetGetPlugins(GetPlugins request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        var pending = PendingFiles();
        return new PluginListResponse { Items = options.Repos.Select(r => PluginDto.From(r, pending)).ToList() };
    }

    private async Task<object> DoGetGetPlugin(GetPlugin request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options, id: request.Id);
        if (blocked != null) return blocked;

        var repo = options.Repos.Find(r => r.Id == request.Id);
        if (repo == null) return ApiError.NotFound("That plugin is not in the list.");

        var backups = string.IsNullOrEmpty(repo.FileName)
                          ? new List<PluginBackup>()
                          : await new PluginBackupService(_host, _logger).GetBackupsAsync(repo.FileName);
        return PluginDto.From(repo, PendingFiles(), backups);
    }

    private object DoGetGetHistory(GetHistory request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        var limit = Math.Max(1, Math.Min(request.Limit ?? 10, RuntimeStateStore.MaxHistory));
        return new HistoryResponse { Items = NewState().Load().History.Take(limit).Select(RunDto.From).ToList() };
    }

    private object DoGetGetRegistries(GetRegistries request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        return new RegistryListResponse
               {
                   Items = options.PluginRegistries
                                  .Select(r => new RegistryDto
                                               {
                                                   Id = r.Id, Name = r.Name, RawUrl = r.RawUrl, Enabled = r.Enabled,
                                                   BuiltIn = r.Id == PluginManagementService.BuiltInRegistryId
                                               }).ToList()
               };
    }

    private async Task<object> DoGetGetAvailablePlugins(GetAvailablePlugins request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        try
        {
            List<PluginRegistryEntry> all;
            using (var client = new GitHubApiClient(options.GitHubToken, _host.Resolve<IJsonSerializer>(), _logger))
                all = await client.GetAllRegistryPluginsAsync(options.PluginRegistries);

            var keys  = options.Repos.Select(r => r.RepoKey).ToHashSet();
            var names = options.Repos.Select(r => r.Repository).ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new AvailablePluginsResponse
                   {
                       Items = all.Select(p =>
                       {
                           var key  = GitHubApiClient.RegistryKey(p.Url);
                           var name = key.Substring(key.IndexOf('/') + 1);
                           return new AvailablePluginDto
                                  {
                                      Name = p.Name, Description = p.Description, Url = p.Url, RegistrySource = p.RegistrySource,
                                      AlreadyAdded = keys.Contains(key) || names.Contains(name)
                                  };
                       }).ToList()
                   };
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Could not read the registries", ex);
            return new ApiError(502, "RegistryUnavailable", UI.MainPageView.Brief(ex.Message, 200));
        }
    }

    private object DoGetGetSettings(GetSettings request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        return SettingsOf(options);
    }

    private static SettingsResponse SettingsOf(PluginUIOptions o) => new SettingsResponse
    {
        TokenSet                  = !string.IsNullOrEmpty(o.EncryptedGitHubToken) || !string.IsNullOrEmpty(o.GitHubToken),
        RestartServerAfterInstall = o.RestartServerAfterInstall,
        ApiEnabled                = o.ApiEnabled,
        AllowManage               = o.ApiEnabled && o.ApiAllowManage
    };

    // ---- Runs -----------------------------------------------------------------------------------------------

    private object DoPostPostRun(PostRun request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options) ?? Busy();
        if (blocked != null) return blocked;

        RunMode mode;
        string[] ids = null;
        switch ((request.Mode ?? "").Trim().ToLowerInvariant())
        {
            case "auto":  mode = RunMode.Auto; break;
            case "all":   mode = RunMode.All; break;
            case "check": mode = RunMode.CheckOnly; break;
            case "available":
                mode = RunMode.All;
                ids  = options.Repos.Where(r => r.GetStatus() == RepoStatus.UpdateAvailable || r.GetStatus() == RepoStatus.UpdateWaiting)
                              .Select(r => r.Id).ToArray();
                if (ids.Length == 0)
                    return ApiError.Conflict("NothingToUpdate", "No plugins have an update right now. Run a check first.");
                break;
            default:
                return ApiError.Invalid("Mode must be Auto, All, Check or Available.");
        }

        return StartRun(mode, ids, ids?.Length ?? options.Repos.Count);
    }

    private object DoPostPostUpdatePlugin(PostUpdatePlugin request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options, id: request.Id) ?? Busy();
        if (blocked != null) return blocked;
        if (options.Repos.All(r => r.Id != request.Id)) return ApiError.NotFound("That plugin is not in the list.");

        return StartRun(RunMode.All, new[] { request.Id }, 1);
    }

    private object DoPostPostCheckPlugin(PostCheckPlugin request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options, id: request.Id) ?? Busy();
        if (blocked != null) return blocked;
        if (options.Repos.All(r => r.Id != request.Id)) return ApiError.NotFound("That plugin is not in the list.");

        return StartRun(RunMode.CheckOnly, new[] { request.Id }, 1);
    }

    private object StartRun(RunMode mode, string[] ids, int total)
    {
        var service = new RepoUpdateService(_host, _logger);
        _ = Task.Run(async () =>
                     {
                         try
                         {
                             if (ids != null)
                                 await service.RunForReposAsync(RepoUpdateService.TriggerManual, mode, ids).ConfigureAwait(false);
                             else
                                 await service.RunAsync(RepoUpdateService.TriggerManual, mode).ConfigureAwait(false);
                         }
                         catch (InvalidOperationException)
                         {
                             // another run won the race; it reports its own progress
                         }
                         catch (Exception ex)
                         {
                             _logger.ErrorException("Background update run failed", ex);
                         }
                     });

        Audit(mode == RunMode.CheckOnly ? "Checked for plugin updates" : $"Started a plugin update run ({mode})");

        return ApiResult.Accepted(new RunAcceptedResponse
                                  {
                                      Message = "Started. Follow it with GET /status, or listen for the pushed events.",
                                      Total   = total
                                  });
    }

    // ---- Changes to one plugin ------------------------------------------------------------------------------

    private async Task<object> DoPostPostRestorePlugin(PostRestorePlugin request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options, id: request.Id) ?? Busy();
        if (blocked != null) return blocked;

        var result = await Manage(options, store).RestoreAsync(request.Id);
        if (!result.Ok) return FromFailure(result);

        Audit(result.Message);
        return Done(result);
    }

    private object DoPostPostPluginSettings(PostPluginSettings request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options, id: request.Id) ?? Busy() ?? TooLong("AssetNameFilter", request.AssetNameFilter, 100);
        if (blocked != null) return blocked;

        var result = Manage(options, store).UpdateSettings(request.Id, request.AutoUpdate, request.AllowPreRelease, request.AssetNameFilter);
        if (!result.Ok) return FromFailure(result);

        Audit(result.Message);
        return Done(result);
    }

    private async Task<object> DoPostPostAddPlugin(PostAddPlugin request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options, needsManage: true) ?? Busy()
                      ?? TooLong("Url", request.Url, 300) ?? TooLong("AssetNameFilter", request.AssetNameFilter, 100);
        if (blocked != null) return blocked;
        if (string.IsNullOrWhiteSpace(request.Url)) return ApiError.Invalid("Url is required.");

        var result = await Manage(options, store).ApplyRepoAsync(new RepoSettings
                                                                 {
                                                                     Url             = request.Url,
                                                                     AutoUpdate      = request.AutoUpdate,
                                                                     AllowPreRelease = request.AllowPreRelease,
                                                                     AssetNameFilter = request.AssetNameFilter
                                                                 });
        if (!result.Ok) return FromFailure(result);

        Audit($"Added plugin {result.Repo?.Repository}");

        // The first check ran after the add, so show the plugin as the saved file now has it
        var fresh = NewStore().GetOptions().Repos.Find(r => r.Id == result.Repo?.Id) ?? result.Repo;
        return new ActionResponse { Message = result.Message, Plugin = PluginDto.From(fresh, PendingFiles()) };
    }

    private object DoDeleteDeletePlugin(DeletePlugin request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options, needsManage: true, id: request.Id) ?? Busy();
        if (blocked != null) return blocked;

        var result = Manage(options, store).RemoveRepo(request.Id);
        if (!result.Ok) return FromFailure(result);

        Audit(result.Message);
        return new ActionResponse { Message = result.Message, RestartNeeded = result.RestartNeeded };
    }

    // ---- Restart --------------------------------------------------------------------------------------------

    private object DoPostPostRestart(PostRestart request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        if (!_host.CanSelfRestart)
            return ApiError.Conflict("CannotRestart", "Emby can't restart itself on this system. Restart it manually.");

        Audit("Restarted Emby");

        // Answer first, then restart, so the app gets its reply
        _ = Task.Run(async () =>
                     {
                         await Task.Delay(1500).ConfigureAwait(false);
                         _host.Restart();
                     });

        return new ActionResponse { Message = "Emby is restarting." };
    }

    private object DoPostPostDismissRestart(PostDismissRestart request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        NewState().ClearPendingRestart();
        RunEvents.Raise(PushEvents.PluginsChanged, new ChangeEventData { Reason = "RestartDismissed" });
        return new ActionResponse { Message = "The restart notice was cleared." };
    }

    // ---- Settings -------------------------------------------------------------------------------------------

    private object DoPostPostSettings(PostSettings request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options) ?? TooLong("Token", request.Token, 255);
        if (blocked != null) return blocked;

        if (request.ClearToken == true)
        {
            options.GitHubToken          = null;
            options.EncryptedGitHubToken = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.Token))
        {
            options.GitHubToken = request.Token.Trim();
        }

        if (request.RestartServerAfterInstall.HasValue)
            options.RestartServerAfterInstall = request.RestartServerAfterInstall.Value;

        store.SetOptions(options);
        Audit("Changed the plugin installer's settings");

        return SettingsOf(store.GetOptions());
    }

    private async Task<object> DoPostPostTestToken(PostTestToken request)
    {
        var options = NewStore().GetOptions();
        var blocked = Gate(options);
        if (blocked != null) return blocked;

        try
        {
            using (var client = new GitHubApiClient(options.GitHubToken, _host.Resolve<IJsonSerializer>(), _logger))
            {
                var result = await client.TestConnectionAsync();
                return new TokenTestResponse { Ok = result.Ok, Message = result.Message };
            }
        }
        catch (Exception ex)
        {
            return new TokenTestResponse { Ok = false, Message = "The test could not run: " + UI.MainPageView.Brief(ex.Message, 200) };
        }
    }

    // ---- Registries -----------------------------------------------------------------------------------------

    private object DoPostPostAddRegistry(PostAddRegistry request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options, needsManage: true) ?? TooLong("Name", request.Name, 100) ?? TooLong("RawUrl", request.RawUrl, 300);
        if (blocked != null) return blocked;
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RawUrl))
            return ApiError.Invalid("Name and RawUrl are required.");
        if (!Uri.TryCreate(request.RawUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return ApiError.Invalid("RawUrl must be an https address.");

        var result = Manage(options, store).UpsertRegistry(new RegistrySettings { Name = request.Name.Trim(), RawUrl = request.RawUrl.Trim(), Enabled = request.Enabled });
        if (!result.Ok) return FromFailure(result);

        Audit(result.Message);
        return Done(result);
    }

    private object DoPostPostEditRegistry(PostEditRegistry request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options, needsManage: true, id: request.Id) ?? TooLong("Name", request.Name, 100) ?? TooLong("RawUrl", request.RawUrl, 300);
        if (blocked != null) return blocked;
        if (options.PluginRegistries.All(r => r.Id != request.Id)) return ApiError.NotFound("That registry is not in the list.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RawUrl))
            return ApiError.Invalid("Name and RawUrl are required.");
        if (!Uri.TryCreate(request.RawUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return ApiError.Invalid("RawUrl must be an https address.");

        var result = Manage(options, store).UpsertRegistry(new RegistrySettings { Id = request.Id, Name = request.Name.Trim(), RawUrl = request.RawUrl.Trim(), Enabled = request.Enabled });
        if (!result.Ok) return FromFailure(result);

        Audit(result.Message);
        return Done(result);
    }

    private object DoDeleteDeleteRegistry(DeleteRegistry request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options, needsManage: true, id: request.Id);
        if (blocked != null) return blocked;

        var result = Manage(options, store).RemoveRegistry(request.Id);
        if (!result.Ok) return FromFailure(result);

        Audit(result.Message);
        return Done(result);
    }

    private object DoPostPostRegistryEnabled(PostRegistryEnabled request)
    {
        var store   = NewStore();
        var options = store.GetOptions();
        var blocked = Gate(options, id: request.Id);
        if (blocked != null) return blocked;

        var result = Manage(options, store).ToggleRegistry(request.Id, request.Enabled);
        if (!result.Ok) return FromFailure(result);

        Audit(result.Message);
        return Done(result);
    }
}
