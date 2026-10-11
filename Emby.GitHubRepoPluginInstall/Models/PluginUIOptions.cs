using System.Collections.Generic;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using MediaBrowser.Model.Attributes;

namespace Emby.GitHubRepoPluginInstall.Models;

/// <summary>
/// The plugin page. Properties are drawn top to bottom in the order they are declared here.
/// Everything you can do to a plugin or a registry is on its own card, so there are no tables to select from.
/// </summary>
public class PluginUIOptions : EditableOptionsBase
{
    [DontSave]
    public override string EditorTitle => "GitHub Repo Plugin Install";

    [DontSave]
    public override string EditorDescription =>
        "Keeps your Emby plugins up to date from their GitHub releases.";

    // ---- Status: what needs attention, what the last run did -------------------------------------------------

    /// <summary>Set to a value while a restart is pending. Drives the visibility of the banner below.</summary>
    [Browsable(false)]
    [DontSave]
    public string RestartFlag { get; set; }

    [DisplayName("Restart needed")]
    [DontSave]
    [VisibleCondition(nameof(RestartFlag), SimpleCondition.IsNotNullOrEmpty)]
    public GenericItemList RestartBanner { get; set; } = new GenericItemList();

    [DisplayName("Last run")]
    [DontSave]
    public GenericItemList RunSummary { get; set; } = new GenericItemList();

    [DontSave]
    public CaptionItem Logs { get; set; } = new CaptionItem("")
                                            {
                                                IsVisible = false
                                            };

    // ---- Plugins ---------------------------------------------------------------------------------------------

    [DontSave]
    public SpacerItem SpacerStatus { get; set; } = new SpacerItem();

    [DontSave]
    public CaptionItem CaptionLatestReleases { get; set; } = new CaptionItem("Plugins");

    /// <summary>
    /// Sits above the plugin cards and is only visible while at least one plugin has an update. It installs those
    /// plugins and nothing else. The caption and the confirmation text are filled in when the page is built.
    /// </summary>
    [DontSave]
    public ButtonItem UpdateAvailableBtn { get; set; } = new ButtonItem
                                                         {
                                                             Caption   = "Update plugins with updates",
                                                             Data1     = "UpdateAvailable",
                                                             Icon      = IconNames.system_update_alt,
                                                             IsVisible = false
                                                         };

    [DisplayName("Status of each plugin")]
    [DontSave]
    public GenericItemList Releases { get; set; } = new GenericItemList();

    [DontSave]
    public ButtonItem Add =>
        new ButtonItem
        {
            Icon    = IconNames.add,
            Caption = "Add Plugin",
            Data1   = "Add"
        };

    [DontSave]
    public ButtonItem UpdateAll =>
        new ButtonItem
        {
            Icon    = IconNames.system_update_alt,
            Caption = "Update Everything Now",
            Data1   = "UpdateAll",
            ConfirmationPrompt = "Install the newest release of every plugin in the list, including ones with auto-update turned off?"
        };

    [DontSave]
    public CaptionItem RepoLogs { get; set; } = new CaptionItem("")
                                               {
                                                   IsVisible = false
                                               };

    [Browsable(false)]
    public List<ReposToProcess> Repos { get; set; } = new List<ReposToProcess>();

    // ---- History ---------------------------------------------------------------------------------------------

    [DontSave]
    public SpacerItem SpacerHistory { get; set; } = new SpacerItem();

    [DontSave]
    public CaptionItem CaptionHistory { get; set; } = new CaptionItem("Run history");

    [DisplayName("Recent runs")]
    [DontSave]
    public GenericItemList RunHistory { get; set; } = new GenericItemList();

    // ---- Settings --------------------------------------------------------------------------------------------

    [DontSave]
    public SpacerItem Spacer1 { get; set; } = new SpacerItem();

    [DontSave]
    public CaptionItem CaptionBasic { get; set; } = new CaptionItem("Settings");

    [DisplayName("GitHub Token (PAT)")]
    [Description("Needed for private repositories, and it raises GitHub's limit from 60 to 5,000 requests per hour. " +
                 "A fine-grained token with read-only access to Contents is enough.")]
    [DontSave]
    public string GitHubToken { get; set; }

    [Browsable(false)]
    public string EncryptedGitHubToken { get; set; }

    /// <summary>Result of Test Token. A list item rather than a caption, because the message can be long and a caption is cut off.</summary>
    [DisplayName("Token check")]
    [DontSave]
    public GenericItemList TokenStatus { get; set; } = new GenericItemList();

    [DontSave]
    public ButtonItem TestTokenBtn =>
        new ButtonItem
        {
            Icon    = IconNames.fact_check,
            Caption = "Test Token",
            Data1   = "TestToken"
        };

    [DisplayName("Restart Emby automatically after scheduled updates")]
    [Description("Off by default. When on, the daily task restarts the server as soon as it installs an update. " +
                 "Updates you start from this page never restart the server on their own.")]
    public bool RestartServerAfterInstall { get; set; }

    /// <summary>True once the installer's own repository has been offered for self-update (it is added once, never re-added after removal).</summary>
    [Browsable(false)]
    public bool SelfEntryHandled { get; set; }

    [DontSave]
    public ButtonItem SaveTokenBtn =>
        new ButtonItem
        {
            Icon    = IconNames.save_alt,
            Caption = "Save Settings",
            Data1   = "Save"
        };

    // ---- Registries ------------------------------------------------------------------------------------------

    [DontSave]
    public SpacerItem Spacer3 { get; set; } = new SpacerItem();

    [DontSave]
    public CaptionItem CaptionRegistries { get; set; } = new CaptionItem("Plugin Registries");

    [Browsable(false)]
    public List<PluginRegistry> PluginRegistries { get; set; } = new List<PluginRegistry>();

    [DisplayName("Registries")]
    [DontSave]
    public GenericItemList Registries { get; set; } = new GenericItemList();

    [DontSave]
    public ButtonItem AddRegistry =>
        new ButtonItem
        {
            Icon    = IconNames.library_add,
            Caption = "Add Registry",
            Data1   = "AddRegistry"
        };

    [DontSave]
    public CaptionItem RegistryLogs { get; set; } = new CaptionItem("")
                                                   {
                                                       IsVisible = false
                                                   };

    [DontSave]
    public SpacerItem Spacer4 { get; set; } = new SpacerItem();
}
