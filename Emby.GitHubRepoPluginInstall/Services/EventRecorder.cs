using System.Linq;
using System.Text.RegularExpressions;
using Emby.GitHubRepoPluginInstall.Api;
using Emby.GitHubRepoPluginInstall.Storage;

namespace Emby.GitHubRepoPluginInstall.Services;

/// <summary>
/// Turns what the update code raises into the events the phone app can catch up on. Run progress is not logged (it is a
/// ticker, and /status has it); the other four are.
/// </summary>
public static class EventRecorder
{
    private const string Prefix = "GitHubRepoPluginInstall.";

    // A Windows drive path, a UNC share, or a long path with slashes. Event text is meant to name plugins, never places.
    private static readonly Regex PathLike = new Regex(@"([A-Za-z]:[\\/][^\s,;""']*|\\\\[^\s,;""']+|(?:/[^\s/,;""']+){2,})",
                                                       RegexOptions.Compiled);

    public static bool IsLogged(string name) =>
        name == PushEvents.RunStarted || name == PushEvents.RunCompleted ||
        name == PushEvents.RestartPending || name == PushEvents.PluginsChanged;

    /// <summary>The name without the service prefix, such as RunCompleted.</summary>
    public static string ShortName(string name) =>
        name != null && name.StartsWith(Prefix) ? name.Substring(Prefix.Length) : name;

    /// <summary>Removes anything that looks like a file path from a piece of text.</summary>
    public static string Scrub(string text) =>
        string.IsNullOrEmpty(text) ? text : PathLike.Replace(text, "(path removed)");

    private static string[] Scrub(string[] items) => items?.Select(Scrub).ToArray();

    /// <summary>
    /// Builds the event's own fields from the object that was raised, or returns null for an event that is not logged
    /// or an object of an unexpected type.
    /// </summary>
    public static EventPayload ToPayload(string name, object data)
    {
        switch (name)
        {
            case PushEvents.RunStarted when data is RunEventData r:
                return new EventPayload { Trigger = Scrub(r.Trigger), Total = r.Total, CheckOnly = r.CheckOnly };

            case PushEvents.RunCompleted when data is RunEventData r:
                return new EventPayload
                       {
                           Trigger = Scrub(r.Trigger), Total = r.Total, Done = r.Done, CheckOnly = r.CheckOnly,
                           Updated = r.Updated, UpToDate = r.UpToDate, Failed = r.Failed, Waiting = r.Waiting,
                           UpdatedPlugins = Scrub(r.UpdatedPlugins) ?? new string[0],
                           FailedPlugins  = Scrub(r.FailedPlugins) ?? new string[0]
                       };

            case PushEvents.RestartPending when data is RunEventData r:
                return new EventPayload { Trigger = Scrub(r.Trigger), Updated = r.Updated, UpdatedPlugins = Scrub(r.UpdatedPlugins) ?? new string[0] };

            case PushEvents.PluginsChanged when data is ChangeEventData c:
                return new EventPayload
                       {
                           Reason = Scrub(c.Reason), PluginId = Scrub(c.PluginId), PluginName = Scrub(c.PluginName),
                           RestartNeeded = c.RestartNeeded
                       };

            default:
                return null;
        }
    }

    /// <summary>
    /// Logs the event if it is one the phone app gets and the phone API is on. Returns the stored event (with its sequence
    /// number), or null when nothing was logged.
    /// </summary>
    public static LogEvent Record(EventLogStore store, bool apiEnabled, string name, object data)
    {
        if (!apiEnabled || !IsLogged(name)) return null;

        var payload = ToPayload(name, data);
        return payload == null ? null : store.Append(name, ShortName(name), payload);
    }
}
