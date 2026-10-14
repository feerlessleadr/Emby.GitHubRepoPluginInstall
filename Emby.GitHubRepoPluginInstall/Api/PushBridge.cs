using System;
using System.Threading;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace Emby.GitHubRepoPluginInstall.Api;

/// <summary>
/// Receives the plugin's events: logs the alerts for the phone app (event log, then its websocket subscription), and sends them to administrators' open Emby sessions, which is what an app with an Emby WebSocket
/// connection receives as {"MessageType": "...", "Data": {...}}. Emby starts this when the server starts.
/// </summary>
public class PushBridge : IServerEntryPoint
{
    private readonly ISessionManager        _sessions;
    private readonly IServerApplicationHost _host;
    private readonly ILogger                _logger;

    public PushBridge(ISessionManager sessions, IServerApplicationHost host, ILogManager logManager)
    {
        _sessions = sessions;
        _host     = host;
        _logger   = logManager.GetLogger(GitHubRepPluginInstall.PluginName);
    }

    public void Run()
    {
        RunEvents.Sink = Send;
    }

    private void Send(string name, object data)
    {
        try
        {
            // Only when the owner has switched the phone API on
            var options = new SecurePluginOptionsStore(_host, _logger, GitHubRepPluginInstall.PluginName).GetOptions();
            if (!options.ApiEnabled) return;

            // The alerts are written to the event log first, which gives them their sequence number. The app's own
            // subscription (EventsPushListener) and its catch-up route both read that log, because the app's websocket
            // is not an administrator session and never receives the session message below.
            object payload = data;
            try
            {
                var logged = EventRecorder.Record(new EventLogStore(_host, _logger, GitHubRepPluginInstall.PluginName), true, name, data);
                if (logged != null)
                {
                    payload = logged.Data;
                    EventsPushListener.NotifyChanged();
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Could not log {name}: {ex.Message}");
            }

            _sessions.SendMessageToAdminSessions(name, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Debug($"Could not push {name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        RunEvents.Sink = null;
    }
}
