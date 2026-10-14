using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;

namespace Emby.GitHubRepoPluginInstall.Api;

/// <summary>
/// Alerts over Emby's own websocket, for an app that sends its token in a header (Emby only hands
/// SendMessageToAdminSessions messages to sockets that carry the token in the address). An app connects to
/// /embywebsocket and sends {"MessageType":"GitHubRepoPluginInstallEventsStart","Data":"0,30000,&lt;last Seq it has seen&gt;"};
/// from then on the plugin sends {"MessageType":"GitHubRepoPluginInstallEvents","Data":{...}} as soon as an event is logged,
/// and every 30 seconds as a heartbeat with an empty Events list. Administrators only, and only while the phone API is on.
/// The rules themselves are in <see cref="EventsSubscription"/>.
/// </summary>
public class EventsPushListener : BasePeriodicWebSocketListener<EventsPush, EventsPushState>
{
    private static EventsPushListener _instance;

    private readonly IServerApplicationHost _host;
    private readonly Timer                  _timer;

    protected override string Name => "GitHubRepoPluginInstallEvents";

    protected override bool SendOnInitialConnection => true;

    public EventsPushListener(ILogManager logManager, IServerApplicationHost host)
        : base(logManager.GetLogger(GitHubRepPluginInstall.PluginName))
    {
        _host     = host;
        _instance = this;

        // Every few seconds each connection is looked at; it only gets something when it is due a heartbeat
        _timer = new Timer(_ => NotifyChanged(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>Gives every subscribed connection its new events. Safe to call from anywhere, any time.</summary>
    public static void NotifyChanged()
    {
        try
        {
            _instance?.SendData(force: true);
        }
        catch (Exception ex)
        {
            _instance?.Logger.Debug($"Could not push events: {ex.Message}");
        }
    }

    protected override void ParseMessageParams(string[] values, EventsPushState state) =>
        EventsSubscription.ParseStartValues(values, state);

    protected override Task<EventsPush> GetDataToSend(IWebSocketConnection connection, EventsPushState state, CancellationToken cancellationToken)
    {
        // One subscription per connection: a second "Start" on the same socket must not multiply the traffic
        lock (ActiveConnections)
        {
            var first = ActiveConnections.FirstOrDefault(c => c.Item1 == connection);
            if (first != null && first.Item3 != state)
                return Task.FromResult<EventsPush>(null);
        }

        try
        {
            var logger         = Logger;
            var options        = new SecurePluginOptionsStore(_host, logger, GitHubRepPluginInstall.PluginName).GetOptions();
            var isAdministrator = connection.User?.Policy?.IsAdministrator == true;
            var store          = new EventLogStore(_host, logger, GitHubRepPluginInstall.PluginName);

            // A timer tick and a new event can arrive together; one at a time keeps "never sent twice" true
            lock (state)
                return Task.FromResult(EventsSubscription.Next(state, store, isAdministrator, options.ApiEnabled, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not prepare events for a connection: {ex.Message}");
            return Task.FromResult<EventsPush>(null);
        }
    }

    protected override void Dispose(bool dispose)
    {
        if (dispose)
        {
            _timer.Dispose();
            if (_instance == this) _instance = null;
        }
        base.Dispose(dispose);
    }
}
