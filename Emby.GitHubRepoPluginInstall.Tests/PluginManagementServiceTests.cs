using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.Services;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

/// <summary>
/// The service on its own, working straight on the saved options the way the phone API will:
/// a fresh copy is read for every call and written back after the change.
/// </summary>
[Collection("UpdateRuns")]
public class PluginManagementServiceTests
{
    private static PluginManagementService NewService(TestEnv env)
    {
        var store   = env.NewStore();
        var options = store.GetOptions();
        return new PluginManagementService(env.Host, env.Logger, () => options, () => store.SetOptions(options), null,
                                           t => new GithubAPI.GitHubApiClient(t, new System.Net.Http.HttpClient(env.Handler), env.Json, env.Logger));
    }

    [Fact]
    public void SetAutoUpdate_IsSaved_AndFlipsWhenNoValueIsGiven()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", auto: true);

        var off = NewService(env).SetAutoUpdate(repo.Id);
        Assert.True(off.Ok);
        Assert.False(env.Repo("Cool.Plugin").AutoUpdate);

        NewService(env).SetAutoUpdate(repo.Id, true);
        Assert.True(env.Repo("Cool.Plugin").AutoUpdate);
    }

    [Fact]
    public async Task AnUnknownPlugin_IsNotFound_AndNothingIsSaved()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");

        Assert.Equal(ManageOutcome.NotFound, NewService(env).SetAutoUpdate("nope").Outcome);
        Assert.Equal(ManageOutcome.NotFound, NewService(env).RemoveRepo("nope").Outcome);
        Assert.Equal(ManageOutcome.NotFound, (await NewService(env).RestoreAsync("nope")).Outcome);
        Assert.Single(env.Options.Repos);
    }

    [Fact]
    public void UpdateSettings_ChangesOnlyTheFieldsGiven()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", auto: true);

        NewService(env).UpdateSettings(repo.Id, autoUpdate: null, allowPreRelease: true, assetNameFilter: "x64");

        var saved = env.Repo("Cool.Plugin");
        Assert.True(saved.AutoUpdate);
        Assert.True(saved.GetPreRelease);
        Assert.Equal("x64", saved.AssetNameFilter);
    }

    [Fact]
    public void RemovingAPluginWithAFile_DeletesIt_AndAsksForARestart()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "build");

        var result = NewService(env).RemoveRepo(repo.Id);

        Assert.True(result.RestartNeeded);
        Assert.False(File.Exists(env.PluginFile("Cool.Plugin.dll")));
        Assert.DoesNotContain(env.Options.Repos, r => r.Repository == "Cool.Plugin");
        Assert.Contains("NotifyPendingRestart", env.HostState.Calls);
    }

    [Fact]
    public void RemovingAPluginThatWasNeverInstalled_NeedsNoRestart()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin");

        var result = NewService(env).RemoveRepo(repo.Id);

        Assert.True(result.Ok);
        Assert.False(result.RestartNeeded);
        Assert.DoesNotContain("NotifyPendingRestart", env.HostState.Calls);
    }

    [Fact]
    public async Task Restore_PutsTheOldFileBack_AndTurnsAutoUpdateOff()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "the good old build");
        env.Publish("Cool.Plugin", "v2.0.0");
        await env.Service.RunAsync(RepoUpdateService.TriggerManual, RunMode.All);

        var result = await NewService(env).RestoreAsync(repo.Id);

        Assert.True(result.Ok);
        Assert.Equal("the good old build", File.ReadAllText(env.PluginFile("Cool.Plugin.dll")));
        Assert.False(env.Repo("Cool.Plugin").AutoUpdate);
        Assert.Equal("v1.0.0", env.Repo("Cool.Plugin").LastVersionDownloaded);
    }

    [Fact]
    public async Task Restore_WithNoBackup_IsRefused()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");

        var result = await NewService(env).RestoreAsync(repo.Id);

        Assert.Equal(ManageOutcome.Refused, result.Outcome);
        Assert.Contains("No backup", result.Message);
    }

    [Fact]
    public async Task ApplyRepo_AddsTheEntry_AndChecksItStraightAway()
    {
        using var env = new TestEnv();
        env.Publish("Cool.Plugin", "v1.0.0");

        var result = await NewService(env).ApplyRepoAsync(new RepoSettings { Url = "alex/Cool.Plugin", AutoUpdate = true });

        Assert.True(result.Ok);
        var saved = env.Repo("Cool.Plugin");
        Assert.True(saved.AutoUpdate);
        Assert.Equal("v1.0.0", saved.LatestVersion);
    }

    [Fact]
    public async Task ApplyRepo_RejectsABadAddress_AndADuplicate()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");

        var bad = await NewService(env).ApplyRepoAsync(new RepoSettings { Url = "not a repo" });
        var dup = await NewService(env).ApplyRepoAsync(new RepoSettings { Url = "https://github.com/alex/Cool.Plugin" });

        Assert.Equal(ManageOutcome.Invalid, bad.Outcome);
        Assert.Equal(ManageOutcome.Duplicate, dup.Outcome);
        Assert.Single(env.Options.Repos);
    }

    [Fact]
    public void Registries_CanBeAddedToggledAndRemoved_ButTheBuiltInOneStays()
    {
        using var env = new TestEnv();
        var added = NewService(env).UpsertRegistry(new RegistrySettings { Name = "Mine", RawUrl = "https://example.com/plugins.json" });
        Assert.True(added.Ok);
        var id = env.Options.PluginRegistries.Single(r => r.Name == "Mine").Id;

        NewService(env).ToggleRegistry(id, false);
        Assert.False(env.Options.PluginRegistries.Single(r => r.Id == id).Enabled);

        Assert.True(NewService(env).RemoveRegistry(id).Ok);
        Assert.DoesNotContain(env.Options.PluginRegistries, r => r.Id == id);

        env.NewStore().SetOptions(WithBuiltIn(env));
        Assert.Equal(ManageOutcome.Refused, NewService(env).RemoveRegistry(PluginManagementService.BuiltInRegistryId).Outcome);
    }

    private static Models.PluginUIOptions WithBuiltIn(TestEnv env)
    {
        var options = env.NewStore().GetOptions();
        options.PluginRegistries.Add(new Models.PluginRegistry { Id = PluginManagementService.BuiltInRegistryId, Name = "Built-in", RawUrl = "x", Enabled = true });
        return options;
    }
}
