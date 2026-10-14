using System;
using System.IO;
using System.Linq;
using Emby.GitHubRepoPluginInstall.Api;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.GitHubRepoPluginInstall.Storage;
using Xunit;

namespace Emby.GitHubRepoPluginInstall.Tests;

public class EventLogTests
{
    private static EventLogStore Make(TempDir dir) =>
        new EventLogStore(dir.Path, new TestJson(), new TestLogger(), "GitHub Repo Plugin Install");

    private static LogEvent Add(EventLogStore store, string name = "GitHubRepoPluginInstall.RunCompleted") =>
        store.Append(name, EventRecorder.ShortName(name), new EventPayload { Updated = 1 });

    [Fact]
    public void EachEventGetsTheNextNumber_AndTheCommonFields()
    {
        using var dir = new TempDir();
        var store = Make(dir);

        var first  = Add(store);
        var second = Add(store);

        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal(2, store.LatestSeq());
        Assert.Equal(1, second.Data.ApiLevel);
        Assert.Equal(2, second.Data.Seq);
        Assert.Equal("RunCompleted", second.Data.Event);
        Assert.True(Guid.TryParse(second.Data.EventId, out _));
        Assert.Equal(DateTimeKind.Utc, second.Data.ServerTimeUtc.Kind);
        Assert.Equal(second.AtUtc, second.Data.ServerTimeUtc);
    }

    [Fact]
    public void TheLogAndTheNumberSurviveARestart()
    {
        using var dir = new TempDir();
        Add(Make(dir));
        Add(Make(dir));

        var again = Make(dir);
        var read  = again.Read(0, 100);

        Assert.Equal(2, read.LatestSeq);
        Assert.Equal(new long[] { 1, 2 }, read.Events.Select(e => e.Seq));
        Assert.Equal(1, read.Events[0].Data.Updated);
        Assert.Equal(3, Add(again).Seq);
    }

    [Fact]
    public void NumbersNeverGoBack_EvenIfTheLogIsDeletedOrDamaged()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Add(store);
        Add(store);
        Add(store);

        File.Delete(Path.Combine(dir.Path, "GitHub Repo Plugin Install.events.json"));
        Assert.Equal(4, Add(store).Seq);

        File.WriteAllText(Path.Combine(dir.Path, "GitHub Repo Plugin Install.events.json"), "{ this is not json");
        Assert.Equal(5, Add(store).Seq);
        Assert.Equal(5, store.LatestSeq());
    }

    [Fact]
    public void ALostNumberFileIsRebuiltFromTheLog()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Add(store);
        Add(store);

        File.Delete(Path.Combine(dir.Path, "GitHub Repo Plugin Install.events.seq"));

        Assert.Equal(2, store.LatestSeq());
        Assert.Equal(3, Add(store).Seq);
    }

    [Fact]
    public void OnlyTheLast500AreKept()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        for (var i = 0; i < 505; i++) Add(store);

        var read = store.Read(0, 1000);

        Assert.Equal(505, read.LatestSeq);
        Assert.Equal(500, read.Events.Count);
        Assert.Equal(6, read.Events.First().Seq);
        Assert.Equal(505, read.Events.Last().Seq);
        Assert.True(read.Truncated);
    }

    [Fact]
    public void ReadReturnsWhatComesAfter_OldestFirst_UpToTheLimit()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        for (var i = 0; i < 10; i++) Add(store);

        var page = store.Read(3, 4);

        Assert.Equal(new long[] { 4, 5, 6, 7 }, page.Events.Select(e => e.Seq));
        Assert.Equal(10, page.LatestSeq);
        Assert.False(page.Truncated);
        Assert.Equal(1, page.ApiLevel);
        Assert.Empty(store.Read(10, 100).Events);
        Assert.Single(store.Read(0, 0).Events);   // a limit below 1 is raised to 1
    }

    [Fact]
    public void AnEmptyLog_IsNotTruncated()
    {
        using var dir = new TempDir();
        var read = Make(dir).Read(0, 100);

        Assert.Equal(0, read.LatestSeq);
        Assert.False(read.Truncated);
        Assert.Empty(read.Events);
    }

    [Fact]
    public void TruncatedMeansTheLogNoLongerReachesBack()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        for (var i = 0; i < 600; i++) Add(store);   // keeps 101..600

        Assert.True(store.Read(50, 10).Truncated);
        Assert.False(store.Read(100, 10).Truncated);   // 101 is the very next one
        Assert.False(store.Read(600, 10).Truncated);
        Assert.True(store.Read(9999, 10).Truncated);   // the caller is ahead of the plugin: data was reset
    }
}

public class EventRecorderTests
{
    private static EventLogStore Make(TempDir dir) =>
        new EventLogStore(dir.Path, new TestJson(), new TestLogger(), "GitHub Repo Plugin Install");

    [Fact]
    public void RunCompleted_IsLoggedWithItsFields()
    {
        using var dir = new TempDir();
        var store = Make(dir);

        var ev = EventRecorder.Record(store, true, PushEvents.RunCompleted, new RunEventData
        {
            Trigger = "Manual", Total = 5, Done = 5, Updated = 2, UpToDate = 2, Failed = 1, Waiting = 0,
            UpdatedPlugins = new[] { "Alpha", "Beta" }, FailedPlugins = new[] { "Gamma" }
        });

        Assert.NotNull(ev);
        Assert.Equal("GitHubRepoPluginInstall.RunCompleted", ev.Name);
        Assert.Equal("RunCompleted", ev.Data.Event);
        Assert.Equal("Manual", ev.Data.Trigger);
        Assert.Equal(2, ev.Data.Updated);
        Assert.Equal(1, ev.Data.Failed);
        Assert.Equal(new[] { "Alpha", "Beta" }, ev.Data.UpdatedPlugins);
        Assert.Equal(new[] { "Gamma" }, ev.Data.FailedPlugins);
        Assert.Null(ev.Data.Reason);
        Assert.Equal(1, store.LatestSeq());
    }

    [Fact]
    public void RestartPending_AndPluginsChanged_AreLogged()
    {
        using var dir = new TempDir();
        var store = Make(dir);

        var restart = EventRecorder.Record(store, true, PushEvents.RestartPending,
                                           new RunEventData { Trigger = "Scheduled", Updated = 1, UpdatedPlugins = new[] { "Alpha" } });
        var changed = EventRecorder.Record(store, true, PushEvents.PluginsChanged,
                                           new ChangeEventData { Reason = "Added", PluginId = "abc-1", PluginName = "Alpha", RestartNeeded = true });

        Assert.Equal("RestartPending", restart.Data.Event);
        Assert.Equal(1, restart.Data.Updated);
        Assert.Null(restart.Data.Total);
        Assert.Equal("PluginsChanged", changed.Data.Event);
        Assert.Equal("Added", changed.Data.Reason);
        Assert.Equal("abc-1", changed.Data.PluginId);
        Assert.True(changed.Data.RestartNeeded);
        Assert.Equal(new long[] { 1, 2 }, store.Read(0, 10).Events.Select(e => e.Seq));
    }

    [Fact]
    public void RunProgress_IsNotLogged_AndNeitherIsAnythingWhileThePhoneApiIsOff()
    {
        using var dir = new TempDir();
        var store = Make(dir);

        Assert.Null(EventRecorder.Record(store, true, PushEvents.RunProgress, new RunEventData { Total = 3, Done = 1 }));
        Assert.Null(EventRecorder.Record(store, false, PushEvents.RunCompleted, new RunEventData { Total = 3 }));
        Assert.Null(EventRecorder.Record(store, true, "Something.Else", new RunEventData()));
        Assert.Null(EventRecorder.Record(store, true, PushEvents.RunCompleted, "the wrong kind of data"));
        Assert.Equal(0, store.LatestSeq());
    }

    [Fact]
    public void NoFilePathsReachAPayload()
    {
        using var dir = new TempDir();
        var store = Make(dir);

        var ev = EventRecorder.Record(store, true, PushEvents.RunCompleted, new RunEventData
        {
            Trigger = "Manual",
            FailedPlugins = new[] { @"Alpha (C:\Emby\plugins\Alpha.dll)", @"Beta \\nas\share\Beta.dll", "Gamma /var/lib/emby/plugins/Gamma.dll" }
        });
        var json = new TestJson().SerializeToString(ev);

        Assert.DoesNotContain(@"C:\", json);
        Assert.DoesNotContain("nas", json);
        Assert.DoesNotContain("/var/", json);
        Assert.DoesNotContain(".dll", json);
        Assert.Contains("Alpha", ev.Data.FailedPlugins[0]);
        Assert.Equal("Plain Name 1.2", EventRecorder.Scrub("Plain Name 1.2"));
    }

    [Fact]
    public void ThePayloadDoesNotCarryFieldsThatDoNotApply()
    {
        using var dir = new TempDir();
        var ev = EventRecorder.Record(Make(dir), true, PushEvents.PluginsChanged, new ChangeEventData { Reason = "Removed", PluginName = "Alpha" });

        var json = new TestJson().SerializeToString(ev.Data);

        Assert.Contains("\"Seq\":1", json);
        Assert.Contains("\"Event\":\"PluginsChanged\"", json);
        Assert.DoesNotContain("Updated", json);
        Assert.DoesNotContain("Trigger", json);
    }
}

public class CatalogAndInfoTests
{
    [Fact]
    public void ApiVersionIsUnchanged_AndTheAlertLevelStartsAtOne()
    {
        Assert.Equal(1, ApiVersions.Current);
        Assert.Equal(1, ApiVersions.Level);
    }

    [Fact]
    public void TheCatalogDescribesTheTwoAlerts()
    {
        var catalog = NotificationCatalog.Build();

        Assert.Equal(1, catalog.ApiLevel);
        Assert.Equal("GitHubRepoPluginInstall", catalog.Service.Id);
        Assert.False(string.IsNullOrEmpty(catalog.Service.Title));
        Assert.False(string.IsNullOrEmpty(catalog.Service.ChannelId));
        Assert.Equal(new[] { "GitHubRepoPluginInstall.RunCompleted", "GitHubRepoPluginInstall.RestartPending" }, catalog.Events.Select(e => e.Id));
        Assert.All(catalog.Events, e =>
        {
            Assert.False(string.IsNullOrEmpty(e.Title));
            Assert.False(string.IsNullOrEmpty(e.Description));
            Assert.False(string.IsNullOrEmpty(e.LockScreenText));
            Assert.NotNull(e.DefaultRule);
            Assert.True(e.DefaultRule.Enabled);
            Assert.All(e.Params, p => Assert.Contains(p.Type, new[] { "int", "bool", "text", "choice" }));
        });

        var run = catalog.Events[0].Params.Single();
        Assert.Equal("OnlyWhenSomethingHappened", run.Name);
        Assert.Equal("bool", run.Type);
        Assert.Equal(true, run.Default);
        Assert.Equal(true, catalog.Events[0].DefaultRule.Params["OnlyWhenSomethingHappened"]);
        Assert.Empty(catalog.Events[1].Params);
    }

    [Fact]
    public void EveryCatalogEventIsOneThePluginLogs()
    {
        Assert.All(NotificationCatalog.Build().Events, e => Assert.True(EventRecorder.IsLogged(e.Id)));
    }

    [Fact]
    public void TheCatalogSerializesWithTheContractsFieldNames()
    {
        var json = new TestJson().SerializeToString(NotificationCatalog.Build());

        foreach (var field in new[] { "\"ApiLevel\"", "\"Service\"", "\"ChannelId\"", "\"ChannelTitle\"", "\"Events\"", "\"Params\"", "\"DefaultRule\"", "\"LockScreenText\"", "\"OpensTab\"" })
            Assert.Contains(field, json);
    }
}

public class EventsSubscriptionTests
{
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static EventLogStore Make(TempDir dir) =>
        new EventLogStore(dir.Path, new TestJson(), new TestLogger(), "GitHub Repo Plugin Install");

    private static void Log(EventLogStore store, int count = 1)
    {
        for (var i = 0; i < count; i++)
            EventRecorder.Record(store, true, PushEvents.RunCompleted, new RunEventData { Updated = 1 });
    }

    private static EventsPushState Start(string data = null)
    {
        var state = new EventsPushState();
        EventsSubscription.ParseStartValues(data == null ? new string[0] : data.Split(','), state);
        return state;
    }

    [Fact]
    public void TheThirdValueOfTheStartMessageIsTheLastSeqSeen()
    {
        Assert.Null(Start().AfterSeq);
        Assert.Equal(17, Start("17").AfterSeq);
        Assert.Null(Start("nonsense").AfterSeq);
        Assert.Null(Start("-4").AfterSeq);
    }

    [Fact]
    public void ANonAdministratorGetsOneErrorAndNothingElse()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store, 3);
        var state = Start("0");

        var first = EventsSubscription.Next(state, store, isAdministrator: false, apiEnabled: true, T0);

        Assert.Equal("Administrator login required.", first.Error);
        Assert.Null(first.Events);
        Assert.Null(first.LatestSeq);
        Assert.Null(EventsSubscription.Next(state, store, false, true, T0.AddSeconds(40)));
        Assert.Null(EventsSubscription.Next(state, store, false, true, T0.AddSeconds(80)));
    }

    [Fact]
    public void WhileThePhoneApiIsOff_TheAnswerIsAnErrorAndNoEvents()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store, 3);
        var state = Start("0");

        var first = EventsSubscription.Next(state, store, true, apiEnabled: false, T0);

        Assert.NotNull(first.Error);
        Assert.Null(first.Events);
        Assert.Null(EventsSubscription.Next(state, store, true, false, T0.AddSeconds(40)));
    }

    [Fact]
    public void SwitchingTheApiBackOnResumesWithoutLosingOrRepeating()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        var state = Start("0");
        Log(store);
        var initial = EventsSubscription.Next(state, store, true, true, T0);
        Assert.Equal(new long[] { 1 }, initial.Events.Select(e => e.Seq));

        Assert.NotNull(EventsSubscription.Next(state, store, true, false, T0.AddSeconds(1)).Error);
        Log(store);   // would not be logged while off in real life; here it shows nothing is re-sent
        var back = EventsSubscription.Next(state, store, true, true, T0.AddSeconds(2));

        Assert.Null(back.Error);
        Assert.Equal(new long[] { 2 }, back.Events.Select(e => e.Seq));
    }

    [Fact]
    public void WithoutALastSeq_TheFirstPushIsOnlyTheBaseline()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store, 4);
        var state = Start();

        var first = EventsSubscription.Next(state, store, true, true, T0);

        Assert.Equal(4, first.LatestSeq);
        Assert.Empty(first.Events);
        Assert.False(first.Truncated);
        Assert.Equal(1, first.ApiLevel);
        Assert.Null(first.Error);
        Assert.Null(EventsSubscription.Next(state, store, true, true, T0.AddSeconds(1)));
    }

    [Fact]
    public void WithALastSeq_TheFirstPushHoldsTheEventsAfterIt()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store, 5);

        var first = EventsSubscription.Next(Start("2"), store, true, true, T0);

        Assert.Equal(new long[] { 3, 4, 5 }, first.Events.Select(e => e.Seq));
        Assert.Equal(5, first.LatestSeq);
        Assert.False(first.Truncated);
    }

    [Fact]
    public void AnAppThatIsFullyCaughtUpGetsAnEmptyFirstPush()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store, 2);

        var first = EventsSubscription.Next(Start("2"), store, true, true, T0);

        Assert.Empty(first.Events);
        Assert.Equal(2, first.LatestSeq);
    }

    [Fact]
    public void ALastSeqTheLogNoLongerReaches_IsReportedAsTruncated()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store, 520);

        var first = EventsSubscription.Next(Start("10"), store, true, true, T0);

        Assert.True(first.Truncated);
        Assert.Equal(520, first.LatestSeq);
        Assert.Equal(500, first.Events.Count);
    }

    [Fact]
    public void NewEventsGoOutOnceEach_AndNothingIsSentTwiceOnOneConnection()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        var state = Start("0");
        EventsSubscription.Next(state, store, true, true, T0);

        Log(store, 2);
        var push = EventsSubscription.Next(state, store, true, true, T0.AddSeconds(1));
        Assert.Equal(new long[] { 1, 2 }, push.Events.Select(e => e.Seq));
        Assert.Equal(2, push.LatestSeq);

        Assert.Null(EventsSubscription.Next(state, store, true, true, T0.AddSeconds(2)));

        Log(store);
        var next = EventsSubscription.Next(state, store, true, true, T0.AddSeconds(3));
        Assert.Equal(new long[] { 3 }, next.Events.Select(e => e.Seq));
    }

    [Fact]
    public void EachConnectionKeepsItsOwnLastSentSeq()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store, 3);
        var behind = Start("0");
        var caughtUp = Start("3");

        Assert.Equal(3, EventsSubscription.Next(behind, store, true, true, T0).Events.Count);
        Assert.Empty(EventsSubscription.Next(caughtUp, store, true, true, T0).Events);

        Log(store);
        Assert.Equal(new long[] { 4 }, EventsSubscription.Next(behind, store, true, true, T0.AddSeconds(1)).Events.Select(e => e.Seq));
        Assert.Equal(new long[] { 4 }, EventsSubscription.Next(caughtUp, store, true, true, T0.AddSeconds(1)).Events.Select(e => e.Seq));
    }

    [Fact]
    public void AQuietConnectionGetsAnEmptyHeartbeatAboutEvery30Seconds()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store, 2);
        var state = Start("2");
        EventsSubscription.Next(state, store, true, true, T0);

        Assert.Null(EventsSubscription.Next(state, store, true, true, T0.AddSeconds(10)));
        Assert.Null(EventsSubscription.Next(state, store, true, true, T0.AddSeconds(20)));

        var beat = EventsSubscription.Next(state, store, true, true, T0.AddSeconds(30));
        Assert.Empty(beat.Events);
        Assert.Equal(2, beat.LatestSeq);
        Assert.Null(beat.Error);

        Assert.Null(EventsSubscription.Next(state, store, true, true, T0.AddSeconds(40)));
        Assert.NotNull(EventsSubscription.Next(state, store, true, true, T0.AddSeconds(60)));
    }

    [Fact]
    public void AnEventPushCountsAsTheLastSend_SoNoHeartbeatFollowsAtOnce()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        var state = Start("0");
        EventsSubscription.Next(state, store, true, true, T0);

        Log(store);
        Assert.NotNull(EventsSubscription.Next(state, store, true, true, T0.AddSeconds(28)));
        Assert.Null(EventsSubscription.Next(state, store, true, true, T0.AddSeconds(40)));
    }

    [Fact]
    public void APushSerializesToTheContractsShape()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        Log(store);
        var json = new TestJson();

        var ok = json.SerializeToString(EventsSubscription.Next(Start("0"), store, true, true, T0));
        Assert.Contains("\"ApiLevel\":1", ok);
        Assert.Contains("\"LatestSeq\":1", ok);
        Assert.Contains("\"Truncated\":false", ok);
        Assert.Contains("\"Events\":[{\"Seq\":1,\"Name\":\"GitHubRepoPluginInstall.RunCompleted\"", ok);
        Assert.Contains("\"AtUtc\"", ok);
        Assert.Contains("\"Data\":{", ok);
        Assert.DoesNotContain("Error", ok);

        var refusal = json.SerializeToString(EventsSubscription.Next(Start("0"), store, false, true, T0));
        Assert.StartsWith("{\"Error\":\"Administrator login required.\",\"ServerTimeUtc\":\"2026-10-14T12:00:00", refusal);
    }
}

public class EventsSubscriptionTokenTests
{
    private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static EventLogStore Make(TempDir dir) =>
        new EventLogStore(dir.Path, new TestJson(), new TestLogger(), "GitHub Repo Plugin Install");

    private static EventsPushState Start(string data)
    {
        var state = new EventsPushState();
        EventsSubscription.ParseStartValues(data.Split(','), state);
        return state;
    }

    [Fact]
    public void TheStartMessageCarriesTheLastSeqThenTheToken()
    {
        var state = Start("7,tok123");
        Assert.Equal(7, state.AfterSeq);
        Assert.Equal("tok123", state.Token);

        Assert.Null(Start("0").Token);
        Assert.Null(Start("0,  ").Token);
    }

    [Fact]
    public void AnAdministratorsTokenIsEnoughWhenConnectionUserIsEmpty()
    {
        using var dir = new TempDir();
        var state = Start("0,admin-token");

        var push = EventsSubscription.Next(state, Make(dir), isAdministrator: false, apiEnabled: true, T0, t => t == "admin-token");

        Assert.Null(push.Error);
        Assert.NotNull(push.Events);
    }

    [Fact]
    public void AWrongMissingOrNonAdministratorTokenGetsTheError()
    {
        using var dir = new TempDir();

        var wrong = EventsSubscription.Next(Start("0,nope"), Make(dir), false, true, T0, t => false);
        var none  = EventsSubscription.Next(Start("0"), Make(dir), false, true, T0, t => true);
        var boom  = EventsSubscription.Next(Start("0,x"), Make(dir), false, true, T0, t => throw new InvalidOperationException());

        Assert.Equal(EventsSubscription.NotAdministrator, wrong.Error);
        Assert.Equal(EventsSubscription.NotAdministrator, none.Error);
        Assert.Equal(EventsSubscription.NotAdministrator, boom.Error);
    }

    [Fact]
    public void TheTokenIsCheckedAgainOnEveryPush()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        var state = Start("0,tok");
        var valid = true;

        Assert.Null(EventsSubscription.Next(state, store, false, true, T0, t => valid).Error);
        valid = false;
        Assert.Equal(EventsSubscription.NotAdministrator, EventsSubscription.Next(state, store, false, true, T0.AddSeconds(10), t => valid).Error);
        Assert.Null(EventsSubscription.Next(state, store, false, true, T0.AddSeconds(20), t => valid));
    }

    [Fact]
    public void ConnectionUserAdministratorStillWorksWithoutAToken()
    {
        using var dir = new TempDir();
        Assert.Null(EventsSubscription.Next(Start("0"), Make(dir), true, true, T0, t => false).Error);
    }

    [Fact]
    public void TheApiSwitchStillAppliesToATokenSubscriber_AndTheTokenNeverAppearsInAPush()
    {
        using var dir = new TempDir();
        var store = Make(dir);
        EventRecorder.Record(store, true, PushEvents.RunCompleted, new RunEventData { Updated = 1 });
        var json = new TestJson();

        var off = EventsSubscription.Next(Start("0,secret-token"), store, false, false, T0, t => true);
        var on  = EventsSubscription.Next(Start("0,secret-token"), store, false, true, T0, t => true);

        Assert.Equal(EventsSubscription.ApiOff, off.Error);
        Assert.Null(off.Events);
        Assert.DoesNotContain("secret-token", json.SerializeToString(off));
        Assert.DoesNotContain("secret-token", json.SerializeToString(on));
        Assert.DoesNotContain("secret-token", File.ReadAllText(Directory.GetFiles(dir.Path, "*.json").Single()));
    }
}
