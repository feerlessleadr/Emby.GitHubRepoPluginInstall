using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Emby.GitHubRepoPluginInstall.Helpers;

public static class ChecksumHelper
{
    private static readonly Regex HexRegex = new Regex("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public static string Sha256Hex(string filePath)
    {
        using (var sha = SHA256.Create())
        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            return ToHex(sha.ComputeHash(stream));
        }
    }

    public static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Reads the hash out of a GitHub asset digest such as "sha256:abc...". Null if it isn't a SHA-256 digest.</summary>
    public static string ParseDigest(string digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;

        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var hash = digest.Substring(prefix.Length).Trim();
        return HexRegex.IsMatch(hash) ? hash.ToLowerInvariant() : null;
    }

    /// <summary>
    /// Finds the SHA-256 for <paramref name="assetName"/> in a checksum file. Handles both a bare hash
    /// (one-line .sha256 file) and "hash  filename" lines as written by sha256sum.
    /// </summary>
    public static string FindHashInChecksumFile(string content, string assetName)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !HexRegex.IsMatch(parts[0])) continue;

            if (parts.Length == 1)
                return parts[0].ToLowerInvariant();

            var name = parts[parts.Length - 1].TrimStart('*');
            if (string.Equals(Path.GetFileName(name), assetName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }

        return null;
    }
}
