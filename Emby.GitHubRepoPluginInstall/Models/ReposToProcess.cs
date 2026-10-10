using System;
using System.ComponentModel;
using System.Linq;

namespace Emby.GitHubRepoPluginInstall.Models;

public enum RepoStatus
{
    NotChecked,
    NoRelease,
    UpToDate,
    UpdateAvailable,
    UpdateWaiting,
    Failed
}

public class ReposToProcess
{
    public ReposToProcess()
    {
        Id = Guid.NewGuid()
                 .ToString("N");
    }

    public string Id            { get; set; }
    public string Url           { get; set; }
    public bool   GetPreRelease { get; set; }
    public bool   AutoUpdate    { get; set; } = true;

    /// <summary>
    /// If set, only GitHub release assets whose filename contains this text are used.
    /// Example: "Emby" matches Moonfin.Emby-2.1.0.0.zip and skips Moonfin.Server-*.zip.
    /// </summary>
    public string AssetNameFilter { get; set; } = "";

    [ReadOnly(true)]
    public DateTime? LastDateTimeChecked { get; set; }

    public string LastVersionDownloaded { get; set; }
    public string FileName              { get; set; }

    /// <summary>Newest release tag seen on the last check.</summary>
    public string LatestVersion { get; set; }

    public DateTime? LatestPublishedUtc { get; set; }

    /// <summary>Release notes from the last check, truncated.</summary>
    public string LatestNotes { get; set; }

    /// <summary>Asset names on the latest release, joined with '|'.</summary>
    public string LatestAssets { get; set; }

    /// <summary>Message from the last failed check or install. Null when the last attempt worked.</summary>
    public string LastError { get; set; }

    public DateTime? LastUpdatedUtc { get; set; }

    private (string Owner, string Repository) RepositoryInfo
    {
        get
        {
            if (string.IsNullOrEmpty(Url)) return (string.Empty, string.Empty);

            try
            {
                // Split the URL by '/' and get owner and repo parts
                var parts = Url.TrimEnd('/')
                               .Split('/');
                if (parts.Length >= 5)
                    return (parts[3], parts[4]
                        .Split('/')[0]);
            }
            catch
            {
                // Return empty if parsing fails
                return (string.Empty, string.Empty);
            }

            return (string.Empty, string.Empty);
        }
    }

    public string Owner      => RepositoryInfo.Owner;
    public string Repository => RepositoryInfo.Repository;

    /// <summary>Owner/Repository in lower case, used to compare repos regardless of URL spelling.</summary>
    public string RepoKey => $"{Owner}/{Repository}".ToLowerInvariant();

    public RepoStatus GetStatus()
    {
        if (!string.IsNullOrEmpty(LastError))
            return RepoStatus.Failed;

        if (string.IsNullOrEmpty(LatestVersion))
        {
            if (!string.IsNullOrEmpty(LastVersionDownloaded)) return RepoStatus.UpToDate;

            // It was looked up and nothing was found, which is different from never having been looked at
            return LastDateTimeChecked != null ? RepoStatus.NoRelease : RepoStatus.NotChecked;
        }

        if (string.Equals(LatestVersion, LastVersionDownloaded, StringComparison.OrdinalIgnoreCase))
            return RepoStatus.UpToDate;

        return AutoUpdate ? RepoStatus.UpdateAvailable : RepoStatus.UpdateWaiting;
    }

    /// <summary>Plain-language status, also used as a grid column.</summary>
    public string StatusText
    {
        get
        {
            switch (GetStatus())
            {
                case RepoStatus.Failed:          return "Failed";
                case RepoStatus.UpdateAvailable: return "Update available";
                case RepoStatus.UpdateWaiting:   return "Update waiting";
                case RepoStatus.UpToDate:        return "Up to date";
                case RepoStatus.NoRelease:       return "No release yet";
                default:                         return "Not checked yet";
            }
        }
    }

    public string[] GetLatestAssetNames()
    {
        return string.IsNullOrEmpty(LatestAssets)
            ? Array.Empty<string>()
            : LatestAssets.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
    }

    /// <summary>Copies the fields written by update runs, so a stale UI copy doesn't overwrite them.</summary>
    public void CopyRuntimeStateFrom(ReposToProcess other)
    {
        LastDateTimeChecked   = other.LastDateTimeChecked;
        LastVersionDownloaded = other.LastVersionDownloaded;
        FileName              = other.FileName;
        LatestVersion         = other.LatestVersion;
        LatestPublishedUtc    = other.LatestPublishedUtc;
        LatestNotes           = other.LatestNotes;
        LatestAssets          = other.LatestAssets;
        LastError             = other.LastError;
        LastUpdatedUtc        = other.LastUpdatedUtc;
    }

    public bool AssetMatchesFilter(string assetName)
    {
        if (string.IsNullOrWhiteSpace(AssetNameFilter))
            return true;
        if (string.IsNullOrEmpty(assetName))
            return false;
        return assetName.IndexOf(AssetNameFilter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
