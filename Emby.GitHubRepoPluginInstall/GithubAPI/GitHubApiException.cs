using System;

namespace Emby.GitHubRepoPluginInstall.GithubAPI;

public enum GitHubErrorKind
{
    Unauthorized,
    RateLimited,
    Forbidden,
    NotFound,
    Network,
    Other
}

/// <summary>A failed GitHub call, classified so the UI and activity log can say what to do about it.</summary>
public class GitHubApiException : Exception
{
    public GitHubApiException(GitHubErrorKind kind, string message, DateTime? resetUtc = null, Exception inner = null)
        : base(message, inner)
    {
        Kind     = kind;
        ResetUtc = resetUtc;
    }

    public GitHubErrorKind Kind     { get; }
    public DateTime?       ResetUtc { get; }
}

public class RateLimitInfo
{
    public int      Limit     { get; set; }
    public int      Remaining { get; set; }
    public DateTime ResetUtc  { get; set; }
}

public class TokenTestResult
{
    public bool   Ok      { get; set; }
    public string Message { get; set; }
}

public class DownloadResult
{
    /// <summary>File name inside the plugins folder that represents this plugin.</summary>
    public string FileName { get; set; }

    /// <summary>"verified (GitHub digest)", "not verified (release publishes no checksum)", etc.</summary>
    public string Verification { get; set; }
}
