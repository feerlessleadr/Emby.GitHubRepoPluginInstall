using System;
using System.Collections.Generic;
using System.Linq;

namespace Emby.GitHubRepoPluginInstall.Models;

/// <summary>Outcome names stored in run history. Kept as strings so the JSON stays readable.</summary>
public static class RepoOutcome
{
    public const string UpToDate      = "UpToDate";
    public const string Updated       = "Updated";
    public const string Failed        = "Failed";
    public const string WaitingManual = "WaitingManual";
    public const string NoRelease     = "NoRelease";
}

public class RepoRunResult
{
    public string RepoId      { get; set; }
    public string Name        { get; set; }
    public string Outcome     { get; set; }
    public string FromVersion { get; set; }
    public string ToVersion   { get; set; }
    public string Message     { get; set; }
}

public class RunRecord
{
    public string                Id              { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime              StartedUtc      { get; set; }
    public double                DurationSeconds { get; set; }

    /// <summary>"Scheduled" or "Manual".</summary>
    public string                Trigger         { get; set; }
    public List<RepoRunResult>   Results         { get; set; } = new List<RepoRunResult>();

    public int Updated => Results.Count(r => r.Outcome == RepoOutcome.Updated);
    public int UpToDate => Results.Count(r => r.Outcome == RepoOutcome.UpToDate || r.Outcome == RepoOutcome.NoRelease);
    public int Failed => Results.Count(r => r.Outcome == RepoOutcome.Failed);
    public int Waiting => Results.Count(r => r.Outcome == RepoOutcome.WaitingManual);
}

public class PendingRestartItem
{
    public string Name        { get; set; }
    public string FromVersion { get; set; }
    public string ToVersion   { get; set; }
    public string FileName    { get; set; }
}

/// <summary>Everything the plugin remembers between runs that isn't user configuration.</summary>
public class PluginRuntimeState
{
    public List<RunRecord>          History         { get; set; } = new List<RunRecord>();
    public List<PendingRestartItem> PendingRestart  { get; set; } = new List<PendingRestartItem>();
    public DateTime?                PendingSinceUtc { get; set; }
}
