using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.Api;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

[Collection("UpdateRuns")]
public class ApiTests
{
    [Fact]
    public void ApiVersion_StartsAtOne_AndTheMinimumNeverExceedsIt()
    {
        Assert.Equal(1, ApiVersions.Current);
        Assert.True(ApiVersions.MinSupported <= ApiVersions.Current);
    }

    [Fact]
    public void ErrorsCarryAStatus_ATitleAndASentence()
    {
        var error = ApiError.NotFound("That plugin is not in the list.");
        var body  = Assert.IsType<ErrorBody>(error.Body);

        Assert.Equal(404, error.Status);
        Assert.Equal(404, body.Status);
        Assert.Equal("NotFound", body.Title);
        Assert.Equal("That plugin is not in the list.", body.Detail);
        Assert.Equal(403, ApiError.Disabled().Status);
        Assert.Equal(403, ApiError.ManageDisabled().Status);
        Assert.Equal(409, ApiError.RunInProgress().Status);
        Assert.Equal(202, ApiResult.Accepted(new object()).Status);
    }

    [Fact]
    public void APluginIsShownWithItsStatusAsAName_AndWhetherARestartIsPending()
    {
        var repo = new ReposToProcess
                   {
                       Url = "https://github.com/alex/Cool.Plugin", AutoUpdate = false, LatestVersion = "v2",
                       LastVersionDownloaded = "v1", FileName = "Cool.Plugin.dll", LatestAssets = "a.dll|b.zip"
                   };

        var dto = PluginDto.From(repo, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cool.plugin.dll" });

        Assert.Equal("UpdateWaiting", dto.Status);
        Assert.Equal("Cool.Plugin", dto.Name);
        Assert.Equal("alex", dto.Owner);
        Assert.Equal("v1", dto.InstalledVersion);
        Assert.Equal(new[] { "a.dll", "b.zip" }, dto.AssetNames);
        Assert.True(dto.RestartPending);
        Assert.Null(dto.Backups);
    }

    [Fact]
    public void EveryRepoStatus_HasAStableName()
    {
        // The phone app maps these names, so renaming one is a breaking change
        Assert.Equal(new[] { "NotChecked", "NoRelease", "UpToDate", "UpdateAvailable", "UpdateWaiting", "Failed" },
                     Enum.GetNames(typeof(RepoStatus)));
    }

    [Fact]
    public async Task ARun_RaisesStartedProgressAndCompletedEvents_AndAnUpdateRaisesRestartPending()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1", fileName: "Cool.Plugin.dll");
        System.IO.File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old");
        env.Publish("Cool.Plugin", "v2");

        var seen = new List<(string Name, object Data)>();
        RunEvents.Sink = (n, d) => seen.Add((n, d));
        try
        {
            await env.Service.RunAsync(RepoUpdateService.TriggerManual, RunMode.All);
        }
        finally
        {
            RunEvents.Sink = null;
        }

        Assert.Equal(new[] { PushEvents.RunStarted, PushEvents.RunProgress, PushEvents.RunCompleted, PushEvents.RestartPending },
                     seen.Select(s => s.Name).ToArray());
        var done = (RunEventData)seen.Single(s => s.Name == PushEvents.RunCompleted).Data;
        Assert.Equal(1, done.Updated);
        Assert.Equal(new[] { "Cool.Plugin" }, done.UpdatedPlugins);
    }

    [Fact]
    public async Task ASinkThatThrows_NeverBreaksARun()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1", fileName: "Cool.Plugin.dll");
        env.Publish("Cool.Plugin", "v1");

        RunEvents.Sink = (n, d) => throw new InvalidOperationException("nobody is listening");
        try
        {
            var run = await env.Service.RunAsync(RepoUpdateService.TriggerManual, RunMode.All);
            Assert.Single(run.Results);
        }
        finally
        {
            RunEvents.Sink = null;
        }
    }

    [Fact]
    public void ManagementChanges_AreAnnounced()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin");
        var store = env.NewStore();
        var options = store.GetOptions();
        var service = new PluginManagementService(env.Host, env.Logger, () => options, () => store.SetOptions(options));

        var seen = new List<string>();
        RunEvents.Sink = (n, d) => seen.Add(n + ":" + ((ChangeEventData)d).Reason);
        try
        {
            service.SetAutoUpdate(repo.Id, false);
        }
        finally
        {
            RunEvents.Sink = null;
        }

        Assert.Equal(new[] { PushEvents.PluginsChanged + ":SettingsChanged" }, seen);
    }
}
