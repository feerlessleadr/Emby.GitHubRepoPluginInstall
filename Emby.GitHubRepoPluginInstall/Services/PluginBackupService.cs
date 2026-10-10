using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.Helpers;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;

namespace Emby.GitHubRepoPluginInstall.Services;

/// <summary>
/// Keeps copies of plugin files before they are replaced, so an update that breaks a plugin can be undone.
/// Backups are stored as "*.bak" so Emby never tries to load them as plugins.
/// File name: {yyyyMMddHHmmss}~{version}~{original file name}.bak, inside a folder per plugin file.
/// </summary>
public class PluginBackupService : IPluginBackupService
{
    private const string Extension = ".bak";
    private const char   Separator = '~';

    private readonly ILogger _logger;
    private readonly string  _pluginsPath;
    private readonly string  _backupPath;

    public PluginBackupService(IApplicationHost applicationHost, ILogger logger)
        : this(applicationHost.Resolve<IApplicationPaths>().PluginsPath, logger)
    {
    }

    public PluginBackupService(string pluginsPath, ILogger logger)
    {
        _logger      = logger;
        _pluginsPath = pluginsPath;
        _backupPath  = Path.Combine(pluginsPath, "plugin-backups");
    }

    public async Task<string> CreateBackupAsync(string pluginFilePath, string version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pluginFilePath) || !File.Exists(pluginFilePath))
            return null;

        var fileName  = Path.GetFileName(pluginFilePath);
        var backupDir = GetPluginBackupDir(fileName);
        Directory.CreateDirectory(backupDir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var target = Path.Combine(backupDir, $"{stamp}{Separator}{Sanitize(version)}{Separator}{fileName}{Extension}");

        using (var source = new FileStream(pluginFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var backup = new FileStream(target, FileMode.Create, FileAccess.Write))
        {
            await source.CopyToAsync(backup, 81920, cancellationToken).ConfigureAwait(false);
        }

        _logger.Info($"Backed up {fileName} (version {version}) to {target}");
        await CleanupOldBackupsAsync(fileName, 3, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public async Task<PluginBackup> RestoreBackupAsync(string pluginFileName, string version = null, CancellationToken cancellationToken = default)
    {
        var backups = await GetBackupsAsync(pluginFileName, cancellationToken).ConfigureAwait(false);
        var backup = string.IsNullOrEmpty(version)
            ? backups.FirstOrDefault()
            : backups.FirstOrDefault(b => string.Equals(b.Version, Sanitize(version), StringComparison.OrdinalIgnoreCase));

        if (backup == null)
        {
            _logger.Warn($"No backup found for {pluginFileName}{(version == null ? "" : " version " + version)}");
            return null;
        }

        var target = Path.Combine(_pluginsPath, backup.OriginalFileName);
        FileOps.OverwriteFile(backup.BackupPath, target);

        _logger.Info($"Restored {backup.BackupPath} to {target}");
        return backup;
    }

    public Task<List<PluginBackup>> GetBackupsAsync(string pluginFileName, CancellationToken cancellationToken = default)
    {
        var dir = GetPluginBackupDir(pluginFileName);
        var result = new List<PluginBackup>();

        if (!Directory.Exists(dir))
            return Task.FromResult(result);

        foreach (var file in Directory.GetFiles(dir, "*" + Extension))
        {
            // timestamp ~ version ~ original file name (which may itself contain '~')
            var name = Path.GetFileNameWithoutExtension(file);
            var parts = name.Split(new[] { Separator }, 3);
            if (parts.Length != 3) continue;

            if (!DateTime.TryParseExact(parts[0], "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
                continue;

            result.Add(new PluginBackup
                       {
                           OriginalFileName = parts[2],
                           Version          = parts[1],
                           BackupPath       = file,
                           CreatedUtc       = created,
                           FileSize         = new FileInfo(file).Length
                       });
        }

        return Task.FromResult(result.OrderByDescending(b => b.CreatedUtc).ToList());
    }

    public async Task CleanupOldBackupsAsync(string pluginFileName, int maxBackups = 3, CancellationToken cancellationToken = default)
    {
        var backups = await GetBackupsAsync(pluginFileName, cancellationToken).ConfigureAwait(false);
        foreach (var old in backups.Skip(Math.Max(1, maxBackups)))
        {
            if (FileOps.TryDelete(old.BackupPath))
                _logger.Debug($"Removed old backup {old.BackupPath}");
        }
    }

    private string GetPluginBackupDir(string pluginFileName)
    {
        return Path.Combine(_backupPath, Path.GetFileNameWithoutExtension(pluginFileName));
    }

    private static string Sanitize(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "unknown";

        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { Separator }).ToArray();
        return new string(version.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
