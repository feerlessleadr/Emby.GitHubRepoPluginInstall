using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Emby.GitHubRepoPluginInstall.GithubAPI;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.UI;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

/// <summary>Opens the plugin page against a fake server and presses its buttons.</summary>
[Collection("UpdateRuns")]
public class MainPageViewTests
{
    private const string SelfOwnerRepo = "feerlessleadr/Emby.GitHubRepoPluginInstall";

    private static MainPageView OpenPage(TestEnv env)
    {
        // The installer's own repository is added on first open; keep GitHub quiet about it
        env.Handler.OnJson($"/repos/{SelfOwnerRepo}/releases", "[]");

        var info = new PluginInfo { Id = Guid.NewGuid().ToString() };
        return new MainPageView(info, env.NewStore(), env.Logger, env.Host, env.Json,
                                token => new GitHubApiClient(token, new HttpClient(env.Handler), env.Json, env.Logger));
    }

    private static async Task Press(MainPageView view, string command, string itemId = null)
    {
        await view.RunCommand(itemId, command, null);
    }


    private static async Task WaitForRunToFinish()
    {
        for (var i = 0; i < 200; i++)
        {
            if (!RepoUpdateService.GetProgress().IsRunning) { await Task.Delay(50); return; }
            await Task.Delay(25);
        }

        throw new TimeoutException("The update run did not finish");
    }

    // ---- Opening the page -------------------------------------------------------------------------------------

    [Fact]
    public void FirstOpen_OffersSelfUpdate_AndTheBuiltInRegistry()
    {
        using var env = new TestEnv();
        var view = OpenPage(env);

        var self = Assert.Single(view.PluginUiOptions.Repos);
        Assert.Equal("Emby.GitHubRepoPluginInstall", self.Repository);
        Assert.True(self.AutoUpdate);
        Assert.Equal("embedded-default", Assert.Single(view.PluginUiOptions.PluginRegistries).Id);
        Assert.True(env.Options.SelfEntryHandled);
    }

    [Fact]
    public void OpeningThePage_DoesNotCallGitHub()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");
        env.Publish("Cool.Plugin", "v1.0.0");

        OpenPage(env);

        Assert.Empty(env.Handler.Requests);
    }

    [Fact]
    public void SelfUpdate_IsOfferedOnce_NotAgainAfterTheUserRemovesIt()
    {
        using var env = new TestEnv();
        var first = OpenPage(env);
        var options = env.NewStore().GetOptions();
        options.Repos.Clear();
        env.NewStore().SetOptions(options);

        var second = OpenPage(env);

        Assert.Empty(second.PluginUiOptions.Repos);
    }

    [Fact]
    public void ExistingInstalls_GetTheSelfEntryToo_OnTheirNextVisit()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");   // an install from before self-update was tracked this way

        var view = OpenPage(env);

        Assert.Equal(2, view.PluginUiOptions.Repos.Count);
    }

    [Fact]
    public void ADifferentOwnersCopyOfTheInstaller_IsNotDuplicated()
    {
        using var env = new TestEnv();
        var store = env.NewStore();
        var options = store.GetOptions();
        options.Repos.Add(new ReposToProcess { Url = "https://github.com/someone-else/Emby.GitHubRepoPluginInstall" });
        store.SetOptions(options);

        var view = OpenPage(env);

        Assert.Single(view.PluginUiOptions.Repos);
        Assert.Equal("someone-else", view.PluginUiOptions.Repos[0].Owner);
    }

    // The registry may list the same plugin under another owner (upstream or a fork). Adding both would make two
    // entries install the same file, so the picker leaves it out.
    [Fact]
    public async Task AddDialog_DoesNotOfferAPluginAlreadyListedUnderAnotherOwner()
    {
        using var env = new TestEnv();
        env.Handler.OnJson("raw.githubusercontent.com", @"{""name"":""R"",""plugins"":[
            {""name"":""Installer (upstream)"",""url"":""https://github.com/someone-else/Emby.GitHubRepoPluginInstall""},
            {""name"":""Other"",""url"":""https://github.com/someone-else/Emby.Other""}]}");
        var view = OpenPage(env);   // the list now holds feerlessleadr/Emby.GitHubRepoPluginInstall

        var dialog = (RepoConfigDialogView)await view.RunCommand(null, "Add", null);

        var offered = dialog.RepoConfigUi.AvailablePlugins.Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "Other" }, offered);
    }

    [Fact]
    public void TheNewPage_ShowsASummary_ThePluginCards_AndNoBanner()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");

        var view = OpenPage(env);
        var o = view.PluginUiOptions;

        Assert.StartsWith("No runs yet", Assert.Single(o.RunSummary).PrimaryText);
        Assert.Equal(2, o.Releases.Count);
        Assert.Empty(o.RestartBanner);
        Assert.Null(o.RestartFlag);
        Assert.Empty(o.RunHistory);
    }

    [Fact]
    public void ThePageModel_BuildsItsButtonsAndLists()
    {
        var options = new PluginUIOptions();

        Assert.NotNull(options.Add);
        Assert.NotNull(options.UpdateAll);
        Assert.NotNull(options.AddRegistry);
        Assert.NotNull(options.TestTokenBtn);
        Assert.NotNull(options.Releases);
        Assert.NotNull(options.Registries);
    }

    // ---- Buttons ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CheckForUpdates_FillsInTheStatusOfEveryPlugin()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0");
        env.Publish("Cool.Plugin", "v2.0.0");
        var view = OpenPage(env);

        await Press(view, "CheckAll");

        var card = view.PluginUiOptions.Releases.Single(i => i.PrimaryText == "Cool.Plugin");
        Assert.Contains("Installed v1.0.0 → v2.0.0", card.SecondaryText);
        Assert.Equal("Download", card.Button1.Data1);
        Assert.Contains("Checked 2 plugin(s)", view.PluginUiOptions.Logs.Caption);
        Assert.Contains("1 with an update available", view.PluginUiOptions.Logs.Caption);

        // looking is not installing
        Assert.Equal("v1.0.0", env.Repo("Cool.Plugin").LastVersionDownloaded);
    }

    [Fact]
    public async Task UpdateButtonOnACard_InstallsThatPlugin_AndShowsTheRestartBanner()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old");
        env.Publish("Cool.Plugin", "v2.0.0");
        var view = OpenPage(env);

        await Press(view, "Download", repo.Id);

        var o = view.PluginUiOptions;
        Assert.Contains("updated to v2.0.0", o.Logs.Caption);
        var banner = Assert.Single(o.RestartBanner);
        Assert.Contains("Cool.Plugin v1.0.0 → v2.0.0", banner.SecondaryText);
        Assert.NotNull(o.RestartFlag);
        Assert.Equal("v2.0.0", env.Repo("Cool.Plugin").LastVersionDownloaded);
        Assert.Contains("Manual", Assert.Single(o.RunHistory).PrimaryText);
        Assert.Contains("Updated v1.0.0 → v2.0.0", o.RunHistory[0].SubItems[0].PrimaryText.Replace("<br/>", " "));
    }

    [Fact]
    public async Task AFailedUpdate_SaysWhyOnThePage()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Gone.Plugin");
        env.Handler.OnJson("/repos/alex/Gone.Plugin/releases", "{}", HttpStatusCode.NotFound);
        var view = OpenPage(env);

        await Press(view, "Download", repo.Id);

        var reason = view.PluginUiOptions.Releases.Single(i => i.PrimaryText == "Gone.Plugin").SubItems[0].PrimaryText;
        Assert.Contains("Repository not found", reason.Replace("<br/>", " "));
        var card = view.PluginUiOptions.Releases.Single(i => i.PrimaryText == "Gone.Plugin");
        Assert.Equal(ItemStatus.Failed, card.Status);
        Assert.Equal("Retry", card.Button1.Caption);
    }

    [Fact]
    public async Task RunNow_WorksInTheBackground_AndTheSummaryShowsTheResult()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");
        env.Publish("Cool.Plugin", "v1.0.0");
        var view = OpenPage(env);

        await Press(view, "RunNow");
        await WaitForRunToFinish();
        await Press(view, "RefreshView");

        Assert.False(view.PluginUiOptions.Logs.IsVisible, "the 'run started' message should be gone after Refresh");
        var summary = Assert.Single(view.PluginUiOptions.RunSummary);
        Assert.Contains("Last run", summary.PrimaryText);
        Assert.Contains("1 updated", summary.SecondaryText);
        Assert.Single(view.PluginUiOptions.RestartBanner);
        Assert.Single(view.PluginUiOptions.RunHistory);
    }

    // ---- "Update N plugins" above the cards: only the plugins that have an update ----------------------------

    // Four plugins in four situations. Only the two that show an update may be touched.
    private static async Task<(ReposToProcess withUpdate, ReposToProcess manual, ReposToProcess current, ReposToProcess unchecked_, ReposToProcess broken)>
        SetUpMixedPlugins(TestEnv env)
    {
        var a = env.AddRepo("Has.Update", installed: "v1", fileName: "Has.Update.dll");
        var m = env.AddRepo("Manual.Update", auto: false, installed: "v1", fileName: "Manual.Update.dll");
        var c = env.AddRepo("Is.Current", installed: "v2", fileName: "Is.Current.dll");
        var u = env.AddRepo("Never.Checked");
        var b = env.AddRepo("Is.Broken", installed: "v1", fileName: "Is.Broken.dll");
        foreach (var f in new[] { "Has.Update.dll", "Manual.Update.dll", "Is.Current.dll", "Is.Broken.dll" })
            File.WriteAllText(env.PluginFile(f), "old " + f);

        env.Publish("Has.Update", "v2");
        env.Publish("Manual.Update", "v2");
        env.Publish("Is.Current", "v2");
        env.Publish("Never.Checked", "v9");            // a newer release exists, but nothing has looked yet
        env.Handler.OnJson("/repos/alex/Is.Broken/releases", "{}", HttpStatusCode.NotFound);

        // look at everything except Never.Checked, so it has no status yet
        foreach (var id in new[] { a.Id, m.Id, c.Id, b.Id })
            await env.Service.RunAsync(RepoUpdateService.TriggerManual, RunMode.CheckOnly, id);

        return (a, m, c, u, b);
    }

    [Fact]
    public async Task TheUpdateButton_IsHiddenAtFirst_AndAppearsOnceAnUpdateIsFound()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1");
        env.Publish("Cool.Plugin", "v2");
        var view = OpenPage(env);
        Assert.False(view.PluginUiOptions.UpdateAvailableBtn.IsVisible);

        await Press(view, "CheckAll");

        var button = view.PluginUiOptions.UpdateAvailableBtn;
        Assert.True(button.IsVisible);
        Assert.Equal("Update 1 plugin", button.Caption);
        Assert.Contains("Cool.Plugin", button.ConfirmationPrompt);
    }

    [Fact]
    public async Task TheUpdateButton_UpdatesOnlyPluginsThatHaveAnUpdate()
    {
        using var env = new TestEnv();
        var (a, m, c, u, b) = await SetUpMixedPlugins(env);
        var view = OpenPage(env);
        Assert.Equal("Update 2 plugins", view.PluginUiOptions.UpdateAvailableBtn.Caption);
        var requestsBefore = env.Handler.Requests.Count;

        await Press(view, "UpdateAvailable");
        await WaitForRunToFinish();

        // the two with updates were installed, including the one with auto-update off
        Assert.Equal("v2", env.Repo("Has.Update").LastVersionDownloaded);
        Assert.Equal("v2", env.Repo("Manual.Update").LastVersionDownloaded);
        Assert.NotEqual("old Has.Update.dll", File.ReadAllText(env.PluginFile("Has.Update.dll")));
        Assert.NotEqual("old Manual.Update.dll", File.ReadAllText(env.PluginFile("Manual.Update.dll")));

        // everything else was left exactly as it was, and not even contacted
        Assert.Equal("v2", env.Repo("Is.Current").LastVersionDownloaded);
        Assert.Equal("old Is.Current.dll", File.ReadAllText(env.PluginFile("Is.Current.dll")));
        Assert.Null(env.Repo("Never.Checked").LastVersionDownloaded);
        Assert.False(File.Exists(env.PluginFile("Never.Checked.dll")));
        Assert.Equal("v1", env.Repo("Is.Broken").LastVersionDownloaded);
        Assert.Equal("old Is.Broken.dll", File.ReadAllText(env.PluginFile("Is.Broken.dll")));

        var after = env.Handler.Requests.Skip(requestsBefore).ToList();
        Assert.All(after, r => Assert.True(r.Contains("Has.Update") || r.Contains("Manual.Update"), "unexpected request: " + r));
    }

    [Fact]
    public async Task TheUpdateButton_RecordsOneRunWithJustThosePlugins()
    {
        using var env = new TestEnv();
        await SetUpMixedPlugins(env);
        var view = OpenPage(env);

        await Press(view, "UpdateAvailable");
        await WaitForRunToFinish();

        var run = Assert.Single(env.NewState().Load().History);
        Assert.Equal("Manual", run.Trigger);
        Assert.Equal(new[] { "Has.Update", "Manual.Update" }, run.Results.Select(r => r.Name).OrderBy(n => n).ToArray());
        Assert.All(run.Results, r => Assert.Equal(RepoOutcome.Updated, r.Outcome));
    }

    [Fact]
    public async Task AfterTheUpdateButtonRuns_ItDisappears_AndTheRestartBannerShowsWhatChanged()
    {
        using var env = new TestEnv();
        await SetUpMixedPlugins(env);
        var view = OpenPage(env);

        await Press(view, "UpdateAvailable");
        await WaitForRunToFinish();
        await Press(view, "RefreshView");

        Assert.False(view.PluginUiOptions.UpdateAvailableBtn.IsVisible);
        var banner = Assert.Single(view.PluginUiOptions.RestartBanner);
        Assert.Equal("2 plugins changed, listed below", banner.SecondaryText);
    }

    [Fact]
    public async Task TheUpdateButton_WithNothingToUpdate_SaysSo_AndStartsNoRun()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1");
        env.Publish("Cool.Plugin", "v1");
        var view = OpenPage(env);
        await Press(view, "CheckAll");
        var requestsBefore = env.Handler.Requests.Count;

        await Press(view, "UpdateAvailable");

        Assert.Contains("No plugins have an update", view.PluginUiOptions.Logs.Caption);
        Assert.Equal(requestsBefore, env.Handler.Requests.Count);
        Assert.False(RepoUpdateService.GetProgress().IsRunning);
    }

    [Fact]
    public async Task UpdateEverything_AlsoInstallsPluginsWithAutoUpdateOff()
    {
        using var env = new TestEnv();
        env.AddRepo("Manual.Plugin", auto: false);
        env.Publish("Manual.Plugin", "v1.0.0");
        var view = OpenPage(env);

        await Press(view, "UpdateAll");
        await WaitForRunToFinish();

        Assert.Equal("v1.0.0", env.Repo("Manual.Plugin").LastVersionDownloaded);
    }

    [Fact]
    public async Task DismissingTheBanner_ClearsIt()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin");
        env.Publish("Cool.Plugin", "v1.0.0");
        var view = OpenPage(env);
        await Press(view, "Download", repo.Id);
        Assert.NotEmpty(view.PluginUiOptions.RestartBanner);

        await Press(view, "DismissRestart");

        Assert.Empty(view.PluginUiOptions.RestartBanner);
        Assert.Null(view.PluginUiOptions.RestartFlag);
    }

    [Fact]
    public async Task RestartNow_AsksTheServerToRestart()
    {
        using var env = new TestEnv();
        var view = OpenPage(env);

        await Press(view, "RestartNow");

        Assert.Equal(new[] { "Restart" }, env.HostState.Calls.ToArray());
    }

    // ---- Restore and remove -----------------------------------------------------------------------------------

    [Fact]
    public async Task RestorePreviousVersion_PutsTheOldFileBack_AndStopsAutoUpdateFromUndoingIt()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "the good old build");
        env.Publish("Cool.Plugin", "v2.0.0");
        var view = OpenPage(env);
        await Press(view, "Download", repo.Id);

        await Press(view, "RestoreBackup", repo.Id);

        Assert.Equal("the good old build", File.ReadAllText(env.PluginFile("Cool.Plugin.dll")));
        var saved = env.Repo("Cool.Plugin");
        Assert.Equal("v1.0.0", saved.LastVersionDownloaded);
        Assert.False(saved.AutoUpdate);
        Assert.Contains("Auto-update is now off", view.PluginUiOptions.RepoLogs.Caption);

        // a scheduled run must not put v2 straight back
        var run = await env.Service.RunAsync(RepoUpdateService.TriggerScheduled, RunMode.Auto);
        Assert.Equal(RepoOutcome.WaitingManual, run.Results.Single(r => r.Name == "Cool.Plugin").Outcome);
        Assert.Equal("the good old build", File.ReadAllText(env.PluginFile("Cool.Plugin.dll")));
    }

    // Updating and then rolling back before a restart leaves the file matching what is still running.
    [Fact]
    public async Task RestoringRightAfterAnUpdate_NeedsNoRestart()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old");
        env.Publish("Cool.Plugin", "v2.0.0");
        var view = OpenPage(env);
        await Press(view, "Download", repo.Id);
        Assert.NotEmpty(view.PluginUiOptions.RestartBanner);
        env.HostState.Calls.Clear();

        await Press(view, "RestoreBackup", repo.Id);

        Assert.Empty(view.PluginUiOptions.RestartBanner);
        Assert.Null(view.PluginUiOptions.RestartFlag);
        Assert.Empty(env.HostState.Calls);
    }

    // After a restart the server is running v2. Restoring v1 then really does need another restart.
    [Fact]
    public async Task RestoringAfterARestart_AsksForARestartToGoBack()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old");
        env.Publish("Cool.Plugin", "v2.0.0");
        var view = OpenPage(env);
        await Press(view, "Download", repo.Id);
        env.NewState().ClearPendingRestart();       // the server has since restarted and is running v2
        env.HostState.Calls.Clear();

        await Press(view, "RestoreBackup", repo.Id);

        var banner = Assert.Single(view.PluginUiOptions.RestartBanner);
        Assert.Contains("Cool.Plugin v2.0.0 → v1.0.0", banner.SecondaryText);
        Assert.Equal(new[] { "NotifyPendingRestart" }, env.HostState.Calls.ToArray());
    }

    [Fact]
    public async Task RestorePreviousVersion_WithNoBackup_ExplainsThereIsNone()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "current");
        var view = OpenPage(env);

        await Press(view, "RestoreBackup", repo.Id);

        Assert.Contains("No backup of Cool.Plugin", view.PluginUiOptions.RepoLogs.Caption);
        Assert.Equal("current", File.ReadAllText(env.PluginFile("Cool.Plugin.dll")));
        Assert.True(env.Repo("Cool.Plugin").AutoUpdate);
    }

    [Theory]
    [InlineData("RestoreBackup")]
    [InlineData("Remove")]
    [InlineData("Edit")]
    [InlineData("ToggleAuto")]
    [InlineData("Download")]
    public async Task ACardThatNoLongerExists_GetsAFriendlyMessage_NotACrash(string command)
    {
        using var env = new TestEnv();
        var view = OpenPage(env);
        var before = env.Options.Repos.Count;

        await Press(view, command, "not-a-real-id");

        Assert.Contains("Could not find that plugin", view.PluginUiOptions.Logs.Caption);
        Assert.Equal(before, env.Options.Repos.Count);
    }

    [Fact]
    public async Task Remove_DeletesThePluginFile_AndTheEntry()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin", installed: "v1", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "x");
        var view = OpenPage(env);

        await Press(view, "Remove", repo.Id);

        Assert.False(File.Exists(env.PluginFile("Cool.Plugin.dll")));
        Assert.DoesNotContain(env.Options.Repos, r => r.Repository == "Cool.Plugin");
        Assert.Equal(new[] { "NotifyPendingRestart" }, env.HostState.Calls.ToArray());
    }

    // ---- Per-card actions (no table, nothing to select first) -------------------------------------------------

    [Fact]
    public async Task EditOnACard_OpensTheDialogForThatPlugin()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin");
        var view = OpenPage(env);

        var dialog = Assert.IsType<RepoConfigDialogView>(await view.RunCommand(repo.Id, "Edit", null));

        Assert.Equal(repo.Id, dialog.RepoConfigUi.Id);
        Assert.Equal("https://github.com/alex/Cool.Plugin", dialog.RepoConfigUi.Url);
    }

    [Fact]
    public async Task TheAutoUpdateSwitch_FlipsThatPluginAndSavesIt()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin");
        var view = OpenPage(env);

        await Press(view, "ToggleAuto", repo.Id);
        Assert.False(env.Repo("Cool.Plugin").AutoUpdate);
        Assert.Contains("Auto-update is now off for Cool.Plugin", view.PluginUiOptions.Logs.Caption);

        await Press(view, "ToggleAuto", repo.Id);
        Assert.True(env.Repo("Cool.Plugin").AutoUpdate);
        Assert.Contains("now on", view.PluginUiOptions.Logs.Caption);
    }

    [Fact]
    public async Task TheAutoUpdateSwitch_OnlyTouchesItsOwnPlugin()
    {
        using var env = new TestEnv();
        var a = env.AddRepo("A.Plugin");
        env.AddRepo("B.Plugin");
        var view = OpenPage(env);

        await Press(view, "ToggleAuto", a.Id);

        Assert.False(env.Repo("A.Plugin").AutoUpdate);
        Assert.True(env.Repo("B.Plugin").AutoUpdate);
    }

    [Fact]
    public async Task TheCardShowsTheSwitchStateAfterItIsFlipped()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin");
        var view = OpenPage(env);

        await Press(view, "ToggleAuto", repo.Id);

        var card = view.PluginUiOptions.Releases.Single(i => i.PrimaryText == "Cool.Plugin");
        var row = card.SubItems.Single(s => s.Toggle != null);
        Assert.False(row.Toggle.IsChecked);
        Assert.Equal("ToggleAuto", row.Toggle.Data1);
        Assert.Equal(repo.Id, row.Toggle.Data2);
    }

    [Fact]
    public void EveryCardHasAManageMenu_WhoseItemsPointAtThatPlugin()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Cool.Plugin");
        var view = OpenPage(env);

        var card = view.PluginUiOptions.Releases.Single(i => i.PrimaryText == "Cool.Plugin");
        var menu = card.Button2.SubMenuButtons;

        Assert.Equal(new[] { "Edit", "RestoreBackup", "Remove" }, menu.Select(m => m.Data1).ToArray());
        Assert.All(menu, m => Assert.Equal(repo.Id, m.Data2));
        Assert.Null(menu[0].ConfirmationPrompt);                                  // editing needs no confirmation
        Assert.Contains("Cool.Plugin", menu[1].ConfirmationPrompt);               // undoing and deleting do
        Assert.Contains("Cool.Plugin", menu[2].ConfirmationPrompt);
    }

    [Fact]
    public void ThePageHasNoTables_SoThereIsNothingToSelectFirst()
    {
        var properties = typeof(PluginUIOptions).GetProperties().Select(p => p.PropertyType.Name);

        Assert.DoesNotContain("DxDataGrid", properties);
    }

    // ---- Registries as cards ----------------------------------------------------------------------------------

    [Fact]
    public async Task RegistryCards_ShowTheBuiltInOneWithoutAMenu_AndCustomOnesWithOne()
    {
        using var env = new TestEnv();
        var view = OpenPage(env);
        view.PluginUiOptions.PluginRegistries.Add(new PluginRegistry { Id = "mine", Name = "My list", RawUrl = "https://example.com/p.json" });
        await Press(view, "RefreshView");
        view.PluginUiOptions.Registries = StatusLists.BuildRegistries(view.PluginUiOptions.PluginRegistries);

        var builtIn = view.PluginUiOptions.Registries.Single(r => r.Tag?.Text == "Built-in");
        var custom  = view.PluginUiOptions.Registries.Single(r => r.PrimaryText == "My list");

        Assert.Null(builtIn.Button2);
        Assert.Equal("Community plugins", builtIn.PrimaryText);
        Assert.Equal("https://example.com/p.json", custom.SecondaryText);
        Assert.Equal(new[] { "EditRegistry", "RemoveRegistry" }, custom.Button2.SubMenuButtons.Select(b => b.Data1).ToArray());
        Assert.All(custom.Button2.SubMenuButtons, b => Assert.Equal("mine", b.Data2));
    }

    [Fact]
    public async Task TheRegistrySwitch_TurnsAListOnAndOff_AndSavesIt()
    {
        using var env = new TestEnv();
        var view = OpenPage(env);

        await Press(view, "ToggleRegistry", "embedded-default");

        Assert.False(env.Options.PluginRegistries.Single(r => r.Id == "embedded-default").Enabled);
        var card = view.PluginUiOptions.Registries.Single();
        Assert.False(card.Toggle.IsChecked);
    }

    [Fact]
    public async Task RemovingACustomRegistry_AndTheBuiltInOneIsProtected()
    {
        using var env = new TestEnv();
        var view = OpenPage(env);
        view.PluginUiOptions.PluginRegistries.Add(new PluginRegistry { Id = "mine", Name = "My list", RawUrl = "https://example.com/p.json" });

        await Press(view, "RemoveRegistry", "mine");
        Assert.DoesNotContain(env.Options.PluginRegistries, r => r.Id == "mine");
        Assert.DoesNotContain(view.PluginUiOptions.Registries, r => r.PrimaryText == "My list");

        await Press(view, "RemoveRegistry", "embedded-default");
        Assert.Contains(env.Options.PluginRegistries, r => r.Id == "embedded-default");
        Assert.Contains("can't be removed", view.PluginUiOptions.RegistryLogs.Caption);
    }

    [Fact]
    public async Task EditingACustomRegistry_OpensItsDialog()
    {
        using var env = new TestEnv();
        var view = OpenPage(env);
        view.PluginUiOptions.PluginRegistries.Add(new PluginRegistry { Id = "mine", Name = "My list", RawUrl = "https://example.com/p.json" });

        var dialog = Assert.IsType<RegistryConfigDialogView>(await view.RunCommand("mine", "EditRegistry", null));

        Assert.Equal("My list", dialog.RegistryConfigUi.Name);
    }

    // ---- Settings and token -----------------------------------------------------------------------------------

    [Fact]
    public async Task SavingSettings_KeepsTheTokenInTheField_AndEncryptedOnDisk()
    {
        using var env = new TestEnv(token: null);
        var view = OpenPage(env);
        view.PluginUiOptions.GitHubToken = "ghp_newtoken";
        view.PluginUiOptions.RestartServerAfterInstall = true;

        await Press(view, "Save");

        Assert.Equal("ghp_newtoken", view.PluginUiOptions.GitHubToken);
        Assert.Equal("ghp_newtoken", env.Options.GitHubToken);
        Assert.True(env.Options.RestartServerAfterInstall);
        Assert.StartsWith("v2:", env.Options.EncryptedGitHubToken);
        Assert.DoesNotContain("ghp_newtoken", File.ReadAllText(Path.Combine(env.Config, GitHubRepPluginInstall.PluginName + ".json")));
    }

    [Fact]
    public async Task ClearingTheTokenField_AndSaving_RemovesTheSavedToken()
    {
        using var env = new TestEnv(token: "ghp_old");
        var view = OpenPage(env);
        view.PluginUiOptions.GitHubToken = "";

        await Press(view, "Save");

        Assert.True(string.IsNullOrEmpty(env.Options.GitHubToken));
        Assert.True(string.IsNullOrEmpty(env.Options.EncryptedGitHubToken));
    }

    [Fact]
    public async Task TestToken_ShowsTheResultNextToTheField()
    {
        using var env = new TestEnv();
        env.Handler.OnJson("/rate_limit", "{}", HttpStatusCode.OK, r =>
        {
            r.Headers.Add("X-RateLimit-Limit", "5000");
            r.Headers.Add("X-RateLimit-Remaining", "4999");
            r.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());
        });
        var view = OpenPage(env);

        await Press(view, "TestToken");

        var result = Assert.Single(view.PluginUiOptions.TokenStatus);
        Assert.Equal(ItemStatus.Succeeded, result.Status);
        Assert.Contains("Token works", result.PrimaryText);
    }

    // Found on a real Emby server: the long "classic token" warning was cut off because captions show one line.
// Found on a real Emby server: a caption shows one line, so the long "classic token" warning was cut off.    [Fact]    public async Task TestToken_ALongResultIsKeptWhole_InAListItem_NotACaption()    {        using var env = new TestEnv();        env.Handler.OnJson("/rate_limit", "{}", HttpStatusCode.OK, r =>        {            r.Headers.Add("X-RateLimit-Limit", "5000");            r.Headers.Add("X-RateLimit-Remaining", "4999");            r.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString());            r.Headers.Add("X-OAuth-Scopes", "repo");        });        var view = OpenPage(env);        await Press(view, "TestToken");        var text = Assert.Single(view.PluginUiOptions.TokenStatus).PrimaryText;        Assert.Contains("fine-grained", text);        Assert.DoesNotContain("<br/>", text);    }

    [Fact]
    public async Task AFailedUpdate_KeepsTheCaptionShort_AndLeavesTheReasonToTheCard()
    {
        using var env = new TestEnv();
        var repo = env.AddRepo("Gone.Plugin");
        env.Handler.OnJson("/repos/alex/Gone.Plugin/releases", "{}", HttpStatusCode.NotFound);
        var view = OpenPage(env);

        await Press(view, "Download", repo.Id);

        Assert.True(view.PluginUiOptions.Logs.Caption.Length <= 70, view.PluginUiOptions.Logs.Caption);
        Assert.Contains("card below", view.PluginUiOptions.Logs.Caption);
    }

    [Fact]
    public async Task AnInvalidAddress_IsNotSaved_AndTheMessageStaysOneLine()
    {
        using var env = new TestEnv();
        var view = OpenPage(env);
        var dialog = new RepoConfigDialogView(Guid.NewGuid().ToString());
        dialog.RepoConfigUi.Url = "not a repository at all";

        view.OnDialogResult(dialog, true, null);

        Assert.Single(view.PluginUiOptions.Repos);   // only the installer's own entry
        Assert.StartsWith("Not saved.", view.PluginUiOptions.RepoLogs.Caption);
        Assert.True(view.PluginUiOptions.RepoLogs.Caption.Length <= 100);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AddingByShorthand_SavesTheCanonicalAddress_AndChecksItStraightAway()
    {
        using var env = new TestEnv();
        env.Publish("Cool.Plugin", "v1.0.0");
        var view = OpenPage(env);
        var dialog = new RepoConfigDialogView(Guid.NewGuid().ToString());
        dialog.RepoConfigUi.Url = "alex/Cool.Plugin";

        view.OnDialogResult(dialog, true, null);

        var saved = env.Repo("Cool.Plugin");
        Assert.Equal("https://github.com/alex/Cool.Plugin", saved.Url);
        Assert.Equal("v1.0.0", saved.LatestVersion);                     // looked up on save
        Assert.Equal(RepoStatus.UpdateAvailable, saved.GetStatus());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AddingTheSameRepoTwice_IsRefused_NoMatterHowItIsWritten()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin");
        var view = OpenPage(env);
        var dialog = new RepoConfigDialogView(Guid.NewGuid().ToString());
        dialog.RepoConfigUi.Url = "ALEX/cool.plugin.git";

        view.OnDialogResult(dialog, true, null);

        Assert.Equal(1, env.Options.Repos.Count(r => r.Repository.Equals("Cool.Plugin", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("already in the list", view.PluginUiOptions.RepoLogs.Caption);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData(null, "unexpected error")]
    [InlineData("a b c d e f g h i j k l m n o p q r s t u v w x y z a b c d e f g h", "a b c d e f g h i j k l m n o p q r s t u v w x y…")]
    public void Brief_KeepsMessagesToOneLine(string input, string expected)
    {
        var result = MainPageView.Brief(input);

        Assert.Equal(expected, result);
        Assert.True(result.Length <= 51);
    }

    // ---- Stale page state -------------------------------------------------------------------------------------

    // The page holds its own copy of the settings. A scheduled run that finishes while the page is open
    // records new versions in the file; saving from the page must not put the old versions back.
    [Fact]
    public async Task SavingFromAnOldPage_DoesNotUndoWhatARunRecordedMeanwhile()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0", fileName: "Cool.Plugin.dll");
        File.WriteAllText(env.PluginFile("Cool.Plugin.dll"), "old");
        env.Publish("Cool.Plugin", "v2.0.0");
        var view = OpenPage(env);                      // page opens showing v1.0.0

        await env.Service.RunAsync(RepoUpdateService.TriggerScheduled, RunMode.Auto);   // run installs v2.0.0 meanwhile
        Assert.Equal("v2.0.0", env.Repo("Cool.Plugin").LastVersionDownloaded);

        await Press(view, "Save");                     // user clicks Save on the old page

        Assert.Equal("v2.0.0", env.Repo("Cool.Plugin").LastVersionDownloaded);
    }

    [Fact]
    public async Task ToggleSettingsFromAnOldPage_KeepsTheLatestVersionInfoToo()
    {
        using var env = new TestEnv();
        env.AddRepo("Cool.Plugin", installed: "v1.0.0");
        env.Publish("Cool.Plugin", "v2.0.0");
        var view = OpenPage(env);

        await env.Service.RunAsync(RepoUpdateService.TriggerScheduled, RunMode.CheckOnly);
        view.PluginUiOptions.RestartServerAfterInstall = true;
        await Press(view, "Save");

        var saved = env.Repo("Cool.Plugin");
        Assert.Equal("v2.0.0", saved.LatestVersion);
        Assert.NotNull(saved.LastDateTimeChecked);
        Assert.True(env.Options.RestartServerAfterInstall);
    }
}
