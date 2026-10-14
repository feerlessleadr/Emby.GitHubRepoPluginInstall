using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.GitHubRepoPluginInstall.Api;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.GitHubRepoPluginInstall.Storage;

/// <summary>
/// The log of events the phone app can catch up on: the last 500, oldest first, in <c>&lt;plugin&gt;.events.json</c>.
/// Sequence numbers come from a separate <c>&lt;plugin&gt;.events.seq</c> file, so they keep growing even if the log is
/// damaged, deleted or trimmed.
/// </summary>
public class EventLogStore
{
    public const int MaxEvents    = 500;
    public const int DefaultLimit = 100;

    private static readonly object FileLock = new object();

    private readonly IJsonSerializer _json;
    private readonly ILogger         _logger;
    private readonly string          _logPath;
    private readonly string          _seqPath;

    private class LogFile
    {
        public List<LogEvent> Events { get; set; } = new List<LogEvent>();
    }

    public EventLogStore(IApplicationHost applicationHost, ILogger logger, string pluginName)
        : this(applicationHost.Resolve<IApplicationPaths>().PluginConfigurationsPath,
               applicationHost.Resolve<IJsonSerializer>(), logger, pluginName)
    {
    }

    public EventLogStore(string folder, IJsonSerializer json, ILogger logger, string pluginName)
    {
        _json    = json;
        _logger  = logger;
        _logPath = Path.Combine(folder, pluginName + ".events.json");
        _seqPath = Path.Combine(folder, pluginName + ".events.seq");
    }

    /// <summary>
    /// Gives the event the next sequence number, fills in the common fields of its payload and stores it.
    /// Returns the stored event.
    /// </summary>
    public LogEvent Append(string name, string shortName, EventPayload payload)
    {
        lock (FileLock)
        {
            var events = ReadEvents();
            var seq    = Math.Max(ReadSeq(), events.Count == 0 ? 0 : events.Max(e => e.Seq)) + 1;

            // The number is saved first: a failed log write may lose an event, but never reuses a number
            WriteSeq(seq);

            var now = DateTime.UtcNow;
            payload.ApiLevel      = ApiVersions.Level;
            payload.Seq           = seq;
            payload.EventId       = Guid.NewGuid().ToString();
            payload.ServerTimeUtc = now;
            payload.Event         = shortName;

            var entry = new LogEvent { Seq = seq, Name = name, AtUtc = now, Data = payload };
            events.Add(entry);
            if (events.Count > MaxEvents)
                events = events.Skip(events.Count - MaxEvents).ToList();

            WriteEvents(events);
            return entry;
        }
    }

    /// <summary>The highest sequence number handed out so far, 0 before the first event. Cheap: reads one small file.</summary>
    public long LatestSeq()
    {
        lock (FileLock)
        {
            var seq = ReadSeq();
            if (seq > 0) return seq;

            var events = ReadEvents();
            return events.Count == 0 ? 0 : events.Max(e => e.Seq);
        }
    }

    /// <summary>Events after <paramref name="after"/>, oldest first, at most <paramref name="limit"/>.</summary>
    public EventsResponse Read(long after, int limit)
    {
        limit = Math.Max(1, Math.Min(limit, MaxEvents));

        lock (FileLock)
        {
            var events = ReadEvents().OrderBy(e => e.Seq).ToList();
            var latest = Math.Max(ReadSeq(), events.Count == 0 ? 0 : events[events.Count - 1].Seq);
            after      = Math.Max(0, after);

            // The log must reach back to the event right after the one the caller has. If the caller is ahead of the
            // plugin (data was reset), it is also told, so it reads the status instead.
            var truncated = after > latest
                            || (latest > after && (events.Count == 0 || events[0].Seq > after + 1));

            return new EventsResponse
                   {
                       ApiLevel  = ApiVersions.Level,
                       LatestSeq = latest,
                       Truncated = truncated,
                       Events    = events.Where(e => e.Seq > after).Take(limit).ToList()
                   };
        }
    }

    private long ReadSeq()
    {
        try
        {
            if (!File.Exists(_seqPath)) return 0;
            return long.TryParse(File.ReadAllText(_seqPath).Trim(), out var seq) && seq > 0 ? seq : 0;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not read the event number file {_seqPath}: {ex.Message}");
            return 0;
        }
    }

    private void WriteSeq(long seq)
    {
        try
        {
            EnsureFolder();
            File.WriteAllText(_seqPath, seq.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not write the event number file {_seqPath}: {ex.Message}");
        }
    }

    private List<LogEvent> ReadEvents()
    {
        try
        {
            if (!File.Exists(_logPath)) return new List<LogEvent>();

            using (var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var file = _json.DeserializeFromStream<LogFile>(stream);
                return file?.Events?.Where(e => e != null).ToList() ?? new List<LogEvent>();
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not read the event log {_logPath}: {ex.Message}");
            return new List<LogEvent>();
        }
    }

    private void WriteEvents(List<LogEvent> events)
    {
        try
        {
            EnsureFolder();

            // Write to a temp file first so a crash mid-write doesn't leave a truncated log
            var temp = _logPath + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                _json.SerializeToStream(new LogFile { Events = events }, stream);
            }

            File.Copy(temp, _logPath, true);
            File.Delete(temp);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Could not write the event log {_logPath}: {ex.Message}");
        }
    }

    private void EnsureFolder()
    {
        var dir = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}
