using System;
using System.IO;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Security;
using Emby.GitHubRepoPluginInstall.UIBaseClasses.Store;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Logging;

namespace Emby.GitHubRepoPluginInstall.Storage;

public class SecurePluginOptionsStore : PluginOptionsStore
{
    private const string KeyFileName = "github-repo-plugin-install.key";

    private readonly ISecureStorage _secureStorage;
    private readonly ILogger        _logger;
    private          bool           _migrating;

    public SecurePluginOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
        : base(applicationHost, logger, pluginFullName)
    {
        _logger = logger;

        // The key lives in the data folder, not next to the config file that holds the encrypted token
        var paths = applicationHost.Resolve<IApplicationPaths>();
        _secureStorage = new SecureStorage(logger, Path.Combine(paths.DataPath, KeyFileName));

        // Subscribe to events to handle encryption/decryption
        this.FileSaving += OnFileSaving;
    }

    public override PluginUIOptions GetOptions()
    {
        var options = base.GetOptions();

        // Always decrypt the token when loading for UI display
        if (!string.IsNullOrEmpty(options.EncryptedGitHubToken))
        {
            options.GitHubToken = _secureStorage.Unprotect(options.EncryptedGitHubToken);

            // A token saved by an older version is re-saved in the current format once
            if (!_migrating &&
                !string.IsNullOrEmpty(options.GitHubToken) &&
                !SecureStorage.IsCurrentFormat(options.EncryptedGitHubToken))
            {
                _migrating = true;
                try
                {
                    var token = options.GitHubToken;
                    SetOptions(options);
                    options.GitHubToken = token;
                    _logger.Info("Re-saved the GitHub token in the new encrypted format.");
                }
                catch (Exception ex)
                {
                    _logger.Warn("Could not upgrade the saved token format: {0}", ex.Message);
                }
                finally
                {
                    _migrating = false;
                }
            }
        }

        return options;
    }

    private void OnFileSaving(object sender, FileSavingEventArgs e)
    {
        if (e.Options is PluginUIOptions options)
        {
            // Encrypt the token before saving
            if (!string.IsNullOrEmpty(options.GitHubToken))
            {
                options.EncryptedGitHubToken = _secureStorage.Protect(options.GitHubToken);
                // Clear the plain text token so it's not kept in memory
                options.GitHubToken = null;
            }
        }
    }
}
