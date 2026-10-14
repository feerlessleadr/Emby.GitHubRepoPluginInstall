using System.Collections.Generic;
using Emby.GitHubRepoPluginInstall.Services;

namespace Emby.GitHubRepoPluginInstall.Api;

/// <summary>
/// What GET /Notifications/Catalog returns: the two events worth a phone alert and how the app should offer them.
/// Kept deliberately small. Run progress and plain list changes are not alerts, so they are not here.
/// </summary>
public static class NotificationCatalog
{
    public const string ServiceId = "GitHubRepoPluginInstall";

    /// <summary>The name of the one rule parameter of the run-finished alert.</summary>
    public const string OnlyWhenSomethingHappened = "OnlyWhenSomethingHappened";

    public static CatalogResponse Build() => new CatalogResponse
    {
        ApiLevel = ApiVersions.Level,
        Service  = new CatalogService
                   {
                       Id           = ServiceId,
                       Title        = "GitHub Repo Plugin Install",
                       ChannelId    = "plugin-updates",
                       ChannelTitle = "Plugin updates"
                   },
        Events = new List<CatalogEvent>
                 {
                     new CatalogEvent
                     {
                         Id          = PushEvents.RunCompleted,
                         Title       = "Plugin update run finished",
                         Description = "Sent when a run that checks or updates your plugins ends. With the option on, a run where nothing was updated and nothing failed stays quiet.",
                         Params      = new List<CatalogParam>
                                       {
                                           new CatalogParam
                                           {
                                               Name = OnlyWhenSomethingHappened, Label = "Only when a plugin was updated or failed",
                                               Type = "bool", Default = true
                                           }
                                       },
                         DefaultRule    = new CatalogRule { Enabled = true, Params = new Dictionary<string, object> { [OnlyWhenSomethingHappened] = true } },
                         LockScreenText = "Plugin updates finished. Unlock to see the result.",
                         OpensTab       = ServiceId
                     },
                     new CatalogEvent
                     {
                         Id             = PushEvents.RestartPending,
                         Title          = "Emby needs a restart",
                         Description    = "Sent when a plugin was installed or changed and Emby has to restart before the change takes effect.",
                         Params         = new List<CatalogParam>(),
                         DefaultRule    = new CatalogRule { Enabled = true },
                         LockScreenText = "Emby needs a restart for a plugin update.",
                         OpensTab       = ServiceId
                     }
                 }
    };
}
