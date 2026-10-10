using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.GitHubRepoPluginInstall.Services;

public interface IPluginBackupService
{
    /// <summary>Copies the plugin file to the backup folder. Returns the backup path, or null if the file doesn't exist.</summary>
    Task<string> CreateBackupAsync(string pluginFilePath, string version, CancellationToken cancellationToken = default);

    /// <summary>Restores a backup over the plugin file. With no version, the newest backup is used. Returns the restored backup, or null.</summary>
    Task<PluginBackup> RestoreBackupAsync(string pluginFileName, string version = null, CancellationToken cancellationToken = default);

    /// <summary>Backups for one plugin file, newest first.</summary>
    Task<List<PluginBackup>> GetBackupsAsync(string pluginFileName, CancellationToken cancellationToken = default);

    /// <summary>Keeps the newest <paramref name="maxBackups"/> backups and deletes the rest.</summary>
    Task CleanupOldBackupsAsync(string pluginFileName, int maxBackups = 3, CancellationToken cancellationToken = default);
}

public class PluginBackup
{
    public string   OriginalFileName { get; set; }
    public string   Version          { get; set; }
    public string   BackupPath       { get; set; }
    public DateTime CreatedUtc       { get; set; }
    public long     FileSize         { get; set; }
}
