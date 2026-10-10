using System;
using System.Collections.Generic;
using System.Linq;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.UI;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

public class StatusListsTests
{
    private static string Flat(string html) => html.Replace("<br/>", " ");

    private static ReposToProcess Repo(string name, string latest = null, string installed = null, bool auto = true, string error = null)
    {
        return new ReposToProcess
        {
            Url = $"https://github.com/alex/{name}",
            LatestVersion = latest,
            LastVersionDownloaded = installed,
            AutoUpdate = auto,
            LastError = error,
            LastDateTimeChecked = DateTime.UtcNow.AddHours(-2)
        };
    }

    private static RunRecord Run(params string[] outcomes)
    {
        var run = new RunRecord { StartedUtc = DateTime.UtcNow.AddHours(-1), Trigger = "Scheduled", DurationSeconds = 42 };
        foreach (var o in outcomes)
            run.Results.Add(new RepoRunResult { Name = "P", Outcome = o, FromVersion = "1", ToVersion = "2", Message = "m" });
        return run;
    }

    // ---- Banner -----------------------------------------------------------------------------------------------

    [Fact]
    public void Banner_IsEmptyWhenNothingIsPending()
    {
        Assert.Empty(StatusLists.BuildBanner(new PluginRuntimeState()));
        Assert.Empty(StatusLists.BuildBanner(null));
    }

    [Fact]
    public void Banner_NamesEachPluginAndOffersRestartNow()
    {
        var state = new PluginRuntimeState
        {
            PendingRestart =
            {
                new PendingRestartItem { Name = "Subtitle Sync", FromVersion = "2.3.0", ToVersion = "2.4.1" },
                new PendingRestartItem { Name = "Fresh", ToVersion = "1.0.0" }
            }
        };

        var item = Assert.Single(StatusLists.BuildBanner(state));

        Assert.Equal("2 plugins changed, listed below", item.SecondaryText);
        Assert.Contains("Subtitle Sync 2.3.0 → 2.4.1", item.SubItems[0].PrimaryText);
        Assert.Contains("Fresh 1.0.0 (new)", item.SubItems[1].PrimaryText);
        Assert.Equal("RestartNow", item.Button1.Data1);
        Assert.NotNull(item.Button1.ConfirmationPrompt);
        Assert.Equal("DismissRestart", item.Button2.Data1);
        Assert.Equal(ItemStatus.Warning, item.Status);
    }

    // ---- Summary ----------------------------------------------------------------------------------------------

    [Fact]
    public void Summary_WithNoRuns_ExplainsTheScheduleAndOffersRunNow()
    {
        var item = Assert.Single(StatusLists.BuildSummary(null, new RunProgressInfo(), true));

        Assert.Equal("No runs yet", item.PrimaryText);
        Assert.Equal("RunNow", item.Button1.Data1);
        Assert.Equal("CheckAll", item.Button2.Data1);
    }

    [Fact]
    public void Summary_AfterARun_ShowsCountsAndTone()
    {
        var clean = Assert.Single(StatusLists.BuildSummary(Run(RepoOutcome.Updated, RepoOutcome.UpToDate), new RunProgressInfo(), true));
        var bad   = Assert.Single(StatusLists.BuildSummary(Run(RepoOutcome.Failed), new RunProgressInfo(), true));

        Assert.Contains("1 updated · 1 up to date · 0 failed", clean.SecondaryText);
        Assert.Equal(ItemStatus.Succeeded, clean.Status);
        Assert.Equal(ItemStatus.Warning, bad.Status);
        Assert.Contains("scheduled", clean.PrimaryText);
    }

    [Fact]
    public void Summary_WhileRunning_ShowsProgressInsteadOfRunNow()
    {
        var progress = new RunProgressInfo { IsRunning = true, Total = 4, Done = 1, Current = "Cool", Trigger = "Manual", StartedUtc = DateTime.UtcNow };

        var item = Assert.Single(StatusLists.BuildSummary(Run(RepoOutcome.UpToDate), progress, true));

        Assert.Equal(ItemStatus.InProgress, item.Status);
        Assert.True(item.HasPercentage);
        Assert.Equal(25, item.PercentComplete);
        Assert.Contains("1 of 4", item.PrimaryText);
        Assert.Contains("Cool", item.PrimaryText);
        Assert.Equal("RefreshView", item.Button1.Data1);
        Assert.Null(item.Button2);
    }

    [Fact]
    public void Summary_WithoutAToken_MentionsTheRateLimit()
    {
        var item = Assert.Single(StatusLists.BuildSummary(null, new RunProgressInfo(), false));
        Assert.Contains(item.SubItems, s => Flat(s.PrimaryText).Contains("60 checks per hour"));
    }

    // ---- Plugin cards -----------------------------------------------------------------------------------------

    [Fact]
    public void Plugins_ProblemsFirst_ThenUpdates_ThenUpToDate()
    {
        var repos = new[]
        {
            Repo("fine", "1", "1"),
            Repo("broken", error: "boom"),
            Repo("newer", "2", "1"),
            Repo("waiting", "2", "1", auto: false)
        };

        var names = StatusLists.BuildPlugins(repos).Select(i => i.PrimaryText).ToArray();

        Assert.Equal(new[] { "broken", "newer", "waiting", "fine" }, names);
    }

    [Fact]
    public void Plugins_UpdateAvailable_ShowsVersionsAndAnUpdateButton()
    {
        var item = Assert.Single(StatusLists.BuildPlugins(new[] { Repo("newer", "2.4.1", "2.3.0") }));

        Assert.Contains("Installed 2.3.0 → 2.4.1", item.SecondaryText);
        Assert.Equal(ItemStatus.Warning, item.Status);
        Assert.Equal("Download", item.Button1.Data1);
        Assert.NotNull(item.Button1.Data2);
    }

    [Fact]
    public void Plugins_Failed_ShowsTheReasonAndARetryButton()
    {
        var item = Assert.Single(StatusLists.BuildPlugins(new[] { Repo("broken", error: "GitHub's request limit was reached.") }));

        Assert.Equal(ItemStatus.Failed, item.Status);
        Assert.Contains(item.SubItems, s => Flat(s.PrimaryText).Contains("request limit"));
        Assert.Equal("Retry", item.Button1.Caption);
    }

    [Fact]
    public void Plugins_UpToDate_HasNoButton()
    {
        var item = Assert.Single(StatusLists.BuildPlugins(new[] { Repo("fine", "1.0", "1.0") }));

        Assert.Equal(ItemStatus.Succeeded, item.Status);
        Assert.Null(item.Button1);
        Assert.Contains("Version 1.0", item.SecondaryText);
    }

    [Fact]
    public void Plugins_WithAutoUpdateOff_SaysTheUpdateIsWaiting()
    {
        var item = Assert.Single(StatusLists.BuildPlugins(new[] { Repo("manual", "2", "1", auto: false) }));

        var row = item.SubItems.Single(s => s.Toggle != null);
        Assert.False(row.Toggle.IsChecked);
        Assert.Equal("Off", row.Toggle.Caption);
        Assert.Contains("wait for you", row.SecondaryText);
        Assert.Equal("Update", item.Button1.Caption);
    }

    [Fact]
    public void Plugins_ShowTheRunningVersionNoteWhenThereIsOne()
    {
        var item = Assert.Single(StatusLists.BuildPlugins(new[] { Repo("fine", "2", "2") }, _ => "Running 1.0, but the installed file is 2.0. Restart Emby to apply."));

        Assert.Contains(item.SubItems, s => Flat(s.PrimaryText).Contains("Restart Emby to apply"));
    }

    // Release notes and error text come from outside, and these items render HTML.
    [Fact]
    public void Plugins_EncodeNotesAndErrorsFromGitHub()
    {
        var repo = Repo("evil", "2", "1");
        repo.LatestNotes = "<script>alert(1)</script>\nsecond line & more";
        repo.LastError = "<img src=x onerror=alert(1)>";
        repo.LatestVersion = "<b>2</b>";

        var item = Assert.Single(StatusLists.BuildPlugins(new[] { repo }));
        var notes = item.SubItems.Single(s => s.PrimaryText.StartsWith("Release notes")).PrimaryText;
        var error = item.SubItems.Single(s => s.PrimaryText.Contains("onerror")).PrimaryText;

        Assert.DoesNotContain("<script>", notes);
        Assert.Contains("&lt;script&gt;", notes);
        Assert.Contains("<br/>", notes);
        Assert.Contains("&amp; more", notes);
        Assert.DoesNotContain("<img", error);
        Assert.Contains("&lt;img", error);
    }

    // Emby draws PrimaryText as HTML and SecondaryText as plain text on one line. Text from outside must be encoded
    // in the first, and must not be encoded or contain markup in the second, or entities and tags show on screen.
    [Fact]
    public void EveryList_FollowsTheTextRules_EvenWithAwkwardCharacters()
    {
        var tricky = "O'Brien's <plugin> & \"friends\"";
        var repos = new[]
        {
            new ReposToProcess { Url = "https://github.com/a/Fine", LatestVersion = "1", LastVersionDownloaded = "1" },
            new ReposToProcess { Url = "https://github.com/a/Broke", LastError = "GitHub's limit <reached> & more" },
            new ReposToProcess { Url = "https://github.com/a/Newer", LatestVersion = "2.0 & co", LastVersionDownloaded = "1 <old>", LatestNotes = tricky },
            new ReposToProcess { Url = "https://github.com/a/Manual", AutoUpdate = false, LatestVersion = "2", LastVersionDownloaded = "1" }
        };
        var run = Run(RepoOutcome.Updated, RepoOutcome.Failed, RepoOutcome.WaitingManual);
        run.Results[0].Name = tricky; run.Results[1].Message = tricky;
        var state = new PluginRuntimeState { PendingRestart = { new PendingRestartItem { Name = tricky, ToVersion = "1 & 2" } } };

        var lists = new[]
        {
            StatusLists.BuildPlugins(repos, _ => tricky),
            StatusLists.BuildSummary(run, new RunProgressInfo(), false),
            StatusLists.BuildSummary(null, new RunProgressInfo { IsRunning = true, Total = 2, Current = tricky, Trigger = "Manual", StartedUtc = DateTime.UtcNow }, false),
            StatusLists.BuildHistory(new[] { run }),
            StatusLists.BuildBanner(state)
        };

        var items = new List<GenericListItem>();
        void Collect(IEnumerable<GenericListItem> source)
        {
            foreach (var i in source ?? Enumerable.Empty<GenericListItem>()) { items.Add(i); Collect(i.SubItems); }
        }
        foreach (var l in lists) Collect(l);

        Assert.NotEmpty(items);
        foreach (var i in items)
        {
            // plain text: no tags, and no entities (an apostrophe must stay an apostrophe)
            if (i.SecondaryText != null)
            {
                Assert.DoesNotContain("<br", i.SecondaryText);
                Assert.DoesNotContain("&#", i.SecondaryText);
                Assert.DoesNotContain("&amp;", i.SecondaryText);
                Assert.DoesNotContain("&lt;", i.SecondaryText);
                Assert.DoesNotContain("\n", i.SecondaryText);
            }

            // HTML: the only tags allowed are the ones this code adds itself
            if (i.PrimaryText != null)
            {
                var withoutOurTags = i.PrimaryText.Replace("<br/>", "").Replace("<b>", "").Replace("</b>", "");
                Assert.DoesNotContain("<", withoutOurTags);
                Assert.DoesNotContain("\"", withoutOurTags);
            }
        }

        // and the awkward characters really are in the text people read
        Assert.Contains(items, i => i.PrimaryText != null && i.PrimaryText.Contains("O&#39;Brien&#39;s &lt;plugin&gt; &amp;"));
        Assert.Contains(items, i => i.SecondaryText != null && i.SecondaryText.Contains("2.0 & co"));
    }

    [Fact]
    public void Plugins_ANeverCheckedRepoPointsToCheckForUpdates()
    {
        var neverChecked = Repo("new");
        neverChecked.LastDateTimeChecked = null;
        var item = Assert.Single(StatusLists.BuildPlugins(new[] { neverChecked }));

        Assert.Equal(ItemStatus.Unknown, item.Status);
        Assert.Contains("Check for updates", item.SecondaryText);
    }

    // Found while taking screenshots on a real server: a repository with no release was shown as "not checked yet".
    [Fact]
    public void Plugins_ACheckedRepoWithNoRelease_SaysSo_InsteadOfNotCheckedYet()
    {
        var repo = Repo("empty");   // checked 2 hours ago, found nothing

        var item = Assert.Single(StatusLists.BuildPlugins(new[] { repo }));

        Assert.Equal("No release yet", item.Tag.Text);
        Assert.Contains("No release published yet", item.SecondaryText);
        Assert.DoesNotContain("Not checked", item.SecondaryText);
        Assert.Null(item.Button1);
    }

    [Fact]
    public void Plugins_NoReleaseSortsAfterThingsThatNeedAttention_AndBeforeUpToDate()
    {
        var repos = new[] { Repo("fine", "1", "1"), Repo("empty"), Repo("newer", "2", "1") };

        var names = StatusLists.BuildPlugins(repos).Select(i => i.PrimaryText).ToArray();

        Assert.Equal(new[] { "newer", "empty", "fine" }, names);
    }

    // ---- History ----------------------------------------------------------------------------------------------

    [Fact]
    public void History_ShowsOneItemPerRunWithAResultPerPlugin()
    {
        var runs = new List<RunRecord> { Run(RepoOutcome.Updated, RepoOutcome.Failed), Run(RepoOutcome.UpToDate) };

        var list = StatusLists.BuildHistory(runs);

        Assert.Equal(2, list.Count);
        Assert.Equal(2, list[0].SubItems.Count);
        Assert.Equal(ItemStatus.Warning, list[0].Status);
        Assert.Contains("Updated 1 → 2", Flat(list[0].SubItems[0].PrimaryText));
        Assert.Contains("Failed: m", Flat(list[0].SubItems[1].PrimaryText));
        Assert.Equal(ItemStatus.Succeeded, list[1].Status);
    }

    // ---- Wrapping long text (Emby cuts each line off with "..." instead of wrapping it) ------------------------

    [Fact]
    public void WrapHtml_BreaksAtWordBoundaries_WithoutLosingOrSplittingWords()
    {
        var text = "GitHub's request limit was reached. It resets at 14:05. Adding a token raises the limit from 60 to 5,000 requests per hour.";

        var html  = StatusLists.WrapHtml(text, 40);
        var lines = html.Split(new[] { "<br/>" }, StringSplitOptions.None);

        Assert.True(lines.Length > 1);
        Assert.All(lines, l => Assert.True(System.Net.WebUtility.HtmlDecode(l).Length <= 40, $"line too long: {l}"));
        Assert.Equal(text, System.Net.WebUtility.HtmlDecode(string.Join(" ", lines)));
    }

    [Fact]
    public void WrapHtml_ShortTextStaysOnOneLine()
    {
        Assert.Equal("Up to date", StatusLists.WrapHtml("Up to date"));
        Assert.Equal("", StatusLists.WrapHtml(null));
        Assert.Equal("", StatusLists.WrapHtml("   "));
    }

    [Fact]
    public void WrapHtml_AWordLongerThanTheWidthIsNotSplit_AndStillEncoded()
    {
        var html = StatusLists.WrapHtml("see https://example.com/a/very/long/path?x=1&y=2 now", 10);

        Assert.Contains("https://example.com/a/very/long/path?x=1&amp;y=2", html);
    }

    [Fact]
    public void AFailureReason_ReadsInFullInsteadOfBeingCutOff()
    {
        var reason = "GitHub's request limit was reached. It resets at 14:05. Adding a token raises the limit from 60 to 5,000 requests per hour.";
        var item = Assert.Single(StatusLists.BuildPlugins(new[] { Repo("broken", error: reason) }));

        var note = item.SubItems[0].PrimaryText;   // the reason comes first, above the auto-update row

        Assert.Contains("<br/>", note);   // it is spread over several lines
        Assert.Equal(reason, System.Net.WebUtility.HtmlDecode(Flat(note)));
    }

    [Fact]
    public void ReleaseNotes_KeepTheirOwnLines_AndWrapTheLongOnes()
    {
        var repo = Repo("n", "2", "1");
        repo.LatestNotes = "- short bullet\n\n- " + new string('x', 5) + " " + string.Join(" ", Enumerable.Repeat("longword", 20));

        var html = StatusLists.NotesHtml(repo.LatestNotes);
        var lines = html.Split(new[] { "<br/>" }, StringSplitOptions.None);

        Assert.Equal("- short bullet", lines[0]);
        Assert.Equal("", lines[1]);                       // blank line between paragraphs kept
        Assert.All(lines, l => Assert.True(l.Length <= 70));
        Assert.True(lines.Length > 3);                    // the long bullet was wrapped
    }

    [Fact]
    public void History_ShowsOnlyTheMostRecentRuns()
    {
        var runs = Enumerable.Range(0, 25).Select(_ => Run(RepoOutcome.UpToDate)).ToList();

        Assert.Equal(StatusLists.HistoryItemsShown, StatusLists.BuildHistory(runs).Count);
    }

    [Fact]
    public void History_EncodesTextFromGitHub()
    {
        var run = Run(RepoOutcome.Failed);
        run.Results[0].Message = "<script>x</script>";

        var list = StatusLists.BuildHistory(new[] { run });

        Assert.DoesNotContain("<script>", list[0].SubItems[0].PrimaryText);
        Assert.Contains("&lt;script&gt;", list[0].SubItems[0].PrimaryText);
    }
}
