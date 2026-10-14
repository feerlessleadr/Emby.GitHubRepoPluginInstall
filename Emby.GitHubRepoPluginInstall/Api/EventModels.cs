using System;
using System.Collections.Generic;

namespace Emby.GitHubRepoPluginInstall.Api;

// The alert side of the phone API: what the plugin logs, what the Events route returns, what the subscription pushes and
// what the notification catalog says. Field names are PascalCase and a null field is left out of the JSON.

/// <summary>
/// The Data of one logged event: the five common fields every service sends, plus the fields of the event itself.
/// Only the fields that mean something for an event are filled in. There are never file paths in here.
/// </summary>
public class EventPayload
{
    // Common to every event
    public int      ApiLevel      { get; set; }
    public long     Seq           { get; set; }
    public string   EventId       { get; set; }
    public DateTime ServerTimeUtc { get; set; }

    /// <summary>The short name, such as RunCompleted.</summary>
    public string   Event         { get; set; }

    // RunStarted, RunCompleted and RestartPending
    /// <summary>Scheduled or Manual.</summary>
    public string   Trigger       { get; set; }
    public int?     Total         { get; set; }
    public int?     Done          { get; set; }
    public bool?    CheckOnly     { get; set; }
    public int?     Updated       { get; set; }
    public int?     UpToDate      { get; set; }
    public int?     Failed        { get; set; }
    public int?     Waiting       { get; set; }
    public string[] UpdatedPlugins { get; set; }
    public string[] FailedPlugins  { get; set; }

    // PluginsChanged
    /// <summary>Added, Removed, Restored, SettingsChanged, RegistryChanged or RestartDismissed.</summary>
    public string   Reason        { get; set; }
    public string   PluginId      { get; set; }
    public string   PluginName    { get; set; }
    public bool?    RestartNeeded { get; set; }
}

/// <summary>One entry of the event log, and one item of the Events route and of a pushed message.</summary>
public class LogEvent
{
    public long         Seq   { get; set; }

    /// <summary>The full message name, such as GitHubRepoPluginInstall.RunCompleted.</summary>
    public string       Name  { get; set; }
    public DateTime     AtUtc { get; set; }
    public EventPayload Data  { get; set; }
}

/// <summary>What GET /Events returns.</summary>
public class EventsResponse
{
    public int            ApiLevel  { get; set; }
    public long           LatestSeq { get; set; }

    /// <summary>True when the log no longer reaches back to the requested point, so the app should read /status instead.</summary>
    public bool           Truncated { get; set; }

    /// <summary>Oldest first.</summary>
    public List<LogEvent> Events    { get; set; } = new List<LogEvent>();
}

/// <summary>
/// The Data of a pushed GitHubRepoPluginInstallEvents message. Normally the same as <see cref="EventsResponse"/>; a refusal
/// carries only Error.
/// </summary>
public class EventsPush
{
    public int?           ApiLevel  { get; set; }
    public long?          LatestSeq { get; set; }
    public bool?          Truncated { get; set; }

    /// <summary>Empty for a heartbeat. Left out of a refusal.</summary>
    public List<LogEvent> Events    { get; set; }
    public string         Error     { get; set; }

    /// <summary>When this message was made, so the app can see a heartbeat is fresh.</summary>
    public DateTime?      ServerTimeUtc { get; set; }
}

// ---- The notification catalog -------------------------------------------------------------------------------------

public class CatalogService
{
    public string Id           { get; set; }
    public string Title        { get; set; }
    public string ChannelId    { get; set; }
    public string ChannelTitle { get; set; }
}

public class CatalogParam
{
    public string Name    { get; set; }
    public string Label   { get; set; }

    /// <summary>int, bool, text or choice.</summary>
    public string Type    { get; set; }
    public object Default { get; set; }
}

public class CatalogRule
{
    public bool                       Enabled { get; set; }
    public Dictionary<string, object> Params  { get; set; } = new Dictionary<string, object>();
}

public class CatalogEvent
{
    public string             Id             { get; set; }
    public string             Title          { get; set; }
    public string             Description    { get; set; }
    public List<CatalogParam> Params         { get; set; } = new List<CatalogParam>();
    public CatalogRule        DefaultRule    { get; set; }
    public string             LockScreenText { get; set; }
    public string             OpensTab       { get; set; }
}

public class CatalogResponse
{
    public int                ApiLevel { get; set; }
    public CatalogService     Service  { get; set; }
    public List<CatalogEvent> Events   { get; set; } = new List<CatalogEvent>();
}
