using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.Helpers;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Validation;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace Emby.GitHubRepoPluginInstall.GithubAPI;

public class GitHubApiClient : IDisposable, IGitHubApiClient
{
    /// <summary>The default registry is fetched from here so new plugins appear without a new installer release.</summary>
    public const string DefaultRegistryUrl =
        "https://raw.githubusercontent.com/feerlessleadr/Emby.GitHubRepoPluginInstall/master/DefaultRegistry/plugins.json";

    public const string EmbeddedRegistryUrl = "embedded://default";

    private readonly HttpClient      _client;
    private readonly IJsonSerializer _jsonSerializer;
    private readonly ILogger         _logger;
    private readonly bool            _disposeClient;
    private readonly bool            _hasToken;

    // Simple in-memory cache
    private static readonly Dictionary<string, CacheItem> _cache = new Dictionary<string, CacheItem>();
    private static readonly object _cacheLock = new object();

    /// <summary>Rate limit headers from the most recent GitHub response, for display in the UI.</summary>
    public static RateLimitInfo LastRateLimit { get; private set; }

    private class CacheItem
    {
        public object Data { get; set; }
        public DateTime ExpiryTime { get; set; }

        public bool IsExpired => DateTime.UtcNow > ExpiryTime;
    }

    public GitHubApiClient(string githubToken, HttpClient httpClient, IJsonSerializer jsonSerializer, ILogger logger)
    {
        _jsonSerializer = jsonSerializer;
        _logger         = logger;
        _client         = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeClient  = false;

        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.UserAgent.Clear();
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitHubReleaseManager", "1.0"));

        if (!string.IsNullOrEmpty(githubToken))
        {
            _hasToken = true;
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        }
    }

    public GitHubApiClient(string githubToken, IJsonSerializer jsonSerializer, ILogger logger)
    {
        _jsonSerializer = jsonSerializer;
        _logger         = logger;
        _client         = new HttpClient();
        _disposeClient  = true;

        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitHubReleaseManager", "1.0"));

        if (!string.IsNullOrEmpty(githubToken))
        {
            _hasToken = true;
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
            _logger.Debug($"GitHub token configured (length: {githubToken.Length})");
        }
        else
        {
            _logger.Warn("No GitHub token provided - API requests are limited to 60 per hour");
        }
    }

    public async Task<List<GitHubRelease>> GetLatestReleasesAsync(ReposToProcess _repo, CancellationToken cancellationToken = default)
    {
        return await GetLatestReleasesAsync(new List<ReposToProcess>
                                            {
                                                _repo
                                            }, cancellationToken);
    }

    public async Task<GitHubRelease> GetLatestReleaseAsync(ReposToProcess _repo, CancellationToken cancellationToken = default)
    {
        var release = await GetLatestReleasesAsync(new List<ReposToProcess>
                                                   {
                                                       _repo
                                                   }, cancellationToken);
        return release.OrderByDescending(x => x.PublishedAt)
                      .FirstOrDefault();
    }

    public async Task<GitHubRelease> GetLatestReleaseAsync(ReposToProcess _repo, bool bypassCache, CancellationToken cancellationToken = default)
    {
        if (!bypassCache)
            return await GetLatestReleaseAsync(_repo, cancellationToken);

        return await GetLatestReleaseForRepoAsync(_repo, cancellationToken, true);
    }

    public async Task<List<GitHubRelease>> GetLatestReleasesAsync(List<ReposToProcess> _repositories, CancellationToken cancellationToken = default)
    {
        var tasks = _repositories.Select(repo => GetLatestReleaseForRepoAsync(repo, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);

        return results.Where(r => r != null).ToList();
    }

    /// <summary>
    /// Returns the newest matching release, or null if the repository has none.
    /// Throws <see cref="GitHubApiException"/> for anything that stops us from finding out.
    /// </summary>
    private async Task<GitHubRelease> GetLatestReleaseForRepoAsync(ReposToProcess repo, CancellationToken cancellationToken = default, bool bypassCache = false)
    {
        var cacheKey = $"releases_{repo.Owner}_{repo.Repository}_{repo.GetPreRelease}";

        // Check cache first (unless bypassed)
        if (!bypassCache && TryGetFromCache<GitHubRelease>(cacheKey, out var cachedRelease))
        {
            _logger.Debug($"Returning cached release for {repo.Owner}/{repo.Repository}");
            return cachedRelease;
        }

        try
        {
            var url = $"https://api.github.com/repos/{repo.Owner}/{repo.Repository}/releases";
            var response = await ExecuteWithRetryAsync(() => _client.GetAsync(url, cancellationToken)).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw MapFailure(response, $"{repo.Owner}/{repo.Repository}");

            RecordRateLimit(response);

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var gitHubReleases = _jsonSerializer.DeserializeFromStream<List<GitHubRelease>>(stream);
                if (gitHubReleases?.Any() != true)
                    return null;

                var filteredReleases = gitHubReleases.Where(x => !x.Draft);
                if (!repo.GetPreRelease)
                    filteredReleases = filteredReleases.Where(x => !x.PreRelease);

                var latestRelease = filteredReleases
                    .OrderByDescending(x => x.PublishedAt)
                    .FirstOrDefault();

                if (latestRelease != null)
                {
                    latestRelease.BaseRepoUrl = $"https://api.github.com/repos/{repo.Owner}/{repo.Repository}";

                    // Commit details only add a fallback for empty release notes, so a failure here must not fail the check.
                    try
                    {
                        latestRelease.GitHubCommit = await GetCommitDetailsAsync(latestRelease, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception commitEx) when (!(commitEx is OperationCanceledException))
                    {
                        _logger.Debug($"Could not load commit details for {repo.Owner}/{repo.Repository}: {commitEx.Message}");
                    }

                    // Cache the result for 15 minutes
                    AddToCache(cacheKey, latestRelease, TimeSpan.FromMinutes(15));
                }

                return latestRelease;
            }
        }
        catch (GitHubApiException ex)
        {
            _logger.Warn($"GitHub check failed for {repo.Owner}/{repo.Repository}: {ex.Kind} - {ex.Message}");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Error getting releases for {repo.Owner}/{repo.Repository}: {ex.Message}");
            throw new GitHubApiException(GitHubErrorKind.Network, ex.Message, null, ex);
        }
    }

    public async Task<GitHubCommit> GetCommitDetailsAsync(GitHubRelease release, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(release.TargetCommitish))
            throw new InvalidOperationException("No commit hash available for this release.");

        if (string.IsNullOrEmpty(release.BaseRepoUrl))
            throw new InvalidOperationException("Unable to determine repository information from release URL.");

        var cacheKey = $"commit_{release.BaseRepoUrl}_{release.TargetCommitish}";

        if (TryGetFromCache<GitHubCommit>(cacheKey, out var cachedCommit))
        {
            return cachedCommit;
        }

        try
        {
            var url = $"{release.BaseRepoUrl}/commits/{release.TargetCommitish}";
            var response = await ExecuteWithRetryAsync(() => _client.GetAsync(url, cancellationToken)).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var commit = _jsonSerializer.DeserializeFromStream<GitHubCommit>(stream);
                AddToCache(cacheKey, commit, TimeSpan.FromHours(1));
                return commit;
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error fetching commit details: {ex.Message}", ex);
        }
    }

    /// <summary>The asset that would be installed for this release and repo, or null if none matches.</summary>
    public static GitHubReleaseAsset PickAsset(GitHubRelease release, ReposToProcess repo)
    {
        bool Matches(GitHubReleaseAsset a) => a != null && !string.IsNullOrEmpty(a.Name) && (repo == null || repo.AssetMatchesFilter(a.Name));

        // Prefer a direct .dll asset; fall back to a non-source .zip that contains the plugin DLL
        return release?.Assets?.FirstOrDefault(x => Matches(x) && x.IsDll)
               ?? release?.Assets?.FirstOrDefault(x => Matches(x) && x.IsZip);
    }

    public async Task<DownloadResult> DownloadReleaseAsync(GitHubRelease release, string destinationPath, IProgress<double> progress = null, CancellationToken cancellationToken = default, ReposToProcess repo = null)
    {
        var asset = PickAsset(release, repo);

        if (asset == null)
        {
            var filterHint = repo != null && !string.IsNullOrWhiteSpace(repo.AssetNameFilter)
                ? $" matching filter '{repo.AssetNameFilter}'"
                : "";
            _logger.Error($"No DLL or suitable ZIP asset{filterHint} found for release {release.Url}");
            throw new InvalidOperationException($"No DLL or suitable ZIP asset{filterHint} found for this release.");
        }

        var isZip = !asset.IsDll;

        // Asset names come from GitHub. Keep only the file name so a crafted name can't point outside the plugins folder.
        var assetFileName = Path.GetFileName(asset.Name);
        if (string.IsNullOrEmpty(assetFileName))
            throw new InvalidOperationException($"Release asset has an unusable name: '{asset.Name}'.");

        Directory.CreateDirectory(destinationPath);
        var tempDownloadPath = Path.Combine(destinationPath, $"{assetFileName}.temp");

        try
        {
            using (var response = await ExecuteWithRetryAsync(() => _client.SendAsync(CreateAssetRequest(asset), HttpCompletionOption.ResponseHeadersRead, cancellationToken)).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw MapFailure(response, asset.Name);

                using (var fs = new FileStream(tempDownloadPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs).ConfigureAwait(false);
                }
            }

            var verification = await VerifyChecksumAsync(release, asset, tempDownloadPath, cancellationToken).ConfigureAwait(false);
            _logger.Info($"Downloaded {asset.Name}: {verification}");

            if (!isZip)
            {
                // Direct DLL: move into place (renames the old file aside if it is locked)
                var fullPath = Path.Combine(destinationPath, assetFileName);
                FileOps.ReplaceFile(tempDownloadPath, fullPath);
                return new DownloadResult { FileName = assetFileName, Verification = verification };
            }

            // ZIP handling: extract all contents (some plugins ship extra DLLs and a web folder)
            string extractedDllName;
            var extractDir = Path.Combine(Path.GetTempPath(), "EmbyPluginZip_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(tempDownloadPath, extractDir);

                var dllFiles = Directory.GetFiles(extractDir, "*.dll", SearchOption.AllDirectories);
                if (dllFiles.Length == 0)
                {
                    throw new InvalidOperationException($"ZIP asset '{asset.Name}' does not contain any .dll files.");
                }

                var topEntries = Directory.GetFileSystemEntries(extractDir);
                var copyRoot = extractDir;
                if (topEntries.Length == 1 && Directory.Exists(topEntries[0]))
                {
                    copyRoot = topEntries[0];
                }

                var topLevelDlls = Directory.GetFiles(copyRoot, "*.dll", SearchOption.TopDirectoryOnly)
                                            .Select(Path.GetFileName)
                                            .ToArray();

                CopyDirectory(copyRoot, destinationPath);

                extractedDllName = FileOps.PickTrackedDll(topLevelDlls, repo?.Repository, repo?.FileName)
                                   ?? Path.GetFileName(dllFiles[0]);

                _logger.Info($"Extracted ZIP '{asset.Name}' into plugins folder (tracked dll: {extractedDllName}).");
            }
            finally
            {
                FileOps.TryDelete(tempDownloadPath);
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { /* ignore */ }
            }

            return new DownloadResult { FileName = extractedDllName, Verification = verification };
        }
        catch (GitHubApiException)
        {
            FileOps.TryDelete(tempDownloadPath);
            throw;
        }
        catch (Exception ex)
        {
            // Clean up temp download if it still exists
            FileOps.TryDelete(tempDownloadPath);

            throw new Exception($"Error downloading/processing release: {ex.Message}", ex);
        }
    }

    // Private repos need the API URL (with the token); public ones use the plain download URL.
    private HttpRequestMessage CreateAssetRequest(GitHubReleaseAsset asset)
    {
        var useApiUrl = _hasToken && !string.IsNullOrEmpty(asset.Url);
        var downloadUrl = useApiUrl ? asset.Url : asset.BrowserDownloadUrl;

        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException($"No download URL available for asset '{asset.Name}'.");

        var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (useApiUrl)
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }

        return request;
    }

    /// <summary>
    /// Checks the downloaded file against a hash the release publishes (GitHub's asset digest, or a
    /// .sha256 / SHA256SUMS file next to the asset). Throws on a mismatch. A release with no published hash
    /// is not an error; the returned text says it wasn't verified.
    /// </summary>
    private async Task<string> VerifyChecksumAsync(GitHubRelease release, GitHubReleaseAsset asset, string filePath, CancellationToken cancellationToken)
    {
        var expected = ChecksumHelper.ParseDigest(asset.Digest);
        var source   = "GitHub digest";

        if (expected == null)
        {
            var sidecar = release.Assets?.FirstOrDefault(a => IsChecksumAssetFor(a, asset));
            if (sidecar != null)
            {
                var text = await TryDownloadTextAsync(sidecar, cancellationToken).ConfigureAwait(false);
                expected = ChecksumHelper.FindHashInChecksumFile(text, asset.Name);
                source   = sidecar.Name;
            }
        }

        if (expected == null)
            return "not verified (release publishes no checksum)";

        var actual = ChecksumHelper.Sha256Hex(filePath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Checksum mismatch for {asset.Name}: expected {expected}, got {actual}. The file was not installed.");

        return $"verified ({source})";
    }

    private static readonly string[] SharedChecksumNames =
    {
        "sha256sums", "sha256sums.txt", "sha256sum.txt", "checksums.txt", "checksums.sha256", "checksums"
    };

    public static bool IsChecksumAssetFor(GitHubReleaseAsset candidate, GitHubReleaseAsset asset)
    {
        if (candidate == null || string.IsNullOrEmpty(candidate.Name) || ReferenceEquals(candidate, asset)) return false;

        var name = candidate.Name;
        if (string.Equals(name, asset.Name + ".sha256",     StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, asset.Name + ".sha256sum",  StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, asset.Name + ".sha256.txt", StringComparison.OrdinalIgnoreCase))
            return true;

        return SharedChecksumNames.Contains(name.ToLowerInvariant());
    }

    private async Task<string> TryDownloadTextAsync(GitHubReleaseAsset asset, CancellationToken cancellationToken)
    {
        try
        {
            using (var response = await ExecuteWithRetryAsync(() => _client.SendAsync(CreateAssetRequest(asset), cancellationToken)).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                if (response.Content.Headers.ContentLength > 1024 * 1024) return null;

                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            _logger.Debug($"Could not read checksum file {asset.Name}: {ex.Message}");
            return null;
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            FileOps.OverwriteFile(file, Path.Combine(destDir, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    public async Task<bool> ValidateRepositoryAsync(string owner, string repository, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{owner}/{repository}";
            var response = await ExecuteWithRetryAsync(() => _client.GetAsync(url, cancellationToken)).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Calls GitHub's rate-limit endpoint, which doesn't count against the limit, to check the token.</summary>
    public async Task<TokenTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using (var response = await _client.GetAsync("https://api.github.com/rate_limit", cancellationToken).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return new TokenTestResult { Ok = false, Message = "GitHub rejected this token. It may have expired or been revoked. Create a new one and paste it in." };

                if (!response.IsSuccessStatusCode)
                    return new TokenTestResult { Ok = false, Message = $"GitHub answered with {(int)response.StatusCode}. Try again in a minute." };

                RecordRateLimit(response);
                var rate = LastRateLimit;

                if (!_hasToken)
                {
                    return new TokenTestResult
                           {
                               Ok = true,
                               Message = $"No token set. Public repositories work, but GitHub allows only {rate?.Limit ?? 60} requests per hour without one."
                           };
                }

                var message = rate != null
                    ? $"Token works. {rate.Remaining} of {rate.Limit} requests left this hour (resets {rate.ResetUtc.ToLocalTime():HH:mm})."
                    : "Token works.";

                if (response.Headers.TryGetValues("X-OAuth-Scopes", out var scopes) &&
                    string.Join(",", scopes).Split(',').Select(s => s.Trim()).Contains("repo"))
                {
                    message += " This is a classic token with the 'repo' scope, which can read all your private repositories. " +
                               "A fine-grained token with read-only Contents access is safer.";
                }

                return new TokenTestResult { Ok = true, Message = message };
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            return new TokenTestResult { Ok = false, Message = $"Couldn't reach GitHub: {ex.Message}. Check the server's internet connection." };
        }
    }

    private void RecordRateLimit(HttpResponseMessage response)
    {
        try
        {
            if (TryGetHeaderInt(response, "X-RateLimit-Limit", out var limit) &&
                TryGetHeaderInt(response, "X-RateLimit-Remaining", out var remaining) &&
                TryGetHeaderLong(response, "X-RateLimit-Reset", out var reset))
            {
                LastRateLimit = new RateLimitInfo
                                {
                                    Limit     = limit,
                                    Remaining = remaining,
                                    ResetUtc  = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime
                                };
            }
        }
        catch
        {
            // Rate limit info is informational only
        }
    }

    private static bool TryGetHeaderInt(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out value);
    }

    private static bool TryGetHeaderLong(HttpResponseMessage response, string name, out long value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) && long.TryParse(values.FirstOrDefault(), out value);
    }

    /// <summary>Turns a non-success response into an exception that says what actually went wrong.</summary>
    private GitHubApiException MapFailure(HttpResponseMessage response, string what)
    {
        RecordRateLimit(response);
        var status = response.StatusCode;

        if (status == HttpStatusCode.Unauthorized)
            return new GitHubApiException(GitHubErrorKind.Unauthorized, $"GitHub returned 401 for {what}.");

        if (status == HttpStatusCode.Forbidden || status == HttpStatusCode.TooManyRequests)
        {
            TryGetHeaderInt(response, "X-RateLimit-Remaining", out var remaining);
            var hasRemaining = response.Headers.Contains("X-RateLimit-Remaining");

            if (status == HttpStatusCode.TooManyRequests || (hasRemaining && remaining == 0))
            {
                DateTime? reset = null;
                if (TryGetHeaderLong(response, "X-RateLimit-Reset", out var resetSeconds))
                    reset = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).UtcDateTime;

                return new GitHubApiException(GitHubErrorKind.RateLimited, $"GitHub rate limit reached while checking {what}.", reset);
            }

            return new GitHubApiException(GitHubErrorKind.Forbidden, $"GitHub returned 403 for {what}.");
        }

        if (status == HttpStatusCode.NotFound)
            return new GitHubApiException(GitHubErrorKind.NotFound, $"GitHub returned 404 for {what}.");

        return new GitHubApiException(GitHubErrorKind.Other, $"GitHub returned {(int)status} for {what}.");
    }

    // Basic retry logic without external dependencies
    private async Task<HttpResponseMessage> ExecuteWithRetryAsync(Func<Task<HttpResponseMessage>> operation, int maxRetries = 3)
    {
        Exception lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await operation().ConfigureAwait(false);

                // Check if we should retry based on status code
                if (ShouldRetry(response.StatusCode) && attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                    _logger.Warn($"Request failed with {response.StatusCode}, retrying in {delay.TotalSeconds} seconds (attempt {attempt + 1}/{maxRetries + 1})");
                    await Task.Delay(delay).ConfigureAwait(false);
                    continue;
                }

                return response;
            }
            catch (Exception ex) when (ShouldRetryException(ex) && attempt < maxRetries)
            {
                lastException = ex;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // Exponential backoff
                _logger.Warn($"Request failed with {ex.GetType().Name}: {ex.Message}, retrying in {delay.TotalSeconds} seconds (attempt {attempt + 1}/{maxRetries + 1})");
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }

        throw lastException ?? new Exception("Max retries exceeded");
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.InternalServerError ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout ||
               statusCode == HttpStatusCode.TooManyRequests;
    }

    private static bool ShouldRetryException(Exception ex)
    {
        return ex is HttpRequestException ||
               ex is TaskCanceledException ||
               ex is TimeoutException;
    }

    // Simple cache methods
    private bool TryGetFromCache<T>(string key, out T value)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cacheItem) && !cacheItem.IsExpired)
            {
                value = (T)cacheItem.Data;
                return true;
            }

            // Remove expired item
            if (_cache.ContainsKey(key))
            {
                _cache.Remove(key);
            }

            value = default(T);
            return false;
        }
    }

    private void AddToCache<T>(string key, T value, TimeSpan expiry)
    {
        lock (_cacheLock)
        {
            _cache[key] = new CacheItem
            {
                Data = value,
                ExpiryTime = DateTime.UtcNow.Add(expiry)
            };

            // Simple cleanup - remove expired items if cache gets too large
            if (_cache.Count > 100)
            {
                var expiredKeys = _cache.Where(kvp => kvp.Value.IsExpired).Select(kvp => kvp.Key).ToList();
                foreach (var expiredKey in expiredKeys)
                {
                    _cache.Remove(expiredKey);
                }
            }
        }
    }

    public async Task<List<PluginRegistryEntry>> GetAllRegistryPluginsAsync(List<PluginRegistry> registries, CancellationToken cancellationToken = default)
    {
        var allPlugins = new List<PluginRegistryEntry>();
        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var registry in registries.Where(r => r.Enabled))
        {
            try
            {
                var registryData = registry.RawUrl == EmbeddedRegistryUrl
                    ? await LoadDefaultRegistryAsync(cancellationToken).ConfigureAwait(false)
                    : await LoadRegistryFromUrlAsync(registry.RawUrl, cancellationToken).ConfigureAwait(false);

                if (registryData?.Plugins != null)
                {
                    foreach (var plugin in registryData.Plugins)
                    {
                        if (string.IsNullOrWhiteSpace(plugin?.Url)) continue;

                        plugin.RegistrySource = registry.Name;

                        // Duplicates are judged by owner/repo, so URL spelling differences don't create a second entry
                        var key = RegistryKey(plugin.Url);
                        if (seen.Add(key))
                        {
                            allPlugins.Add(plugin);
                        }
                        else
                        {
                            _logger.Debug($"Skipping duplicate plugin {plugin.Url} from {registry.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to fetch registry {registry.Name} from {registry.RawUrl}: {ex.Message}");
            }
        }

        return allPlugins;
    }

    public static string RegistryKey(string url)
    {
        var (owner, repo) = GitHubUrlValidator.ParseUrl(GitHubUrlValidator.Normalize(url));
        return string.IsNullOrEmpty(owner) ? (url ?? string.Empty).Trim().ToLowerInvariant() : $"{owner}/{repo}".ToLowerInvariant();
    }

    private async Task<PluginRegistryData> LoadRegistryFromUrlAsync(string url, CancellationToken cancellationToken)
    {
        using (var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) return null;

            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                return _jsonSerializer.DeserializeFromStream<PluginRegistryData>(stream);
            }
        }
    }

    // The built-in registry is read from the repository so new entries show up without a new release.
    // The copy embedded in the DLL is used when GitHub can't be reached.
    private async Task<PluginRegistryData> LoadDefaultRegistryAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = "default_registry";
        if (TryGetFromCache<PluginRegistryData>(cacheKey, out var cached))
            return cached;

        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var remote = await LoadRegistryFromUrlAsync(DefaultRegistryUrl, timeout.Token).ConfigureAwait(false);
                if (remote?.Plugins?.Count > 0)
                {
                    AddToCache(cacheKey, remote, TimeSpan.FromHours(1));
                    return remote;
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException) || !cancellationToken.IsCancellationRequested)
        {
            _logger.Debug($"Could not fetch the default registry, using the built-in copy: {ex.Message}");
        }

        var assembly = typeof(GitHubApiClient).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
                                   .FirstOrDefault(n => n.EndsWith("DefaultRegistry.plugins.json"));
        if (resourceName == null) return null;

        using (var stream = assembly.GetManifestResourceStream(resourceName))
        {
            return _jsonSerializer.DeserializeFromStream<PluginRegistryData>(stream);
        }
    }

    public void Dispose()
    {
        if (_disposeClient)
            _client?.Dispose();
    }
}
