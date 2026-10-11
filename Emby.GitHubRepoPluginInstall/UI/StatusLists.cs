using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Emby.GitHubRepoPluginInstall.Helpers;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.GitHubRepoPluginInstall.Services;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;

namespace Emby.GitHubRepoPluginInstall.UI;

/// <summary>
/// Builds the status cards on the plugin page from stored data. Nothing here talks to GitHub,
/// so opening the page is instant and never uses up API requests.
///
/// How Emby draws a list item decides where text goes:
///  - PrimaryText is HTML. Anything that came from outside (release notes, error text, names) is encoded first.
///  - SecondaryText is plain text on a single line that is cut off with "..." when it is too long. It must stay short
///    and must not be encoded or contain markup, or the markup shows up on screen.
///  - SubItems appear under the card and use the same rules, so longer text goes there as PrimaryText.
///
/// Emby also limits plugin pages to 80 characters wide and cuts every list line off with "...". Neither can be changed
/// from C#, so the page carries a small stylesheet (<see cref="PageStyle"/>) that lifts both limits for this page only.
/// </summary>
internal static class StatusLists
{
    public const int HistoryItemsShown = 10;

    private static string H(string text) => HttpUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>Encodes text for use in PrimaryText.</summary>
    public static string Encode(string text) => H(text);

    /// <summary>The class on the marker element that scopes the page stylesheet to this page.</summary>
    public const string PageMarkerClass = "ghrpi-page";

    /// <summary>
    /// Added to the summary card's text, since list text is the one place a plugin can put its own markup.
    ///  - Emby's "form { max-width: 80ch }" is lifted so the page uses the whole width of the window.
    ///  - List text wraps instead of being cut off with "...", so long text reads in full at any width.
    ///  - A card's row can wrap, and its text column keeps a minimum width, so on a phone the buttons and switches
    ///    drop below the text instead of squeezing it into a sliver.
    /// Every rule only matches something that contains the marker, so they apply to this page only. The stylesheet is
    /// removed with the page, so nothing leaks into other settings pages. A browser too old to understand :has() ignores
    /// the rules, and the page keeps Emby's own narrow layout.
    /// </summary>
    public static readonly string PageStyle =
        "<span class=\"" + PageMarkerClass + "\"></span><style>" +
        "form:has(." + PageMarkerClass + "),.readOnlyContent:has(." + PageMarkerClass + "){max-width:none!important}" +
        ".readOnlyContent:has(." + PageMarkerClass + ") .listItemBodyText-nowrap" +
        "{white-space:normal!important;overflow:visible!important;text-overflow:clip!important}" +
        ".readOnlyContent:has(." + PageMarkerClass + ") .ge-itemlist-listitem-listitemcontent{flex-wrap:wrap}" +
        ".readOnlyContent:has(." + PageMarkerClass + ") .ge-itemlist-listitem-listitemcontent>.listItemBody" +
        "{flex:1 1 11em;min-width:11em}" +
        "</style>";

    /// <summary>A line of detail under a card.</summary>
    private static GenericListItem Note(string text, IconNames? icon = null)
    {
        return new GenericListItem
               {
                   PrimaryText = H(text),
                   Icon        = icon,
                   IconMode    = icon == null ? ItemListIconMode.NoIcons : ItemListIconMode.LargeRegular
               };
    }

    private static List<GenericListItem> NotesOrNull(List<GenericListItem> notes) => notes.Count == 0 ? null : notes;

    public static GenericItemList BuildBanner(PluginRuntimeState state)
    {
        var list = new GenericItemList();
        if (state == null || state.PendingRestart.Count == 0) return list;

        var changes = state.PendingRestart
                           .Select(p => string.IsNullOrEmpty(p.FromVersion)
                                       ? $"{p.Name} {p.ToVersion} (new)"
                                       : $"{p.Name} {p.FromVersion} → {p.ToVersion}")
                           .ToList();

        list.Add(new GenericListItem
                 {
                     PrimaryText   = "Restart Emby to finish updating",
                     SecondaryText = changes.Count == 1 ? changes[0] : $"{changes.Count} plugins changed, listed below",
                     Status        = ItemStatus.Warning,
                     Icon          = IconNames.restart_alt,
                     IconMode      = ItemListIconMode.LargeRegular,
                     SubItems      = changes.Count == 1 ? null : changes.Select(c => Note(c)).ToList(),
                     Button1 = new ButtonItem
                               {
                                   Caption            = "Restart now",
                                   Icon               = IconNames.restart_alt,
                                   Data1              = "RestartNow",
                                   ConfirmationPrompt = "Restart Emby now? Playback, scans and downloads in progress will stop."
                               },
                     Button2 = new ButtonItem
                               {
                                   Caption = "Dismiss",
                                   Data1   = "DismissRestart"
                               }
                 });

        return list;
    }

    public static GenericItemList BuildSummary(RunRecord last, RunProgressInfo progress, bool hasToken)
    {
        var list  = new GenericItemList();
        var item  = new GenericListItem { IconMode = ItemListIconMode.LargeRegular };
        var notes = new List<GenericListItem>();

        if (progress != null && progress.IsRunning)
        {
            item.Status          = ItemStatus.InProgress;
            item.Icon            = IconNames.sync;
            item.HasPercentage   = true;
            item.PercentComplete = progress.Total == 0 ? 0 : Math.Min(100, progress.Done * 100 / progress.Total);
            item.PrimaryText     = $"Update in progress: {progress.Done} of {progress.Total}" +
                                   (string.IsNullOrEmpty(progress.Current) ? "" : $" ({H(progress.Current)})");
            item.SecondaryText   = $"{progress.Trigger} run started {TimeFormat.Ago(progress.StartedUtc)}. Click Refresh for progress.";
            item.Button1 = new ButtonItem { Caption = "Refresh", Icon = IconNames.refresh, Data1 = "RefreshView" };
        }
        else
        {
            item.Button1 = new ButtonItem { Caption = "Run now", Icon = IconNames.play_arrow, Data1 = "RunNow" };
            item.Button2 = new ButtonItem { Caption = "Check for updates", Icon = IconNames.refresh, Data1 = "CheckAll" };

            if (last == null)
            {
                item.Status        = ItemStatus.Unknown;
                item.Icon          = IconNames.schedule;
                item.PrimaryText   = "No runs yet";
                item.SecondaryText = "Click Run now to check every plugin straight away.";
            }
            else
            {
                item.Status        = last.Failed > 0 ? ItemStatus.Warning : ItemStatus.Succeeded;
                item.Icon          = last.Failed > 0 ? IconNames.warning : IconNames.check_circle_outline;
                item.PrimaryText   = $"Last run: {TimeFormat.Stamp(last.StartedUtc)} · {H(last.Trigger?.ToLowerInvariant())} · took {TimeFormat.Duration(last.DurationSeconds)}";
                item.SecondaryText = Counts(last);
            }

            notes.Add(Note("The daily task checks every plugin and installs updates for the ones set to auto-update. " +
                           "Change the schedule under Scheduled Tasks.", IconNames.schedule));
        }

        if (!hasToken)
        {
            notes.Add(Note("No GitHub token is set, so GitHub allows only 60 checks per hour. " +
                           "Add a token under Settings if you see rate limit errors.", IconNames.info));
        }

        item.SubItems = NotesOrNull(notes);

        // This card is always on the page, so it carries the page stylesheet (see PageStyle)
        item.PrimaryText += PageStyle;

        list.Add(item);
        return list;
    }

    public static string Counts(RunRecord run)
    {
        var text = $"{run.Updated} updated · {run.UpToDate} up to date · {run.Failed} failed";
        return run.Waiting > 0 ? text + $" · {run.Waiting} waiting on you" : text;
    }

    private static int Order(RepoStatus status)
    {
        switch (status)
        {
            case RepoStatus.Failed:          return 0;
            case RepoStatus.UpdateAvailable: return 1;
            case RepoStatus.UpdateWaiting:   return 2;
            case RepoStatus.NotChecked:      return 3;
            case RepoStatus.NoRelease:       return 4;
            default:                         return 5;
        }
    }

    /// <param name="runningNote">Returns text like "Running 2.3.0, but the installed file is 2.4.1", or null.</param>
    public static GenericItemList BuildPlugins(IEnumerable<ReposToProcess> repos, Func<ReposToProcess, string> runningNote = null)
    {
        var list = new GenericItemList();

        foreach (var repo in (repos ?? Enumerable.Empty<ReposToProcess>())
                             .OrderBy(r => Order(r.GetStatus()))
                             .ThenBy(r => r.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var status = repo.GetStatus();
            var notes  = new List<GenericListItem>();
            var item = new GenericListItem
                       {
                           PrimaryText = H(repo.Repository),
                           IconMode    = ItemListIconMode.LargeRegular,
                           Tag         = new TagItem
                                         {
                                             Text    = repo.StatusText,
                                             TagType = status == RepoStatus.UpToDate || status == RepoStatus.NotChecked || status == RepoStatus.NoRelease ? TagTypes.Light : TagTypes.Solid
                                         }
                       };

            switch (status)
            {
                case RepoStatus.Failed:
                    item.Status = ItemStatus.Failed;
                    item.Icon   = IconNames.error_outline;
                    break;
                case RepoStatus.UpdateAvailable:
                case RepoStatus.UpdateWaiting:
                    item.Status = ItemStatus.Warning;
                    item.Icon   = IconNames.new_releases;
                    break;
                case RepoStatus.UpToDate:
                    item.Status = ItemStatus.Succeeded;
                    item.Icon   = IconNames.check_circle_outline;
                    break;
                default:
                    item.Status = ItemStatus.Unknown;
                    item.Icon   = IconNames.info;
                    break;
            }

            string versionLine;
            if (status == RepoStatus.UpToDate)
                versionLine = $"Version {repo.LastVersionDownloaded}";
            else if (status == RepoStatus.UpdateAvailable || status == RepoStatus.UpdateWaiting)
                versionLine = string.IsNullOrEmpty(repo.LastVersionDownloaded)
                    ? $"Not installed by this plugin yet → {repo.LatestVersion}"
                    : $"Installed {repo.LastVersionDownloaded} → {repo.LatestVersion}";
            else if (status == RepoStatus.NoRelease)
                versionLine = "No release published yet";
            else if (status == RepoStatus.NotChecked)
                versionLine = "Not checked yet. Click Check for updates above.";
            else
                versionLine = string.IsNullOrEmpty(repo.LastVersionDownloaded) ? "Problem found. Details below." : $"Installed {repo.LastVersionDownloaded}. Problem found, details below.";

            var checkedLine = repo.LastDateTimeChecked == null ? null : $"checked {TimeFormat.Ago(repo.LastDateTimeChecked)}";
            item.SecondaryText = string.Join(" · ", new[] { versionLine, checkedLine }.Where(s => !string.IsNullOrEmpty(s)));

            // Anything long goes under the card, where it wraps
            if (status == RepoStatus.Failed)
                notes.Add(Note(repo.LastError, IconNames.error_outline));
            // The auto-update switch has a row of its own, so the card's top row keeps room for the name and badge
            notes.Add(AutoUpdateRow(repo));

            var running = runningNote?.Invoke(repo);
            if (!string.IsNullOrEmpty(running))
                notes.Add(Note(running, IconNames.restart_alt));

            if (!string.IsNullOrWhiteSpace(repo.LatestNotes))
            {
                notes.Add(new GenericListItem
                          {
                              PrimaryText   = "Release notes<br/>" + NotesHtml(repo.LatestNotes),
                              SecondaryText = repo.LatestPublishedUtc == null ? null : "Published " + TimeFormat.Ago(repo.LatestPublishedUtc),
                              Icon          = IconNames.message,
                              IconMode      = ItemListIconMode.LargeRegular
                          });
            }

            item.SubItems = NotesOrNull(notes);

            // Everything you can do to a plugin lives on its own card, so there is nothing to select first
            item.Button2 = ManageMenu(repo);

            if (status == RepoStatus.UpdateAvailable || status == RepoStatus.UpdateWaiting)
            {
                item.Button1 = new ButtonItem
                               {
                                   Caption = "Update",
                                   Icon    = IconNames.system_update_alt,
                                   Data1   = "Download",
                                   Data2   = repo.Id
                               };
            }
            else if (status == RepoStatus.Failed)
            {
                item.Button1 = new ButtonItem
                               {
                                   Caption = "Retry",
                                   Icon    = IconNames.refresh,
                                   Data1   = "Download",
                                   Data2   = repo.Id
                               };
            }

            list.Add(item);
        }

        return list;
    }

    /// <summary>A row under the card with the on/off switch for installing new releases by itself.</summary>
    private static GenericListItem AutoUpdateRow(ReposToProcess repo)
    {
        return new GenericListItem
               {
                   PrimaryText   = "Update automatically",
                   SecondaryText = repo.AutoUpdate ? "New releases are installed during the daily run" : "Off. New releases wait for you to click Update",
                   Icon          = IconNames.update,
                   IconMode      = ItemListIconMode.LargeRegular,
                   Toggle        = new ToggleButtonItem
                                   {
                                       Caption   = repo.AutoUpdate ? "On" : "Off",
                                       IsChecked = repo.AutoUpdate,
                                       Data1     = "ToggleAuto",
                                       Data2     = repo.Id
                                   }
               };
    }

    private static ButtonItem ManageMenu(ReposToProcess repo)
    {
        return new ButtonItem
               {
                   Caption      = "",
                   StandardIcon = StandardIcons.ContextMenu,
                   SubMenuButtons = new List<ButtonItem>
                                    {
                                        new ButtonItem { Caption = "Edit settings", Icon = IconNames.edit, Data1 = "Edit", Data2 = repo.Id },
                                        new ButtonItem
                                        {
                                            Caption            = "Restore previous version",
                                            Icon               = IconNames.restore,
                                            Data1              = "RestoreBackup",
                                            Data2              = repo.Id,
                                            ConfirmationPrompt = $"Put back the version of {repo.Repository} that was installed before the last update? " +
                                                                 "Auto-update is switched off for it so the older version stays until you turn it back on."
                                        },
                                        new ButtonItem
                                        {
                                            Caption            = "Remove",
                                            Icon               = IconNames.delete_forever,
                                            Data1              = "Remove",
                                            Data2              = repo.Id,
                                            ConfirmationPrompt = $"Remove {repo.Repository} from the list? Its plugin file is deleted too."
                                        }
                                    }
               };
    }

    public const string BuiltInRegistryId = "embedded-default";

    public static GenericItemList BuildRegistries(IEnumerable<PluginRegistry> registries)
    {
        var list = new GenericItemList();

        foreach (var registry in registries ?? Enumerable.Empty<PluginRegistry>())
        {
            var builtIn = registry.Id == BuiltInRegistryId;
            var item = new GenericListItem
                       {
                           PrimaryText   = builtIn ? "Community plugins" : H(registry.Name),
                           SecondaryText = builtIn ? "A starter list, kept up to date from this project" : registry.RawUrl,
                           Icon          = IconNames.folder_open,
                           IconMode      = ItemListIconMode.LargeRegular,
                           Status        = registry.Enabled ? ItemStatus.Succeeded : ItemStatus.Unavailable,
                           Tag           = builtIn ? new TagItem { Text = "Built-in", TagType = TagTypes.Light } : null,
                           Toggle        = new ToggleButtonItem
                                           {
                                               Caption   = "Enabled",
                                               IsChecked = registry.Enabled,
                                               Data1     = "ToggleRegistry",
                                               Data2     = registry.Id
                                           }
                       };

            if (!builtIn)
            {
                item.Button2 = new ButtonItem
                               {
                                   Caption      = "Manage",
                                   StandardIcon = StandardIcons.ContextMenu,
                                   SubMenuButtons = new List<ButtonItem>
                                                    {
                                                        new ButtonItem { Caption = "Edit", Icon = IconNames.edit, Data1 = "EditRegistry", Data2 = registry.Id },
                                                        new ButtonItem
                                                        {
                                                            Caption            = "Remove",
                                                            Icon               = IconNames.delete_forever,
                                                            Data1              = "RemoveRegistry",
                                                            Data2              = registry.Id,
                                                            ConfirmationPrompt = $"Remove the registry {registry.Name}?"
                                                        }
                                                    }
                               };
            }

            list.Add(item);
        }

        return list;
    }

    public static string NotesHtml(string notes)
    {
        // each line the author wrote stays a line, so paragraphs and bullet lists keep their shape
        var lines = notes.Replace("\r\n", "\n").Split('\n');
        return string.Join("<br/>", lines.Select(l => string.IsNullOrWhiteSpace(l) ? "" : H(l)));
    }

    public static GenericItemList BuildHistory(IEnumerable<RunRecord> runs)
    {
        var list = new GenericItemList();

        foreach (var run in (runs ?? Enumerable.Empty<RunRecord>()).Take(HistoryItemsShown))
        {
            var item = new GenericListItem
                       {
                           PrimaryText   = $"{TimeFormat.Stamp(run.StartedUtc)} · {H(run.Trigger)} · {TimeFormat.Duration(run.DurationSeconds)}",
                           SecondaryText = Counts(run),
                           Status        = run.Failed > 0 ? ItemStatus.Warning : ItemStatus.Succeeded,
                           Icon          = IconNames.history,
                           IconMode      = ItemListIconMode.LargeRegular,
                           SubItems      = new List<GenericListItem>()
                       };

            foreach (var result in run.Results)
            {
                // The result text can be long (an error reason), so it is the wrapping first line, with the plugin name leading it
                item.SubItems.Add(new GenericListItem
                                  {
                                      PrimaryText = $"<b>{H(result.Name)}</b><br/>{H(ResultText(result))}",
                                      Status      = ResultStatus(result),
                                      IconMode    = ItemListIconMode.NoIcons
                                  });
            }

            list.Add(item);
        }

        return list;
    }

    private static string ResultText(RepoRunResult r)
    {
        switch (r.Outcome)
        {
            case RepoOutcome.Updated:
                return string.IsNullOrEmpty(r.FromVersion)
                    ? $"Installed {r.ToVersion}"
                    : $"Updated {r.FromVersion} → {r.ToVersion}";
            case RepoOutcome.Failed:        return "Failed: " + r.Message;
            case RepoOutcome.WaitingManual: return $"{r.ToVersion} is available. Waiting for you to update.";
            case RepoOutcome.NoRelease:     return "No release published yet";
            default:                        return $"Up to date ({r.ToVersion})";
        }
    }

    private static ItemStatus ResultStatus(RepoRunResult r)
    {
        switch (r.Outcome)
        {
            case RepoOutcome.Failed:        return ItemStatus.Failed;
            case RepoOutcome.WaitingManual: return ItemStatus.Warning;
            case RepoOutcome.Updated:       return ItemStatus.Succeeded;
            default:                        return ItemStatus.None;
        }
    }
}
