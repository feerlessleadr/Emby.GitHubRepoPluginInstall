using System;

namespace Emby.GitHubRepoPluginInstall.Services;

/// <summary>Names of the messages pushed to administrators' open Emby sessions (WebSocket MessageType).</summary>
public static class PushEvents
{
    private const string Prefix = "GitHubRepoPluginInstall.";

    public const string RunStarted     = Prefix + "RunStarted";
    public const string RunProgress    = Prefix + "RunProgress";
    public const string RunCompleted   = Prefix + "RunCompleted";
    public const string RestartPending = Prefix + "RestartPending";
    public const string PluginsChanged = Prefix + "PluginsChanged";
}

public class RunEventData
{
    public string Trigger { get; set; }
    public int    Total   { get; set; }
    public int    Done    { get; set; }
    public string Current { get; set; }

    // Set on RunCompleted only
    public int    Updated { get; set; }
    public int    UpToDate { get; set; }
    public int    Failed  { get; set; }
    public int    Waiting { get; set; }
    public string[] UpdatedPlugins { get; set; }
    public string[] FailedPlugins  { get; set; }
    public bool   CheckOnly { get; set; }
}

/// <summary>
/// A small switchboard between the update code and whatever delivers events to the phone. The update code raises an
/// event and doesn't know or care who is listening; with nobody listening, nothing happens.
/// </summary>
public static class RunEvents
{
    /// <summary>Set once at server start by the part that can reach Emby's sessions.</summary>
    public static Action<string, object> Sink { get; set; }

    public static void Raise(string name, object data)
    {
        try
        {
            Sink?.Invoke(name, data);
        }
        catch
        {
            // A problem delivering a notification must never affect an update run
        }
    }
}

/// <summary>Sent as PluginsChanged when the list or a plugin's settings or files change outside a run.</summary>
public class ChangeEventData
{
    /// <summary>Added, Removed, Restored, SettingsChanged, RegistryChanged or RestartDismissed.</summary>
    public string Reason        { get; set; }
    public string PluginId      { get; set; }
    public string PluginName    { get; set; }
    public bool   RestartNeeded { get; set; }
}
