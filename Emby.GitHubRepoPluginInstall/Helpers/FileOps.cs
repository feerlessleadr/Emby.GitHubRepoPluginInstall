using System;
using System.IO;
using System.Linq;

namespace Emby.GitHubRepoPluginInstall.Helpers;

public static class FileOps
{
    public const string OldSuffix = ".old";

    /// <summary>
    /// Copies <paramref name="source"/> over <paramref name="destination"/>. If the destination can't be
    /// overwritten because it's in use (a loaded plugin DLL on Windows), the existing file is renamed to
    /// "*.old" first, which Windows allows, and the new file takes its name.
    /// </summary>
    public static void OverwriteFile(string source, string destination)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            File.Copy(source, destination, true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            if (!File.Exists(destination)) throw;

            var parked = ParkExisting(destination);
            File.Copy(source, destination, true);
            TryDelete(parked);
        }
    }

    /// <summary>Moves <paramref name="source"/> to <paramref name="destination"/>, replacing it, with the same locked-file fallback.</summary>
    public static void ReplaceFile(string source, string destination)
    {
        OverwriteFile(source, destination);
        TryDelete(source);
    }

    /// <summary>Removes "*.old" leftovers from earlier locked-file replacements. Safe to call often.</summary>
    public static int CleanupOldFiles(string folder)
    {
        if (!Directory.Exists(folder)) return 0;

        var removed = 0;
        foreach (var file in Directory.GetFiles(folder, "*" + OldSuffix, SearchOption.AllDirectories))
        {
            if (file.IndexOf("plugin-backups", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (TryDelete(file)) removed++;
        }

        return removed;
    }

    private static string ParkExisting(string path)
    {
        var parked = path + OldSuffix;
        if (File.Exists(parked) && !TryDelete(parked))
            parked = path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + OldSuffix;

        File.Move(path, parked);
        return parked;
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Chooses which DLL to remember as "the plugin" after unpacking a ZIP: the one tracked before if it is
    /// still there, else the one named like the repository, else the only or first top-level DLL.
    /// </summary>
    public static string PickTrackedDll(string[] topLevelDllNames, string repositoryName, string previousFileName)
    {
        if (topLevelDllNames == null || topLevelDllNames.Length == 0) return null;

        if (!string.IsNullOrEmpty(previousFileName))
        {
            var previous = topLevelDllNames.FirstOrDefault(n => string.Equals(n, previousFileName, StringComparison.OrdinalIgnoreCase));
            if (previous != null) return previous;
        }

        if (topLevelDllNames.Length == 1) return topLevelDllNames[0];

        var wanted = Normalize(repositoryName);
        if (wanted.Length > 0)
        {
            var best = topLevelDllNames
                       .Select(n => new { Name = n, Norm = Normalize(Path.GetFileNameWithoutExtension(n)) })
                       .Where(x => x.Norm.Length > 0 && (x.Norm.Contains(wanted) || wanted.Contains(x.Norm)))
                       .OrderBy(x => Math.Abs(x.Norm.Length - wanted.Length))
                       .FirstOrDefault();
            if (best != null) return best.Name;
        }

        return topLevelDllNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).First();
    }

    private static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
