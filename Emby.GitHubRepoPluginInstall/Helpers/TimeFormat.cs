using System;

namespace Emby.GitHubRepoPluginInstall.Helpers;

public static class TimeFormat
{
    /// <summary>"just now", "5 min ago", "2 h ago", "3 d ago". Falls back to a date after 30 days.</summary>
    public static string Ago(DateTime? utc, DateTime? nowUtc = null)
    {
        if (utc == null) return "never";

        var now   = nowUtc ?? DateTime.UtcNow;
        var delta = now - utc.Value;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;

        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours   < 24) return $"{(int)delta.TotalHours} h ago";
        if (delta.TotalDays    < 30) return $"{(int)delta.TotalDays} d ago";

        return utc.Value.ToLocalTime().ToString("yyyy-MM-dd");
    }

    /// <summary>Local date and time for history rows, e.g. "Today 03:00" or "2025-10-09 03:00".</summary>
    public static string Stamp(DateTime utc, DateTime? nowLocal = null)
    {
        var local = utc.ToLocalTime();
        var today = (nowLocal ?? DateTime.Now).Date;

        if (local.Date == today)                 return "Today " + local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1))     return "Yesterday " + local.ToString("HH:mm");
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    public static string Duration(double seconds)
    {
        if (seconds < 1)  return "under 1 s";
        if (seconds < 60) return $"{(int)Math.Round(seconds)} s";

        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalMinutes} min {ts.Seconds} s";
    }
}
