using System;
using Emby.GitHubRepoPluginInstall.Helpers;
using Emby.GitHubRepoPluginInstall.Models;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

public class ActivityLogHelperTests
{
    // Emby keeps the whitespace in the HTML it is given, so template indentation became big blank gaps on screen.
    [Fact]
    public void Cards_HaveNoWhitespaceBetweenTags()
    {
        var cards = new[]
        {
            ActivityLogHelper.CreateSuccessHtml("Updated", "Installed.", "1.2.3", "notes"),
            ActivityLogHelper.CreateWarningHtml("Careful", "Something odd.", "details"),
            ActivityLogHelper.CreateErrorHtml("Failed", "It broke.", "boom", "stack"),
            ActivityLogHelper.CreateInfoHtml("FYI", "Nothing to do.", "details"),
            ActivityLogHelper.CreateRunSummaryHtml(new RunRecord
            {
                Trigger = "Scheduled", DurationSeconds = 3,
                Results = { new RepoRunResult { Name = "A", Outcome = RepoOutcome.Updated, FromVersion = "1", ToVersion = "2" } }
            })
        };

        Assert.All(cards, c =>
        {
            Assert.DoesNotContain("\n", c);
            Assert.DoesNotMatch(@">\s+<", c);
            Assert.StartsWith("<div", c);
            Assert.EndsWith("</div>", c);
        });
    }

    [Fact]
    public void Cards_StillCarryTheirText_AndEncodeIt()
    {
        var html = ActivityLogHelper.CreateErrorHtml("Failed <b>x</b>", "It's \"broken\" & sad", "<script>1</script>", null);

        Assert.Contains("Failed &lt;b&gt;x&lt;/b&gt;", html);
        Assert.Contains("&amp; sad", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void Compact_LeavesTextInsideTagsAlone()
    {
        var html = "<div>\n   <pre>line one\n  line two</pre>\n   <span>a  b</span>\n</div>";

        var compact = ActivityLogHelper.Compact(html);

        Assert.Equal("<div><pre>line one\n  line two</pre><span>a  b</span></div>", compact);
    }

    [Fact]
    public void Compact_HandlesNull()
    {
        Assert.Equal("", ActivityLogHelper.Compact(null));
    }

    [Fact]
    public void RunSummary_ListsEveryPluginWithItsOutcome()
    {
        var html = ActivityLogHelper.CreateRunSummaryHtml(new RunRecord
        {
            Trigger = "Manual", DurationSeconds = 65,
            Results =
            {
                new RepoRunResult { Name = "Alpha", Outcome = RepoOutcome.Updated, FromVersion = "1", ToVersion = "2" },
                new RepoRunResult { Name = "Beta",  Outcome = RepoOutcome.Failed,  Message = "Repository not found." },
                new RepoRunResult { Name = "Gamma", Outcome = RepoOutcome.WaitingManual, ToVersion = "9" }
            }
        });

        Assert.Contains("1 updated", html);
        Assert.Contains("1 failed", html);
        Assert.Contains("Alpha</strong> — updated 1 → 2", html);
        Assert.Contains("Beta</strong> — failed: Repository not found.", html);
        Assert.Contains("Gamma</strong> — 9 available, waiting on you", html);
        Assert.Contains("1 min 5 s", html);
    }
}
