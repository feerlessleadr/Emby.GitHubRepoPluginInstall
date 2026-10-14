using System;
using System.Collections.Generic;
using Emby.GitHubRepoPluginInstall.Storage;
using MediaBrowser.Controller.Net;

namespace Emby.GitHubRepoPluginInstall.Api;

/// <summary>What one subscribed connection has been sent so far.</summary>
public class EventsPushState : WebSocketListenerState
{
    /// <summary>The highest Seq the app said it has already seen, or null when it said nothing.</summary>
    public long?           AfterSeq     { get; set; }

    public bool            Started      { get; set; }

    /// <summary>The highest Seq sent on this connection. Nothing at or below it is ever sent again.</summary>
    public long            LastSentSeq  { get; set; }

    public DateTimeOffset  LastSentAt   { get; set; }

    public bool            RefusedAdmin { get; set; }
    public bool            RefusedOff   { get; set; }
}

/// <summary>
/// The rules of the events subscription, with no Emby in them so they can be tested: who may subscribe, what the first
/// push holds, when new events go out, and the heartbeat.
/// </summary>
public static class EventsSubscription
{
    public const string NotAdministrator = "Administrator login required.";
    public const string ApiOff           = "The phone API is switched off in the plugin's settings.";

    /// <summary>A quiet connection gets an empty push this often.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(30);

    /// <summary>The listener checks every few seconds, so a heartbeat is due a little before the full 30 seconds.</summary>
    public static readonly TimeSpan HeartbeatDue = TimeSpan.FromSeconds(25);

    public static void ParseStartValues(string[] values, EventsPushState state)
    {
        if (values != null && values.Length > 0 &&
            long.TryParse(values[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var after) &&
            after >= 0)
            state.AfterSeq = after;
    }

    private static EventsPush Refusal(string text) => new EventsPush { Error = text };

    private static EventsPush From(EventsResponse r) => new EventsPush
    {
        ApiLevel = r.ApiLevel, LatestSeq = r.LatestSeq, Truncated = r.Truncated, Events = r.Events
    };

    /// <summary>
    /// What to send this connection now, or null for nothing. Called for the first push, whenever a new event is logged,
    /// and on a timer. Updates the connection's state.
    /// </summary>
    public static EventsPush Next(EventsPushState state, EventLogStore store, bool isAdministrator, bool apiEnabled, DateTimeOffset now)
    {
        if (!isAdministrator)
        {
            // Said once; the connection then stays silent
            if (state.RefusedAdmin) return null;
            state.RefusedAdmin = true;
            return Refusal(NotAdministrator);
        }
        state.RefusedAdmin = false;

        if (!apiEnabled)
        {
            // The owner's switch is checked on every push, so turning it off ends the alerts at once
            if (state.RefusedOff) return null;
            state.RefusedOff = true;
            return Refusal(ApiOff);
        }
        state.RefusedOff = false;

        var latest = store.LatestSeq();

        if (!state.Started)
        {
            state.Started    = true;
            state.LastSentAt = now;

            if (state.AfterSeq == null)
            {
                // No point of reference: just say where the log is now
                state.LastSentSeq = latest;
                return new EventsPush { ApiLevel = ApiVersions.Level, LatestSeq = latest, Truncated = false, Events = new List<LogEvent>() };
            }

            var first = store.Read(state.AfterSeq.Value, EventLogStore.MaxEvents);
            state.LastSentSeq = first.LatestSeq;
            return From(first);
        }

        if (latest > state.LastSentSeq)
        {
            var news = store.Read(state.LastSentSeq, EventLogStore.MaxEvents);
            state.LastSentSeq = news.LatestSeq;
            state.LastSentAt  = now;
            return From(news);
        }

        if (now - state.LastSentAt >= HeartbeatDue)
        {
            state.LastSentAt = now;
            return new EventsPush { ApiLevel = ApiVersions.Level, LatestSeq = latest, Truncated = false, Events = new List<LogEvent>() };
        }

        return null;
    }
}
