using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Emby.GitHubRepoPluginInstall.Helpers;
using Emby.GitHubRepoPluginInstall.Models;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;

namespace Emby.GitHubRepoPluginInstall.UI;

public class RepoConfigUi : EditableOptionsBase
{
    public override string EditorTitle => "Manage GitHub Repositories";

    public override string EditorDescription => "Configure the repositories to download plugins from.";

    [Browsable(false)]
    public List<PluginRegistryEntry> AvailablePlugins { get; set; } = new List<PluginRegistryEntry>();

    [Browsable(false)]
    public IEnumerable<EditorSelectOption> PluginList
    {
        get
        {
            var options = new List<EditorSelectOption>();
            
            // Add empty option
            options.Add(new EditorSelectOption 
            { 
                Name = "-- Manual Entry --", 
                Value = "",
                ShortName = "Manual",
                IsEnabled = true
            });
            
            // Add plugins grouped by registry
            foreach (var plugin in AvailablePlugins.OrderBy(p => p.RegistrySource).ThenBy(p => p.Name))
            {
                options.Add(new EditorSelectOption
                {
                    Name = $"[{plugin.RegistrySource}] {plugin.Name}",
                    Value = plugin.Url,
                    ShortName = plugin.Name,
                    ToolTip = plugin.Description ?? "No description available",
                    IsEnabled = true
                });
            }
            
            return options;
        }
    }

    [DisplayName("Select from Registry")]
    [Description("Choose a plugin from available registries or enter URL manually below")]
    [SelectItemsSource(nameof(PluginList))]
    [VisibleCondition(nameof(PluginList), SimpleCondition.IsNotNullOrEmpty)]
    [AutoPostBack("PluginSelected", nameof(SelectedPlugin))]
    public string SelectedPlugin { get; set; }

    public SpacerItem Spacer1 { get; set; } = new SpacerItem();

    [DisplayName("GitHub Repo URL")]
    [Description("Paste the repository address, or just owner/repository (for example alex-dev/emby-subtitle-sync).")]
    [Required]
    public string Url { get; set; }

    /// <summary>File names on the repository's latest release. Filled in when editing an existing entry.</summary>
    [Browsable(false)]
    public List<string> AvailableAssets { get; set; } = new List<string>();

    /// <summary>Set to a value when there are release files to pick from. Drives the picker's visibility.</summary>
    [Browsable(false)]
    public string AssetPickerFlag => AvailableAssets != null && AvailableAssets.Count > 0 ? "1" : null;

    [Browsable(false)]
    public IEnumerable<EditorSelectOption> AssetList
    {
        get
        {
            var options = new List<EditorSelectOption>
                          {
                              new EditorSelectOption { Name = "-- Choose a file to match --", Value = "", ShortName = "None", IsEnabled = true }
                          };

            foreach (var asset in AvailableAssets ?? new List<string>())
            {
                var filter = AssetFilterHelper.SuggestFilter(asset);
                if (string.IsNullOrEmpty(filter)) continue;

                options.Add(new EditorSelectOption
                            {
                                Name      = $"{asset}  (filter: {filter})",
                                Value     = filter,
                                ShortName = filter,
                                ToolTip   = $"Only use release files whose name contains \"{filter}\"",
                                IsEnabled = true
                            });
            }

            return options;
        }
    }

    [DisplayName("Pick from the latest release")]
    [Description("Choosing a file fills in the filter below with the part of its name that stays the same between versions.")]
    [SelectItemsSource(nameof(AssetList))]
    [VisibleCondition(nameof(AssetPickerFlag), SimpleCondition.IsNotNullOrEmpty)]
    [AutoPostBack("AssetSelected", nameof(SelectedAsset))]
    public string SelectedAsset { get; set; }

    [DisplayName("Asset name filter (optional)")]
    [Description("Only use release files whose name contains this text. Example: Emby (picks Moonfin.Emby-*.zip, skips Moonfin.Server-*.zip / Jellyfin)")]
    public string AssetNameFilter { get; set; } = "";

    [DisplayName("Allow PreRelease Versions")]
    public bool AllowPreReleaseVersions { get; set; }

    [DisplayName("Auto Update")]
    public bool AutoUpdate { get; set; } = true;

    [Browsable(false)]
    [DontSave]
    public string Id { get; set; }
}