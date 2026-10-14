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

    /// <summary>The administrator's access token from the start message. Held in memory only: never logged, echoed or saved.</summary>
    public string          Token        { get; set; }

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

    /// <summary>values[0] is the last Seq seen, values[1] the administrator's access token (both optional).</summary>
    public static void ParseStartValues(string[] values, EventsPushState state)
    {
        if (values != null && values.Length > 0 &&
            long.TryParse(values[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var after) &&
            after >= 0)
            state.AfterSeq = after;

        if (values != null && values.Length > 1 && !string.IsNullOrWhiteSpace(values[1]))
            state.Token = values[1].Trim();
    }

    private static EventsPush Refusal(string text, DateTimeOffset now) => new EventsPush { Error = text, ServerTimeUtc = now.UtcDateTime };

    private static EventsPush From(EventsResponse r, DateTimeOffset now) => new EventsPush
    {
        ApiLevel = r.ApiLevel, LatestSeq = r.LatestSeq, Truncated = r.Truncated, Events = r.Events, ServerTimeUtc = now.UtcDateTime
    };

    /// <summary>
    /// What to send this connection now, or null for nothing. Called for the first push, whenever a new event is logged,
    /// and on a timer. Updates the connection's state.
    /// </summary>
    public static EventsPush Next(EventsPushState state, EventLogStore store, bool isAdministrator, bool apiEnabled, DateTimeOffset now,
                                  Func<string, bool> tokenIsAdministrator = null)
    {
        // Emby leaves connection.User empty for a socket that authenticated with a header, so the token in the start message
        // is checked too, on every push. A token that no longer belongs to an administrator ends the access.
        if (!isAdministrator && state.Token != null && tokenIsAdministrator != null)
        {
            try { isAdministrator = tokenIsAdministrator(state.Token); }
            catch { isAdministrator = false; }
        }

        if (!isAdministrator)
        {
            // Said once; the connection then stays silent
            if (state.RefusedAdmin) return null;
            state.RefusedAdmin = true;
            return Refusal(NotAdministrator, now);
        }
        state.RefusedAdmin = false;

        if (!apiEnabled)
        {
            // The owner's switch is checked on every push, so turning it off ends the alerts at once
            if (state.RefusedOff) return null;
            state.RefusedOff = true;
            return Refusal(ApiOff, now);
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
                return new EventsPush { ApiLevel = ApiVersions.Level, LatestSeq = latest, Truncated = false, Events = new List<LogEvent>(), ServerTimeUtc = now.UtcDateTime };
            }

            var first = store.Read(state.AfterSeq.Value, EventLogStore.MaxEvents);
            state.LastSentSeq = first.LatestSeq;
            return From(first, now);
        }

        if (latest > state.LastSentSeq)
        {
            var news = store.Read(state.LastSentSeq, EventLogStore.MaxEvents);
            state.LastSentSeq = news.LatestSeq;
            state.LastSentAt  = now;
            return From(news, now);
        }

        if (now - state.LastSentAt >= HeartbeatDue)
        {
            state.LastSentAt = now;
            return new EventsPush { ApiLevel = ApiVersions.Level, LatestSeq = latest, Truncated = false, Events = new List<LogEvent>(), ServerTimeUtc = now.UtcDateTime };
        }

        return null;
    }
}
