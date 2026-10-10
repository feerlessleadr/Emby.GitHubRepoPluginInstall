using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Emby.GitHubRepoPluginInstall.Models;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.GitHubRepoPluginInstall.Storage;

/// <summary>
/// Remembers run history and which plugins are waiting for a server restart.
/// Kept in its own file so the user-editable options file stays small and a bad write here can't touch settings.
/// </summary>
public class RuntimeStateStore
{
    public const int MaxHistory = 20;

    private static readonly object FileLock = new object();

    private readonly IJsonSerializer _json;
    private readonly ILogger         _logger;
    private readonly string          _path;

    public RuntimeStateStore(IApplicationHost applicationHost, ILogger logger, string pluginName)
        : this(applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath,
               applicationHost.Resolve<IJsonSerializer>(), logger, pluginName)
    {
    }

    public RuntimeStateStore(string folder, IJsonSerializer json, ILogger logger, string pluginName)
    {
        _json   = json;
        _logger = logger;
        _path   = Path.Combine(folder, pluginName + ".state.json");
    }

    public PluginRuntimeState Load()
    {
        lock (FileLock)
        {
            var state = ReadFile();
            if (ClearPendingIfServerRestarted(state))
                WriteFile(state);

            return state;
        }
    }

    public void AddRun(RunRecord record)
    {
        lock (FileLock)
        {
            var state = ReadFile();
            state.History.Insert(0, record);
            if (state.History.Count > MaxHistory)
                state.History = state.History.Take(MaxHistory).ToList();

            WriteFile(state);
        }
    }

    public void AddPendingRestart(PendingRestartItem item)
    {
        lock (FileLock)
        {
            var state = ReadFile();
            ClearPendingIfServerRestarted(state);

            // A second update to the same plugin keeps the original "from" version
            var existing = state.PendingRestart.FirstOrDefault(p => string.Equals(p.FileName, item.FileName, StringComparison.OrdinalIgnoreCase)
                                                                    && !string.IsNullOrEmpty(item.FileName));
            if (existing != null)
            {
                // A missing version and the "unknown" a backup is labelled with are the same thing: the original file
                if (string.Equals(OrUnknown(existing.FromVersion), OrUnknown(item.ToVersion), StringComparison.OrdinalIgnoreCase))
                {
                    // Back to the version that is still running, so there is nothing left to restart for
                    state.PendingRestart.Remove(existing);
                    if (state.PendingRestart.Count == 0) state.PendingSinceUtc = null;
                    WriteFile(state);
                    return;
                }

                existing.ToVersion = item.ToVersion;
            }
            else
            {
                state.PendingRestart.Add(item);
            }

            state.PendingSinceUtc ??= DateTime.UtcNow;
            WriteFile(state);
        }
    }

    private static string OrUnknown(string version) => string.IsNullOrEmpty(version) ? "unknown" : version;

    public void ClearPendingRestart()
    {
        lock (FileLock)
        {
            var state = ReadFile();
            state.PendingRestart.Clear();
            state.PendingSinceUtc = null;
            WriteFile(state);
        }
    }

    /// <summary>
    /// Pending changes are applied by a restart. If the server process started after the change was recorded,
    /// a restart has happened and the list is stale.
    /// </summary>
    private bool ClearPendingIfServerRestarted(PluginRuntimeState state)
    {
        if (state.PendingRestart.Count == 0 && state.PendingSinceUtc == null) return false;

        DateTime started;
        try
        {
            started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            return false;
        }

        if (state.PendingSinceUtc != null && state.PendingSinceUtc.Value < started)
        {
            state.PendingRestart.Clear();
            state.PendingSinceUtc = null;
            return true;
        }

        return false;
    }

    private PluginRuntimeState ReadFile()
    {
        try
        {
            if (!File.Exists(_path)) return new PluginRuntimeState();

            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var state = _json.DeserializeFromStream<PluginRuntimeState>(stream);
                if (state == null) return new PluginRuntimeState();

                state.History        ??= new System.Collections.Generic.List<RunRecord>();
                state.PendingRestart ??= new System.Collections.Generic.List<PendingRestartItem>();
                return state;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not read plugin state file {_path}: {ex.Message}");
            return new PluginRuntimeState();
        }
    }

    private void WriteFile(PluginRuntimeState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Write to a temp file first so a crash mid-write doesn't leave a truncated state file
            var temp = _path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                _json.SerializeToStream(state, stream, new JsonSerializerOptions { Indent = true });
            }

            File.Copy(temp, _path, true);
            File.Delete(temp);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not write plugin state file {_path}: {ex.Message}");
        }
    }
}
