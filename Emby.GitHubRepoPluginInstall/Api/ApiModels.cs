using System;
using System.Collections.Generic;
using System.Linq;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;

namespace Emby.GitHubRepoPluginInstall.Api;

// Everything here is the JSON the phone app sees. Field names are PascalCase, enums are strings, and
// a field may be added in a later release without raising ApiVersion, so clients must ignore fields they don't know.

public class InfoResponse
{
    public string PluginVersion          { get; set; }
    public int    ApiVersion             { get; set; }
    public int    MinSupportedApiVersion { get; set; }
    public bool   ApiEnabled             { get; set; }
    public bool   AllowManage            { get; set; }
    public bool   RunInProgress          { get; set; }
    public bool   RestartPending         { get; set; }
}

/// <summary>NotChecked, NoRelease, UpToDate, UpdateAvailable, UpdateWaiting, Failed.</summary>
public class PluginDto
{
    public string   Id                { get; set; }
    public string   Name              { get; set; }
    public string   Owner             { get; set; }
    public string   Url               { get; set; }
    public string   Status            { get; set; }
    public string   StatusText        { get; set; }
    public bool     AutoUpdate        { get; set; }
    public bool     AllowPreRelease   { get; set; }
    public string   AssetNameFilter   { get; set; }
    public string   InstalledVersion  { get; set; }
    public string   LatestVersion     { get; set; }
    public DateTime? LatestPublishedUtc { get; set; }
    public DateTime? LastCheckedUtc   { get; set; }
    public DateTime? LastUpdatedUtc   { get; set; }
    public string   LastError         { get; set; }
    public string   ReleaseNotes      { get; set; }
    public string[] AssetNames        { get; set; } = new string[0];

    /// <summary>The plugin's file was replaced and Emby hasn't been restarted yet.</summary>
    public bool     RestartPending    { get; set; }

    /// <summary>Detail only (GET /plugins/{Id}); the list leaves it out.</summary>
    public List<BackupDto> Backups    { get; set; }

    public static PluginDto From(ReposToProcess r, ISet<string> pendingFiles, List<PluginBackup> backups = null) => new PluginDto
    {
        Id                  = r.Id,
        Name                = r.Repository,
        Owner               = r.Owner,
        Url                 = r.Url,
        Status              = r.GetStatus().ToString(),
        StatusText          = r.StatusText,
        AutoUpdate          = r.AutoUpdate,
        AllowPreRelease     = r.GetPreRelease,
        AssetNameFilter     = r.AssetNameFilter ?? "",
        InstalledVersion    = r.LastVersionDownloaded,
        LatestVersion       = r.LatestVersion,
        LatestPublishedUtc  = r.LatestPublishedUtc,
        LastCheckedUtc      = r.LastDateTimeChecked,
        LastUpdatedUtc      = r.LastUpdatedUtc,
        LastError           = r.LastError,
        ReleaseNotes        = r.LatestNotes,
        AssetNames          = r.GetLatestAssetNames(),
        RestartPending      = !string.IsNullOrEmpty(r.FileName) && pendingFiles.Contains(r.FileName),
        Backups             = backups?.Select(b => new BackupDto { Version = b.Version, CreatedUtc = b.CreatedUtc, SizeBytes = b.FileSize }).ToList()
    };
}

public class BackupDto
{
    public string   Version    { get; set; }
    public DateTime CreatedUtc { get; set; }
    public long     SizeBytes  { get; set; }
}

public class RunProgressDto
{
    public bool      IsRunning  { get; set; }
    public int       Total      { get; set; }
    public int       Done       { get; set; }
    public string    Current    { get; set; }
    public string    Trigger    { get; set; }
    public DateTime? StartedUtc { get; set; }
}

/// <summary>Outcome is one of UpToDate, Updated, Failed, WaitingManual, NoRelease.</summary>
public class RunResultDto
{
    public string PluginId    { get; set; }
    public string Name        { get; set; }
    public string Outcome     { get; set; }
    public string FromVersion { get; set; }
    public string ToVersion   { get; set; }
    public string Message     { get; set; }
}

public class RunDto
{
    public string   Id              { get; set; }
    public DateTime StartedUtc      { get; set; }
    public double   DurationSeconds { get; set; }
    public string   Trigger         { get; set; }
    public int      Updated         { get; set; }
    public int      UpToDate        { get; set; }
    public int      Failed          { get; set; }
    public int      Waiting         { get; set; }
    public List<RunResultDto> Results { get; set; } = new List<RunResultDto>();

    public static RunDto From(RunRecord r) => new RunDto
    {
        Id = r.Id, StartedUtc = r.StartedUtc, DurationSeconds = r.DurationSeconds, Trigger = r.Trigger,
        Updated = r.Updated, UpToDate = r.UpToDate, Failed = r.Failed, Waiting = r.Waiting,
        Results = r.Results.Select(x => new RunResultDto
                  { PluginId = x.RepoId, Name = x.Name, Outcome = x.Outcome, FromVersion = x.FromVersion, ToVersion = x.ToVersion, Message = x.Message }).ToList()
    };
}

public class PendingRestartDto
{
    public string Name        { get; set; }
    public string FromVersion { get; set; }
    public string ToVersion   { get; set; }
}

public class StatusCounts
{
    public int Total           { get; set; }
    public int NotChecked      { get; set; }
    public int NoRelease       { get; set; }
    public int UpToDate        { get; set; }
    public int UpdateAvailable { get; set; }
    public int UpdateWaiting   { get; set; }
    public int Failed          { get; set; }
}

public class StatusResponse
{
    public RunProgressDto        Run            { get; set; }
    public RunDto                LastRun        { get; set; }
    public bool                  RestartPending { get; set; }
    public List<PendingRestartDto> PendingRestart { get; set; } = new List<PendingRestartDto>();
    public bool                  CanSelfRestart { get; set; }
    public StatusCounts          Counts         { get; set; }

    /// <summary>Plugins that have an update ready to install, so "Update N plugins" can be labelled.</summary>
    public int                   UpdatesReady   { get; set; }
    public DateTime              ServerTimeUtc  { get; set; }
}

public class PluginListResponse
{
    public List<PluginDto> Items { get; set; } = new List<PluginDto>();
}

public class HistoryResponse
{
    public List<RunDto> Items { get; set; } = new List<RunDto>();
}

public class RegistryDto
{
    public string Id       { get; set; }
    public string Name     { get; set; }
    public string RawUrl   { get; set; }
    public bool   Enabled  { get; set; }
    public bool   BuiltIn  { get; set; }
}

public class RegistryListResponse
{
    public List<RegistryDto> Items { get; set; } = new List<RegistryDto>();
}

public class AvailablePluginDto
{
    public string Name           { get; set; }
    public string Description    { get; set; }
    public string Url            { get; set; }
    public string RegistrySource { get; set; }
    public bool   AlreadyAdded   { get; set; }
}

public class AvailablePluginsResponse
{
    public List<AvailablePluginDto> Items { get; set; } = new List<AvailablePluginDto>();
}

public class SettingsResponse
{
    /// <summary>The token itself is never sent back.</summary>
    public bool TokenSet                   { get; set; }
    public bool RestartServerAfterInstall  { get; set; }
    public bool ApiEnabled                 { get; set; }
    public bool AllowManage                { get; set; }
}

public class TokenTestResponse
{
    public bool   Ok      { get; set; }
    public string Message { get; set; }
}

public class RunAcceptedResponse
{
    public string Message { get; set; }
    public int    Total   { get; set; }
}

public class ActionResponse
{
    public string     Message        { get; set; }
    public bool       RestartNeeded  { get; set; }
    public PluginDto  Plugin         { get; set; }
}
