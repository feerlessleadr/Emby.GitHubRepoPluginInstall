using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.GithubAPI;

namespace Emby.GitHubRepoPluginInstall.Validation;

public static class GitHubUrlValidator
{
    private static readonly Regex GitHubUrlRegex = new Regex(
        @"^https?://github\.com/([^/]+)/([^/]+)(?:/.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ValidationResult ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ValidationResult(false, "URL cannot be empty.");
        }

        // Auto-correct common URL formats
        var correctedUrl = Normalize(url);
        var wasCorrect = correctedUrl == url;

        var match = GitHubUrlRegex.Match(correctedUrl);
        if (!match.Success)
        {
            return new ValidationResult(false, "Invalid GitHub repository URL format. Expected format: https://github.com/owner/repository");
        }

        var owner = match.Groups[1].Value;
        var repository = match.Groups[2].Value;

        // Basic validation of owner and repository names
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            return new ValidationResult(false, "Owner and repository names cannot be empty.");
        }

        // GitHub username/org name validation
        if (!IsValidGitHubName(owner))
        {
            return new ValidationResult(false, "Invalid GitHub owner name format.");
        }

        // GitHub repository name validation
        if (!IsValidGitHubRepositoryName(repository))
        {
            return new ValidationResult(false, "Invalid GitHub repository name format.");
        }

        return new ValidationResult(true, wasCorrect ? null : $"URL auto-corrected to: {correctedUrl}", correctedUrl);
    }

    public static async Task<ValidationResult> ValidateUrlAsync(string url, IGitHubApiClient apiClient, CancellationToken cancellationToken = default)
    {
        var basicValidation = ValidateUrl(url);
        if (!basicValidation.IsValid)
        {
            return basicValidation;
        }

        var correctedUrl = basicValidation.CorrectedUrl ?? url;
        var (owner, repository) = ParseUrl(correctedUrl);

        try
        {
            var exists = await apiClient.ValidateRepositoryAsync(owner, repository, cancellationToken);
            if (!exists)
            {
                return new ValidationResult(false, "Repository does not exist or is not accessible with the current GitHub token.");
            }

            return new ValidationResult(true, basicValidation.Message, correctedUrl);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, $"Error validating repository: {ex.Message}");
        }
    }

    private static readonly Regex ShorthandRegex = new Regex(
        @"^([A-Za-z0-9][A-Za-z0-9-]*)/([A-Za-z0-9_][A-Za-z0-9._-]*)$", RegexOptions.Compiled);

    private static readonly Regex SshRegex = new Regex(
        @"^git@github\.com:([^/]+)/(.+?)(?:\.git)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the canonical "https://github.com/owner/repo" form for anything the validator accepts
    /// (owner/repo, ssh addresses, .git suffixes, /releases links). Other input is returned trimmed.
    /// </summary>
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        var corrected = AutoCorrectUrl(url);
        var match     = GitHubUrlRegex.Match(corrected);
        if (!match.Success) return corrected;

        var repository = match.Groups[2].Value;
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository.Substring(0, repository.Length - 4);

        return $"https://github.com/{match.Groups[1].Value}/{repository}";
    }

    private static string AutoCorrectUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        url = url.Trim();

        var ssh = SshRegex.Match(url);
        if (ssh.Success)
            return $"https://github.com/{ssh.Groups[1].Value}/{ssh.Groups[2].Value}";

        // "owner/repo" shorthand
        if (ShorthandRegex.IsMatch(url))
            return "https://github.com/" + url;

        // Add https:// if missing
        if (!url.StartsWith("http://") && !url.StartsWith("https://"))
        {
            url = "https://" + url;
        }

        // Convert http to https
        if (url.StartsWith("http://github.com"))
        {
            url = url.Replace("http://", "https://");
        }

        // Remove trailing slashes and common suffixes
        url = url.TrimEnd('/');
        if (url.EndsWith(".git"))
        {
            url = url.Substring(0, url.Length - 4);
        }

        // Handle www subdomain
        if (url.Contains("www.github.com"))
        {
            url = url.Replace("www.github.com", "github.com");
        }

        return url;
    }

    private static bool IsValidGitHubName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // GitHub username rules:
        // - May only contain alphanumeric characters or single hyphens
        // - Cannot begin or end with a hyphen
        // - Maximum 39 characters
        var nameRegex = new Regex(@"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,37}[a-zA-Z0-9])?$");
        return nameRegex.IsMatch(name);
    }

    private static bool IsValidGitHubRepositoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // GitHub repository name rules:
        // - Can contain alphanumeric characters, hyphens, underscores, and periods
        // - Cannot start with a period or hyphen
        // - Maximum 100 characters
        var repoRegex = new Regex(@"^[a-zA-Z0-9_][a-zA-Z0-9._-]{0,99}$");
        return repoRegex.IsMatch(name);
    }

    public static (string Owner, string Repository) ParseUrl(string url)
    {
        var match = GitHubUrlRegex.Match(url);
        if (!match.Success)
            return (string.Empty, string.Empty);

        return (match.Groups[1].Value, match.Groups[2].Value);
    }
}

public class ValidationResult
{
    public ValidationResult(bool isValid, string message = null, string correctedUrl = null)
    {
        IsValid = isValid;
        Message = message;
        CorrectedUrl = correctedUrl;
    }

    public bool IsValid { get; }
    public string Message { get; }
    public string CorrectedUrl { get; }
}