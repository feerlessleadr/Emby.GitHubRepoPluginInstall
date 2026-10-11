using System;
using System.Linq;
using System.Reflection;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
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
}

[Route("/Plugins/GitHubRepoPluginInstall/info", "GET", Summary = "Plugin version, API version and whether the API is switched on")]
[Authenticated(Roles = "Admin")]
public class GetInfo : IReturn<InfoResponse>
{
}

public class InfoResponse
{
    public string PluginVersion          { get; set; }
    public int    ApiVersion             { get; set; }
    public int    MinSupportedApiVersion { get; set; }
    public bool   ApiEnabled             { get; set; }
    public bool   AllowManage            { get; set; }
    public bool   RunInProgress          { get; set; }
    public bool   RestartPending         { get; set; }
}

/// <summary>Served inside Emby's own web server, so Emby's token authentication and admin check apply.</summary>
public class InfoService : IService
{
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger                _logger;

    public InfoService(IServerApplicationHost appHost, ILogManager logManager)
    {
        _appHost = appHost;
        _logger  = logManager.GetLogger(GitHubRepPluginInstall.PluginName);
    }

    public object Get(GetInfo request)
    {
        var options = new SecurePluginOptionsStore(_appHost, _logger, GitHubRepPluginInstall.PluginName).GetOptions();
        var state   = new RuntimeStateStore(_appHost, _logger, GitHubRepPluginInstall.PluginName).Load();

        return new InfoResponse
               {
                   PluginVersion          = typeof(InfoService).Assembly.GetName().Version?.ToString(),
                   ApiVersion             = ApiVersions.Current,
                   MinSupportedApiVersion = ApiVersions.MinSupported,
                   ApiEnabled             = options.ApiEnabled,
                   AllowManage            = options.ApiEnabled && options.ApiAllowManage,
                   RunInProgress          = RepoUpdateService.GetProgress().IsRunning,
                   RestartPending         = state.PendingRestart.Count > 0
               };
    }
}
