using System.IO;
using System.Text.RegularExpressions;

namespace Emby.GitHubRepoPluginInstall.Helpers;

public static class AssetFilterHelper
{
    private static readonly Regex TrailingVersion = new Regex(@"[-_. ]?v?\d+(\.\d+)*([-+].*)?$", RegexOptions.Compiled);

    /// <summary>
    /// Turns a release file name into a filter that keeps matching after the version changes:
    /// "Moonfin.Emby-2.1.0.0.zip" becomes "Moonfin.Emby".
    /// </summary>
    public static string SuggestFilter(string assetName)
    {
        if (string.IsNullOrWhiteSpace(assetName)) return string.Empty;

        var name = Path.GetFileNameWithoutExtension(assetName);
        name = TrailingVersion.Replace(name, string.Empty);
        return name.Trim('-', '_', '.', ' ');
    }
}
