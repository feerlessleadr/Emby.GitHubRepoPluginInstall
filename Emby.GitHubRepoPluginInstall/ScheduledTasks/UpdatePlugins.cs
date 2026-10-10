using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.Services;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.GitHubRepoPluginInstall.ScheduledTasks;

/// <summary>
/// Daily check. All the work happens in <see cref="RepoUpdateService"/>, the same code the plugin page uses,
/// so a run from here and a run from the page behave the same and end up in the same history.
/// </summary>
public class UpdatePlugins : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger          _logger;

    public UpdatePlugins(ILogManager logManager, IApplicationHost applicationHost)
    {
        _logger          = logManager.GetLogger("UpdatePluginsScheduledTask");
        _applicationHost = applicationHost;
    }

    public bool IsHidden  => false;
    public bool IsEnabled => true;
    public bool IsLogged  => true;

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        var service = new RepoUpdateService(_applicationHost, _logger);

        try
        {
            var record = await service.RunAsync(RepoUpdateService.TriggerScheduled, RunMode.Auto, null, progress, cancellationToken)
                                      .ConfigureAwait(false);

            _logger.Info($"Plugin update run finished: {record.Updated} updated, {record.UpToDate} up to date, " +
                         $"{record.Failed} failed, {record.Waiting} waiting on the user.");
        }
        catch (InvalidOperationException ex)
        {
            // Another run (started from the plugin page) is already in progress
            _logger.Info(ex.Message);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
               {
                   new TaskTriggerInfo
                   {
                       Type = TaskTriggerInfo.TriggerInterval,
                       IntervalTicks = TimeSpan.FromHours(24)
                                               .Ticks
                   }
               };
    }

    public string Name        { get; } = "Update Plugins From Github Repos";
    public string Key         { get; } = nameof(UpdatePlugins);
    public string Description { get; } = "Installs new releases for plugins marked auto-update, checks the rest, and records the result on the plugin page.";
    public string Category    { get; } = "Github Repo Plugins Update";
}
